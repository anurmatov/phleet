using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

public sealed record EpicBudget(int Used, int Max);
public sealed record EpicRunBudget(int Used, int PendingRunning, int Max);
public sealed record EpicGrantBudgets(EpicBudget Decisions, EpicBudget ChildIssues, EpicRunBudget DelegatedRuns, EpicBudget AcceptanceRounds);
public sealed record EpicGrantAdmission(string Result, string? Reason, bool NeedsBinding, EpicGrantBudgets? Budgets, DateTimeOffset CachedAt);
public sealed record EpicGrantBindingView(string Namespace, string Repo, long Issue, string WorkflowId, string RunId, string ReviewRef, DateTime CreatedAt);
public sealed record EpicGrantRunView(string Namespace, string WorkflowId, string RunId, string WorkflowType, string Repo, long? Issue, int MaxFixRound, DateTime FirstReservedAt);

public sealed partial class EpicGrantService
{
    private static readonly EpicRunCache<EpicGrantAdmission> AdmissionReplies = new();

    public async Task<EpicGrantAdmission> AdmissionAsync(string id, string? workflowType, string? repo, long? issue, string? planItem, CancellationToken ct)
    {
        EpicGrantAdmission Deny(string reason, EpicGrantBudgets? budgets = null) => new("deny",reason,false,budgets,clock.GetUtcNow());
        if (!_options.AutonomyEnabled) return Deny(EpicGrantRefusal.AutonomyDisabled);
        try
        {
            if (ParseId(id) is not { } grantId) return Deny(EpicGrantRefusal.GrantInactive);
            var grant = await db.EpicGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == grantId,ct);
            if (grant is null || grant.Status != EpicGrantStatus.Active) return Deny(EpicGrantRefusal.GrantInactive);
            if (UtcNow >= AsUtc(grant.ExpiresAt)) return Deny(EpicGrantRefusal.Expired);
            if (grant.Mode != "autonomous") return Deny("not_autonomous");
            // Always verify exact database bytes, even before serving a ten-second cached reply.
            var preflight = EpicGrantPreflight.ReadStoredVisibility(grant.PreflightJson,grant.PreflightSha256);
            if (!preflight.Valid)
            {
                logger.LogWarning("EpicGrant preflight_integrity grant={GrantId} reader=admission result={Result}",id,preflight.Failure);
                return Deny(EpicGrantRefusal.Unknown);
            }
            if (Sha256Hex(grant.ScopeJson) != grant.ScopeSha256
                || EpicScopeParser.Parse(grant.ScopeJson,autonomyEnabled:true).Scope is not { Autonomy: { } charter } scope)
                return Deny(EpicGrantRefusal.Unknown);
            var key = System.Text.Json.JsonSerializer.Serialize(new object?[] { id,workflowType,repo,issue,planItem });
            if (AdmissionReplies.TryGet(key,out var cached) && clock.GetUtcNow()-cached.CachedAt < TimeSpan.FromSeconds(10)) return cached;
            var driver = await temporal.DescribeAsync(scope.Driver.Namespace,scope.Driver.WorkflowId,scope.Driver.RunId,ct);
            if (driver is null || !driver.IsRunning || driver.RunId != scope.Driver.RunId) return Deny("driver_not_running");
            var definition = await db.WorkflowDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Name == workflowType,ct);
            var pin = definition is null ? null : scope.Workflows.FirstOrDefault(w => w.Type == definition.Name && w.Version == definition.Version);
            var item = charter.Plan.FirstOrDefault(p => p.Id == planItem);
            var guarded = definition is null ? [] : EpicJson.GuardedGates(definition.Definition);
            if (pin is null || Sha256Hex(definition!.Definition) != pin.Sha256
                || (item is null ? !scope.Gates.Any(g => guarded.Contains(g)) : item.Gates.Any(g => !guarded.Contains(g))))
                return Deny("definition_unpinned");
            var bound = repo is not null && issue is not null && await db.EpicGrantBindings.AsNoTracking()
                .AnyAsync(b => b.GrantId == grantId && b.Repo == repo.ToLower() && b.Issue == issue,ct);
            var fits = repo is not null && !_options.DeniedRepoSet().Contains(repo)
                && preflight.Visibility.ContainsKey(repo) && item is not null && SameRepo(item.Repo,repo)
                && item.Workflow == workflowType && item.Issue == issue
                && (issue is null ? charter.Children.Repos.Any(r => SameRepo(r.Repo,repo))
                    : bound || scope.Targets.Any(t => SameRepo(t.Repo,repo) && t.Issues.Contains(issue.Value)));
            if (!fits) return Deny(EpicGrantRefusal.TargetOutOfScope);
            if (charter.Exclusions.Contains(planItem!,StringComparer.Ordinal)
                || issue is not null && charter.Plan.Any(p => p.Issue == issue && SameRepo(p.Repo,repo)
                    && p.Workflow == workflowType && charter.Exclusions.Contains(p.Id,StringComparer.Ordinal))) return Deny(EpicGrantRefusal.Excluded);
            if (linkage is null) return Deny(EpicGrantRefusal.Unknown);
            var linked = await linkage.ListLinkedRunsAsync(scope.Driver.Namespace,scope.Driver,DateTimeOffset.MaxValue,200,ct);
            if (linked.Truncated || linked.Errors.Count != 0) return Deny(EpicGrantRefusal.Unknown);
            var runs = await db.EpicGrantRuns.AsNoTracking().Where(r => r.GrantId == grantId).ToListAsync(ct);
            var pending = linked.Children.Count(r => r.Description.IsRunning && !runs.Any(record => record.Namespace == r.Summary.Namespace
                && record.WorkflowId == r.Summary.WorkflowId && record.RunId == r.Summary.RunId));
            var budgets = await ReadBudgetsAsync(grantId,charter,ct,pending);
            var reason = issue is null && budgets.ChildIssues.Used >= budgets.ChildIssues.Max ? "children_exhausted"
                : budgets.DelegatedRuns.Used+budgets.DelegatedRuns.PendingRunning >= budgets.DelegatedRuns.Max ? "delegated_runs_exhausted"
                : budgets.AcceptanceRounds.Used > budgets.AcceptanceRounds.Max ? "rounds_exhausted"
                : budgets.Decisions.Used >= budgets.Decisions.Max ? "decisions_exhausted" : null;
            var result = new EpicGrantAdmission(reason is null ? "admit":"deny",reason,issue is null,budgets,clock.GetUtcNow());
            AdmissionReplies.Put(key,result); return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Deny(EpicGrantRefusal.Unknown); }
    }

    internal async Task<EpicGrantBudgets> ReadBudgetsAsync(Guid grantId, EpicAutonomyScope charter, CancellationToken ct, int pending = 0) => new(
        new(await db.EpicGrantDecisions.CountAsync(d => d.GrantId == grantId,ct),charter.Limits.MaxDecisions),
        new(await db.EpicGrantBindings.CountAsync(b => b.GrantId == grantId,ct),charter.Children.MaxIssues),
        new(await db.EpicGrantRuns.CountAsync(r => r.GrantId == grantId,ct),pending,charter.Limits.MaxDelegatedRuns),
        new(await db.EpicGrantRuns.Where(r => r.GrantId == grantId).SumAsync(r => r.MaxFixRound,ct),charter.Limits.MaxTotalAcceptanceRounds));
}
