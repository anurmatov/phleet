using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;

namespace Fleet.Orchestrator.Services.McpTools;

/// <summary>One tool a server offers: its own name (no <c>mcp__server__</c> prefix) and what it says it does.</summary>
public sealed record McpToolInfo(string Name, string? Description);

/// <summary>
/// What one of an agent's MCP servers answered when asked for its tools, or why it was not asked.
/// </summary>
/// <param name="Reason">A fixed sentence for a status other than <c>ok</c>. Never a header value or an exception's text.</param>
/// <param name="Truncated">The server offers more tools than are listed here.</param>
public sealed record McpServerTools(
    string Name, string Status, string? Reason, IReadOnlyList<McpToolInfo> Tools, bool Truncated = false)
{
    public const string Ok = "ok";
    /// <summary>Nothing answered at the address.</summary>
    public const string Unreachable = "unreachable";
    public const string TimedOut = "timeout";
    /// <summary>The server answered 401 or 403.</summary>
    public const string Refused = "refused";
    /// <summary>A header carries a bound secret, which only the agent's own requests can use.</summary>
    public const string NeedsAgentSecret = "needs_agent_secret";
    /// <summary>The saved endpoint cannot be asked as it stands: its address, its mappings or a value.</summary>
    public const string Misconfigured = "misconfigured";
    public const string UnsupportedTransport = "unsupported_transport";
    public const string Error = "error";

    public static McpServerTools NotAsked(string name, string status, string reason) => new(name, status, reason, []);
}

/// <summary>A server to ask. The header values are used for the one request and nowhere else.</summary>
public sealed record McpListTarget(string Name, Uri Url, bool Sse, IReadOnlyDictionary<string, string> Headers);

/// <summary>One endpoint of the plan: a server to ask, or the answer that stands in for asking it.</summary>
public sealed record McpPlanEntry(string Name, McpListTarget? Target, McpServerTools? NotAsked);

/// <summary>
/// Which of an agent's saved MCP endpoints can be asked for their tools, at what address and with
/// which headers. Pure: no request is made here.
/// </summary>
/// <remarks>
/// The address is the one provisioning writes for the agent (<see cref="AgentConfigFiles.AgentMcpUrl"/>),
/// so the list is of the server the agent reaches. A header mapped to an unbound Env Ref carries its
/// <c>.env</c> value, the value provisioning writes into the agent's own MCP configuration. A header
/// mapped to a bound secret is never resolved: the orchestrator does not send a bound key anywhere,
/// and that endpoint is reported as <see cref="McpServerTools.NeedsAgentSecret"/> instead.
/// </remarks>
public static class McpToolPlan
{
    public static IReadOnlyList<McpPlanEntry> Build(
        Agent agent,
        IReadOnlyDictionary<string, string> envValues,
        IReadOnlyCollection<SecretBinding> bindings,
        string fleetMemoryMcpUrl,
        Func<string?> journalReadToken)
    {
        var entries = new List<McpPlanEntry>();
        foreach (var endpoint in agent.McpEndpoints.OrderBy(e => e.McpName, StringComparer.Ordinal))
            entries.Add(Plan(agent, endpoint, envValues, bindings, journalReadToken));

        // Every agent is given fleet-memory, whether or not it has a row for it.
        if (!agent.McpEndpoints.Any(e => e.McpName == "fleet-memory"))
        {
            var implicitMemory = new AgentMcpEndpoint
            {
                McpName = "fleet-memory", Url = fleetMemoryMcpUrl, TransportType = McpHeaderMappings.HttpTransport,
            };
            entries.Add(Plan(agent, implicitMemory, envValues, bindings, journalReadToken));
        }

        return [.. entries.OrderBy(e => e.Name, StringComparer.Ordinal)];
    }

    private static McpPlanEntry Plan(
        Agent agent, AgentMcpEndpoint endpoint, IReadOnlyDictionary<string, string> envValues,
        IReadOnlyCollection<SecretBinding> bindings, Func<string?> journalReadToken)
    {
        var name = endpoint.McpName;
        McpPlanEntry NotAsked(string status, string reason) => new(name, null, McpServerTools.NotAsked(name, status, reason));

        if (string.Equals(name, AgentConfigFiles.JournalFilesServerName, StringComparison.OrdinalIgnoreCase))
            return NotAsked(McpServerTools.Misconfigured, "This name is reserved for the agent's own file server.");

        bool sse;
        switch (endpoint.TransportType)
        {
            case McpHeaderMappings.HttpTransport: sse = false; break;
            case "sse": sse = true; break;
            default:
                return NotAsked(McpServerTools.UnsupportedTransport,
                    $"Transport '{endpoint.TransportType}' cannot be asked from here. Only http and sse can.");
        }

        var isJournal = string.Equals(name, AgentConfigFiles.JournalMcpServerName, StringComparison.OrdinalIgnoreCase);
        var address = isJournal ? AgentConfigFiles.WithoutQuery(endpoint.Url) : AgentConfigFiles.AgentMcpUrl(agent, endpoint);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return NotAsked(McpServerTools.Misconfigured, "The saved address is not an http or https URL.");

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (isJournal)
        {
            // The journal answers only a read token for this agent, the one provisioning mints for it.
            if (journalReadToken() is not { Length: > 0 } token)
                return NotAsked(McpServerTools.Misconfigured, "The journal key is not configured, so the journal cannot be asked.");
            headers["Authorization"] = $"Bearer {token}";
            return new(name, new McpListTarget(name, url, sse, headers), null);
        }

        if (!McpHeaderMappings.TryRead(endpoint, out var mappings, out _))
            return NotAsked(McpServerTools.Misconfigured, "The saved header mappings are not valid. Replace or clear them.");
        if (mappings.Count == 0)
            return new(name, new McpListTarget(name, url, sse, headers), null);

        if (McpHeaderMappings.DescribeShapeFault(mappings) is not null
            || McpHeaderMappings.DescribeEndpointFault(name, endpoint.Url, endpoint.TransportType, agent.Provider) is not null)
            return NotAsked(McpServerTools.Misconfigured, "The saved header mappings do not fit this endpoint. Reprovisioning refuses them too.");

        foreach (var mapping in mappings)
        {
            if (!agent.EnvRefs.Any(r => string.Equals(r.EnvKeyName, mapping.EnvRefName, StringComparison.Ordinal)))
                return NotAsked(McpServerTools.Misconfigured,
                    $"Header {mapping.HeaderName} maps {mapping.EnvRefName}, which is not an Env Ref of this agent.");

            if (bindings.Any(b => string.Equals(b.EnvKeyName, mapping.EnvRefName, StringComparison.Ordinal)))
                return NotAsked(McpServerTools.NeedsAgentSecret,
                    $"Header {mapping.HeaderName} carries the bound secret {mapping.EnvRefName}, which only the agent's own requests can use.");

            // The same test provisioning applies before it writes the value into the agent's files.
            if (!envValues.TryGetValue(mapping.EnvRefName, out var value) || value.Length == 0 || value == "<secret>"
                || value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                return NotAsked(McpServerTools.Misconfigured,
                    $"Header {mapping.HeaderName} maps {mapping.EnvRefName}, which has no usable value in .env.");
            headers[mapping.HeaderName] = value;
        }

        return new(name, new McpListTarget(name, url, sse, headers), null);
    }
}
