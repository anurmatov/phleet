namespace Fleet.Orchestrator.Data;
using System.ComponentModel.DataAnnotations;
using Fleet.Shared;

public class Agent
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string DisplayName { get; set; }
    public required string Role { get; set; }
    public required string Model { get; set; }
    public string Provider { get; set; } = "claude";
    public int MemoryLimitMb { get; set; }
    public required string ContainerName { get; set; }
    public bool IsEnabled { get; set; } = true;

    // Behavior
    public string PermissionMode { get; set; } = "acceptEdits";
    public int MaxTurns { get; set; } = 50;
    public string WorkDir { get; set; } = "/workspace";
    public int ProactiveIntervalMinutes { get; set; } = 0;
    public string GroupListenMode { get; set; } = "mention";
    public int GroupDebounceSeconds { get; set; } = 15;

    /// <summary>
    /// Seconds <c>WarmupService</c> may spend pre-spawning the executor before it cancels (#357).
    /// Default <see cref="WarmupTimeout.DefaultSeconds"/> keeps cloud agents unchanged; local-model
    /// agents with cold starts near three minutes opt up per agent. Provision-time configuration:
    /// changing it requires <c>reprovision_agent</c>, not <c>restart_agent</c>. Validated on every
    /// write path via <see cref="WarmupTimeout.DescribeFault"/>; timeout stays non-fatal.
    /// </summary>
    public int WarmupTimeoutSeconds { get; set; } = WarmupTimeout.DefaultSeconds;
    public string ShortName { get; set; } = "";
    public bool ShowStats { get; set; } = true;
    public bool PrefixMessages { get; set; } = false;
    public FormattingMode FormattingMode { get; set; } = FormattingMode.PlainText;
    public bool SuppressToolMessages { get; set; } = false;
    public bool TelegramSendOnly { get; set; } = false;
    public string? Image { get; set; }
    public string? Effort { get; set; }
    public string? JsonSchema { get; set; }
    public string? AgentsJson { get; set; }
    public int? HostPort { get; set; }
    public bool AutoMemoryEnabled { get; set; } = true;
    /// <summary>
    /// Opts this agent into journaling its human Telegram conversations. Provisioning remains
    /// byte-identical while false; enabling it requires the journal signing key.
    /// </summary>
    public bool JournalEnabled { get; set; } = false;
    public bool JournalCrossChatEnabled { get; set; } = false;
    [MaxLength(30)]
    public string? CodexSandboxMode { get; set; }

    /// <summary>
    /// Name of the <see cref="Data.OutputStyle"/> row this agent runs with, or <c>null</c> for none.
    /// </summary>
    /// <remarks>
    /// <c>null</c> is the rollout switch and the rollback: an agent with no style is provisioned
    /// byte-for-byte as it was before styles existed — no <c>outputStyle</c> key in
    /// <c>settings.json</c>, no mount, no change to the assembled prompt. Deliberately NOT a
    /// foreign key: a name with no row must be reachable so provisioning can refuse it loudly
    /// rather than emit an unresolvable reference.
    /// </remarks>
    [MaxLength(100)]
    public string? OutputStyle { get; set; }

    /// <summary>
    /// Origin of a local inference server, stored canonical, or <c>null</c> (#340, #382). Set on a
    /// claude or codex agent, it runs that harness against the server — see <see cref="LocalModel"/>.
    /// <c>null</c> is the rollout switch and the rollback.
    /// </summary>
    /// <remarks>
    /// Stored in the <c>AnthropicBaseUrl</c> column (#382 renamed only this property): the rollback
    /// image reads the same column, and the agent-side key and API aliases keep the old name.
    /// </remarks>
    [MaxLength(500)]
    public string? LocalBaseUrl { get; set; }

    /// <summary>
    /// The local server's context size in tokens, or <c>null</c> (#367). Provisioning passes it to
    /// Claude CLI as <see cref="Fleet.Shared.ContextWindow.EnvVar"/> only when the agent is in local
    /// mode; a cloud agent ignores it. <c>null</c> keeps Claude CLI's own behaviour. Validated on
    /// every write path via <see cref="Fleet.Shared.ContextWindow.DescribeFault"/>; changing it
    /// requires <c>reprovision_agent</c>.
    /// </summary>
    public int? ContextWindow { get; set; }


    /// <summary>
    /// The one per-agent harness override (#460), or <c>null</c> for none: Codex <c>key = value</c>
    /// lines or Claude <c>--flag</c>/<c>!--flag</c>/<c>NAME=value</c> lines, merged over the mode
    /// defaults by <see cref="HarnessDefaults.Build"/>. Every write path validates it against the
    /// allowlist through <see cref="Helpers.AgentPatchHelpers.DescribeHarnessExtraFault"/>; claude and
    /// codex only. Stored verbatim (<see cref="Fleet.Shared.HarnessExtra.MaxChars"/> chars,
    /// <see cref="Fleet.Shared.HarnessExtra.MaxLines"/> lines at most). Changing it requires <c>reprovision_agent</c>.
    /// </summary>
    public string? HarnessExtra { get; set; }

    public List<AgentTool> Tools { get; set; } = [];
    public List<AgentProject> Projects { get; set; } = [];
    public List<AgentMcpEndpoint> McpEndpoints { get; set; } = [];
    public List<AgentInstruction> Instructions { get; set; } = [];
    public List<AgentEnvRef> EnvRefs { get; set; } = [];
    public List<AgentTelegramUser> TelegramUsers { get; set; } = [];
    public List<AgentTelegramGroup> TelegramGroups { get; set; } = [];
    public List<AgentNetwork> Networks { get; set; } = [];
    public List<AgentCredentialMount> CredentialMounts { get; set; } = [];

    // ── Container provisioning ────────────────────────────────────────────────

    /// <summary>
    /// When true, /var/run/docker.sock is bind-mounted into the container.
    /// Default false (safe-by-default). Only enable for agents that manage containers.
    /// </summary>
    public bool MountDockerSock { get; set; } = false;

    /// <summary>
    /// Set once when the first-provision welcome DM is dispatched. Acts as the primary
    /// idempotency gate — if non-null, the welcome is never re-sent on reprovision.
    /// </summary>
    public DateTime? WelcomeSentAt { get; set; }

    // ── Access-request flow ──────────────────────────────────────────────────

    /// <summary>
    /// When true, DMs from unknown users trigger an access request routed to
    /// the CTO agent (resolved from FLEET_CTO_AGENT at runtime). Default false (silent drop).
    /// </summary>
    public bool CanReceiveChatRequests { get; set; } = false;

    /// <summary>
    /// Optional reply sent to the requesting user when their access request is queued.
    /// Null → built-in default fallback in the agent.
    /// </summary>
    public string? RequestReceivedMessage { get; set; }
}

