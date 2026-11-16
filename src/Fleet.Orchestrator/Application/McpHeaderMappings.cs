using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;

namespace Fleet.Orchestrator.Application;

/// <summary>
/// One header an HTTP MCP endpoint is sent with, and the Env Ref whose value it carries (#593). Names
/// only: the value is resolved at provisioning, from the agent's own Env Ref.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpHeaderEnvRef(string HeaderName, string EnvRefName);

/// <summary>
/// The rules of an endpoint's header mappings, shared by <c>PUT /api/agents/{name}/config</c>,
/// <c>POST /api/agents</c>, <c>manage_agent_mcp_endpoints</c> and provisioning. Every fault starts
/// with its fixed code and names names, never a value.
/// </summary>
/// <remarks>
/// A mapping adds no Env Ref and no binding: it may name only a reference the agent already holds.
/// Removing that reference stays allowed; the mapping is then dangling, and provisioning refuses it
/// until it is corrected or removed.
/// </remarks>
public static class McpHeaderMappings
{
    public const string Invalid = "mcp_header_mapping_invalid";
    public const string RefMissing = "mcp_header_ref_missing";
    public const string ValueMissing = "mcp_header_value_missing";
    public const string Unsupported = "mcp_headers_unsupported";
    public const string BindingMismatch = "mcp_header_binding_mismatch";

    /// <summary>The one transport mapped headers are supported on: streamable HTTP.</summary>
    public const string HttpTransport = "http";

    /// <summary>The stored shape: exactly the two string fields, nothing missing, null or extra.</summary>
    private static readonly JsonSerializerOptions Stored = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>The column value: null for none, so a headerless endpoint stores what it always did.</summary>
    public static string? Serialize(IReadOnlyList<McpHeaderEnvRef> mappings) =>
        mappings.Count == 0 ? null : JsonSerializer.Serialize(mappings, Stored);

    /// <summary>
    /// The endpoint's stored mappings; SQL NULL is none. A stored value that is not a valid list is a
    /// fault, never read as none.
    /// </summary>
    public static bool TryRead(AgentMcpEndpoint endpoint, out IReadOnlyList<McpHeaderEnvRef> mappings, out string? fault)
    {
        mappings = [];
        fault = null;
        if (endpoint.HeaderEnvRefsJson is null)
            return true;

        List<McpHeaderEnvRef>? parsed = null;
        try { parsed = JsonSerializer.Deserialize<List<McpHeaderEnvRef>>(endpoint.HeaderEnvRefsJson, Stored); }
        catch (JsonException) { }

        if (parsed is null || DescribeShapeFault(parsed) is not null)
        {
            fault = $"{Invalid}: the stored header mappings of MCP endpoint '{endpoint.McpName}' are not valid. "
                + "Replace them (headerEnvRefs) or clear them with [].";
            return false;
        }
        mappings = parsed;
        return true;
    }

    /// <summary>
    /// The fault in an endpoint's resulting mappings, or null. <paramref name="stored"/> is what the
    /// endpoint already holds: a pair kept from it is not authorized again, so saving a list that
    /// still carries a dangling mapping never blocks the removal of its Env Ref.
    /// </summary>
    /// <param name="authorizedRefs">The Env Refs the caller may map: the resulting agent's own, never a global key.</param>
    public static WriteFault? Check(
        string mcpName, string url, string transportType, string? provider,
        IReadOnlyList<McpHeaderEnvRef?> mappings, IReadOnlyList<McpHeaderEnvRef> stored,
        IReadOnlyCollection<string> authorizedRefs)
    {
        if (mappings.Count == 0)
            return null;
        if (DescribeShapeFault(mappings) is { } shape)
            return WriteFaults.Invalid(Invalid, $"{shape} (MCP endpoint '{mcpName}')");
        if (DescribeEndpointFault(mcpName, url, transportType, provider) is { } endpoint)
            return WriteFaults.Invalid(endpoint.Code, endpoint.Message);

        foreach (var mapping in mappings)
        {
            if (stored.Any(s => SamePair(s, mapping!)) || authorizedRefs.Contains(mapping!.EnvRefName, StringComparer.Ordinal))
                continue;
            return WriteFaults.Invalid(RefMissing,
                $"{RefMissing}: MCP endpoint '{mcpName}' maps {mapping.HeaderName} to {mapping.EnvRefName}, which is not an "
                + "Env Ref of this agent. Add the Env Ref first, or map one the agent holds. Nothing was changed.");
        }
        return null;
    }

