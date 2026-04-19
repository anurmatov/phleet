namespace Fleet.Orchestrator.Data;
using System.ComponentModel.DataAnnotations;

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
    public bool SuppressToolMessages { get; set; } = false;
    public bool TelegramSendOnly { get; set; } = false;
    public string? TtsServiceUrl { get; set; }
    public string? Image { get; set; }
    public string? Effort { get; set; }
    public string? JsonSchema { get; set; }
    public string? AgentsJson { get; set; }
    public int? HostPort { get; set; }
    public bool AutoMemoryEnabled { get; set; } = true;
    [MaxLength(30)]
    public string? CodexSandboxMode { get; set; }

    public List<AgentTool> Tools { get; set; } = [];
    public List<AgentProject> Projects { get; set; } = [];
    public List<AgentMcpEndpoint> McpEndpoints { get; set; } = [];
    public List<AgentInstruction> Instructions { get; set; } = [];
    public List<AgentEnvRef> EnvRefs { get; set; } = [];
    public List<AgentTelegramUser> TelegramUsers { get; set; } = [];
    public List<AgentTelegramGroup> TelegramGroups { get; set; } = [];
    public List<AgentNetwork> Networks { get; set; } = [];
    public List<AgentCredentialMount> CredentialMounts { get; set; } = [];

    /// <summary>
    /// Set once when the first-provision welcome DM is dispatched. Acts as the primary
    /// idempotency gate — if non-null, the welcome is never re-sent on reprovision.
    /// </summary>
    public DateTime? WelcomeSentAt { get; set; }
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


