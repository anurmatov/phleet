using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Services;

internal sealed record EpicScopeChildren(IReadOnlyList<EpicScopeChildRepo> Repos, int MaxIssues);
internal sealed record EpicScopeChildRepo(string Repo, bool AllowPublic);
internal sealed record EpicScopeAdoption(string Namespace, string WorkflowId, string RunId, string Repo, long Issue, string? ArtifactRef);
internal sealed record EpicScopeLimits(int MaxDecisions, int MaxDelegatedRuns, int MaxBlockedRechecks, int MaxTotalAcceptanceRounds);
internal sealed record EpicScopePlanItem(string Id, string Repo, long? Issue, string Workflow, IReadOnlyList<string> Gates, IReadOnlyList<string> After);
internal sealed record EpicAutonomyScope(string Outcome, EpicScopeChildren Children, IReadOnlyList<EpicScopeAdoption> Adopt,
    EpicScopeLimits Limits, string AcceptanceIsolation, IReadOnlyList<EpicScopePlanItem> Plan, IReadOnlyList<string> Exclusions);

internal static partial class EpicScopeParser
{
    private static readonly string[] AutonomyKeys = ["mode", "outcome", "children", "adopt", "limits", "acceptanceIsolation", "plan", "exclusions"];
    private static readonly string[] PlanWorkflows = ["UweDesignWorkflow", "UweDesignToPrWorkflow", "UwePrImplementationWorkflow"];
    private static readonly Regex PlanId = new(@"\A[a-z0-9-]{1,32}\z", RegexOptions.CultureInvariant);