public class AgentNetwork
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public required string NetworkName { get; set; }

    public Agent Agent { get; set; } = null!;
}

public class AgentTelegramUser
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public long UserId { get; set; }

    public Agent Agent { get; set; } = null!;
}

public class AgentTelegramGroup
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public long GroupId { get; set; }

    public Agent Agent { get; set; } = null!;
}

public class AgentTool
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public required string ToolName { get; set; }
    public bool IsEnabled { get; set; } = true;

    public Agent Agent { get; set; } = null!;
}

public class AgentProject
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public required string ProjectName { get; set; }

    public Agent Agent { get; set; } = null!;
}

public class AgentMcpEndpoint
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public required string McpName { get; set; }
    public required string Url { get; set; }
    public required string TransportType { get; set; }

    public Agent Agent { get; set; } = null!;
}

public class Instruction
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public List<InstructionVersion> Versions { get; set; } = [];
    public List<AgentInstruction> AgentInstructions { get; set; } = [];
}

public class InstructionVersion
{
    public int Id { get; set; }
    public int InstructionId { get; set; }
    public int VersionNumber { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? Reason { get; set; }

    public Instruction Instruction { get; set; } = null!;
}

public class AgentInstruction
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public int InstructionId { get; set; }
    public int LoadOrder { get; set; }

    public Agent Agent { get; set; } = null!;
    public Instruction Instruction { get; set; } = null!;
}

public class AgentEnvRef
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public required string EnvKeyName { get; set; }

    public Agent Agent { get; set; } = null!;
}

// ─── Project Contexts ─────────────────────────────────────────────────────────

