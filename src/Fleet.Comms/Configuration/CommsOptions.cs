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

    // ── Conversation journal (opt-in, #375) ──────────────────────────────────────────

    /// <summary>The journal listener and store. Off by default; see docs/comms-journal.md.</summary>
    public JournalOptions Journal { get; set; } = new();

    /// <summary>
    /// Fails fast on a journal configuration that cannot work. A no-op while the journal is off.
    /// </summary>
    public void ValidateJournal() => Journal.Validate(ConversationsEnabled);

    // ── Journal media (opt-in, #388) ───────────────────────────────────────────────

    /// <summary>
    /// Journal media: <c>Comms__Media__*</c>. Off unless <c>Endpoint</c> is set, and requires the
    /// journal — there is nothing to attach an object to without a journal message.
    /// </summary>
    public MediaOptions Media { get; set; } = new();

    /// <summary>
    /// Fails fast on a media configuration that cannot work. A no-op while media is off.
    /// </summary>
    public void ValidateMedia()
    {
        Media.Validate(requireFields: true);

        if (Media.Enabled && !Journal.Enabled)
            throw new InvalidOperationException(
                "media_requires_journal: Comms__Media__Endpoint is set but the journal is off. "
                + "Objects are attached to journal messages; enable the journal first.");
    }
}

/// <summary>
/// The journal's object store: <c>Comms__Media__*</c> (#388).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Endpoint"/> is the enabling key.</b> Blank means media does not exist: no upload
/// route is mapped, the sweeper never lists the bucket, and every attachment an agent offers is
/// refused <c>409 media_disabled</c> — which is exactly the answer the S2 drainer already knows how
/// to handle.
/// </para>
/// <para>
/// When it IS set, the four fields below are required rather than defaulted. A bucket name, an
/// access key and a secret have no sane default, and a shipped one is a credential every
/// deployment that did not notice it has.
/// </para>
/// <para>
/// ⚠️ The secret is a credential: never logged, never echoed, never a metric label. Every failure
/// this class reports names a field, never a value.
/// </para>
/// </remarks>
public sealed class MediaOptions
{
    /// <summary>Largest object the store accepts, in bytes. Equal to the Telegram Bot API's file cap.</summary>
    public const long MaxObjectBytes = 20_971_520;

    /// <summary>An object no upload has completed or committed is deleted after this long.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(24);

    /// <summary>How long a <c>deleting</c> row's bytes are kept, so a restore can still catch them.</summary>
    public static readonly TimeSpan DeleteGrace = TimeSpan.FromHours(72);

    /// <summary>How often a degraded store re-probes.</summary>
    public static readonly TimeSpan ProbeRetry = TimeSpan.FromSeconds(30);

    /// <summary>Per-request S3 budget. A hung bucket must not hold an upload slot open.</summary>
    public static readonly TimeSpan RequestTimeoutValue = TimeSpan.FromSeconds(30);

    /// <summary>The object-store endpoint (e.g. <c>http://comms-minio:9000</c>). The enabling key.</summary>
    public string Endpoint { get; set; } = "";

    public string Bucket { get; set; } = "comms-journal";

    public string AccessKey { get; set; } = "";

    public string SecretKey { get; set; } = "";

    public string Region { get; set; } = "us-east-1";

    /// <summary>True when media is configured at all.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>The per-request budget, as a value the store can hand to the SDK.</summary>
    public TimeSpan RequestTimeout => RequestTimeoutValue;

