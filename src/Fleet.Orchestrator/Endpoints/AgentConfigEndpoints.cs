using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Fleet.Orchestrator.Helpers;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The full DB-config REST surface for one agent: <c>GET/PUT /api/agents/{{name}}/config</c>.
/// </summary>
/// <remarks>
/// Lifted verbatim from <c>Program.cs</c> (#357) so the endpoint tests can run the production
/// handlers over real HTTP instead of a copy — the same pattern as the other endpoint classes.
/// The PUT handler's unused <c>AgentRegistry</c> parameter was dropped in the move.
/// </remarks>
public static class AgentConfigEndpoints
{
    public static WebApplication MapAgentConfigEndpoints(this WebApplication app)
    {
// REST: get full DB config for an agent (all fields + related tables)
app.MapGet("/api/agents/{name}/config", async (string name, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var agent = await db.Agents
        .Include(a => a.Tools.OrderBy(t => t.ToolName))
        .Include(a => a.Projects.OrderBy(p => p.ProjectName))
        .Include(a => a.McpEndpoints.OrderBy(e => e.McpName))
        .Include(a => a.Networks.OrderBy(n => n.NetworkName))
        .Include(a => a.EnvRefs.OrderBy(r => r.EnvKeyName))
        .Include(a => a.TelegramUsers.OrderBy(u => u.UserId))
        .Include(a => a.TelegramGroups.OrderBy(g => g.GroupId))
        .Include(a => a.Instructions.OrderBy(i => i.LoadOrder))
            .ThenInclude(ai => ai.Instruction)
        .AsSplitQuery()
        .AsNoTracking()
        .FirstOrDefaultAsync(a => a.Name == name);

    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

    return Results.Ok(new
    {
        agent.Name,
        agent.Model,
        agent.MemoryLimitMb,
        agent.IsEnabled,
        agent.Image,
        agent.PermissionMode,
        agent.MaxTurns,
        agent.WorkDir,
        agent.ProactiveIntervalMinutes,
        agent.GroupListenMode,
        agent.GroupDebounceSeconds,
        agent.WarmupTimeoutSeconds,
        agent.ShortName,
        agent.ShowStats,
        agent.PrefixMessages,
        FormattingMode = (byte)agent.FormattingMode,
        agent.SuppressToolMessages,
        agent.TelegramSendOnly,
        agent.Effort,
        agent.JsonSchema,
        agent.AgentsJson,
        agent.HostPort,
        agent.AutoMemoryEnabled,
        agent.Provider,
        agent.CodexSandboxMode,
        agent.OutputStyle,
        agent.AnthropicBaseUrl,
        agent.CanReceiveChatRequests,
        agent.RequestReceivedMessage,
        agent.MountDockerSock,
        Tools = agent.Tools.Select(t => new { t.ToolName, t.IsEnabled }),
        Projects = agent.Projects.Select(p => p.ProjectName),
        McpEndpoints = agent.McpEndpoints.Select(e => new { e.McpName, e.Url, e.TransportType }),
        Networks = agent.Networks.Select(n => n.NetworkName),
        EnvRefs = agent.EnvRefs.Select(r => r.EnvKeyName),
        TelegramUsers = agent.TelegramUsers.Select(u => u.UserId),
        TelegramGroups = agent.TelegramGroups.Select(g => g.GroupId),
        Instructions = agent.Instructions.Select(i => new { i.Instruction.Name, i.LoadOrder }),
    });
});

// REST: update agent DB config (all scalar fields + replace-all for related tables)
app.MapPut("/api/agents/{name}/config", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory, SetupService setupService, AgentConfigPublisherService publisher, IAclChangeNotifier aclNotifier) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<AgentConfigUpdateRequest>();
    if (body is null)
        return Results.BadRequest(new { error = "Invalid request body" });

    var agent = await db.Agents
        .Include(a => a.Tools)
        .Include(a => a.Projects)
        .Include(a => a.McpEndpoints)
        .Include(a => a.Networks)
        .Include(a => a.EnvRefs)
        .Include(a => a.TelegramUsers)
        .Include(a => a.TelegramGroups)
        .Include(a => a.Instructions)
        .AsSplitQuery()
        .FirstOrDefaultAsync(a => a.Name == name);

    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

    // Scalar fields
    if (body.Model is not null) agent.Model = body.Model;
    if (body.MemoryLimitMb is not null) agent.MemoryLimitMb = body.MemoryLimitMb.Value;
    if (body.IsEnabled is not null) agent.IsEnabled = body.IsEnabled.Value;
    if (body.Image is not null) agent.Image = body.Image == "" ? null : body.Image;
    if (body.PermissionMode is not null)
    {
        if (string.Equals(body.PermissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new
            {
                error = "PermissionMode 'bypassPermissions' is not supported — fleet containers run as root and Claude CLI " +
                        "rejects --dangerously-skip-permissions for root processes. Use 'acceptEdits' with an explicit tools list."
            });
        agent.PermissionMode = body.PermissionMode;
    }
    if (body.MaxTurns is not null) agent.MaxTurns = body.MaxTurns.Value;
    if (body.WorkDir is not null) agent.WorkDir = body.WorkDir;
    if (body.ProactiveIntervalMinutes is not null) agent.ProactiveIntervalMinutes = body.ProactiveIntervalMinutes.Value;
    if (body.GroupListenMode is not null) agent.GroupListenMode = body.GroupListenMode;
    if (body.GroupDebounceSeconds is not null) agent.GroupDebounceSeconds = body.GroupDebounceSeconds.Value;
    if (body.WarmupTimeoutSeconds is not null)
    {
        // Rejected, never clamped (#357 MUST NOT): a hidden correction makes live configuration unauditable.
        if (WarmupTimeout.DescribeFault(body.WarmupTimeoutSeconds.Value) is { } warmupFault)
            return Results.BadRequest(new { error = warmupFault });
        agent.WarmupTimeoutSeconds = body.WarmupTimeoutSeconds.Value;
    }
    if (body.ShortName is not null) agent.ShortName = string.IsNullOrWhiteSpace(body.ShortName) ? agent.Name : body.ShortName.Trim();
    if (body.ShowStats is not null) agent.ShowStats = body.ShowStats.Value;
    if (body.PrefixMessages is not null) agent.PrefixMessages = body.PrefixMessages.Value;
    if (body.FormattingMode is not null) agent.FormattingMode = (Fleet.Shared.FormattingMode)body.FormattingMode.Value;
    if (body.SuppressToolMessages is not null) agent.SuppressToolMessages = body.SuppressToolMessages.Value;
    if (body.TelegramSendOnly is not null) agent.TelegramSendOnly = body.TelegramSendOnly.Value;
    if (body.Effort is not null) agent.Effort = body.Effort == "" ? null : body.Effort;
    if (body.JsonSchema is not null) agent.JsonSchema = body.JsonSchema == "" ? null : body.JsonSchema;
    if (body.AgentsJson is not null) agent.AgentsJson = body.AgentsJson == "" ? null : body.AgentsJson;
    if (body.AutoMemoryEnabled is not null) agent.AutoMemoryEnabled = body.AutoMemoryEnabled.Value;
    if (body.Provider is not null) agent.Provider = body.Provider;
    if (body.CodexSandboxMode is not null)
    {
        var (sandboxError, sandboxValue) = AgentPatchHelpers.MapCodexSandboxMode(body.CodexSandboxMode);
        if (sandboxError is not null)
            return Results.BadRequest(new { error = sandboxError });
        agent.CodexSandboxMode = sandboxValue;
    }
    if (body.CanReceiveChatRequests is not null) agent.CanReceiveChatRequests = body.CanReceiveChatRequests.Value;
    if (body.RequestReceivedMessage is not null)
    {
        if (body.RequestReceivedMessage.Length > 500)
            return Results.BadRequest(new { error = $"RequestReceivedMessage exceeds 500-character limit ({body.RequestReceivedMessage.Length} chars)." });
        agent.RequestReceivedMessage = body.RequestReceivedMessage == "" ? null : body.RequestReceivedMessage;
    }
    if (body.MountDockerSock is not null) agent.MountDockerSock = body.MountDockerSock.Value;
    if (body.OutputStyle is not null)
    {
        // Empty string clears, matching effort and codex_sandbox_mode. A name is checked against
        // output_styles HERE rather than at provision time, so the operator finds out while they
        // are still looking at the agent instead of at a failed reprovision.
        var styleName = body.OutputStyle.Trim();
        if (styleName.Length > 0 && !await db.OutputStyles.AnyAsync(s => s.Name == styleName))
            return Results.BadRequest(new { error = $"Output style '{styleName}' does not exist." });
        agent.OutputStyle = styleName.Length == 0 ? null : styleName;
    }
    if (body.AnthropicBaseUrl is not null) agent.AnthropicBaseUrl = body.AnthropicBaseUrl == "" ? null : body.AnthropicBaseUrl;

    // Replace-all for related tables (omit field = keep current)
    if (body.Tools is not null)
    {
        var existingEnabled = agent.Tools
            .ToDictionary(t => t.ToolName, t => t.IsEnabled, StringComparer.OrdinalIgnoreCase);
        db.AgentTools.RemoveRange(agent.Tools);
        agent.Tools = body.Tools
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new AgentTool
            {
                AgentId = agent.Id,
                ToolName = t,
                IsEnabled = existingEnabled.TryGetValue(t, out var was) ? was : true,
            })
            .ToList();
    }

    if (body.Projects is not null)
    {
        db.AgentProjects.RemoveRange(agent.Projects);
        agent.Projects = body.Projects
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new AgentProject { AgentId = agent.Id, ProjectName = p })
            .ToList();
    }

