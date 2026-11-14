using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Helpers;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The agent-create REST surface: <c>POST /api/agents</c>.
/// </summary>
/// <remarks>
/// An adapter over <see cref="AgentCreator"/>, which <c>create_agent</c> calls too: it reads the
/// body, asks for the agent to be created and provisioned (unless <c>provision: false</c>) and
/// renders the outcome. Every rule, and the order they are checked in, is the creator's.
/// </remarks>
public static class AgentCreateEndpoints
{
    public static WebApplication MapAgentCreateEndpoints(this WebApplication app)
    {
        // REST: create a new agent (insert into DB + provision container)
        app.MapPost("/api/agents", async (HttpRequest request, AgentCreator creator) =>
        {
            CreateAgentRequest? body;
            try { body = await request.ReadFromJsonAsync<CreateAgentRequest>(); }
            catch (JsonException ex) { return Results.BadRequest(new { error = AgentPatchHelpers.DescribeBodyFault(ex) }); }
            if (body is null)
                return Results.BadRequest(new { error = "name is required" });

            var outcome = await creator.CreateAsync(ToNewAgent(body), WriteCaller.Rest);
            if (outcome.Fault is { } fault)
                return fault.Code switch
                {
                    "telegram_not_configured" or "cto_required_first"
                        => Results.Conflict(new { error = fault.Code, message = fault.Message }),
                    "harness_extra_invalid"
                        => Results.BadRequest(AgentPatchHelpers.HarnessExtraBadRequest((HarnessExtraFault)fault.Detail!)),
                    _ => WriteHttp.Fault(fault),
                };

            var created = outcome.Value!;
            var name = created.Name;
            if (created.ProvisionFailure is { } failure)
                return Results.Json(
                    new { message = $"Agent '{name}' created in DB but provisioning failed: {failure}", agentName = name },
                    statusCode: 207);

            return Results.Created($"/api/agents/{name}", new
            {
                message = created.ProvisionRequested
                    ? $"Agent '{name}' created and provisioned"
                    : $"Agent '{name}' created (not provisioned — use POST /api/agents/{name}/reprovision when ready)",
                agentName = name,
            });
        });

        return app;
    }

    private static NewAgent ToNewAgent(CreateAgentRequest body) => new()
    {
        Name = body.Name,
        Role = body.Role,
        Model = body.Model,
        DisplayName = body.DisplayName,
        ContainerName = body.ContainerName,
        MemoryLimitMb = body.MemoryLimitMb,
        IsEnabled = body.IsEnabled,
        Image = body.Image,
        PermissionMode = body.PermissionMode,
        MaxTurns = body.MaxTurns,
        WorkDir = body.WorkDir,
        ProactiveIntervalMinutes = body.ProactiveIntervalMinutes,
        GroupListenMode = body.GroupListenMode,
        GroupDebounceSeconds = body.GroupDebounceSeconds,
        WarmupTimeoutSeconds = body.WarmupTimeoutSeconds,
        ShortName = body.ShortName,
        ShowStats = body.ShowStats,
        PrefixMessages = body.PrefixMessages,
        FormattingMode = body.FormattingMode,
        SuppressToolMessages = body.SuppressToolMessages,
        TelegramSendOnly = body.TelegramSendOnly,
        Effort = body.Effort,
        JsonSchema = body.JsonSchema,
        AgentsJson = body.AgentsJson,
        AutoMemoryEnabled = body.AutoMemoryEnabled,
        Provider = body.Provider,
        CodexSandboxMode = body.CodexSandboxMode,
        MountDockerSock = body.MountDockerSock,
        LocalBaseUrl = body.LocalBaseUrl,
        ContextWindow = body.ContextWindow,
        HarnessExtra = body.HarnessExtra,
        Tools = body.Tools,
        Projects = body.Projects,
        McpEndpoints = body.McpEndpoints?.Select(e => new McpEndpointSpec(e.McpName, e.Url, e.TransportType)).ToList(),
        Networks = body.Networks,
        EnvRefs = body.EnvRefs,
        TelegramUsers = body.TelegramUsers,
        TelegramGroups = body.TelegramGroups,
        // The dashboard provisions unless told not to.
        Provision = body.Provision != false,
    };
}
