using Fleet.Orchestrator.Auth;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>What the gate concluded about one request.</summary>
public enum AccessDecision
{
    Allow,

    /// <summary>A credential is required and none verified. 401.</summary>
    Unauthorized,

    /// <summary>A browser's write arrived without <see cref="OrchestratorAuth.RequestHeader"/>. 403.</summary>
    RequestHeaderRequired,
}

/// <summary>
/// Which requests the orchestrator gates, and what each must carry (docs/sign-in.md).
/// </summary>
/// <remarks>
/// <para>
/// The rule lives here rather than inline in <c>Program.cs</c> for one
/// reason: a test that re-declares it proves only that the test's copy behaves. The endpoint tests
/// build a host around <b>this</b> predicate, so deleting a path from the exemption list — or
/// making a write method read-only by accident — turns them red.
/// </para>
/// <para>
/// Method-based, not route-based. Every non-GET outside the exempt paths needs the token, which is
/// why a new mutating route inherits the gate without registering anything: that is the property
/// AC6 depends on, and it is a property of this function rather than of any endpoint file.
/// </para>
/// </remarks>
public static class OrchestratorAuth
{
    /// <summary>
    /// True when the request must carry <c>Orchestrator:AuthToken</c>.
    ///
    /// Reads (GET/HEAD/OPTIONS) never do. Neither do the paths with their own auth or no auth at
    /// all: WebSocket upgrades, the MCP endpoint, the separately-tokened config API, health,
    /// and the exact chat-service paths — cross-chat authorization, the sign-in check and the chat
    /// reply push — each of which requires its own Comms-only token.
    /// </summary>
    public static bool RequiresBearerToken(string method, string path)
    {
        var isReadOnly = HttpMethods.IsGet(method)
                      || HttpMethods.IsHead(method)
                      || HttpMethods.IsOptions(method);

        var isExemptPath = path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/api/config", StringComparison.OrdinalIgnoreCase)
                        || path.Equals(Fleet.Orchestrator.Endpoints.JournalCrossChatAuthorizationEndpoints.Path, StringComparison.OrdinalIgnoreCase)
                        || path.Equals(Fleet.Orchestrator.Endpoints.AuthEndpoints.CheckPath, StringComparison.OrdinalIgnoreCase)
                        || path.Equals(SignIn.ChatReplyPushPath, StringComparison.OrdinalIgnoreCase)
                        || path.Equals("/health", StringComparison.OrdinalIgnoreCase);

        return !isReadOnly && !isExemptPath;
    }

    /// <summary>The proxy's mark on a request from outside. See <see cref="SignIn.EdgeHeader"/>.</summary>
    public const string EdgeHeader = SignIn.EdgeHeader;

    /// <summary>What a browser's write must carry. See <see cref="SignIn.RequestHeader"/>.</summary>
    public const string RequestHeader = SignIn.RequestHeader;

    /// <summary>
    /// The routes anyone may call: they are how a credential is obtained, or they answer "am I
    /// signed in?" themselves.
    /// </summary>
    public static bool IsPublicAuthRoute(string method, string path) =>
        (HttpMethods.IsPost(method) && (
            path.Equals("/api/auth/browser-session", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/auth/devices", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/auth/token", StringComparison.OrdinalIgnoreCase)))
        || (HttpMethods.IsGet(method) && path.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The gate, as one pure function.
    /// </summary>
    /// <param name="authRequired">False when no <c>Orchestrator:AuthToken</c> is configured: nothing is gated.</param>
    /// <param name="isEdge">The request carries <see cref="EdgeHeader"/>.</param>
    /// <param name="caller">How the caller proved who it is, already verified.</param>
    /// <param name="hasRequestHeader">The request carries <see cref="RequestHeader"/>.</param>
    /// <remarks>
    /// <para>
    /// <b>From outside, everything under <c>/api</c> and <c>/ws</c> needs a credential, reads
    /// included.</b> From the internal network the older rule stands (<see cref="RequiresBearerToken"/>:
    /// writes need one, reads do not), so the services that read this API directly keep working
    /// without holding a credential.
    /// </para>
    /// <para>
    /// <c>/api/config</c> is not decided here. It has its own token and its own middleware, which
    /// applies the same browser rule.
    /// </para>
    /// </remarks>
    public static AccessDecision Decide(
        bool authRequired, string method, string path, bool isEdge, CallerKind caller, bool hasRequestHeader)
    {
        if (!authRequired || IsPublicAuthRoute(method, path) || IsConfigApi(path))
            return AccessDecision.Allow;

        var needsCredential = isEdge
            ? path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)
            : RequiresBearerToken(method, path);

        if (needsCredential && caller == CallerKind.None)
            return AccessDecision.Unauthorized;

        return IsBrowserWriteWithoutHeader(method, caller, hasRequestHeader)
            ? AccessDecision.RequestHeaderRequired
            : AccessDecision.Allow;
    }

    public static bool IsConfigApi(string path) =>
        path.StartsWith("/api/config", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A cookie-authenticated request that changes something and lacks <see cref="RequestHeader"/>.
    /// The rule itself is shared with the chat service: <see cref="SignIn.IsBrowserWriteWithoutHeader"/>.
    /// </summary>
    public static bool IsBrowserWriteWithoutHeader(string method, CallerKind caller, bool hasRequestHeader) =>
        SignIn.IsBrowserWriteWithoutHeader(method, caller == CallerKind.Browser, hasRequestHeader);
}