    if (body.McpEndpoints is not null)
    {
        db.AgentMcpEndpoints.RemoveRange(agent.McpEndpoints);
        agent.McpEndpoints = body.McpEndpoints
            .DistinctBy(e => e.McpName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new AgentMcpEndpoint { AgentId = agent.Id, McpName = e.McpName, Url = e.Url, TransportType = e.TransportType })
            .ToList();
    }

    if (body.Networks is not null)
    {
        db.AgentNetworks.RemoveRange(agent.Networks);
        agent.Networks = body.Networks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new AgentNetwork { AgentId = agent.Id, NetworkName = n })
            .ToList();
    }

    if (body.EnvRefs is not null)
    {
        db.AgentEnvRefs.RemoveRange(agent.EnvRefs);
        agent.EnvRefs = body.EnvRefs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => new AgentEnvRef { AgentId = agent.Id, EnvKeyName = r })
            .ToList();
    }

    // Capture old allowlists before mutation so we can publish the diff.
    var oldUserIds  = new HashSet<long>(agent.TelegramUsers.Select(u => u.UserId));
    var oldGroupIds = new HashSet<long>(agent.TelegramGroups.Select(g => g.GroupId));

    if (body.TelegramUsers is not null)
    {
        db.AgentTelegramUsers.RemoveRange(agent.TelegramUsers);
        var tuSet = new HashSet<long>(body.TelegramUsers.Distinct());
        var ownerId = setupService.GetTelegramUserId();
        if (ownerId.HasValue) tuSet.Add(ownerId.Value);
        agent.TelegramUsers = tuSet
            .Select(u => new AgentTelegramUser { AgentId = agent.Id, UserId = u })
            .ToList();
    }

    if (body.TelegramGroups is not null)
    {
        db.AgentTelegramGroups.RemoveRange(agent.TelegramGroups);
        agent.TelegramGroups = body.TelegramGroups
            .Distinct()
            .Select(g => new AgentTelegramGroup { AgentId = agent.Id, GroupId = g })
            .ToList();
    }

    if (body.Instructions is not null)
    {
        // Resolve instruction names to IDs, then upsert the join table
        var instructionNames = body.Instructions
            .Select(i => i.InstructionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var instructions = await db.Instructions
            .Where(i => instructionNames.Contains(i.Name))
            .ToListAsync();

        var missing = instructionNames
            .Where(n => instructions.All(i => !i.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count > 0)
            return Results.BadRequest(new { error = $"Instruction(s) not found: {string.Join(", ", missing)}" });

        db.AgentInstructions.RemoveRange(agent.Instructions);
        agent.Instructions = body.Instructions
            .DistinctBy(i => i.InstructionName, StringComparer.OrdinalIgnoreCase)
            .Select(i =>
            {
                var instr = instructions.First(x => x.Name.Equals(i.InstructionName, StringComparison.OrdinalIgnoreCase));
                return new AgentInstruction { AgentId = agent.Id, InstructionId = instr.Id, LoadOrder = i.LoadOrder };
            })
            .ToList();
    }

    // #340: the agent's resulting state, after every field in the request is applied. A 400 here
    // returns before SaveChanges, so nothing from this request is persisted.
    if (AgentPatchHelpers.FinalizeClaudeLocalModel(agent) is { } localModelFault)
        return Results.BadRequest(new { error = localModelFault });

    await db.SaveChangesAsync();

    // Project assignment IS the memory-ACL grant. Sync after the save so the hook sees the
    // committed assignment list, and only when the caller actually touched projects.
    if (body.Projects is not null)
    {
        await AgentProjectAccessSync.SyncAndBroadcastAsync(
            db, aclNotifier, agent.Name, agent.Projects.Select(p => p.ProjectName));
    }

    // Publish live allowlist diff so running agents update without reprovision.
    var newUserIds  = new HashSet<long>(agent.TelegramUsers.Select(u => u.UserId));
    var newGroupIds = new HashSet<long>(agent.TelegramGroups.Select(g => g.GroupId));
    var addedUsers   = newUserIds.Except(oldUserIds)
        .Select(id => new Fleet.Orchestrator.Services.AddedUserInfo { UserId = id })
        .ToList();
    var removedUsers = oldUserIds.Except(newUserIds).ToList();
    var addedGroups  = newGroupIds.Except(oldGroupIds).ToList();
    var removedGroups = oldGroupIds.Except(newGroupIds).ToList();
    if (addedUsers.Count > 0 || removedUsers.Count > 0 || addedGroups.Count > 0 || removedGroups.Count > 0)
        await publisher.PublishAllowlistUpdateAsync(agent.ShortName,
            addedUsers: addedUsers, removedUserIds: removedUsers,
            addedGroupIds: addedGroups, removedGroupIds: removedGroups);

    return Results.Ok(new { message = $"Agent '{name}' config updated" });
});

        return app;
    }
}

