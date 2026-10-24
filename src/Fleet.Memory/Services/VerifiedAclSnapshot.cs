using System.Collections.Frozen;
using System.Text.Json;

namespace Fleet.Memory.Services;

public sealed class VerifiedAclUnavailableException : InvalidOperationException
{
    public VerifiedAclUnavailableException() : base("Authoritative request ACL unavailable.") { }
}

/// <summary>Immutable authorization observation for one request, never a reusable positive cache.</summary>
public sealed class VerifiedAclSnapshot
{
    internal VerifiedAclSnapshot(FrozenDictionary<string, FrozenSet<string>> projects,
        FrozenSet<string> publicProjects, bool allowNoProject)
    {
        Projects = projects;
        PublicProjects = publicProjects;
        AllowNoProject = allowNoProject;
    }

    internal FrozenDictionary<string, FrozenSet<string>> Projects { get; }
    internal FrozenSet<string> PublicProjects { get; }
    internal bool AllowNoProject { get; }
}

public sealed partial class AclCacheService
{
    private readonly object _verifiedGate = new();
    private long _verifiedGeneration;

    /// <summary>
    /// One authoritative fetch, bounded to five seconds including body parsing. Never reads or
    /// populates the legacy cache. A config change during acquisition rejects the entire result.
    /// The authorization boundary is the final generation check; already authorized calls may finish.
    /// </summary>
    public async Task<VerifiedAclSnapshot> GetVerifiedSnapshotAsync(CancellationToken ct = default)
    {
        long generation;
        lock (_verifiedGate) generation = _verifiedGeneration;
        var options = _aclOptions.Value;
        if (!options.EnableProjectScopedAcl) throw new VerifiedAclUnavailableException();
        if (options.AclPublicProjects is null || options.AclPublicProjects.Any(p => p is null))
            throw new VerifiedAclUnavailableException();
        var operatorAgent = (options.AclOperatorAgent ?? "").Trim().ToLowerInvariant();
        var publicProjects = options.AclPublicProjects.Select(p => p.Trim().ToLowerInvariant())
            .ToFrozenSet(StringComparer.Ordinal);
        var allowNoProject = options.AclAllowNoProject;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var snapshot = await FetchVerifiedAsync(operatorAgent, publicProjects, allowNoProject, deadline.Token)
                .WaitAsync(deadline.Token);
            lock (_verifiedGate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (generation != _verifiedGeneration) throw new VerifiedAclUnavailableException();
                return snapshot;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException
            or JsonException or InvalidOperationException or OperationCanceledException or IOException)
        {
            // No transport exception, URL, payload, or stale snapshot is exposed to the caller.
            _logger.LogWarning("Authoritative request ACL acquisition failed; verified request denied");
            throw new VerifiedAclUnavailableException();
        }
    }

    public (bool allowed, string? denyReason) CanRead(VerifiedAclSnapshot snapshot, string agentName, string? project)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Projects.TryGetValue(agentName.ToLowerInvariant(), out var projects);
        return EvaluateRead(agentName, project, projects, snapshot.AllowNoProject, snapshot.PublicProjects);
    }

    private void InvalidateVerifiedRequests()
    {
        lock (_verifiedGate) _verifiedGeneration++;
    }

    private async Task<VerifiedAclSnapshot> FetchVerifiedAsync(string operatorAgent, FrozenSet<string> publicProjects,
        bool allowNoProject, CancellationToken ct)
    {
        var baseUrl = _orchestratorOptions.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new VerifiedAclUnavailableException();
        var url = $"{baseUrl.TrimEnd('/')}/internal/agent-project-access";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new VerifiedAclUnavailableException();
        using var response = await _verifiedHttp.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("acl", out var acl) || acl.ValueKind != JsonValueKind.Object)
            throw new VerifiedAclUnavailableException();
        var projects = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        foreach (var row in acl.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(row.Name) || row.Name != row.Name.Trim()
                || row.Value.ValueKind != JsonValueKind.Array)
                throw new VerifiedAclUnavailableException();
            var grants = new List<string>();
            foreach (var grant in row.Value.EnumerateArray())
            {
                if (grant.ValueKind != JsonValueKind.String) throw new VerifiedAclUnavailableException();
                grants.Add(grant.GetString()!.ToLowerInvariant());
            }
            if (!projects.TryAdd(row.Name.ToLowerInvariant(), grants.ToFrozenSet(StringComparer.Ordinal)))
                throw new VerifiedAclUnavailableException();
        }
        if (operatorAgent.Length != 0 && (!projects.TryGetValue(operatorAgent, out var operatorProjects)
            || !operatorProjects.Contains("*"))) throw new VerifiedAclUnavailableException();
        return new(projects.ToFrozenDictionary(StringComparer.Ordinal), publicProjects, allowNoProject);
    }

    private static (bool allowed, string? denyReason) EvaluateRead(string agentName, string? project,
        IReadOnlyCollection<string>? projects, bool allowNoProject, IEnumerable<string> publicProjects)
    {
        if (projects?.Contains("*") == true) return (true, null);
        var memProject = (project ?? "").Trim().ToLowerInvariant();
        if (memProject.Length == 0)
            return allowNoProject ? (true, null)
                : (false, "memory has no project — denied to non-wildcard agents (set AclAllowNoProject=true to permit during rollout)");
        if (publicProjects.Any(p => string.Equals(p.Trim(), memProject, StringComparison.OrdinalIgnoreCase))) return (true, null);
        if (projects?.Contains(memProject) == true) return (true, null);
        return (false, $"project '{memProject}' not in agent '{agentName.ToLowerInvariant()}' allow-list");
    }
}