    private static void ParseAutonomy(JsonElement root, EpicScopeParse result, IReadOnlySet<string>? deniedRepos)
    {
        var errors = result.Errors;
        var outcome = Text(root, "outcome", "scope", 2000);
        var children = new List<EpicScopeChildRepo>();
        var childrenJson = ObjectField(root, "children", ["repos", "maxIssues"]);
        foreach (var child in ArrayField(childrenJson, "repos", 0, 5))
        {
            if (!Object(child, "children.repos", ["repo", "allowPublic"])) continue;
            var repo = Repo(child, "children.repos");
            var allowPublic = false;
            if (child.TryGetProperty("allowPublic", out var allow))
            {
                if (allow.ValueKind is JsonValueKind.True or JsonValueKind.False) allowPublic = allow.GetBoolean();
                else errors.Add("children.repos.allowPublic must be a boolean");
            }
            if (children.Any(c => string.Equals(c.Repo, repo, StringComparison.OrdinalIgnoreCase))) errors.Add("children.repos contains a duplicate repo");
            if (deniedRepos?.Contains(repo) == true) errors.Add("child repo is on the deny list");
            children.Add(new(repo, allowPublic));
        }
        var maxIssues = Integer(childrenJson, "maxIssues", 0, 10);
        var limitsJson = ObjectField(root, "limits", ["maxDecisions", "maxDelegatedRuns", "maxBlockedRechecks", "maxTotalAcceptanceRounds"]);
        var limits = new EpicScopeLimits(Integer(limitsJson, "maxDecisions", 1, 50), Integer(limitsJson, "maxDelegatedRuns", 1, 30),
            Integer(limitsJson, "maxBlockedRechecks", 0, 5), Integer(limitsJson, "maxTotalAcceptanceRounds", 0, 25));
        var isolation = Text(root, "acceptanceIsolation", "scope", 32);
        if (isolation != "isolated") errors.Add("acceptanceIsolation must be isolated");
        var adopt = new List<EpicScopeAdoption>();
        foreach (var entry in ArrayField(root, "adopt", 0, 20, optional: true))
        {
            if (!Object(entry, "adopt", ["namespace", "workflowId", "runId", "repo", "issue", "artifactRef"])) continue;
            var ns = Text(entry, "namespace", "adopt", 64);
            var workflow = Text(entry, "workflowId", "adopt", 255);
            var run = Text(entry, "runId", "adopt", 64);
            var repo = Repo(entry, "adopt");
            var issue = Issue(entry, nullable: false);
            string? artifact = null;
            if (entry.TryGetProperty("artifactRef", out var a))
            {
                if (a.ValueKind == JsonValueKind.String && a.GetString()!.Length <= 64) artifact = a.GetString();
                else errors.Add("adopt.artifactRef must be a string of at most 64 characters");
            }
            if (issue is { } n && !result.Targets.Any(t => string.Equals(t.Repo, repo, StringComparison.OrdinalIgnoreCase) && t.Issues.Contains(n)))
                errors.Add("adopt entry is outside targets");
            adopt.Add(new(ns, workflow, run, repo, issue ?? 0, artifact));
        }
        var exclusions = Strings(root, "exclusions", 50, 300, optional: true);
        var plan = new List<EpicScopePlanItem>();
        foreach (var entry in ArrayField(root, "plan", 1, 50))
        {
            if (!Object(entry, "plan", ["id", "repo", "issue", "workflow", "gates", "after"])) continue;
            var id = Text(entry, "id", "plan", 32);
            if (!PlanId.IsMatch(id)) errors.Add("plan.id must match [a-z0-9-]{1,32}");
            if (plan.Any(p => p.Id == id)) errors.Add("plan.id must be unique");
            var repo = Repo(entry, "plan");
            var workflow = Text(entry, "workflow", "plan", 100);
            if (!PlanWorkflows.Contains(workflow, StringComparer.Ordinal)) errors.Add("plan.workflow is not supported");
            var issue = Issue(entry, nullable: true);
            if (issue is null && (workflow == "UwePrImplementationWorkflow" || !children.Any(c => string.Equals(c.Repo, repo, StringComparison.OrdinalIgnoreCase))))
                errors.Add("CREATE plan items require a design workflow and children repo");
            var gates = Strings(entry, "gates", int.MaxValue, 64);
            if (gates.Any(g => !result.Gates.Contains(g, StringComparer.Ordinal))) errors.Add("plan gates must be within scope gates");
            plan.Add(new(id, repo, issue, workflow, gates, Strings(entry, "after", int.MaxValue, 32, optional: true)));
        }
        ValidatePlanGraph(plan, errors);
        result.Autonomy = new(outcome, new(children, maxIssues), adopt, limits, isolation, plan, exclusions);

        bool Object(JsonElement obj, string path, string[] keys)
        {
            if (obj.ValueKind != JsonValueKind.Object) { errors.Add(path + " must be an object"); return false; }
            CheckKeys(obj, path, keys, errors); return true;
        }
        JsonElement ObjectField(JsonElement parent, string key, string[] keys)
        {
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value) && Object(value, key, keys)) return value;
            errors.Add(key + " is required and must be an object"); return default;
        }
        JsonElement[] ArrayField(JsonElement obj, string key, int min, int max, bool optional = false)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value))
            { if (!optional) errors.Add(key + " is required"); return []; }
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
            { errors.Add($"{key} must be an array with {min}..{max} entries"); return []; }
            return value.EnumerateArray().ToArray();
        }
        string Text(JsonElement obj, string key, string path, int max)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text && text.Length <= max) return text;
            errors.Add($"{path}.{key} must be a string with 1..{max} characters"); return "";
        }
        string Repo(JsonElement obj, string path)
        {
            var repo = Text(obj, "repo", path, 200);
            if (!Regex.IsMatch(repo, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant)) errors.Add(path + ".repo must be owner/name");
            return repo;
        }
        int Integer(JsonElement obj, string key, int min, int max)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var number) && number >= min && number <= max) return number;
            errors.Add($"{key} must be an integer within {min}..{max}"); return 0;
        }
        long? Issue(JsonElement obj, bool nullable)
        {
            if (obj.TryGetProperty("issue", out var value))
            {
                if (nullable && value.ValueKind == JsonValueKind.Null) return null;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n > 0) return n;
            }
            errors.Add(nullable ? "plan.issue must be a positive integer or null" : "adopt.issue must be a positive integer"); return null;
        }
        IReadOnlyList<string> Strings(JsonElement obj, string key, int maxCount, int maxLength, bool optional = false)
        {
            var strings = new List<string>();
            foreach (var item in ArrayField(obj, key, 0, maxCount, optional))
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && text.Length <= maxLength) strings.Add(text);
                else errors.Add($"{key} must contain strings with 1..{maxLength} characters");
            }
            return strings;
        }
    }

    private static void ValidatePlanGraph(IReadOnlyList<EpicScopePlanItem> plan, List<string> errors)
    {
        var nodes = plan.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (plan.Any(p => p.After.Any(id => !nodes.ContainsKey(id)))) errors.Add("plan_unknown_dependency");
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (nodes.Keys.Any(Cycle)) errors.Add("plan_cycle");
        bool Cycle(string id)
        {
            if (!nodes.ContainsKey(id) || visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (nodes[id].After.Any(Cycle)) return true;
            visiting.Remove(id); visited.Add(id); return false;
        }
    }
}
