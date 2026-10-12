using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

public static class EpicDriverEndpoints
{
    public static IEndpointRouteBuilder MapEpicDriverEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/epic-drivers/{workflowId}/state", async (string workflowId, string? @namespace,
            EpicDriverStateService state, CancellationToken ct) =>
            Results.Ok(await state.ReadAsync(@namespace ?? "fleet", workflowId, ct)));
        return app;
    }
}
