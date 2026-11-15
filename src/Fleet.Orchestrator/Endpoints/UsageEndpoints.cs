using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>GET /api/usage</c>: the dashboard's bounded usage read, same-origin. The browser and edge
/// authentication of every other <c>/api</c> read applies unchanged; only this process calls the
/// receiver, with its own bearer.
/// </summary>
public static class UsageEndpoints
{
    public static WebApplication MapUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/usage", (HttpContext ctx, UsageReader reader) =>
            reader.ReadAsync(ctx.Request.Query, ctx.RequestAborted));
        return app;
    }
}
