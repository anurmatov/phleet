namespace Fleet.Shared;

/// <summary>How much is known about who is calling.</summary>
public enum AgentCallerKind
{
    /// <summary>No authenticated caller has been resolved.</summary>
    None,

    /// <summary>An agent, by a token the orchestrator minted for it.</summary>
    Agent,

    /// <summary>A fleet service or the operator, by a service token or the server's own operator check.</summary>
    Service,
}

/// <summary>Who is calling one of the fleet's MCP servers.</summary>
/// <param name="Name">The agent's name, or the service's. Empty for <see cref="AgentCallerKind.None"/>.</param>
public sealed record AgentCaller(string Name, AgentCallerKind Kind)
{
    public static readonly AgentCaller None = new("", AgentCallerKind.None);

    /// <summary>Whether a token (or the server's operator check) stands behind the name.</summary>
    public bool IsVerified => Kind is AgentCallerKind.Agent or AgentCallerKind.Service;

    /// <summary>
    /// The agent this call is made as: its signed name when the caller is an agent. Empty for a service and for nobody.
    /// </summary>
    public string AgentName => Kind == AgentCallerKind.Agent ? Name : "";
}

public enum AgentIdentityOutcome
{
    Accepted,

    /// <summary>A token of this fleet's shape that no configured key signed. Always refused.</summary>
    InvalidToken,

    /// <summary>No token, where tokens are required.</summary>
    TokenRequired,
}

/// <summary>
/// The one rule for who a caller is, shared by every fleet MCP server. Pure: the header and verification keys in; the caller and whether to serve it out.
/// </summary>
public static class AgentIdentityRules
{
    /// <summary>Only a valid signed agent or service token identifies a caller.</summary>
    public static (AgentIdentityOutcome Outcome, AgentCaller Caller) Resolve(
        string? authorization, IReadOnlyList<byte[]> keys)
    {
        var bearer = AgentIdentityTokens.BearerOf(authorization);
        if (!AgentIdentityTokens.LooksLikeToken(bearer))
            return (AgentIdentityOutcome.TokenRequired, AgentCaller.None);
        if (!AgentIdentityTokens.TryVerify(bearer, keys, out var purpose, out var subject))
            return (AgentIdentityOutcome.InvalidToken, AgentCaller.None);
        return (AgentIdentityOutcome.Accepted, new AgentCaller(subject,
            purpose == AgentIdentityTokens.PurposeService ? AgentCallerKind.Service : AgentCallerKind.Agent));
    }

}

/// <summary>
/// Whether an agent's granted tools include one tool of one MCP server. The same names the
/// provider's own allow-list uses, checked by the server instead of only by the agent's CLI.
/// </summary>
public static class AgentToolGrants
{
    public const string Prefix = "mcp__";

    /// <summary>The name a tool of <paramref name="server"/> is granted under.</summary>
    public static string GrantName(string server, string tool) => $"{Prefix}{server}__{tool}";

    /// <summary>
    /// True for the tool's own grant, and for a grant of the whole server (<c>mcp__server</c> or
    /// <c>mcp__server__*</c>). Names compare without regard to case, as the generated allow-lists do.
    /// </summary>
    public static bool Allows(IEnumerable<string> grants, string server, string tool)
    {
        var exact = GrantName(server, tool);
        var whole = Prefix + server;
        var wildcard = whole + "__*";
        foreach (var grant in grants)
        {
            if (string.Equals(grant, exact, StringComparison.OrdinalIgnoreCase)
                || string.Equals(grant, whole, StringComparison.OrdinalIgnoreCase)
                || string.Equals(grant, wildcard, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
