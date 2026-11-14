using Fleet.Orchestrator.Data;

namespace Fleet.Orchestrator.Services;

public record ContainerSpec(
    string Image,
    long MemoryBytes,
    List<string> Env,
    List<string> Binds,
    List<string> Networks)
{
    /// <summary><c>host:address</c> entries: bound hosts pointed at <c>fleet-secrets</c>.</summary>
    public List<string> ExtraHosts { get; init; } = [];

    /// <summary>The agent's fixed address on the secrets network, when it holds a bound secret.</summary>
    public string? SecretNetworkAddress { get; init; }
}

public record ProvisionPreview(
    string AgentName,
    string ContainerName,
    /// <summary>The actual container name that was found (may differ from ContainerName if compose naming was used).</summary>
    string ResolvedContainerName,
    ContainerSpec Desired,
    ContainerSpec? Actual,
    List<string> Diffs)
{
    public static ProvisionPreview NotFound(string agentName) =>
        new(agentName, "", "", new ContainerSpec("", 0, [], [], []), null, [$"agent '{agentName}' not found in DB"]);
}

public record NetworkEnsureResult(
    List<string> Ensured,
    List<string> Failed)
{
    public bool AllOk => Failed.Count == 0;
}

public record ProvisionResult(string AgentName, bool Success, string Message)
{
    /// <summary>
    /// Why a failure happened, for a caller that has to act on it. Never serialised: the REST and
    /// MCP answers are the three fields above, unchanged.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProvisionFailure Failure { get; init; }

    public static ProvisionResult Ok(string agentName, string message)   => new(agentName, true,  message);
    public static ProvisionResult Fail(string agentName, string message) => new(agentName, false, message);
}

/// <summary>The failures a caller branches on. Everything else is <see cref="Unspecified"/>.</summary>
public enum ProvisionFailure
{
    Unspecified = 0,
    AgentNotFound,
    /// <summary>A deprovision found no container to remove.</summary>
    ContainerNotFound,
}

/// <summary>
/// One provision, decided and rendered: the loaded agent, the container to create and the files to
/// write. Built once by <c>PrepareAsync</c>; <c>CreateAsync</c> reads nothing else.
/// </summary>
internal sealed record PreparedProvision(
    Agent Agent,
    ContainerSpec Spec,
    List<string> ExpandedBinds,
    SecretPlan SecretPlan,
    string BaseDir,
    GeneratedFiles Files);

/// <summary>The contents of the agent's <c>.generated/</c> directory, before any of it is written.</summary>
/// <param name="Style">The agent's resolved output style row, or null. Whether it becomes a file is <see cref="AgentConfigFiles.HasStyleFile"/>.</param>
/// <param name="Instructions">The <c>roles/</c> files, or null when the agent has no instructions.</param>
internal sealed record GeneratedFiles(
    string Appsettings,
    string McpJson,
    string SettingsJson,
    string? CodexRootToml,
    OutputStyle? Style,
    IReadOnlyList<RenderedInstruction>? Instructions,
    ProjectContextPlan ProjectContexts);

/// <summary>One instruction's <c>roles/{Directory}/system.md</c>.</summary>
internal sealed record RenderedInstruction(string Directory, string Content);

internal sealed record JournalProvisioning(
    string? IngestToken,
    IReadOnlyList<long> ExcludedChatIds,
    string? ReadToken,
    bool MediaEnabled = false,
    bool FilesEnabled = false,
    bool SendEnabled = false,
    string? CrossChatToken = null);

/// <summary>One assignment's project context, resolved once per provision.</summary>
/// <param name="ProjectName">The assignment's name — the <c>projects/</c> directory the agent reads.</param>
/// <param name="ContextMd">The <c>context.md</c> body.</param>
internal sealed record ProjectContextAssignment(string ProjectName, string ContextMd);

/// <summary>Every assignment's project context.</summary>
internal sealed record ProjectContextPlan(IReadOnlyList<ProjectContextAssignment> Assignments)
{
    public static readonly ProjectContextPlan Empty = new([]);
}