public class ProjectContext
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ProjectContextVersion> Versions { get; set; } = [];
}

public class ProjectContextVersion
{
    public int Id { get; set; }
    public int ProjectContextId { get; set; }
    public int VersionNumber { get; set; }
    public required string Content { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? Reason { get; set; }

    public ProjectContext ProjectContext { get; set; } = null!;
}

// ─── Repositories ─────────────────────────────────────────────────────────────

/// <summary>A managed GitHub repository used as a picker source in workflow start forms.</summary>
public class Repository
{
    public int Id { get; set; }

    /// <summary>Short name used as a key (e.g. "fleet").</summary>
    public required string Name { get; set; }

    /// <summary>Full GitHub org/repo string (e.g. "your-org/your-repo").</summary>
    public required string FullName { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Universal Workflow Engine ────────────────────────────────────────────────

/// <summary>
/// A workflow definition stored in the DB and interpreted by the universal workflow engine.
/// Replaces (or co-exists with) a statically-compiled C# workflow class.
/// </summary>
public class WorkflowDefinition
{
    public int Id { get; set; }

    /// <summary>Temporal workflow type name (e.g. "TaskDelegationWorkflow").</summary>
    public required string Name { get; set; }

    public required string Namespace { get; set; }
    public required string TaskQueue { get; set; }
    public string? Description { get; set; }

    /// <summary>JSON step tree (root StepDefinition). Stored as MySQL JSON column.</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "json")]
    public required string Definition { get; set; }

    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    public List<WorkflowDefinitionVersion> Versions { get; set; } = [];
}

/// <summary>Version history for a workflow definition (enables rollback).</summary>
public class WorkflowDefinitionVersion
{
    public int Id { get; set; }
    public int WorkflowDefinitionId { get; set; }
    public int Version { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "json")]
    public required string Definition { get; set; }

    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;
}

// ─── Credential Files ─────────────────────────────────────────────────────────

public class CredentialFile
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string Type { get; set; } = "generic"; // "ssh-private-key" | "certificate" | "generic"
    public required string FileName { get; set; }  // actual filename on disk
    public required string FilePath { get; set; }  // absolute path
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AgentCredentialMount> Mounts { get; set; } = [];
}

public class AgentCredentialMount
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public int CredentialFileId { get; set; }
    public required string MountPath { get; set; }   // e.g. /workspace/.ssh/server.key
    public string Mode { get; set; } = "ro";          // "ro" | "rw"

    public Agent Agent { get; set; } = null!;
    public CredentialFile CredentialFile { get; set; } = null!;
}




// ─── Agent Project Access (memory ACL) ───────────────────────────────────────

/// <summary>
/// Maps an agent to the memory projects it is allowed to read.
/// A row with Project = "*" grants wildcard access (all projects).
/// Agent names and project names are stored lowercase, whitespace-trimmed.
/// </summary>
public class AgentProjectAccess
{
    public required string AgentName { get; set; }
    public required string Project { get; set; }

    /// <summary>
    /// Provenance of this grant. <see cref="AgentProjectAccessSource.Assignment"/> rows are owned by
    /// the project-assignment hook and are the only rows it may remove;
    /// <see cref="AgentProjectAccessSource.Manual"/> rows were created by an operator and survive
    /// unassignment. See <see cref="Services.AgentProjectAccessSync"/>.
    /// </summary>
    public string Source { get; set; } = AgentProjectAccessSource.Manual;
}

/// <summary>Valid values for <see cref="AgentProjectAccess.Source"/>.</summary>
public static class AgentProjectAccessSource
{
    /// <summary>Written and removed by the project-assignment hook.</summary>
    public const string Assignment = "assignment";

    /// <summary>Created by an operator; never removed by the assignment hook.</summary>
    public const string Manual = "manual";
}

// ─── Output Styles ────────────────────────────────────────────────────────────

