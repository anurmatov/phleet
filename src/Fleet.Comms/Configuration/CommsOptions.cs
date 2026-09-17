namespace Fleet.Comms.Configuration;

/// <summary>
/// Operator configuration for the north boundary.
///
/// <para>Deliberately small, and deliberately carrying no secret, port, certificate, hostname or
/// deployment identity: this is a public repository, and those belong to the deployment (issue #292
/// scope 7). Listener addresses come from the host environment at run time, not from a file here.</para>
/// </summary>
public sealed class CommsOptions
{
    public const string SectionName = "Comms";

    /// <summary>
    /// Non-secret display label returned by <c>GET /v1/session</c>. Not an agent identity and not
    /// used for authorisation.
    /// </summary>
    public string AgentLabel { get; set; } = "assistant";

    /// <summary>
    /// Filesystem path of the durable auth database.
    ///
    /// <para>A path, not a credential — the file itself is the deployment's, and the deployment is
    /// responsible for putting it somewhere that survives a container restart. Blank is rejected at
    /// startup rather than silently falling back to an in-process store: "the owner has to
    /// re-enroll after every deploy" is not a failure mode worth reaching by omission.</para>
    /// </summary>
    public string AuthStorePath { get; set; } = "auth.db";
}

/// <summary>
/// The admission bound on the auth routes (docs/first-party-api.md §5.4, §12).
///
/// <para><b>This is not a nicety, and the reason is the KDF.</b> Every credential check on this
/// boundary spends one Argon2id evaluation at 19 MiB — deliberately, and deliberately on the
/// failures too, so that a rejection cannot be timed to learn whether a record exists. That makes
/// an unauthenticated request an amplifier: cheap to send, expensive to answer. Admission is
/// therefore decided <b>before</b> the endpoint runs, by middleware, so a throttled request never
/// reaches the hasher or contends for the store.</para>
///
/// <para>The window is sized against what the owner actually needs — one token mint per fifteen
/// minutes, plus the occasional enrollment — so the bound is far above legitimate use and far below
/// what makes the amplifier worth having.</para>
/// </summary>
public static class AuthRateLimits
{
    /// <summary>The named policy the auth routes opt into.</summary>
    public const string PolicyName = "auth";

    /// <summary>Requests admitted per partition per window.</summary>
    public const int PermitsPerWindow = 30;

    /// <summary>Window length in seconds, and the <c>Retry-After</c> a client is given.</summary>
    public const int WindowSeconds = 60;

    /// <summary>
    /// Zero. A queue would hold a rejected caller's request open and let the backlog itself become
    /// the resource being exhausted; §5.4 gives the caller a delay to obey instead.
    /// </summary>
    public const int QueueLimit = 0;
}

/// <summary>
/// The server limits <c>GET /v1/session</c> advertises, fixed by docs/first-party-api.md.
///
/// <para>Each is a single value with a unit in the contract, so each is a constant here rather than
/// configuration — a client that asked the server and got a different answer than the document
/// states would have no way to tell which one to believe.</para>
/// </summary>
public static class CommsLimits
{
    /// <summary>§5.1 catch-up default page size.</summary>
    public const int CatchUpLimitDefault = 200;

    /// <summary>§5.1 catch-up maximum page size. Above it is `unsupported_kind`, never a clamp.</summary>
    public const int CatchUpLimitMax = 1000;

    /// <summary>§4 identifier length bound, shared by all four client-chosen identifiers.</summary>
    public const int IdentifierMaxLength = 128;

    /// <summary>§9 per-connection outbound buffer, matching the in-process progress capacity.</summary>
    public const int OutboundBufferEvents = 256;
}
