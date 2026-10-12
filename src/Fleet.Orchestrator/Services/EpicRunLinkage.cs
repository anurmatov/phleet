using System.Collections.Concurrent;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Models;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

public sealed record EpicCloseResult(string Encoding, string? Json);
public sealed record EpicLinkedRun(WorkflowSummary Summary, EpicRunDescription Description, EpicRunHistory History);
public sealed record EpicLinkedRuns(IReadOnlyList<EpicLinkedRun> Children, IReadOnlyList<string> Errors, bool Truncated, int Candidates);

public sealed class EpicRunLinkage(IEpicGrantTemporal temporal, IEpicDriverTemporal visibility)
{
    public const int MaxCandidates = 500;
    public static readonly string ChildTypesQuery = "(WorkflowType=\"UweDesignWorkflow\" OR WorkflowType=\"UweDesignToPrWorkflow\" OR WorkflowType=\"UwePrImplementationWorkflow\" OR WorkflowType=\"UweDocMaintenanceWorkflow\")";
    private readonly EpicRunCache<EpicRunHistory> _histories = new();

    public async Task<EpicRunHistory> ReadRunAsync(string ns, string id, string run, CancellationToken ct)
    {
        var key = ns + ":" + run;
        if (_histories.TryGet(key, out var history)) return history;
        history = await temporal.ReadRunAsync(ns, id, run, ct) ?? throw new InvalidOperationException("Run history unavailable");
        if (history.DefinitionName is not null && history.DefinitionVersion is not null) _histories.Put(key, history);
        return history;
    }

    internal async Task<EpicLinkedRuns> ListLinkedRunsAsync(string ns, EpicScopeDriver driver,
        DateTimeOffset includeClosedSince, int max, CancellationToken ct)
    {
        var runs = new ConcurrentBag<EpicLinkedRun>();
        var errors = new ConcurrentBag<string>();
        var candidates = 0;
        try
        {
            var query = ChildTypesQuery + " AND (ExecutionStatus=\"Running\" OR CloseTime>=\"" + includeClosedSince.UtcDateTime.ToString("O") + "\")";
            var result = await visibility.ListAsync(ns, query, MaxCandidates, ct);
            candidates = result.Runs.Count;
            if (!result.Complete) errors.Add(result.Error ?? "candidates_incomplete");
            await Parallel.ForEachAsync(result.Runs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (candidate, token) =>
            {
                try
                {
                    var d = await temporal.DescribeAsync(ns, candidate.WorkflowId, candidate.RunId, token)
                        ?? throw new InvalidOperationException("Candidate disappeared");
                    if (d.RunId != candidate.RunId || d.WorkflowType != candidate.WorkflowType)
                        throw new InvalidOperationException("Candidate run identity changed");
                    var history = await ReadRunAsync(ns, candidate.WorkflowId, candidate.RunId, token);
                    if (await IsLinkedAsync(temporal, ns, d, ParseInput(history.InputJson), driver, token))
                        runs.Add(new(candidate, d, history));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { errors.Add("linkage_unknown:" + candidate.WorkflowId); }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { errors.Add("deadline"); }
        catch { errors.Add("candidates_page_failed"); }
        var sorted = runs.OrderBy(r => r.Summary.WorkflowId, StringComparer.Ordinal).ThenBy(r => r.Summary.RunId, StringComparer.Ordinal).ToArray();
        var truncated = sorted.Length > max;
        if (truncated) errors.Add("children_truncated");
        return new(sorted.Take(max).ToArray(), errors.Distinct().Order(StringComparer.Ordinal).ToArray(), truncated, candidates);
    }

    private static JsonElement ParseInput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
    /// <summary>
    /// Linked when the run's input <c>WaiterWorkflowId</c> is the driver workflow id, or when a
    /// parent at most two levels up is the driver run itself or has that input.
    /// </summary>
    internal static async Task<bool> IsLinkedAsync(IEpicGrantTemporal temporal, 
        string ns, EpicRunDescription target, JsonElement input, EpicScopeDriver driver, CancellationToken ct)
    {
        if (WaitsOn(input, driver)) return true;

        var (parentId, parentRunId) = (target.ParentWorkflowId, target.ParentRunId);
        for (var level = 1; level <= 2; level++)
        {
            if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(parentRunId)) return false;
            if (string.Equals(ns, driver.Namespace, StringComparison.Ordinal)
                && parentId == driver.WorkflowId && parentRunId == driver.RunId)
                return true;

            var parentHistory = await temporal.ReadRunAsync(ns, parentId, parentRunId, ct)
                ?? throw new InvalidOperationException("parent run has no history page");
            if (WaitsOn(ParseInput(parentHistory.InputJson), driver)) return true;
            if (level == 2) break;

            var parent = await temporal.DescribeAsync(ns, parentId, parentRunId, ct);
            if (parent is null) return false;
            (parentId, parentRunId) = (parent.ParentWorkflowId, parent.ParentRunId);
        }

        return false;

        static bool WaitsOn(JsonElement input, EpicScopeDriver driver) =>
            EpicJson.String(input, "WaiterWorkflowId") is { Length: > 0 } waiter && waiter == driver.WorkflowId;
    }

    /// <summary>
    /// The stored definition for exactly (type, version): the main row when its version matches,
    /// otherwise the archived version row. Null when neither exists.
    /// </summary>
    public static async Task<string?> LoadStoredDefinitionAsync(OrchestratorDbContext db, string type, int version, CancellationToken ct)
    {
        var main = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.Name == type)
            .Select(d => new { d.Id, d.Name, d.Version, d.Definition })
            .FirstOrDefaultAsync(ct);
        if (main is null || main.Name != type) return null;
        if (main.Version == version) return main.Definition;
        return await db.WorkflowDefinitionVersions.AsNoTracking()
            .Where(v => v.WorkflowDefinitionId == main.Id && v.Version == version)
            .Select(v => v.Definition)
            .FirstOrDefaultAsync(ct);
    }

}
