using Fleet.Shared;

namespace Fleet.Agent.Configuration;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    public required string Name { get; set; }
    public required string Role { get; set; }
    public List<string> Projects { get; set; } = [];
    public string Model { get; set; } = "claude-sonnet-4-6";
    public List<string> AllowedTools { get; set; } = ["Read", "Write", "Edit", "Bash", "Glob", "Grep"];
    public string PermissionMode { get; set; } = "acceptEdits";
    public int MaxTurns { get; set; } = 50;
    public required string WorkDir { get; set; }
    public int ProactiveIntervalMinutes { get; set; } = 0;
    public string GroupListenMode { get; set; } = "mention";
    public int GroupDebounceSeconds { get; set; } = 15;
    public string ShortName { get; set; } = "";
    public bool ShowStats { get; set; } = true;
    public bool PrefixMessages { get; set; } = false;
    /// <summary>
    /// Controls how outbound Telegram messages are formatted and sent.
    /// 0=PlainText (legacy dumb-split), 1=LegacyHtml (Markdown→HTML via TelegramFormatter),
    /// 2=Rich (sendRichMessage with per-message LegacyHtml→PlainText fallback).
    /// Default: PlainText (0) — byte-identical to legacy UseFormatter=false behavior.
    /// </summary>
    public FormattingMode FormattingMode { get; set; } = FormattingMode.PlainText;
    /// <summary>
    /// When true, intermediate tool-use progress messages are not sent to Telegram.
    /// Only the final assistant text response is posted. Default: false (preserves existing behavior).
    /// </summary>
    public bool SuppressToolMessages { get; set; } = false;
    public string? Effort { get; set; }
    public string? JsonSchema { get; set; }
    public string? AgentsJson { get; set; }
    public int ToolArgsTruncateLength { get; set; } = 300;
    public string Provider { get; set; } = "claude";
    public string? CodexSandboxMode { get; set; }

    /// <summary>
    /// True when the orchestrator resolved this agent's model to a hosted provider (#335 D5).
    /// <c>entrypoint.sh</c> reads it to decide whether to hand a key over, and the agent checks it
    /// against its own registry at startup (D8).
    /// </summary>
    public bool HostedProvider { get; set; }

    /// <summary>The key env var the orchestrator named for the hosted provider, or null.</summary>
    public string? HostedProviderKeyEnv { get; set; }

    /// <summary>
    /// Origin of a local Anthropic-compatible server (#340), already canonical, or null. Set on a
    /// claude agent, it turns on local model mode: see <see cref="ClaudeLocalModel"/>.
    /// </summary>
    public string? AnthropicBaseUrl { get; set; }

    /// <summary>
    /// Every instruction assigned to this agent, as <c>roles/</c> directory names, already in the
    /// orchestrator's load order (#309).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent reads instructions off the filesystem, where a directory listing carries names and
    /// nothing else — no <c>load_order</c>, and no way to tell an assigned instruction from one that
    /// was unassigned and left a stale directory behind. So the order is <b>supplied</b>, not
    /// inferred: the orchestrator already knows both, and it writes the answer here.
    /// </para>
    /// <para>
    /// <b>Empty means "config generated before #309".</b> An agent that has not been reprovisioned
    /// has no such key, and <see cref="Services.PromptBuilder"/> falls back to exactly the two files
    /// it read before — so an existing agent's prompt does not change until it is reprovisioned.
    /// </para>
    /// </remarks>
    public List<string> InstructionOrder { get; set; } = [];

    /// <summary>
    /// The agent's output style, already stripped of its YAML frontmatter, for providers that have
    /// no output-style mechanism of their own (#314). Empty for claude and for every agent with no
    /// style.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Output styles are a Claude Code feature. A claude agent resolves the same text as a style
    /// file named in <c>settings.json</c>, so it is deliberately NOT sent here — carrying it in
    /// both places would state the same rules twice. Codex and gemini have no such mechanism, and
    /// a style that applied to one provider and vanished for the others would be worse than none.
    /// </para>
    /// <para>
    /// <b>Empty is the normal case</b> and means exactly "no style": the orchestrator omits the key
    /// entirely, and <see cref="Services.PromptBuilder"/> then assembles byte for byte what it
    /// assembled before styles existed.
    /// </para>
    /// </remarks>
    public string OutputStyleBody { get; set; } = "";

    /// <summary>
    /// Per-assignment project context routing (#347), written by the orchestrator only for an agent
    /// with at least one effective <c>card</c> assignment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is the normal case</b> and means "every assignment is full": the router is disabled
    /// silently, every message carries an empty request list, and delivery is exactly what it was
    /// before cards existed.
    /// </para>
    /// <para>
    /// A malformed block does not stop the agent. <see cref="Services.ProjectContextRouter"/>
    /// validates it once, logs one Warning naming the fault and disables itself, so the agent runs
    /// card-only (the resident card plus the <c>get_project_context</c> fallback).
    /// </para>
    /// </remarks>
    public ProjectContextRoutingOptions? ProjectContextRouting { get; set; }
}