    /// <summary>
    /// Names every field that is missing or unusable. Never contains a value of any field.
    /// </summary>
    /// <param name="requireFields">
    /// True from startup and from the object store's own constructor, which is the same guard seen
    /// from the other side. False from a code path that only reads the constants.
    /// </param>
    /// <exception cref="InvalidOperationException">A fixed <c>media_…</c> code and the field name.</exception>
    public void Validate(bool requireFields = true)
    {
        if (!Enabled) return;

        if (!CommsUrl.IsHttp(Endpoint))
            throw new InvalidOperationException(
                "media_endpoint_invalid: Comms__Media__Endpoint must be an absolute http:// or "
                + "https:// URL.");

        if (!requireFields) return;

        if (string.IsNullOrWhiteSpace(Bucket))
            throw new InvalidOperationException(
                "media_bucket_invalid: Comms__Media__Bucket is required when media is enabled.");

        if (string.IsNullOrWhiteSpace(AccessKey))
            throw new InvalidOperationException(
                "media_access_key_invalid: Comms__Media__AccessKey is required when media is enabled.");

        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException(
                "media_secret_key_invalid: Comms__Media__SecretKey is required when media is enabled.");

        if (string.IsNullOrWhiteSpace(Region))
            throw new InvalidOperationException(
                "media_region_invalid: Comms__Media__Region is required when media is enabled.");
    }
}

/// <summary>
/// The conversation journal: <c>Comms__Journal__*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default, and off means absent.</b> With <see cref="Enabled"/> false no journal listener
/// is built, bound or started, no journal route exists anywhere, and the retention sweep never
/// touches a journal table.
/// </para>
/// <para>
/// Every validation failure is an <see cref="InvalidOperationException"/> whose message starts with
/// a fixed code (<c>journal_…</c>) and never contains key material.
/// </para>
/// </remarks>
public sealed class JournalOptions
{
    /// <summary>Most distinct ids <see cref="ExcludedChatIds"/> may hold.</summary>
    public const int MaxExcludedChatIds = 256;

    public static readonly TimeSpan MinimumMessageRetention = TimeSpan.FromDays(1);

    public bool Enabled { get; set; }

    /// <summary>
    /// The internal journal listener. Container-network only: never published as a host port and
    /// never proxied. A separate application from north, south and ops.
    /// </summary>
    public string Url { get; set; } = "http://0.0.0.0:8083";

    /// <summary>
    /// Comma-separated base64url HMAC keys, each at least 32 bytes after decoding. Every key
    /// verifies; the first one mints. <b>A secret</b>: never logged and never echoed.
    /// </summary>
    public string TokenKeys { get; set; } = "";

    /// <summary>
    /// Chat ids that are never journaled, comma-separated. Blank elements and <c>0</c> are ignored,
    /// so the compose default <c>${FLEET_GROUP_CHAT_ID},</c> is valid with an empty tail and with the
    /// installer's "no group" value <c>0</c>.
    /// </summary>
    public string ExcludedChatIds { get; set; } = "";

