using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Fleet.Orchestrator.Helpers;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The full DB-config REST surface for one agent: <c>GET/PUT /api/agents/{{name}}/config</c>.
/// </summary>
/// <remarks>
/// Lifted from <c>Program.cs</c> (#357) so the endpoint tests can run the production handlers over
/// real HTTP instead of a copy. The PUT is an adapter over <see cref="AgentConfigWriter"/>: it
/// reads the body, calls the writer and renders the outcome.
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
        agent.JournalEnabled,
        agent.JournalCrossChatEnabled,
        agent.ChatEnabled,
        agent.Provider,
        agent.CodexSandboxMode,
        agent.OutputStyle,
        // #382: one stored value under both names; anthropicBaseUrl is the pre-#382 alias.
        agent.LocalBaseUrl,
        AnthropicBaseUrl = agent.LocalBaseUrl,
        agent.ContextWindow,
        // #460: stored verbatim, so a GET returns exactly what was saved.
        agent.HarnessExtra,
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

// REST: update agent DB config (all scalar fields + replace-all for related tables).
// An adapter: the rules live in AgentConfigWriter, which update_agent_config calls too.
app.MapPut("/api/agents/{name}/config", async (string name, HttpRequest request, AgentConfigWriter writer) =>
{
    AgentConfigUpdateRequest? body;
    try { body = await request.ReadFromJsonAsync<AgentConfigUpdateRequest>(); }

    catch (JsonException ex) { return Results.BadRequest(new { error = AgentPatchHelpers.DescribeBodyFault(ex) }); }
    if (body is null)
        return Results.BadRequest(new { error = "Invalid request body" });

    var outcome = await writer.UpdateAsync(name, body.ToPatch(), WriteCaller.Rest);
    if (outcome.Fault is { } fault)
        return fault.Code switch
        {
            "journal_not_configured" => Results.BadRequest(new { error = fault.Code, detail = fault.Message }),
            "harness_extra_invalid" => Results.BadRequest(AgentPatchHelpers.HarnessExtraBadRequest((HarnessExtraFault)fault.Detail!)),
            _ => WriteHttp.Fault(fault),
        };
    if (outcome.NotDelivered is { } sentence)
        return LiveDelivery.NotDelivered(sentence);

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
    bool? JournalEnabled,
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
    string? AnthropicBaseUrl,
    int? ContextWindow,
    string? LocalBaseUrl = null,
    bool? JournalCrossChatEnabled = null,
    string? HarnessExtra = null,
    bool? ChatEnabled = null)
{
    internal AgentConfigPatch ToPatch() => new()
    {
        Model = Model,
        MemoryLimitMb = MemoryLimitMb,
        IsEnabled = IsEnabled,
        Image = Image,
        PermissionMode = PermissionMode,
        MaxTurns = MaxTurns,
        WorkDir = WorkDir,
        ProactiveIntervalMinutes = ProactiveIntervalMinutes,
        GroupListenMode = GroupListenMode,
        GroupDebounceSeconds = GroupDebounceSeconds,
        WarmupTimeoutSeconds = WarmupTimeoutSeconds,
        ShortName = ShortName,
        ShowStats = ShowStats,
        PrefixMessages = PrefixMessages,
        FormattingMode = FormattingMode,
        SuppressToolMessages = SuppressToolMessages,
        TelegramSendOnly = TelegramSendOnly,
        Effort = Effort,
        JsonSchema = JsonSchema,
        AgentsJson = AgentsJson,
        AutoMemoryEnabled = AutoMemoryEnabled,
        JournalEnabled = JournalEnabled,
        JournalCrossChatEnabled = JournalCrossChatEnabled,
        ChatEnabled = ChatEnabled,
        Provider = Provider,
        CodexSandboxMode = CodexSandboxMode,
        CanReceiveChatRequests = CanReceiveChatRequests,
        RequestReceivedMessage = RequestReceivedMessage,
        MountDockerSock = MountDockerSock,
        OutputStyle = OutputStyle,
        LocalBaseUrl = LocalBaseUrl,
        LocalBaseUrlAlias = AnthropicBaseUrl,
        ContextWindow = ContextWindow,
        HarnessExtra = HarnessExtra,
        Tools = Tools,
        Projects = Projects,
        McpEndpoints = McpEndpoints?.Select(e => new McpEndpointSpec(e.McpName, e.Url, e.TransportType)).ToList(),
        Networks = Networks,
        EnvRefs = EnvRefs,
        TelegramUsers = TelegramUsers,
        TelegramGroups = TelegramGroups,
        Instructions = Instructions?.Select(i => new InstructionAssignment(i.InstructionName, i.LoadOrder)).ToList(),
    };
}