/// <summary>
/// The <c>Agent:ProjectContextRouting</c> block (#347). Key names are pinned by the orchestrator:
/// <c>cardProjects</c>, <c>fullVersions</c>, <c>routes[{kind,value,project}]</c>.
/// </summary>
/// <remarks>
/// Everything is bound as strings on purpose. A value the binder cannot convert (a version that is
/// not a number) would otherwise throw while <see cref="AgentOptions"/> is materialised and take the
/// whole agent down, where the contract for a malformed block is "router disabled, one Warning".
/// <see cref="Services.ProjectContextRoutingTable.TryCreate"/> does the parsing.
/// </remarks>
public sealed class ProjectContextRoutingOptions
{
    /// <summary>Effective card assignment names. Only these are ever attached.</summary>
    public List<string> CardProjects { get; set; } = [];

    /// <summary>Current full-context version for each card project (positive integer).</summary>
    public Dictionary<string, string> FullVersions { get; set; } = [];

    /// <summary>Every route whose context matches ANY assigned project, in any effective mode.</summary>
    public List<ProjectContextRouteOptions> Routes { get; set; } = [];
}

/// <summary>One route row: <c>kind</c> is <c>repo</c>, <c>workflow</c> or <c>chat</c>.</summary>
public sealed class ProjectContextRouteOptions
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Project { get; set; } = "";
}

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string BotToken { get; set; } = string.Empty;
    public List<long> AllowedUserIds { get; set; } = [];
    public List<long> AllowedGroupIds { get; set; } = [];
    public bool SendOnly { get; set; }

    /// <summary>Prompt injected when a message has images but no caption text. Default: "(image attached — please analyze)".</summary>
    public string DefaultImagePrompt { get; set; } = "(image attached — please analyze)";

    /// <summary>Maximum number of photos to collect from a single media group. Extras are dropped with a user-facing warning. Default: 10.</summary>
    public int MaxImagesPerGroup { get; set; } = 10;

    /// <summary>Maximum individual photo size in bytes; photos above this limit are skipped with a user-facing warning. Default: 10 MB.</summary>
    public int MaxImageBytes { get; set; } = 10_485_760;

    /// <summary>Hard-cap on total buffering time for a media group in milliseconds. If photos keep arriving past this limit, the group is force-flushed. Default: 10000 ms.</summary>
    public int MaxGroupBufferMs { get; set; } = 10_000;

    /// <summary>When true, each downloaded photo is written to disk and a path hint is injected into the message text so agent tools can reach the bytes. Default: true.</summary>
    public bool PersistAttachments { get; set; } = true;

    /// <summary>Directory where attachment files are written. Default: /workspace/attachments.</summary>
    public string AttachmentDir { get; set; } = "/workspace/attachments";

    /// <summary>Attachment files older than this many hours are deleted by the lazy sweeper (called on each photo write and once at startup). Default: 48.</summary>
    public int AttachmentRetentionHours { get; set; } = 48;

    /// <summary>Maximum PDF document size in bytes; documents above this limit are rejected with a user-facing warning and not passed to the LLM. Default: 32 MB (Claude SDK per-document limit).</summary>
    public long MaxDocumentBytes { get; set; } = 33_554_432; // 32 MB

    // ── Access-request flow ────────────────────────────────────────────────────

    /// <summary>
    /// When true, DMs from users not in AllowedUserIds trigger an access-request message
    /// to the CTO agent (resolved from FLEET_CTO_AGENT) instead of being silently dropped.
    /// Default: false (current silent-drop behavior).
    /// </summary>
    public bool CanReceiveChatRequests { get; set; } = false;

    /// <summary>
    /// Optional message sent to the requesting user immediately after their access request is queued.
    /// Falls back to a built-in default when null/empty.
    /// </summary>
    public string? RequestReceivedMessage { get; set; }

}

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    public string Host { get; set; } = "";
    public string Exchange { get; set; } = "fleet.group";
}

public sealed class WhisperOptions
{
    public const string Section = "Whisper";

    /// <summary>Base URL of the fleet-whisper transcription service (e.g. http://fleet-whisper:8080).</summary>
    public string ServiceUrl { get; set; } = "";
}