// PATCH semantics: every scalar is optional — omitted fields keep their stored value, related
// tables are replace-all when present. Validation is shared with the MCP patch path through
// Fleet.Shared.WarmupTimeout, so the two write surfaces cannot disagree.
internal sealed record AgentConfigUpdateRequest(

    string? Model,
    int? MemoryLimitMb,
    bool? IsEnabled,
    string? Image,
    string? PermissionMode,
    int? MaxTurns,
    string? WorkDir,
    int? ProactiveIntervalMinutes,
    string? GroupListenMode,
    int? GroupDebounceSeconds,
    int? WarmupTimeoutSeconds,
    string? ShortName,
    bool? ShowStats,
    bool? PrefixMessages,
    byte? FormattingMode,
    bool? SuppressToolMessages,
    bool? TelegramSendOnly,
    string? Effort,
    string? JsonSchema,
    string? AgentsJson,
    bool? AutoMemoryEnabled,
    string? Provider,
    string? CodexSandboxMode,
    string[]? Tools,
    string[]? Projects,
    McpEndpointEntry[]? McpEndpoints,
    string[]? Networks,
    string[]? EnvRefs,
    long[]? TelegramUsers,
    long[]? TelegramGroups,
    InstructionAssignmentEntry[]? Instructions,
    bool? CanReceiveChatRequests,
    string? RequestReceivedMessage,
    bool? MountDockerSock,
    string? OutputStyle,
    string? AnthropicBaseUrl);
