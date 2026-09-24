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
    /// Origin of a local Anthropic-compatible server, stored canonical, or <c>null</c> (#340). Set on
    /// a claude agent, it runs Claude Code against that server with no Claude credential mounted —
    /// see <see cref="ClaudeLocalModel"/>. <c>null</c> is the rollout switch and the rollback.
    /// </summary>
    [MaxLength(500)]
    public string? AnthropicBaseUrl { get; set; }

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

    /// <summary>
    /// How this project's context reaches the agent — <see cref="ProjectContextMode.Full"/> (the
    /// full context resident in the system prompt, as before cards existed) or
    /// <see cref="ProjectContextMode.Card"/> (a compact card resident, the full context attached to
    /// routed turns). The assignment, not the project, carries the mode.
    /// </summary>
    public string ContextMode { get; set; } = ProjectContextMode.Full;

    public Agent Agent { get; set; } = null!;
}

/// <summary>Valid values for <see cref="AgentProject.ContextMode"/>.</summary>
public static class ProjectContextMode
{
    /// <summary>The canonical full context is resident in the system prompt.</summary>
    public const string Full = "full";

    /// <summary>The project card is resident; the full context is attached to routed turns.</summary>
    public const string Card = "card";

    /// <summary>True for exactly <c>full</c> or <c>card</c> (ordinal, lower-case as stored).</summary>
    public static bool IsValid(string? value) => value is Full or Card;
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

    /// <summary>Current <see cref="ProjectContextCardVersion"/> number, or <c>null</c> when the project has no card.</summary>
    public int? CurrentCardVersion { get; set; }

    public List<ProjectContextVersion> Versions { get; set; } = [];
    public List<ProjectContextCardVersion> CardVersions { get; set; } = [];
    public List<ProjectContextRoute> Routes { get; set; } = [];
}

/// <summary>
/// One version of a project's card — the compact text resident for card-mode assignments.
/// <see cref="BasedOnFullVersion"/> records which full context version the author wrote it for.
/// </summary>
public class ProjectContextCardVersion
{
    public int Id { get; set; }
    public int ProjectContextId { get; set; }
    public int VersionNumber { get; set; }
    public required string Content { get; set; }
    public int BasedOnFullVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? Reason { get; set; }

    public ProjectContext ProjectContext { get; set; } = null!;
}

/// <summary>
/// A deterministic routing signal that attaches a project's full context to a turn. Keyed on the
/// context's <c>Id</c>, never its name, so a route cannot outlive or detach from its context.
/// </summary>
public class ProjectContextRoute
{
    public int Id { get; set; }
    public int ProjectContextId { get; set; }

    /// <summary>One of <see cref="RouteSignalKind"/>.</summary>
    public required string SignalKind { get; set; }

    /// <summary>Normalised signal value — see <see cref="RouteSignalKind.Normalize"/>.</summary>
    public required string SignalValue { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    public ProjectContext ProjectContext { get; set; } = null!;
}

/// <summary>Valid values for <see cref="ProjectContextRoute.SignalKind"/>, and their write-time rules.</summary>
public static class RouteSignalKind
{
    public const string Repo = "repo";
    public const string Workflow = "workflow";
    public const string Chat = "chat";

    public const int MaxValueLength = 256;

    private static readonly System.Text.RegularExpressions.Regex RepoPattern =
        new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsValidKind(string? kind) => kind is Repo or Workflow or Chat;

    /// <summary>
    /// Validates <paramref name="value"/> for <paramref name="kind"/> and returns the stored form:
    /// <c>repo</c> lower-case <c>owner/name</c>; <c>workflow</c> exact; <c>chat</c> the decimal
    /// string of a signed 64-bit integer. Returns <c>null</c> and an error message when invalid.
    /// </summary>
    public static string? Normalize(string? kind, string? value, out string? error)
    {
        error = null;
        if (!IsValidKind(kind))
        {
            error = $"kind must be one of: {Repo}, {Workflow}, {Chat}";
            return null;
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "value must be non-blank";
            return null;
        }
        if (value.Length > MaxValueLength)
        {
            error = $"value must be at most {MaxValueLength} characters";
            return null;
        }

        switch (kind)
        {
            case Repo:
                if (!RepoPattern.IsMatch(value))
                {
                    error = "repo value must be owner/name ([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)";
                    return null;
                }
                return value.ToLowerInvariant();
            case Chat:
                if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var chatId))
                {
                    error = "chat value must be a signed 64-bit integer";
                    return null;
                }
                return chatId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                return value;
        }
    }
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