public sealed class TtsOptions
{
    public const string Section = "Tts";

    /// <summary>Base URL of the Kokoro TTS service (e.g. http://fleet-kokoro-tts:8880).</summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>Kokoro voice ID to use for synthesis. Default: af_nova.</summary>
    public string Voice { get; set; } = "af_nova";
}

/// <summary>
/// Configuration for the channel-neutral client seam.
///
/// Both values must be set for client conversations to be accepted at all — there is
/// deliberately NO default token, so an agent that has not been configured for a client channel
/// rejects every <c>conversation.open</c> with <c>unauthorized</c>.
///
/// This is a BINDING, not authentication: a shared operator-set token exists so the owner check
/// is deterministic rather than a guess. Real authentication is a separate issue.
/// </summary>
public sealed class ClientChannelOptions
{
    public const string Section = "ClientChannel";

    /// <summary>
    /// Opaque operator-set token the client presents in its principal binding. Empty or absent
    /// disables client conversations entirely. Compared in fixed time, never logged, never echoed.
    /// </summary>
    public string OwnerPrincipalToken { get; set; } = "";

    /// <summary>
    /// The existing numeric owner this token binds to. Zero or absent disables client
    /// conversations. Re-checked against the LIVE allowlist at open time, and never emitted in
    /// any event.
    /// </summary>
    public long OwnerUserId { get; set; }
}

/// <summary>
/// The agent's half of the durable conversation seam (#303).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SouthBaseUrl"/> is the ENABLING key. Absent, the agent registers no consumer, no
/// hosted service and no HTTP client, and every existing path is byte-identical to an agent that
/// never heard of this feature.
/// </para>
/// <para>
/// Present but incomplete is a STARTUP FAILURE, never a silent degrade to disabled. An operator who
/// configured half of it would otherwise see a healthy process beside a queue nobody drains — which
/// is the exact state #303 exists to end.
/// </para>
/// <para>
/// ⚠️ <see cref="SouthBearerToken"/> and <see cref="BrokerConnectionString"/> are credentials. They
/// are never logged, never echoed and never included in a counter label (MUST NOT 19).
/// </para>
/// </remarks>
public sealed class ConversationsOptions
{
    public const string Section = "Conversations";

    /// <summary>The agent name pattern the SERVICE already enforces on its side.</summary>
    /// <remarks>
    /// Duplicated as a pattern rather than shared as code because the two sides are different
    /// assemblies with no common home for it. The name is both the routing key and a queue-name
    /// segment, so a value the two sides read differently means the agent binds and drains a queue
    /// nobody publishes to while the real one grows.
    /// <para>
    /// It is validated against <see cref="AgentOptions.ShortName"/> at startup. There is deliberately
    /// no agent-name field here: <c>ShortName</c> is already this agent's identity on this broker —
    /// <c>GroupRelayService</c> builds <c>fleet.agent.{shortName}</c> from it and binds it as a
    /// routing key — and a second field for one identity is the divergence this pattern exists to
    /// catch, installed as a feature.
    /// </para>
    /// </remarks>
    public const string AgentNamePattern = "^[A-Za-z0-9_-]{1,128}$";

    /// <summary>Base URL of the south listener. Absent disables the whole feature.</summary>
    public string SouthBaseUrl { get; set; } = "";

    /// <summary>Bearer credential for the south listener. Required when enabled.</summary>
    public string SouthBearerToken { get; set; } = "";

    /// <summary>
    /// Lease renewal cadence. Must be at most half
    /// <see cref="Fleet.Conversations.Contracts.ConversationLeaseDefaults.LeaseDuration"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many deliveries may be in flight at once.
    /// </summary>
    /// <remarks>
    /// MUST be greater than one. A delivery is not acknowledged until its terminal is committed, so
    /// a prefetch of one would stop a second submission from ever being delivered while the first
    /// turn ran — and injection and queueing, the two dispositions this seam exists to report, could
    /// then never occur.
    /// </remarks>
    public ushort Prefetch { get; set; } = 8;

    /// <summary>Base delay for the bounded backoff on a retried south call or a requeue.</summary>
    /// <remarks>
    /// These three are constants, not settings. They were knobs with no operator who would turn
    /// them, and every additional key is one more thing a provisioned agent has to be given. Promote
    /// one to configuration when something concrete demands it — not in advance.
    /// <para>
    /// <c>static readonly</c> rather than <c>const</c> only because C# has no constant
    /// <see cref="TimeSpan"/>; they are compile-time values in every sense that matters here.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Ceiling for the bounded backoff.</summary>
    public static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>Per-request timeout for a south call.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
}
