using Fleet.Shared.Security;

namespace Fleet.Orchestrator.Services;

/// <summary>Stock enrollment does not authorize local Docker or legacy HTTP routing.</summary>
public static class StockLegacyRuntimeGuard
{
    public static void UseStockLegacyRuntimeGuard(this WebApplication app, IConfiguration configuration)
    {
        // Capture the operator-bound identity, not heartbeat metadata. Keep the route held even
        // after revocation; revocation must never fall back to a same-named legacy container.
        var identity = StockBrokerIdentity.Optional(configuration["StockOpenShell:AuthorityFile"],
            configuration["StockOpenShell:BrokerPasswordFile"], "observer");
        if (identity is null) return;
        app.Use(async (context, next) =>
        {
            var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var legacyAgentRoute = parts.Length >= 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("agents", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals(identity.Agent, StringComparison.OrdinalIgnoreCase)
                && (HttpMethods.IsDelete(context.Request.Method) || (parts.Length >= 4 && parts[3].ToLowerInvariant()
                    is "restart" or "start" or "stop" or "cancel" or "provision" or "reprovision" or "deprovision"));
            var legacyLogs = parts.Length == 3 && parts[0].Equals("ws", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("logs", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals(identity.Agent, StringComparison.OrdinalIgnoreCase);
            if (legacyAgentRoute || legacyLogs)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "stock_runtime_manual_control_required" });
                return;
            }
            await next(context);
        });
    }
}
