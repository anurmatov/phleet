using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

public sealed record EpicPreflightReport(IReadOnlyList<JsonObject> Items, int Unresolved, string Sha256,
    IReadOnlyDictionary<string, string> Visibility);

public sealed partial class EpicGrantService
{
    private async Task<EpicPreflightReport> ComputePreflightAsync(EpicScope scope, ScopeValidationReport report,
        string cto, CancellationToken ct)
    {
        var charter = scope.Autonomy!;
        var items = new List<JsonObject>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in report.Targets.Concat(report.Children)) seen[repo.Repo] = repo.Visibility;
        var denied = _options.DeniedRepoSet();
        foreach (var plan in charter.Plan)
        {
            var verdict = "ok";
            if (charter.Exclusions.Contains(plan.Id, StringComparer.Ordinal)) verdict = "excluded";
            else
            {
                var current = await db.WorkflowDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Name == plan.Workflow, ct);
                var pin = current is null ? null : scope.Workflows.FirstOrDefault(w => w.Type == current.Name && w.Version == current.Version);
                var definitionFits = pin is not null && Sha256Hex(current!.Definition) == pin.Sha256
                    && plan.Gates.All(g => EpicJson.GuardedGates(current.Definition).Contains(g));
                var inScope = plan.Issue is { } issue
                    ? scope.Targets.Any(t => SameRepo(t.Repo, plan.Repo) && t.Issues.Contains(issue))
                    : charter.Children.Repos.Any(r => SameRepo(r.Repo, plan.Repo));
                verdict = denied.Contains(plan.Repo) ? "denied"
                    : !seen.TryGetValue(plan.Repo, out var visibility) || visibility is not ("public" or "private") ? "visibility_unknown"
                    : !inScope ? "out_of_scope" : !definitionFits ? "definition_unpinned"
                    : plan.Issue is null ? "needs_binding" : "ok";
            }
            items.Add(new() { ["source"]="plan", ["id"]=plan.Id, ["repo"]=plan.Repo, ["issue"]=plan.Issue,
                ["workflow"]=plan.Workflow, ["verdict"]=verdict, ["detail"]=verdict });
        }

        // Every adoption is validated, including runs not linked to the driver.
        foreach (var adopt in charter.Adopt)
        {
            var error = await ValidateAdoptionAsync(scope, adopt, cto, ct);
            if (error is not null) report.Errors.Add(error);
        }
        if (linkage is null) report.Errors.Add("linked run visibility unavailable");
        else
        {
            var linked = await linkage.ListLinkedRunsAsync(scope.Driver.Namespace, scope.Driver, DateTimeOffset.MaxValue, 200, ct);
            if (linked.Truncated) report.Errors.Add("too_many_linked_runs");
            report.Errors.AddRange(linked.Errors);
            foreach (var run in linked.Children.Where(r => r.Description.IsRunning))
            {
                var d = run.Description;
                var input = ParseInput(run.History.InputJson);
                var repo = EpicJson.String(input, "Repo");
                var issue = EpicJson.PositiveInteger(input, "ExistingIssueNumber") ?? EpicJson.PositiveInteger(input, "IssueNumber") ?? d.IssueNumber;
                var adoption = charter.Adopt.FirstOrDefault(a => a.Namespace == run.Summary.Namespace
                    && a.WorkflowId == run.Summary.WorkflowId && a.RunId == d.RunId);
                var isCreate = d.WorkflowType is "UweDesignWorkflow" or "UweDesignToPrWorkflow"
                    && EpicJson.PositiveInteger(input,"ExistingIssueNumber") is null && EpicJson.PositiveInteger(input,"IssueNumber") is null;
                var covered = issue is not null && scope.Targets.Any(t => SameRepo(t.Repo,repo) && t.Issues.Contains(issue.Value))
                    || isCreate && charter.Children.Repos.Any(r => SameRepo(r.Repo,repo));
                var author = d.WorkflowType == "UweDocMaintenanceWorkflow" ? EpicJson.String(input,"PrepAgent") : EpicJson.String(input,"TargetAgent");
                var pin = scope.Workflows.FirstOrDefault(w => w.Type == run.History.DefinitionName && w.Version == run.History.DefinitionVersion);
                var stored = pin is null ? null : await EpicRunLinkage.LoadStoredDefinitionAsync(db, pin.Type, pin.Version, ct);
                var pinned = stored is not null && Sha256Hex(stored) == pin!.Sha256
                    && EpicJson.GuardedGates(stored).Any(g => scope.Gates.Contains(g, StringComparer.Ordinal));
                var verdict = charter.Exclusions.Contains(run.Summary.WorkflowId, StringComparer.Ordinal) ? "excluded"
                    : repo is not null && denied.Contains(repo) ? "denied"
                    : repo is null || !seen.TryGetValue(repo, out var visibility) || visibility is not ("public" or "private") ? "visibility_unknown"
                    : !covered ? "out_of_scope"
                    : !pinned ? "definition_unpinned"
                    : string.IsNullOrWhiteSpace(author) || string.Equals(author.Trim(),cto,StringComparison.OrdinalIgnoreCase) ? "author_is_decider"
                    : isCreate && adoption is null ? "create_unbound" : adoption is null ? "needs_adoption" : "adopted";
                items.Add(new() { ["source"]="linked_run", ["workflowId"]=run.Summary.WorkflowId, ["runId"]=d.RunId,
                    ["type"]=d.WorkflowType, ["startTime"]=d.StartTime?.ToString("O"), ["repo"]=repo, ["issue"]=issue,
                    ["gate"]=d.GateVisit, ["verdict"]=verdict, ["detail"]=verdict });
            }
        }
        var sorted = items.OrderBy(i => i["source"]!.GetValue<string>(),StringComparer.Ordinal)
            .ThenBy(i => (i["id"] ?? i["workflowId"])!.GetValue<string>(),StringComparer.Ordinal)
            .ThenBy(i => i["runId"]?.GetValue<string>() ?? "",StringComparer.Ordinal).ToArray();
        var unresolved = sorted.Count(i => i["verdict"]!.GetValue<string>() is not ("ok" or "adopted" or "needs_binding" or "excluded"));
        return new(sorted, unresolved, Sha256Hex(EpicGrantPreflight.CanonicalJson(sorted)), seen);
    }

    private async Task<string?> ValidateAdoptionAsync(EpicScope scope, EpicScopeAdoption adopt, string cto, CancellationToken ct)
    {
        var run = await temporal.DescribeAsync(adopt.Namespace,adopt.WorkflowId,adopt.RunId,ct);
        if (run is null || !run.IsRunning || run.RunId != adopt.RunId) return "adopt_not_running";
        var history = await temporal.ReadRunAsync(adopt.Namespace,adopt.WorkflowId,adopt.RunId,ct);
        if (history is null) return "adopt_definition_unpinned";
        var pin = scope.Workflows.FirstOrDefault(w => w.Type == history.DefinitionName && w.Version == history.DefinitionVersion && w.Type == run.WorkflowType);
        var stored = pin is null ? null : await EpicRunLinkage.LoadStoredDefinitionAsync(db,pin.Type,pin.Version,ct);
        if (stored is null || Sha256Hex(stored) != pin!.Sha256 || !EpicJson.GuardedGates(stored).Any(g => scope.Gates.Contains(g,StringComparer.Ordinal)))
            return "adopt_definition_unpinned";
        var input = ParseInput(history.InputJson);
        var issue = EpicJson.PositiveInteger(input,"ExistingIssueNumber") ?? EpicJson.PositiveInteger(input,"IssueNumber") ?? run.IssueNumber;
        if (!SameRepo(EpicJson.String(input,"Repo"),adopt.Repo) || issue != adopt.Issue
            || !scope.Targets.Any(t => SameRepo(t.Repo,adopt.Repo) && t.Issues.Contains(adopt.Issue))) return "adopt_target_out_of_scope";
        var author = run.WorkflowType == "UweDocMaintenanceWorkflow" ? EpicJson.String(input,"PrepAgent") : EpicJson.String(input,"TargetAgent");
        return string.IsNullOrWhiteSpace(author) || string.Equals(author.Trim(),cto,StringComparison.OrdinalIgnoreCase) ? "adopt_author_is_decider" : null;
    }
    private static bool SameRepo(string? first, string? second) => string.Equals(first,second,StringComparison.OrdinalIgnoreCase);
}
