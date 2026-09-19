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
    /// Filesystem path of the durable auth database. <b>Required, with no default.</b>
    ///
    /// <para>A path, not a credential — the file itself is the deployment's, and the deployment is
    /// responsible for putting it somewhere that survives a container restart.</para>
    ///
    /// <para><b>There is deliberately no fallback value.</b> A default such as <c>auth.db</c> is
    /// worse than nothing here: it is relative, so it resolves against whatever the working
    /// directory happens to be, and a deployment that never set the key would come up healthy,
    /// serve traffic, and lose every device registration on the next container recreation — with
    /// no error at any point. Blank is refused when the application is built, so the failure is a
    /// process that will not start rather than one that quietly forgets.</para>
    /// </summary>
    public string AuthStorePath { get; set; } = "";

    /// <summary>
    /// Where the operations listener binds. <b>Loopback only, and separate from the north
    /// listener.</b>
    ///
    /// <para>`/health` and `/ready` live here and nowhere else. Adding them to the north listener
    /// would make it a five-route surface and break the published contract; filtering them by
    /// `Host` on a single listener would be worse, because `Host` is client-supplied and a north
    /// caller could reach the readiness oracle through the public port by sending the right one.
    /// So this is a genuinely separate <c>WebApplication</c> on its own address.</para>
    ///
    /// <para>The default is loopback deliberately: `/ready` reports whether the auth store is
    /// answering, which on a public address is an availability oracle. It is reachable by the
    /// container's own healthcheck and by nothing else — not the host, not another container on
    /// the same network, not the internet. It must never be proxied.</para>
    /// </summary>
    public string OpsUrl { get; set; } = "http://127.0.0.1:8081";

    /// <summary>
    /// Whether to trust `X-Forwarded-*` from the immediate peer. <b>Off by default.</b>
    ///
    /// <para>The rate limiter partitions on the caller's address. Behind a reverse proxy that is
    /// the proxy for every caller, collapsing the limiter to one shared bucket — degraded, but
    /// bounded, and never wrong in the dangerous direction.</para>
    ///
    /// <para>Turning this on fixes that <b>only if a proxy is actually in front</b> and replaces
    /// `X-Forwarded-For` rather than appending to it. On a port reachable without a proxy it does
    /// the opposite: it hands every caller the limiter's partition key, so anyone can mint a fresh
    /// budget by changing a header. That asymmetry is why the default is off and why the
    /// deployment document explains the condition in the same paragraph as the switch.</para>
    ///
    /// <para>One hop is consumed, never more. Each additional hop is one more position a caller
    /// can forge from.</para>
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Set by the deployment once the auth store has been initialised. <b>Lives outside the
    /// volume</b>, which is the entire point.
    ///
    /// <para>The in-volume marker catches a deleted database. It cannot catch a deleted volume,
    /// because it goes with it — and a wiped volume then looks exactly like a first install, which
    /// is the loss where silently creating empty state is worst. This flag is in the deployment's
    /// environment, so it survives the volume and makes that case explicit: recovery must be
    /// asked for.</para>
    /// </summary>
    public bool StoreProvisioned { get; set; }

    // ── Durable conversations (opt-in) ───────────────────────────────────────────────

    /// <summary>
    /// Runtime connection string for the conversation database. <b>No default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its presence is what ENABLES the conversation feature. An install that leaves it empty is
    /// byte-identical to the one the auth slice shipped: no conversation route is mapped, readiness
    /// checks only the auth store, and <b>no database connection is attempted</b>.
    /// </para>
    /// <para>
    /// This account holds SELECT/INSERT/UPDATE/DELETE and <b>no DDL grants</b>. That is what makes
    /// "migrations are never a startup side effect" assertable rather than assumed — a process that
    /// tried would be refused by the database.
    /// </para>
    /// </remarks>
    public string ConversationConnectionString { get; set; } = "";

    /// <summary>
    /// Separate DDL connection string, used ONLY by <c>conversations migrate</c>. <b>No default.</b>
    /// </summary>
    /// <remarks>
    /// The running service does not have this one. A runtime account that could alter the schema
    /// turns a bug into a migration, and removes the guard that would have caught it.
    /// </remarks>
    public string ConversationMigrationConnectionString { get; set; } = "";

    /// <summary>True when the conversation feature is configured at all.</summary>
    public bool ConversationsEnabled => !string.IsNullOrWhiteSpace(ConversationConnectionString);

    /// <summary>
    /// The agent-facing store listener. Container-internal; <b>never published as a host port and
    /// never proxied.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DIFFERENT rule from the ops listener's, and the difference matters. Ops is loopback-bound
    /// because only the container's own healthcheck calls it. The south caller is a <i>different
    /// container</i>, so a loopback bind would make this surface unreachable by construction —
    /// it binds all interfaces on the container network and is kept private by not being published.
    /// </para>
    /// </remarks>
    public string SouthUrl { get; set; } = "http://0.0.0.0:8082";

    /// <summary>
    /// Bearer credential the south listener requires. <b>No default</b>; startup fails without it
    /// when conversations are enabled.
    /// </summary>
    /// <remarks>
    /// Compared in fixed time, and a wrong credential is indistinguishable from an absent one.
    /// </remarks>
    public string SouthBearerToken { get; set; } = "";

    /// <summary>
    /// The agent commands are routed to. <b>No default</b>; a blank value fails startup.
    /// </summary>
    /// <remarks>
    /// It becomes a routing key AND a queue-name segment, so it is validated rather than trusted: a
    /// value carrying a dot or a slash would produce a queue name the consumer cannot address.
    /// </remarks>
    public string AgentName { get; set; } = "";

    /// <summary>Broker connection for the two outbox publishers.</summary>
    public string BrokerConnectionString { get; set; } = "";

    // ── Attachments (opt-in, #308) ───────────────────────────────────────────────────

    /// <summary>
    /// Directory holding attachment bytes. <b>Its presence is what enables attachments.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unset means the feature is off: a non-empty <c>attachments</c> array on a submission is
    /// refused exactly as it was before #308, the three attachment routes are not mapped at all, and
    /// every other conversation route behaves byte-identically. That is a separate switch from
    /// <see cref="ConversationConnectionString"/> on purpose — a deployment can run conversations
    /// without provisioning a volume, and it should not have to pretend otherwise.
    /// </para>
    /// <para>
    /// ⚠️ <b>The bytes here are NOT in the nightly database dump.</b> Their durability is this
    /// volume's, and backing it up is a deployment decision. A restore of the database alone yields a
    /// transcript whose images serve <c>410 attachment_gone</c> — correct, rendered deliberately by
    /// the client, and not silent.
    /// </para>
    /// <para>
    /// No default, for the same reason <see cref="AuthStorePath"/> has none: a relative fallback
    /// resolves against whatever the working directory happens to be, and a deployment that never set
    /// the key would come up healthy and lose every image on the next container recreation.
    /// </para>
    /// </remarks>
    public string AttachmentRootPath { get; set; } = "";

    /// <summary>
    /// True when attachments are configured. Requires the conversation feature — there is nothing to
    /// attach an image to without a transcript.
    /// </summary>
    public bool AttachmentsEnabled =>
        ConversationsEnabled && !string.IsNullOrWhiteSpace(AttachmentRootPath);

    /// <summary>
    /// Fails fast on a configuration that cannot work, naming the missing key.
    /// </summary>
    public void ValidateConversations()
    {
        if (!ConversationsEnabled) return;

        if (string.IsNullOrWhiteSpace(SouthBearerToken))
            throw new InvalidOperationException(
                "Comms__SouthBearerToken is required when the conversation feature is enabled. "
                + "The south listener carries an administrative surface and has no default credential.");

        if (string.IsNullOrWhiteSpace(AgentName))
            throw new InvalidOperationException(
                "Comms__AgentName is required when the conversation feature is enabled. "
                + "It is the command routing key and a queue-name segment; there is no default.");

        if (!AgentNamePattern.IsMatch(AgentName))
            throw new InvalidOperationException(
                $"Comms__AgentName '{AgentName}' is not usable as a routing key and queue-name "
                + "segment. Allowed: 1-128 characters of [A-Za-z0-9_-]. A dot or a slash would "
                + "produce a queue name the consumer cannot address.");
    }

    private static readonly System.Text.RegularExpressions.Regex AgentNamePattern =
        new("^[A-Za-z0-9_-]{1,128}$", System.Text.RegularExpressions.RegexOptions.Compiled);
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
public static class ConversationRateLimits
{
    /// <summary>The named policy the conversation routes and the stream upgrade opt into.</summary>
    public const string PolicyName = "conversation";

    /// <summary>
    /// Requests admitted per partition per window.
    /// </summary>
    /// <remarks>
    /// Larger than the auth budget because these routes are the ordinary working surface — a client
    /// catching up after a reconnect issues a page request per 200 events — and because they do not
    /// spend an Argon2id evaluation per call the way the auth routes do.
    /// </remarks>
    public const int PermitsPerWindow = 300;

    public const int WindowSeconds = 60;

    public const int QueueLimit = 0;
}

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
