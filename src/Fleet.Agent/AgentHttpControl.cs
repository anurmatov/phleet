using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;

namespace Fleet.Agent;

/// <summary>
/// The agent's HTTP control guard and cancel routes, shared by <c>Program</c> and the host tests so
/// the tested pipeline is the one that ships.
/// </summary>
public static class AgentHttpControl
{
    /// <summary>
    /// Stock profiles answer only health; managed mode (#492) also admits loopback POST cancel from
    /// the adapter's stock-authenticated exec. The journal files path never answers over HTTP.
    /// </summary>
    public static void UseControlGuard(this WebApplication app, bool stockMode, bool managedStock) =>
        app.Use(async (context, next) =>
        {
            if (stockMode && context.Request.Path != "/health"
                && !StockOpenShellEnrollment.AllowsManagedLoopbackCancel(managedStock, context.Request.Method,
                    context.Request.Path.Value, context.Connection.RemoteIpAddress))
            { context.Response.StatusCode = 404; return; }
            if (context.Request.Path.Value == JournalFilesListener.Path) { context.Response.StatusCode = 404; return; }
            await next(context);
        });

    public static void MapCancelEndpoints(this WebApplication app)
    {
        app.MapPost("/cancel", async (IServiceProvider sp) =>
        {
            var taskManager = sp.GetRequiredService<TaskManager>();
            await taskManager.CancelAllAsync();
            return Results.Ok(new { message = "All tasks cancelled and queue cleared" });
        });

        app.MapPost("/cancel/{**taskId}", async (string taskId, IServiceProvider sp) =>
        {
            var taskManager = sp.GetRequiredService<TaskManager>();
            var decoded = Uri.UnescapeDataString(taskId);
            var cancelled = await taskManager.CancelByBridgeTaskIdAsync(decoded);
            return Results.Ok(new { cancelled, taskId = decoded });
        });
    }
}
