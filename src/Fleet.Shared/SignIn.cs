namespace Fleet.Shared;

/// <summary>
/// The names and the one rule that sign-in shares between the orchestrator, which owns it, and the
/// chat service, which asks the orchestrator who a caller is (docs/sign-in.md).
/// </summary>
/// <remarks>
/// Here, and nowhere else, so the two services cannot drift: a header the dashboard's proxy sets, a
/// cookie a browser carries and a rule both services apply are each one constant or one function.
/// </remarks>
public static class SignIn
{
    /// <summary>The cookie a signed-in browser carries. HttpOnly, SameSite=Strict.</summary>
    public const string SessionCookie = "fleet_session";

    /// <summary>
    /// Set to <c>1</c> by the dashboard's proxy on every request it forwards to the orchestrator,
    /// replacing whatever the client sent. It can only add a requirement: a request that carries it
    /// is asked for sign-in on reads too.
    /// </summary>
    public const string EdgeHeader = "X-Fleet-Edge";

    /// <summary>
    /// What a browser's write must carry besides its cookie. A page on another site cannot add a
    /// custom header to a request without this origin's permission, and the session cookie is
    /// SameSite=Strict, so the two together close cross-site request forgery.
    /// </summary>
    public const string RequestHeader = "X-Fleet-Request";

    /// <summary>Where the chat service asks the orchestrator who a caller is.</summary>
    public const string CheckPath = "/internal/auth/check";

    /// <summary>The purpose and subject of the token the chat service presents on <see cref="CheckPath"/>.</summary>
    public const string CheckTokenPurpose = "auth-check";
    public const string CheckTokenSubject = "fleet-comms";

    /// <summary>
    /// Where the chat service tells the orchestrator that an agent replied while no client was
    /// attached, so the phone gets a notification (#530 step 9). Identifiers only, never text.
    /// </summary>
    public const string ChatReplyPushPath = "/internal/push/chat-reply";

    /// <summary>The purpose of the token the chat service presents on <see cref="ChatReplyPushPath"/>.
    /// Its subject is <see cref="CheckTokenSubject"/>.</summary>
    public const string ChatReplyPushTokenPurpose = "chat-push";

    /// <summary>
    /// The purpose of the token an agent presents to the chat service's agent-facing listener. Its
    /// subject is the agent's name, and it acts only on that agent's conversations.
    /// </summary>
    public const string AgentTokenPurpose = "south";

    /// <summary>Whether a request changes something.</summary>
    public static bool IsWrite(string? method) =>
        !(string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
          || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
          || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
          || string.IsNullOrEmpty(method));

    /// <summary>
    /// A cookie-authenticated request that changes something and lacks <see cref="RequestHeader"/>.
    /// A bearer is put on a request on purpose and is never ambient, so only a browser is asked.
    /// </summary>
    public static bool IsBrowserWriteWithoutHeader(string? method, bool isBrowser, bool hasRequestHeader) =>
        isBrowser && !hasRequestHeader && IsWrite(method);
}
