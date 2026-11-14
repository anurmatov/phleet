using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>The REST reads of the agent templates a new agent can start from.</summary>
public static class AgentTemplateEndpoints
{
    public static IEndpointRouteBuilder MapAgentTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agent-templates", () =>
            Results.Ok(AgentTemplateRegistry.GetAll()));

        app.MapGet("/api/agent-templates/{name}", (string name) =>
        {
            var entry = AgentTemplateRegistry.TryGet(name);
            if (entry is null) return Results.NotFound(new { error = $"Template '{name}' not found" });
            var c = entry.Config;
            return Results.Ok(new
            {
                entry.Name,
                entry.DisplayName,
                entry.Description,
                c.Model,
                c.Role,
                c.Provider,
                c.MemoryLimitMb,
                c.PermissionMode,
                c.MaxTurns,
                c.WorkDir,
                c.ProactiveIntervalMinutes,
                c.GroupListenMode,
                c.GroupDebounceSeconds,
                c.ShowStats,
                c.PrefixMessages,
                FormattingMode = (byte)c.FormattingMode,
                c.SuppressToolMessages,
                c.TelegramSendOnly,
                c.AutoMemoryEnabled,
                Tools = c.Tools.Select(t => new { t.ToolName, t.IsEnabled }),
                Projects = c.Projects,
                McpEndpoints = c.McpEndpoints.Select(e => new { e.McpName, e.Url, e.TransportType }),
                Networks = c.Networks,
                EnvRefs = c.EnvRefs,
                Instructions = c.Instructions.Select(i => new { i.Name, i.LoadOrder }),
            });
        });

        return app;
    }
}
