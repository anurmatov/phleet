using System.Text.Json;

namespace Fleet.Orchestrator.Data;

/// <summary>Root of seed.json — a list of agent and workflow definitions to seed on first startup.</summary>
public sealed class SeedFile
{
    public List<SeedAgent>              Agents              { get; set; } = [];
    public List<SeedWorkflowDefinition> WorkflowDefinitions { get; set; } = [];
    public List<SeedSchedule>           Schedules           { get; set; } = [];
    public List<SeedRepository>         Repositories        { get; set; } = [];
}

/// <summary>One agent entry in seed.json.</summary>
public sealed class SeedAgent
{
    // Core identity
    public string Name          { get; set; } = "";
    public string DisplayName   { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string Role          { get; set; } = "";
    public string Model         { get; set; } = "sonnet";

    // Resources
    public int  MemoryLimitMb { get; set; } = 4096;
    public bool IsEnabled     { get; set; } = true;

    // Behaviour
    public string PermissionMode           { get; set; } = "acceptEdits";
    public int    MaxTurns                 { get; set; } = 50;
    public string WorkDir                  { get; set; } = "/workspace";
    public int    ProactiveIntervalMinutes { get; set; } = 0;
    public string GroupListenMode          { get; set; } = "off";
    public int    GroupDebounceSeconds     { get; set; } = 15;
    public string ShortName                { get; set; } = "";
    public bool   ShowStats                { get; set; } = false;
    public bool   TelegramSendOnly         { get; set; } = false;
    public bool   PrefixMessages           { get; set; } = false;

    // Associations
    public List<string>      Tools        { get; set; } = [];
    public List<string>      Projects     { get; set; } = [];
    public List<SeedMcp>     McpEndpoints { get; set; } = [];
    public List<string>      EnvRefs      { get; set; } = [];
    public List<string>      Networks     { get; set; } = [];
    public List<long>        TelegramUsers  { get; set; } = [];
    public List<long>        TelegramGroups { get; set; } = [];
}

/// <summary>An MCP server endpoint in seed.json.</summary>
public sealed class SeedMcp
{
    public string Name      { get; set; } = "";
    public string Url       { get; set; } = "";
    public string Transport { get; set; } = "http";
}

/// <summary>One workflow definition entry in seed.json — seeded into the workflow_definitions table.</summary>
public sealed class SeedWorkflowDefinition
{
    public string       Name        { get; set; } = "";
    public string       Namespace   { get; set; } = "fleet";
    public string       TaskQueue   { get; set; } = "fleet";
    public string?      Description { get; set; }

    /// <summary>The UWE step tree as a JSON object. Stored as a JSON string in the DB.</summary>
    public JsonElement  Definition  { get; set; }
}

/// <summary>One repository entry in seed.json — seeded into the repositories table.</summary>
public sealed class SeedRepository
{
    public string Name     { get; set; } = "";
    public string FullName { get; set; } = "";
}

/// <summary>One Temporal schedule entry in seed.json — created in the Temporal server at startup.</summary>
public sealed class SeedSchedule
{
    public string       ScheduleId     { get; set; } = "";
    public string       Namespace      { get; set; } = "fleet";
    public string       WorkflowType   { get; set; } = "";
    public string       TaskQueue      { get; set; } = "fleet";
    public string       CronExpression { get; set; } = "";

    /// <summary>Optional workflow input passed when the schedule fires.</summary>
    public JsonElement? Input          { get; set; }
    public string?      Memo           { get; set; }
}