/// <summary>
/// A named Claude Code output style, stored once and rendered per provider.
/// </summary>
/// <remarks>
/// <para>
/// Tone and register carried in the appended system prompt lose to Claude Code's own
/// <c>Tone and style</c> and <c>Text output</c> guidance, because the model picks arbitrarily
/// between two rules that contradict. An output style is re-asserted during the conversation,
/// near the point of generation, which is why the identical text wins from here and loses from
/// there.
/// </para>
/// <para>
/// Styles are a Claude Code feature, so this row is the single source for every provider:
/// claude resolves it as a style file, codex and gemini get the same text inlined into their
/// prompt. A style that applied to one provider and vanished for the others would be worse than
/// no style at all.
/// </para>
/// </remarks>
public class OutputStyle
{
    /// <summary>Style name — the value an agent's <see cref="Agent.OutputStyle"/> holds.</summary>
    [MaxLength(100)]
    public required string Name { get; set; }

    /// <summary>
    /// The full style file, YAML frontmatter included. Written verbatim for claude; the
    /// frontmatter is stripped before the body is inlined for the other providers.
    /// </summary>
    public required string Body { get; set; }

    /// <summary>One line describing what the style is for. Operator-facing only.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }
}

// ─── Epic Grants (#436) ───────────────────────────────────────────────────────

/// <summary>
/// One human-approved, run-scoped epic grant: while it is active and unexpired, the configured CTO
/// agent may send <c>approved</c> on the in-scope delegable gates of runs linked to one driver run.
/// </summary>
/// <remarks>
/// <see cref="ScopeJson"/> is the request body exactly as the operator submitted it and is never
/// edited; <see cref="ScopeSha256"/> is its lowercase hex SHA-256 and is re-checked on every
/// decision. The driver columns are derived copies for listing. The only mutation after creation is
/// revocation (<see cref="Status"/>, <see cref="RevokedAt"/>, <see cref="RevokeReason"/>), and only
/// from <see cref="EpicGrantStatus.Active"/>. Expiry is computed, never written.
/// </remarks>
public class EpicGrant
{
    /// <summary>
    /// Grant id, stored <c>char(36)</c>. A <see cref="Guid"/> rather than a string because Pomelo
    /// reserves the <c>char(36)</c> store type for GUIDs; the API carries it in its <c>D</c> form.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary><see cref="EpicGrantStatus.Active"/> or <see cref="EpicGrantStatus.Revoked"/>.</summary>
    public required string Status { get; set; }

    public string Mode { get; set; } = "delegated";
    public string? PreflightJson { get; set; }
    public string? PreflightSha256 { get; set; }

    public required string ScopeJson { get; set; }
    public required string ScopeSha256 { get; set; }
    public required string DriverNamespace { get; set; }
    public required string DriverWorkflowId { get; set; }
    public required string DriverRunId { get; set; }

    /// <summary>The configured <c>FLEET_CTO_AGENT</c> when the grant was created.</summary>
    public required string CtoAgent { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
}

/// <summary>Permanent server-owned issue binding; inserted only with a decision reservation.</summary>
public class EpicGrantBinding
{
    public long Id { get; set; }
    public Guid GrantId { get; set; }
    public required string Namespace { get; set; }
    public required string Repo { get; set; }
    public long Issue { get; set; }
    public required string WorkflowId { get; set; }
    public required string RunId { get; set; }
    public required string ReviewRef { get; set; }
    public DateTime CreatedAt { get; set; }
    public EpicGrant Grant { get; set; } = null!;
}

/// <summary>Permanent budget record for an exact run, including unsuccessful sends.</summary>
public class EpicGrantRun
{
    public long Id { get; set; }
    public Guid GrantId { get; set; }
    public required string Namespace { get; set; }
    public required string WorkflowId { get; set; }
    public required string RunId { get; set; }
    public required string WorkflowType { get; set; }
    public required string Repo { get; set; }
    public long? Issue { get; set; }
    public int MaxFixRound { get; set; }
    public DateTime FirstReservedAt { get; set; }
    public EpicGrant Grant { get; set; } = null!;
}

/// <summary>Valid values for <see cref="EpicGrant.Status"/>.</summary>
public static class EpicGrantStatus
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

/// <summary>
/// The permanent at-most-once record of one delegated decision per gate visit.
/// </summary>
/// <remarks>
/// The UNIQUE key (<see cref="Namespace"/>, <see cref="WorkflowId"/>, <see cref="RunId"/>,
/// <see cref="Gate"/>, <see cref="VisitId"/>) is the only guard against a second decision for the
/// same visit. A row is inserted <see cref="EpicGrantDecisionStatus.Reserved"/> and may move only
/// to <see cref="EpicGrantDecisionStatus.Sent"/> or <see cref="EpicGrantDecisionStatus.SendFailed"/>.
/// It is never deleted, released or set back to reserved: a crash leaves it reserved, which means
/// "delivery unknown", and a new decision needs a new visit.
/// </remarks>
public class EpicGrantDecision
{
    public long Id { get; set; }
    public Guid GrantId { get; set; }
    public required string Namespace { get; set; }
    public required string WorkflowId { get; set; }
    public required string RunId { get; set; }
    public required string Gate { get; set; }
    public required string VisitId { get; set; }
    public required string ArtifactRef { get; set; }
    public required string Evidence { get; set; }
    public required string Caller { get; set; }