    public TimeSpan MessageRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Read-token subjects granted scope <c>all</c> by the read tools (#394), comma-separated.
    /// Blank by default: every reader then sees only the messages its own runtime observed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deployment setting and nothing else. Its <c>.env</c> key sits under the
    /// <c>FLEET_COMMS_JOURNAL_</c> prefix the orchestrator's config API refuses, so no agent can
    /// widen its own scope, and scope is never encoded in a token: removing a subject here and
    /// recreating Comms revokes the grant without rotating a key.
    /// </para>
    /// </remarks>
    public string ReadAllSubjects { get; set; } = "";

    /// <summary>The parsed keys. Call after <see cref="Validate"/>.</summary>
    public IReadOnlyList<byte[]> Keys() => Fleet.Conversations.Journal.JournalTokens.ParseKeys(TokenKeys);

    /// <summary>The parsed exclusion list. Call after <see cref="Validate"/>.</summary>
    public IReadOnlySet<long> ExcludedChats() => ParseExcludedChatIds(ExcludedChatIds);

    /// <summary>The parsed <c>all</c>-scope grants. Call after <see cref="Validate"/>.</summary>
    public IReadOnlySet<string> AllScopeSubjects() => ParseReadAllSubjects(ReadAllSubjects);

    public void Validate(bool conversationsEnabled)
    {
        if (!Enabled) return;

        if (!conversationsEnabled)
            throw new InvalidOperationException(
                "journal_requires_conversations: Comms__Journal__Enabled is true, but the conversation "
                + "feature is not configured (Comms__ConversationConnectionString). The journal lives "
                + "in the conversation database.");

        try
        {
            Keys();
        }
        catch (FormatException e)
        {
            // e.Message names the entry's position, never its value.
            throw new InvalidOperationException(
                $"journal_key_invalid: Comms__Journal__TokenKeys: {e.Message}. Each key is base64url "
                + "and decodes to at least 32 bytes.");
        }

        try
        {
            ParseExcludedChatIds(ExcludedChatIds);
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException(
                $"journal_excluded_ids_invalid: Comms__Journal__ExcludedChatIds: {e.Message}.");
        }

        try
        {
            ParseReadAllSubjects(ReadAllSubjects);
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException(
                $"journal_read_grants_invalid: Comms__Journal__ReadAllSubjects: {e.Message}. Each "
                + "entry is a read-token subject: 1-128 characters of [A-Za-z0-9_-].");
        }

        if (!IsHttpUrl(Url))
            throw new InvalidOperationException(
                "journal_url_invalid: Comms__Journal__Url is not an absolute http:// or https:// URL.");

        if (MessageRetention < MinimumMessageRetention)
            throw new InvalidOperationException(
                "journal_retention_invalid: Comms__Journal__MessageRetention must be at least 1.00:00:00.");
    }

    /// <summary>
    /// Splits on <c>,</c> and trims each element. Empty elements and <c>0</c> are ignored, and
    /// duplicates collapse. A non-empty element that is not an invariant-culture
    /// <see cref="long"/> (leading sign allowed) is invalid, as is a list of more than
    /// <see cref="MaxExcludedChatIds"/> distinct ids.
    /// </summary>
    /// <remarks>
    /// <c>0</c> names no chat — the classifier refuses chat id 0 on its own (rule 1) — and it is what
    /// <c>setup.sh</c> writes for <c>FLEET_GROUP_CHAT_ID</c> when no group is configured. Refusing it
    /// would stop every Comms listener on a default install that turned the journal on.
    /// </remarks>
    /// <exception cref="FormatException">The message names the element's position.</exception>
    public static IReadOnlySet<long> ParseExcludedChatIds(string? value)
    {
        var ids = new HashSet<long>();
        var elements = (value ?? string.Empty).Split(',');

        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i].Trim();
            if (element.Length == 0) continue;

            if (!long.TryParse(element, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var id))
                throw new FormatException($"element {i + 1} is not an integer chat id");

            if (id == 0) continue;

            ids.Add(id);
        }

        if (ids.Count > MaxExcludedChatIds)
            throw new FormatException($"more than {MaxExcludedChatIds} distinct chat ids");

        return ids;
    }

    /// <summary>
    /// Splits on <c>,</c> and trims each element. Empty elements are ignored and duplicates
    /// collapse; any other element must be a valid token subject, compared exactly as the token
    /// verifier yields it (ordinal, case-sensitive).
    /// </summary>
    /// <exception cref="FormatException">The message names the element's position, never its value.</exception>
    public static IReadOnlySet<string> ParseReadAllSubjects(string? value)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var elements = (value ?? string.Empty).Split(',');

        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i].Trim();
            if (element.Length == 0) continue;

            if (!Fleet.Conversations.Journal.JournalTokens.IsValidSubject(element))
                throw new FormatException($"element {i + 1} is not a token subject");

            subjects.Add(element);
        }

        return subjects;
    }

    private static bool IsHttpUrl(string? value) => CommsUrl.IsHttp(value);
}

/// <summary>
/// The URL shape two option classes need to accept, in one definition.
/// </summary>
/// <remarks>
/// Kestrel's wildcard hosts are not URI hosts, so <c>http://*:8083</c> and <c>http://+:8083</c> are
/// rewritten to <c>0.0.0.0</c> before the check. Two copies of that rewrite is how one of them
/// drifts and a bind address starts being refused.
/// </remarks>
internal static class CommsUrl
{
    public static bool IsHttp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var candidate = value.Replace("://*:", "://0.0.0.0:", StringComparison.Ordinal)
            .Replace("://+:", "://0.0.0.0:", StringComparison.Ordinal);

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
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
