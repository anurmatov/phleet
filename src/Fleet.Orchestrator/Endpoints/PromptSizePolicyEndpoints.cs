using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>GET /api/prompt-size-policy</c> (#346): the active instruction and project-context soft
/// limits, so the dashboard meter never hard-codes one. Read-only and unauthenticated like every
/// other GET (<see cref="OrchestratorAuth"/>); it exposes two integers and their key names.
/// </summary>
public static class PromptSizePolicyEndpoints
{
    public static IEndpointRouteBuilder MapPromptSizePolicyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/prompt-size-policy", ([FromServices] PromptSizePolicy policy) => Results.Ok(new
        {
            unit = PromptSizePolicy.Unit,
            instruction = Limit(policy.InstructionLimitBytes, PromptSizePolicy.InstructionKey),
            projectContext = Limit(policy.ProjectContextLimitBytes, PromptSizePolicy.ProjectContextKey),
        }));

        return app;
    }

    private static object Limit(int limitBytes, string key) => new
    {
        limitBytes = limitBytes == 0 ? (int?)null : limitBytes,
        limitKey = key,
    };
}