    /// <summary>One of <see cref="EpicGrantDecisionStatus"/>.</summary>
    public required string Status { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public EpicGrant Grant { get; set; } = null!;
}

/// <summary>Valid values for <see cref="EpicGrantDecision.Status"/>.</summary>
public static class EpicGrantDecisionStatus
{
    public const string Reserved = "reserved";
    public const string Sent = "sent";
    public const string SendFailed = "send_failed";
}

// ─── Credentials Audit ────────────────────────────────────────────────────────

/// <summary>
/// Audit trail for credential saves. Records which key changed and when.
/// The value itself is NEVER stored — audit rows record only the fact of a change.
/// </summary>
public class CredentialsAudit
{
    public long Id { get; set; }

    [MaxLength(255)]
    public required string KeyName { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(255)]
    public string Actor { get; set; } = "CEO";
}

// ─── Workflow failure accountability (#476) ───────────────────────────────────
// Additive tables. Classification/stage/state strings use the constants in
// Fleet.Shared.WorkflowOutcomeContracts. Only classification == confirmed_failure
// (including proven Completed business failures with a business_* reason code) can
// enter coordinator batches. Unresolved failures, cause gaps and health rows are
// never auto-deleted.

/// <summary>One exact run <c>(namespace, workflowId, runId)</c>; never deduplicated by WorkflowId or name.</summary>
public class WorkflowOutcomeRun
{
    public Guid Id { get; set; }
    public required string Namespace { get; set; }
    public required string WorkflowId { get; set; }
    public required string RunId { get; set; }

    /// <summary>SHA-256 of the exact identity; the originals above are always compared on a match.</summary>
    public required string IdentityHash { get; set; }

    public string? FirstExecutionRunId { get; set; }
    public string? PreviousRunId { get; set; }
    public string? NextRunId { get; set; }
    public string? ParentWorkflowId { get; set; }
    public string? ParentRunId { get; set; }
    public required string WorkflowType { get; set; }

    /// <summary>From this run's own LoadWorkflowDefinition result, never the current DB row.</summary>
    public string? DefinitionName { get; set; }
    public int? DefinitionVersion { get; set; }
    public string? DefinitionHash { get; set; }

    public DateTime? StartTime { get; set; }
    public DateTime? CloseTime { get; set; }

    /// <summary>Temporal execution status name, e.g. <c>Running</c>, <c>Failed</c>, <c>ContinuedAsNew</c>.</summary>
    public required string Status { get; set; }

    public long? TerminalEventId { get; set; }
    public string? TerminalEventType { get; set; }
    public string? TerminalEventHash { get; set; }

    /// <summary>One of <c>WorkflowOutcomeClassifications</c>.</summary>
    public required string Classification { get; set; }
    public string? ReasonCode { get; set; }
    public string? CauseRefsJson { get; set; }
    public string? ClassifierId { get; set; }
    public string? ClassifierHash { get; set; }

    public string? ProjectName { get; set; }
    public string? ProjectEvidenceJson { get; set; }
    public string? OwnershipPolicyHash { get; set; }

    /// <summary>Material failure version; increments only on a material failure change and then requires a new ack.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Coalescing key for a chain/cause; each run keeps its own row and version.</summary>
    public Guid? FailureIncidentId { get; set; }

    public string? SourceTaskRefsJson { get; set; }
    public string? ArtifactRefsJson { get; set; }
    public string? Repo { get; set; }
    public long? IssueNumber { get; set; }
    public long? PrNumber { get; set; }
    public string? InputHash { get; set; }

