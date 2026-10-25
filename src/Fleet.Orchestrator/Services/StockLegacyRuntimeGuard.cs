namespace Fleet.Orchestrator.Services;

/// <summary>Stock enrollment does not authorize local Docker or legacy HTTP routing.</summary>
public static class StockLegacyRuntimeGuard
{
    public static void UseStockLegacyRuntimeGuard(this WebApplication app, IConfiguration configuration)
    {
        // Capture the operator-bound identity, not heartbeat metadata. Keep the route held even
        // after revocation; revocation must never fall back to a same-named legacy container.
        var hold = app.Services.GetService<StockRuntimeMutationHold>() ?? new StockRuntimeMutationHold(configuration);
        if (!hold.HasEnrollment) return;
        app.Use(async (context, next) =>
        {
            var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var legacyAgentRoute = parts.Length >= 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("agents", StringComparison.OrdinalIgnoreCase)
                && hold.Denies(parts[2])
                && (HttpMethods.IsDelete(context.Request.Method) || (parts.Length >= 4 && parts[3].ToLowerInvariant()
                    is "restart-with-version" or "cancel_bg" or "restart" or "start" or "stop" or "cancel" or "provision" or "reprovision" or "deprovision"));
            var legacyLogs = parts.Length == 3 && parts[0].Equals("ws", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("logs", StringComparison.OrdinalIgnoreCase)
                && hold.Denies(parts[2]);
            // Raw service names have no stock ownership binding. Refuse this generic bulk
            // runtime mutator rather than guessing whether a submitted target is safe.
            var rawServiceMutation = parts.Length == 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("services", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("restart", StringComparison.OrdinalIgnoreCase);
            if (legacyAgentRoute || legacyLogs || rawServiceMutation)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = StockRuntimeMutationHold.Reason });
                return;
            }
            await next(context);
        });
    }
}
