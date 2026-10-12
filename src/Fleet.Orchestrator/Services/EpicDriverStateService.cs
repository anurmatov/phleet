using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

public sealed record EpicDriverIdentity(string WorkflowId, string RunId, string Status);
public sealed record EpicDriverGrant(string Id, string Status, DateTime ExpiresAt);
public sealed record EpicDriverChild(string WorkflowId, string RunId, string Type, string Status, string? Phase,
    string? GateVisit, string? ReviewRef, string? Repo, long? Issue, string NextAction, string? Eligibility,
    string Outcome, long? OutcomeIssue);
public sealed record EpicDriverState(EpicDriverIdentity Driver, IReadOnlyList<EpicDriverGrant> Grants,
    IReadOnlyList<EpicDriverChild> Children, string Fingerprint, bool Truncated, IReadOnlyList<string> Errors,
    DateTimeOffset ComputedAt, bool Cached);

/// <summary>Bounded read-only snapshot. Partial replies retain errors even on a cache hit.</summary>
public sealed class EpicDriverStateService(IServiceScopeFactory scopes, IEpicGrantTemporal temporal,
    EpicRunLinkage linkage, TimeProvider clock, IConfiguration config, ILogger<EpicDriverStateService> logger)
{
    private readonly EpicRunCache<EpicDriverState> _replies = new();
    private readonly EpicRunCache<EpicChildOutcome> _outcomes = new();

    public async Task<EpicDriverState> ReadAsync(string ns, string workflowId, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45), clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = deadline.Token;
        var errors = new List<string>();
        EpicRunDescription? driver = null;
        var grants = new List<EpicDriverGrant>();
        var children = new List<EpicDriverChild>();
        var truncated = false;
        var candidates = 0;
        try
        {
            driver = await temporal.DescribeAsync(ns, workflowId, null, token).WaitAsync(token);
            if (driver is null) errors.Add("driver_not_found");
            else
            {
                var key = ns + ":" + driver.RunId;
                if (_replies.TryGet(key, out var cached) && clock.GetUtcNow() - cached.ComputedAt < TimeSpan.FromSeconds(30))
                {
                    Log(workflowId, 0, cached, true);
                    return cached with { Cached = true };
                }
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
                if (db is null) errors.Add("database_unavailable");
                else
                {
                    var rows = await db.EpicGrants.AsNoTracking().Where(g => g.DriverRunId == driver.RunId
                        && g.DriverNamespace == ns && g.DriverWorkflowId == workflowId).ToListAsync(token);
                    foreach (var g in rows.OrderBy(g => g.Id.ToString("D"), StringComparer.Ordinal))
                        grants.Add(new(g.Id.ToString("D"), g.Status == "revoked" ? "revoked"
                            : g.ExpiresAt <= clock.GetUtcNow().UtcDateTime ? "expired" : "active", g.ExpiresAt));
                }
                var linked = await linkage.ListLinkedRunsAsync(ns, new(ns, workflowId, driver.RunId),
                    clock.GetUtcNow().AddHours(-24), 50, token);
                candidates = linked.Candidates; truncated = linked.Truncated; errors.AddRange(linked.Errors);
                var collected = new System.Collections.Concurrent.ConcurrentBag<EpicDriverChild>();
                var childErrors = new System.Collections.Concurrent.ConcurrentBag<string>();
                try
                {
                    await Parallel.ForEachAsync(linked.Children,
                        new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (run, childCt) =>
                        {
                            var s = run.Summary; var d = run.Description;
                            var outcome = await OutcomeAsync(ns, run, childCt);
                            if (outcome.Outcome == "unknown") childErrors.Add("outcome_unknown:" + s.WorkflowId);
                            string? eligibility = null;
                            if (!string.IsNullOrEmpty(d.GateVisit))
                            {
                                eligibility = "grant_inactive";
                                using var childScope = scopes.CreateScope();
                                var service = childScope.ServiceProvider.GetService<EpicGrantService>();
                                if (service is null) eligibility = "database_unavailable";
                                else foreach (var grant in grants)
                                {
                                    var gate = d.GateVisit.Split(':')[0];
                                    var result = await service.DryRunDecideAsync(grant.Id,
                                        new(ns, s.WorkflowId, gate, "approved", d.GateVisit, d.ReviewRef,
                                            "https://example.com/review", config["FLEET_CTO_AGENT"]), childCt);
                                    eligibility = result.Reason ?? "unknown";
                                    if (eligibility == "visibility_unknown") break;
                                }
                            }
                            collected.Add(new(s.WorkflowId, s.RunId, s.WorkflowType, d.Status, d.Phase, d.GateVisit,
                                d.ReviewRef, d.Repo, d.IssueNumber, !string.IsNullOrEmpty(d.GateVisit) ? "human_decide"
                                    : d.IsRunning ? "running" : d.Status == "Completed" ? "terminal_ok" : "terminal_failed",
                                eligibility, outcome.Outcome, outcome.Issue));
                        });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { childErrors.Add("deadline"); }
                finally
                {
                    // A deadline can stop outcome/preflight reads after linkage already succeeded.
                    // Preserve every described linked child; unavailable outcome/eligibility is unknown.
                    foreach (var run in linked.Children)
                    {
                        if (collected.Any(c => c.RunId == run.Summary.RunId)) continue;
                        var s = run.Summary; var d = run.Description;
                        var outcome = EpicChildOutcome.Parse(s.WorkflowType, d.Status, null, null, d.IssueNumber, null);
                        collected.Add(new(s.WorkflowId, s.RunId, s.WorkflowType, d.Status, d.Phase, d.GateVisit,
                            d.ReviewRef, d.Repo, d.IssueNumber, !string.IsNullOrEmpty(d.GateVisit) ? "human_decide"
                                : d.IsRunning ? "running" : d.Status == "Completed" ? "terminal_ok" : "terminal_failed",
                            string.IsNullOrEmpty(d.GateVisit) ? null : "unknown", outcome.Outcome, outcome.Issue));
                    }
                    children.AddRange(collected.OrderBy(c => c.WorkflowId, StringComparer.Ordinal).ThenBy(c => c.RunId, StringComparer.Ordinal));
                    errors.AddRange(childErrors);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { errors.Add("deadline"); }
        catch (Exception ex)
        {
            errors.Add("temporal_or_database_error");
            logger.LogWarning("EpicDriverState read failed error={Error}", ex.GetType().Name);
        }
        // Linkage can return partial progress on the internal deadline. A disconnected
        // caller is different: abort rather than publishing or caching that partial reply.
        ct.ThrowIfCancellationRequested();
        var finalErrors = errors.Distinct().Order(StringComparer.Ordinal).ToArray();
        var tuples = children.Select(c => new object?[] { c.WorkflowId, c.RunId, c.Status, c.Phase, c.GateVisit,
            c.ReviewRef, c.NextAction, c.Outcome, c.OutcomeIssue });
        var canonical = JsonSerializer.Serialize(new { children = tuples, grants = grants.Select(g => new[] { g.Id, g.Status }) });
        var fingerprint = finalErrors.Length > 0 ? "none" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var reply = new EpicDriverState(new(workflowId, driver?.RunId ?? "", driver?.Status ?? "unknown"),
            grants, children, fingerprint, truncated, finalErrors, clock.GetUtcNow(), false);
        if (driver is not null) _replies.Put(ns + ":" + driver.RunId, reply);
        Log(workflowId, candidates, reply, false);
        return reply;
    }

    private async Task<EpicChildOutcome> OutcomeAsync(string ns, EpicLinkedRun run, CancellationToken ct)
    {
        var s = run.Summary;
        if (s.WorkflowType != "UweDesignWorkflow" || run.Description.Status != "Completed")
            return EpicChildOutcome.Parse(s.WorkflowType, run.Description.Status, null, null, run.Description.IssueNumber, null);
        var key = ns + ":" + s.RunId;
        if (_outcomes.TryGet(key, out var outcome)) return outcome;
        try
        {
            var close = await temporal.ReadCloseResultAsync(ns, s.WorkflowId, s.RunId, ct).WaitAsync(ct);
            string? existing = null;
            if (!string.IsNullOrEmpty(run.History.InputJson))
            {
                using var input = JsonDocument.Parse(run.History.InputJson);
                if (EpicJson.Property(input.RootElement, "ExistingIssueNumber") is { } field)
                    existing = field.ValueKind == JsonValueKind.String ? field.GetString() : field.GetRawText();
            }
            outcome = EpicChildOutcome.Parse(s.WorkflowType, run.Description.Status, close?.Encoding, close?.Json, run.Description.IssueNumber, existing);
            if (outcome.Outcome != "unknown") _outcomes.Put(key, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new("unknown", null); }
    }

    private void Log(string driver, int candidates, EpicDriverState state, bool cached) =>
        logger.LogInformation("EpicDriverState driver={Driver} candidates={Candidates} children={Children} errors={Errors} invalid_results={InvalidResults} cached={Cached}",
            driver, candidates, state.Children.Count, state.Errors.Count, state.Children.Count(c => c.Outcome == "invalid_result"), cached);
}
