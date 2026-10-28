namespace Fleet.Orchestrator.Services;

/// <summary>Stock enrollment does not authorize local Docker or legacy HTTP routing.</summary>
public static class StockLegacyRuntimeGuard
{
    public static void UseStockLegacyRuntimeGuard(this WebApplication app, IConfiguration configuration)
    {
        // Capture the operator-bound identity, not heartbeat metadata. Keep the route held even
        // after revocation; revocation must never fall back to a same-named legacy container.
        var hold = app.Services.GetService<StockRuntimeMutationHold>() ?? new StockRuntimeMutationHold(configuration);

        app.Use(async (context, next) =>
        {
            var parts = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var agentRuntimeRoute = parts.Length >= 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("agents", StringComparison.OrdinalIgnoreCase)
                && (HttpMethods.IsDelete(context.Request.Method) && parts.Length == 3 || parts.Length >= 4
                    && parts[3].ToLowerInvariant() is "restart-with-version" or "cancel_bg" or "restart" or "start" or "stop" or "cancel" or "provision" or "reprovision" or "deprovision");
            var agentLogRoute = parts.Length == 3 && parts[0] == "ws" && parts[1] == "logs";
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
            var rawServiceMutation = hold.HasEnrollment && parts.Length == 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("services", StringComparison.OrdinalIgnoreCase)
                && parts[2].Equals("restart", StringComparison.OrdinalIgnoreCase);
            if (legacyAgentRoute || legacyLogs || rawServiceMutation)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = StockRuntimeMutationHold.Reason });
                return;
            }
            if ((agentRuntimeRoute || agentLogRoute)
                && context.RequestServices.GetService<StockOpenShellRuntime>() is { Enabled: true } managed)
            {
                (string Desired, string Applied)? kinds;
                try { kinds = await managed.KindsAsync(parts[2], context.RequestAborted); }
                catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException) { kinds = null; context.Items["managed_state_unavailable"] = true; }
                if (context.Items.ContainsKey("managed_state_unavailable"))
                {
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsJsonAsync(new { error = "runtime_state_unavailable" });
                    return;
                }
                var action = parts.Length >= 4 ? parts[3].ToLowerInvariant() : HttpMethods.IsDelete(context.Request.Method) ? "deprovision" : "";
                if (kinds is { } k && (k.Desired == "openshell" || k.Applied == "openshell"))
                {
                    // Apply/reprovision/deprovision reach the provisioning service, which dispatches to the adapter.
                    if (!agentLogRoute && action is "provision" or "reprovision" or "restart-with-version" or "deprovision")
                    { await next(context); return; }
                    if (k.Applied == "openshell")
                    {
                        if (agentLogRoute)
                        {
                            context.Response.StatusCode = 409;
                            await context.Response.WriteAsJsonAsync(new { error = "managed_logs_use_agent_logs_tool" });
                            return;
                        }
                        var result = action switch
                        {
                            "start" => await managed.StartAsync(parts[2], context.RequestAborted),
                            "stop" => await managed.StopAsync(parts[2], context.RequestAborted),
                            "restart" => await managed.RestartAsync(parts[2], context.RequestAborted),
                            "cancel" => await managed.CancelAsync(parts[2], null, context.RequestAborted),
                            "cancel_bg" when parts.Length == 5 => await managed.CancelAsync(parts[2], parts[4], context.RequestAborted),
                            _ => StockOpenShellRuntime.Result.Refused(409, "unsupported_managed_action"),
                        };
                        context.Response.StatusCode = result.Ok ? 200 : result.Status;
                        await context.Response.WriteAsJsonAsync(result.Ok ? new { message = result.Code } : (object)new { error = result.Code, errors = result.Errors });
                        return;
                    }
                }
            }
            if ((agentRuntimeRoute || agentLogRoute) && await hold.DescribeAppliedFaultAsync(parts[2], applying: parts.Length >= 4 && parts[3].ToLowerInvariant() is "provision" or "reprovision" or "restart-with-version") is { } appliedFault)
            {
                context.Response.StatusCode = appliedFault == "runtime_state_unavailable" ? 503 : 409;
                await context.Response.WriteAsJsonAsync(new { error = appliedFault });
                return;
            }
            await next(context);
        });
    }
}