    /// <summary>Recovery owner (configured coordinator), or null for unassigned escalation.</summary>
    public string? Coordinator { get; set; }

    /// <summary>Original task owner (target agent), separate from the coordinator.</summary>
    public string? TaskOwner { get; set; }

    /// <summary>complete, pending, missing, unverified or quarantined.</summary>
    public required string EvidenceState { get; set; }

    /// <summary>Data-protection-encrypted evidence; never raw credentials.</summary>
    public byte[]? PayloadCiphertext { get; set; }
    public string? PayloadKeyPurpose { get; set; }

    public bool Quarantined { get; set; }
    public string? QuarantineReason { get; set; }

    public DateTime DetectedAt { get; set; }
    public DateTime StoredAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClassificationPendingSince { get; set; }

    /// <summary>TTL for nonfailure metadata only; null for failures, cause gaps and unresolved rows.</summary>
    public DateTime? MetadataExpiresAt { get; set; }

    /// <summary>Set when the failure item is closed by an explicit finish/close decision.</summary>
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Append-only terminal/state observation; an older observation never overwrites a newer one.</summary>
public class WorkflowOutcomeObservation
{
    public long Id { get; set; }
    public Guid RunRowId { get; set; }
    public long EventId { get; set; }
    public required string EventType { get; set; }
    public required string EventHash { get; set; }
    public required string Status { get; set; }
    public required string Classification { get; set; }
    public string? ReasonCode { get; set; }
    public bool Contradicting { get; set; }
    public DateTime ObservedAt { get; set; }
    public WorkflowOutcomeRun Run { get; set; } = null!;
}

/// <summary>Namespace scan window cursor. Page token and seen ids commit atomically. The lease row uses <c>WindowKind = "lease"</c>.</summary>
public class WorkflowOutcomeCheckpoint
{
    public long Id { get; set; }
    public required string Namespace { get; set; }

    /// <summary>recent, full, enrollment or lease.</summary>
    public required string WindowKind { get; set; }

    /// <summary>Inclusive lower StartTime bound, or null (full sweep has none).</summary>
    public DateTime? WindowStart { get; set; }

    /// <summary>Exclusive upper StartTime bound.</summary>
    public DateTime? WindowEnd { get; set; }

    /// <summary>Stable key of (namespace, kind, start); unique.</summary>
    public required string WindowKey { get; set; }

