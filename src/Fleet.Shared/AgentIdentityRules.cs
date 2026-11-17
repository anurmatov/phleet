namespace Fleet.Shared;

/// <summary>How much is known about who is calling.</summary>
public enum AgentCallerKind
{
    /// <summary>Nobody said who they are, and nothing required them to.</summary>
    None,

    /// <summary>
    /// A name the caller gave for itself (<c>?agent=</c>) with no token behind it. Believed only
    /// while tokens are not required, and never where a decision is made for a particular agent
    /// under enforcement.
    /// </summary>
    Unverified,

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
    /// The agent this call is made as: its name when the caller is an agent, verified or (while
    /// tokens are not required) claimed. Empty for a service and for nobody.
    /// </summary>
    public string AgentName => Kind is AgentCallerKind.Agent or AgentCallerKind.Unverified ? Name : "";
}

public enum AgentIdentityOutcome
{
    Accepted,

    /// <summary>A token of this fleet's shape that no configured key signed. Refused in every mode.</summary>
    InvalidToken,

    /// <summary>No token, where tokens are required.</summary>
    TokenRequired,
}

/// <summary>
/// The one rule for who a caller is, shared by every fleet MCP server. Pure: the header, the name
/// the URL claims and the settings in; the caller and whether to serve it out.
/// </summary>
public static class AgentIdentityRules
{
    /// <param name="authorization">The request's <c>Authorization</c> header, as it arrived.</param>
    /// <param name="claimedAgent">The <c>?agent=</c> of the URL: what the caller says of itself.</param>
    /// <param name="keys">The configured keys. None is a deployment from before tokens: nothing is verified and nothing is required.</param>
    /// <param name="enforce">Whether a caller without a token is refused.</param>
    public static (AgentIdentityOutcome Outcome, AgentCaller Caller) Resolve(
        string? authorization, string? claimedAgent, IReadOnlyList<byte[]> keys, bool enforce)
    {
        var claimed = (claimedAgent ?? "").Trim();
        var bearer = AgentIdentityTokens.BearerOf(authorization);

        // Without a key nothing can be verified. A token is not refused for that: the orchestrator
        // may already mint them while this server has not been given the key yet.
        if (keys.Count == 0)
            return (AgentIdentityOutcome.Accepted, Claimed(claimed));

        if (AgentIdentityTokens.LooksLikeToken(bearer))
        {
            // A wrong token is never downgraded to "no token": whoever sent it meant to be believed.
            if (!AgentIdentityTokens.TryVerify(bearer, keys, out var purpose, out var subject))
                return (AgentIdentityOutcome.InvalidToken, AgentCaller.None);

            return (AgentIdentityOutcome.Accepted, new AgentCaller(
                subject, purpose == AgentIdentityTokens.PurposeService ? AgentCallerKind.Service : AgentCallerKind.Agent));
        }

        // Any other bearer is not this fleet's token: the server's own operator check may know it.
        return enforce
            ? (AgentIdentityOutcome.TokenRequired, AgentCaller.None)
            : (AgentIdentityOutcome.Accepted, Claimed(claimed));
    }

    private static AgentCaller Claimed(string name) =>
        name.Length == 0 ? AgentCaller.None : new AgentCaller(name, AgentCallerKind.Unverified);
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
