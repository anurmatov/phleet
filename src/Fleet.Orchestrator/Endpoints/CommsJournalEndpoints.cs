using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

public static class CommsJournalEndpoints
{
    public static WebApplication MapCommsJournalEndpoints(this WebApplication app)
    {
        app.MapGet("/api/comms/journal/status", async (
            CommsJournalStatusProxy proxy, CancellationToken ct) =>
            Results.Json(await proxy.GetAsync(ct)));
        return app;
    }
}