    /// <summary>
    /// The field rules, or null: two names, a header that may carry a credential (the rule a secret
    /// binding's header is held to), an Env Ref name, and no header twice in any letter case. A Telegram
    /// bot token and a hosted-model key are consumed or isolated by the agent itself and are never a
    /// header value.
    /// </summary>
    public static string? DescribeShapeFault(IReadOnlyList<McpHeaderEnvRef?> mappings)
    {
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            if (mapping is null)
                return $"{Invalid}: each mapping is {{ \"headerName\", \"envRefName\" }}";
            if (!SecretBindingRules.IsCredentialHeaderName(mapping.HeaderName))
                return $"{Invalid}: headerName must be one request header name that can carry a credential "
                    + "(not Host, Cookie, Content-Type, Accept or another header that routes or frames the request)";
            if (!SecretBindingRules.IsEnvKey(mapping.EnvRefName))
                return $"{Invalid}: envRefName must be an environment variable name (letters, digits, underscore)";
            if (JournalGrants.IsBotRef(mapping.EnvRefName)
                || HostedModelProviders.KeyEnvVars.Contains(mapping.EnvRefName, StringComparer.Ordinal))
                return $"{Invalid}: {mapping.EnvRefName} is used by the agent process itself and cannot be sent as a header";
            if (!headers.Add(mapping.HeaderName))
                return $"{Invalid}: header {mapping.HeaderName} is mapped twice";
        }
        return null;
    }

    /// <summary>
    /// Where mapped headers are supported: an HTTP endpoint, on a provider whose pinned CLI sends
    /// headers (<see cref="AgentConfigFiles.McpHeaderSupport"/>), and not one of the built-in
    /// authenticated endpoints, whose headers provisioning writes itself. Its URL is an absolute
    /// <c>http(s)://</c> address with no user info: a mapped endpoint carries its credentials in
    /// headers, never in the URL, whether its references are bound or not. The URL is never echoed.
    /// </summary>
    public static (string Code, string Message)? DescribeEndpointFault(string mcpName, string url, string transportType, string? provider)
    {
        if (string.Equals(mcpName, AgentConfigFiles.JournalMcpServerName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mcpName, AgentConfigFiles.JournalFilesServerName, StringComparison.OrdinalIgnoreCase))
            return (Invalid, $"{Invalid}: {mcpName} is a built-in endpoint whose headers are provisioned; it takes no header mappings");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length != 0)
            return (Invalid, $"{Invalid}: MCP endpoint '{mcpName}' has header mappings, so its URL must be an absolute "
                + "http:// or https:// address with no user name or password in it");
        if (!string.Equals(transportType, HttpTransport, StringComparison.Ordinal))
            return (Unsupported, $"{Unsupported}: MCP endpoint '{mcpName}' has transport '{transportType}'; "
                + $"header mappings are supported only on transport '{HttpTransport}'");
        if (!AgentConfigFiles.SupportsMcpHeaders(AgentProviders.Normalize(provider)))
            return (Unsupported, $"{Unsupported}: provider '{AgentProviders.Normalize(provider)}' does not send MCP headers; "
                + "header mappings are supported on claude and codex only");
        return null;
    }

    /// <summary>The same mapping: the header in any letter case, the Env Ref exactly.</summary>
    public static bool SamePair(McpHeaderEnvRef a, McpHeaderEnvRef b) =>
        string.Equals(a.HeaderName, b.HeaderName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.EnvRefName, b.EnvRefName, StringComparison.Ordinal);
}
