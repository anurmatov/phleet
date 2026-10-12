using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

/// <summary>Default-off, metadata-only wake fallback. Never delegates work or decides a gate.</summary>
public sealed class EpicDriverWatcherService(IServiceScopeFactory scopes, IEpicDriverTemporal visibility,
    IEpicGrantTemporal temporal, EpicRunLinkage linkage, IOptions<EpicDriverWatcherOptions> options,
    IOptions<TemporalOptions> temporalOptions, TimeProvider clock, ILogger<EpicDriverWatcherService> logger) : BackgroundService
{
    private readonly EpicDriverWatcherOptions _options = options.Value;
    private readonly EpicRunCache<bool> _eligible = new();
    private readonly EpicRunCache<bool> _woken = new();
    private DateTimeOffset? _watermark;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private sealed record Trigger(DateTimeOffset At, string Kind, string Id, string Key);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) { logger.LogInformation("EpicWatcher disabled: flag off"); return; }
        if (_options.IntervalSeconds is < 60 or > 3600)
        {
            logger.LogError("epic_watcher_misconfigured: interval must be 60–3600 seconds"); return;
        }
        if (!visibility.IsConfigured) { logger.LogInformation("EpicWatcher disabled: temporal not configured"); return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await ScanAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), clock, stoppingToken);
        }
    }

    public async Task<bool> ScanAsync(CancellationToken ct)
    {
        if (!_options.Enabled || _options.IntervalSeconds is < 60 or > 3600 || !visibility.IsConfigured) return false;
        await _scanLock.WaitAsync(ct);
        var now = clock.GetUtcNow();
        var from = (_watermark?.AddSeconds(-60) ?? now.AddSeconds(-_options.IntervalSeconds));
        if (from < now.AddHours(-1)) from = now.AddHours(-1);
        var plans = new List<(string Ns, string Id, string Run, List<Trigger> Triggers)>();
        var eligible = 0; var closures = 0; var gates = 0; var grantCount = 0; var wakes = 0; var errors = 0;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>() ?? throw new InvalidOperationException("Database unavailable");
            foreach (var ns in temporalOptions.Value.Namespaces.Distinct(StringComparer.Ordinal))
            {
                var drivers = await visibility.ListAsync(ns, "WorkflowType=\"UweCarteBlancheWorkflow\" AND ExecutionStatus=\"Running\"", null, ct);
                var closed = await visibility.ListAsync(ns, EpicRunLinkage.ChildTypesQuery + " AND ExecutionStatus!=\"Running\" AND CloseTime>=\"" + from.UtcDateTime.ToString("O") + "\"", null, ct);
                var running = await visibility.ListAsync(ns, EpicRunLinkage.ChildTypesQuery + " AND ExecutionStatus=\"Running\"", null, ct);
                if (!drivers.Complete || !closed.Complete || !running.Complete) throw new InvalidOperationException("Visibility incomplete");
                var ids = drivers.Runs.Select(d => d.RunId).ToList();
                var grants = await db.EpicGrants.AsNoTracking().Where(g => g.DriverNamespace == ns && ids.Contains(g.DriverRunId)).ToListAsync(ct);
                foreach (var driver in drivers.Runs)
                {
                    if (!await EligibleAsync(db, ns, driver.WorkflowId, driver.RunId, ct)) continue;
                    eligible++;
                    var triggers = new List<Trigger>();
                    var scopeDriver = new EpicScopeDriver(ns, driver.WorkflowId, driver.RunId);
                    foreach (var child in closed.Runs.Concat(running.Runs))
                    {
                        if (child.Status == "Completed") continue;
                        var d = await temporal.DescribeAsync(ns, child.WorkflowId, child.RunId, ct)
                            ?? throw new InvalidOperationException("Child describe unavailable");
                        var history = await linkage.ReadRunAsync(ns, child.WorkflowId, child.RunId, ct);
                        using var input = JsonDocument.Parse(history.InputJson ?? "{}");
                        if (!await EpicRunLinkage.IsLinkedAsync(temporal, ns, d, input.RootElement, scopeDriver, ct)) continue;
                        Trigger? trigger = null;
                        if (d.IsRunning && !string.IsNullOrEmpty(d.GateVisit))
                        {
                            gates++;
                            trigger = new(now, "gate-entered", child.WorkflowId + ":" + d.GateVisit,
                                "gate:" + ns + ":" + child.RunId + ":" + d.GateVisit);
                        }
                        else if (d.Status is "Failed" or "Terminated" or "Canceled" or "TimedOut")
                        {
                            closures++;
                            var kind = d.Status switch { "Failed" => "run-failed", "Terminated" => "run-terminated", "Canceled" => "run-canceled", _ => "run-timedout" };
                            trigger = new(child.CloseTime ?? now, kind, child.WorkflowId, "run:" + ns + ":" + child.RunId);
                        }
                        if (trigger is not null && !_woken.TryGet(trigger.Key, out _)) triggers.Add(trigger);
                    }
                    foreach (var grant in grants.Where(g => g.DriverRunId == driver.RunId && g.DriverWorkflowId == driver.WorkflowId))
                    {
                        AddGrant(grant.CreatedAt, "grant-created", grant.Id);
                        if (grant.RevokedAt is { } revoked) AddGrant(revoked, "grant-revoked", grant.Id);
                        AddGrant(grant.ExpiresAt, "grant-expired", grant.Id);
                    }
                    plans.Add((ns, driver.WorkflowId, driver.RunId, triggers.OrderBy(t => t.At).ThenBy(t => t.Key, StringComparer.Ordinal).ToList()));

                    void AddGrant(DateTime at, string kind, Guid id)
                    {
                        var time = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
                        if (time <= from || time > now) return;
                        grantCount++;
                        var key = kind + ":" + id.ToString("D");
                        if (!_woken.TryGet(key, out _)) triggers.Add(new(time, kind, id.ToString("D"), key));
                    }
                }
            }
            // Do not send any wake if visibility or DB collection failed anywhere in the scan.
            foreach (var plan in plans.Where(p => p.Triggers.Count > 0))
            {
                var trigger = plan.Triggers[0];
                var result = await visibility.SignalWorkflowAsync(plan.Ns, plan.Id, plan.Run, "blocker-resolved",
                    new { decision = "wake", blockerRef = "epic-watcher:" + trigger.Kind + ":" + trigger.Id }, ct);
                logger.LogInformation("EpicWatcher wake driver={Driver} run={Run} triggers={Triggers} result={Result}",
                    plan.Id, plan.Run, string.Join(',', plan.Triggers.Select(t => t.Kind + ":" + t.Id)), result);
                if (result is "sent" or "not_found")
                {
                    foreach (var item in plan.Triggers) _woken.Put(item.Key, true);
                    wakes++;
                }
                else errors++;
            }
            if (errors == 0) _watermark = now;
            return errors == 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            errors++;
            logger.LogWarning(ex, "EpicWatcher scan failed error={Error}", ex.GetType().Name);
            return false;
        }
        finally
        {
            logger.LogInformation("EpicWatcher scan from={From:O} to={To:O} eligible={Eligible} closures={Closures} gates={Gates} grants={Grants} wakes={Wakes} errors={Errors}",
                from, now, eligible, closures, gates, grantCount, wakes, errors);
            _scanLock.Release();
        }
    }

    private async Task<bool> EligibleAsync(OrchestratorDbContext db, string ns, string id, string run, CancellationToken ct)
    {
        var key = ns + ":" + run;
        if (_eligible.TryGet(key, out var cached)) return cached;
        var history = await linkage.ReadRunAsync(ns, id, run, ct);
        if (history.DefinitionName != "UweCarteBlancheWorkflow" || history.DefinitionVersion is not { } version) return false;
        string? stored;
        try { stored = await EpicRunLinkage.LoadStoredDefinitionAsync(db, history.DefinitionName, version, ct); }
        catch { return false; } // Failed definition reads are not immutable verdicts.
        if (stored is null) return false;
        using var definition = JsonDocument.Parse(stored);
        var verdict = HasWakeWait(definition.RootElement);
        _eligible.Put(key, verdict);
        if (!verdict) logger.LogInformation("EpicWatcher ineligible driver={Driver} run={Run} definition={Definition}@{Version}", id, run, history.DefinitionName, version);
        return verdict;
    }

    private static bool HasWakeWait(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (EpicJson.String(node, "type") == "wait_for_signal" && EpicJson.String(node, "name") == "epic_wake_wait") return true;
            return node.EnumerateObject().Any(p => HasWakeWait(p.Value));
        }
        return node.ValueKind == JsonValueKind.Array && node.EnumerateArray().Any(HasWakeWait);
    }
}