    public required string QueryHash { get; set; }
    public string? PageToken { get; set; }
    public long SeenCount { get; set; }
    public bool Complete { get; set; }
    public DateTime? PassStartedAt { get; set; }
    public DateTime? PassCompletedAt { get; set; }
    public string? LastError { get; set; }
    public long? RetentionSeconds { get; set; }
    public string? ArchivalState { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public string? EnrolledCoordinator { get; set; }
    public string? PolicyHashes { get; set; }
    public string? LeaseOwner { get; set; }
    public long LeaseEpoch { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>One coordinator delivery batch. Membership is immutable per delivery generation.</summary>
public class WorkflowOutcomeBatch
{
    public Guid Id { get; set; }
    public required string Coordinator { get; set; }
    public long CoordinatorGeneration { get; set; }
    public int Priority { get; set; }

    /// <summary>pending, delivered, partially_acknowledged, acknowledged or superseded.</summary>
    public required string State { get; set; }

    /// <summary>Random per-batch nonce (not a secret).</summary>
    public required string Nonce { get; set; }
    public required string ItemVersionsHash { get; set; }
    public string? ReceiptHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LeasedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? AckedAt { get; set; }
    public List<WorkflowOutcomeBatchItem> Items { get; set; } = [];
}

/// <summary>Immutable <c>(outcomeId, version)</c> membership with individual ack state.</summary>
public class WorkflowOutcomeBatchItem
{
    public long Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid OutcomeId { get; set; }
    public int OutcomeVersion { get; set; }
    public long DeliveryGeneration { get; set; }

    /// <summary>pending, delivered, acknowledged or superseded.</summary>
    public required string State { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? AckedAt { get; set; }
    public string? AckedBy { get; set; }

    /// <summary>coordinator or operator.</summary>
    public string? AckSource { get; set; }
    public WorkflowOutcomeBatch Batch { get; set; } = null!;
}

/// <summary>Immutable audit of one record-only decision; never an ack-as-decision shortcut.</summary>
public class WorkflowOutcomeDecision
{
    public Guid Id { get; set; }
    public Guid OutcomeId { get; set; }
    public int ExpectedVersion { get; set; }
    public required string Operation { get; set; }
    public required string DecidedBy { get; set; }

    /// <summary>coordinator or operator.</summary>
    public required string DecidedVia { get; set; }
    public required string Owner { get; set; }
    public required string Reason { get; set; }
    public required string EvidenceRefsJson { get; set; }
    public string? TaskRef { get; set; }
    public string? ArtifactRef { get; set; }
    public string? StopProofRef { get; set; }
    public DateTime? DueAt { get; set; }
    public string? AuthorityRef { get; set; }
    public Guid? BatchId { get; set; }

    /// <summary>The coordinator generation the decision was made in; a resend from another generation is never its duplicate.</summary>
    public long? CoordinatorGeneration { get; set; }

    /// <summary>accepted or rejected.</summary>
    public required string Status { get; set; }
    public string? RejectReason { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Permanent reservation for one recovery attempt; ambiguity never releases it.</summary>
public class WorkflowRecoveryOperation
{
    public Guid Id { get; set; }
    public Guid DecisionId { get; set; }
    public Guid OutcomeId { get; set; }
    public int OutcomeVersion { get; set; }
    public required string Operation { get; set; }
    public required string SourceNamespace { get; set; }
    public required string SourceWorkflowId { get; set; }
    public required string SourceRunId { get; set; }
    public string? SourceTaskHash { get; set; }
    public string? SourceArtifactHash { get; set; }
    public required string TargetNamespace { get; set; }
    public string? TargetWorkflowType { get; set; }
    public string? TargetTaskQueue { get; set; }
    public string? TargetAgent { get; set; }
    public string? InputSha256 { get; set; }

    /// <summary><c>recovery-&lt;OperationUUID&gt;</c>, started with RejectDuplicate.</summary>
    public required string ReplacementWorkflowId { get; set; }
    public required string Scope { get; set; }
    public required string Coordinator { get; set; }
    public required string StopProofRefsJson { get; set; }
    public required string AuthorityRef { get; set; }

    /// <summary>One of <c>WorkflowRecoveryStates</c>.</summary>
    public required string State { get; set; }
    public required string ReservedBy { get; set; }
    public DateTime ReservedAt { get; set; }
    public DateTime? DispatchingAt { get; set; }
    public string? ResultRunId { get; set; }
    public string? ReceiptEventRef { get; set; }
    public string? ReceiptJson { get; set; }
    public string? BlockedReason { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A reply whose waiter was gone; never overwrites the current attempt and is never auto-accepted.</summary>
public class WorkflowOutcomeLateResult
{
    public Guid Id { get; set; }

    /// <summary>Empty string when the namespace is unknown (MySQL unique keys treat NULLs as distinct).</summary>
    public required string Namespace { get; set; }
    public required string TaskId { get; set; }
    public required string ResultHash { get; set; }
    public required string MessageId { get; set; }

    /// <summary>stored, quarantined, accepted or discarded.</summary>
    public required string Status { get; set; }
    public required string ResultStatus { get; set; }
    public byte[]? Ciphertext { get; set; }
    public int SizeBytes { get; set; }
    public string? SourceCorrelation { get; set; }
    public Guid? OutcomeId { get; set; }
    public bool Quarantined { get; set; }
    public string? QuarantineReason { get; set; }
    public int Attempt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime StoredAt { get; set; }
}

/// <summary>Deduplicated component/outage incident; never a workflow or notification.</summary>
public class WorkflowOutcomeHealth
{
    public long Id { get; set; }
    public required string Component { get; set; }
    public required string OutageKey { get; set; }

    /// <summary>open or resolved.</summary>
    public required string State { get; set; }
    public required string Reason { get; set; }
    public string? Namespace { get; set; }
    public DateTime? GapStart { get; set; }
    public DateTime? GapEnd { get; set; }
    public long KnownRunCount { get; set; }
    public bool UnknownRunCount { get; set; }
    public string? CauseReviewRefsJson { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
