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
                if (kinds is { } k && (k.Desired == "openshell" || k.Applied == "openshell"))
                {
                    if (agentLogRoute)
                    {
                        if (k.Applied == "openshell")
                        {
                            context.Response.StatusCode = 409;
                            await context.Response.WriteAsJsonAsync(new { error = "managed_logs_use_agent_logs_tool" });
                            return;
                        }
                    }
                    else
                    {
                        // #502 review F1: only the exact routes with their mapped method (POST, or DELETE for the row) and
                        // an authenticated operator reach a managed action. Reads (which the bearer predicate exempts),
                        // other methods and malformed paths are refused before any dispatch or persisted write.
                        if (ParseManagedRoute(context.Request.Method, context.Request.Path.Value ?? "") is not { } route)
                        {
                            var method = context.Request.Method;
                            context.Response.StatusCode = HttpMethods.IsPost(method) || HttpMethods.IsDelete(method) ? 404 : 405;
                            await context.Response.WriteAsJsonAsync(new { error = "managed_route_not_supported" });
                            return;
                        }
                        if (OperatorAuthFault(context, configuration) is { } denied)
                        {
                            context.Response.StatusCode = denied == "operator_auth_unconfigured" ? 503 : 401;
                            await context.Response.WriteAsJsonAsync(new { error = denied });
                            return;
                        }
                        // Apply/reprovision/deprovision reach the provisioning service, which dispatches to the adapter.
                        if (route.Action is "provision" or "reprovision" or "restart-with-version" or "deprovision")
                        { await next(context); return; }
                        if (k.Applied == "openshell")
                        {
                            var result = route.Action switch
                            {
                                "start" => await managed.StartAsync(parts[2], context.RequestAborted),
                                "stop" => await managed.StopAsync(parts[2], context.RequestAborted),
                                "restart" => await managed.RestartAsync(parts[2], context.RequestAborted),
                                // #502 review F3: a targeted cancel keeps its exact task id; only the bare route cancels all.
                                "cancel" or "cancel_bg" => await managed.CancelAsync(parts[2], route.TaskId, context.RequestAborted),
                                _ => StockOpenShellRuntime.Result.Refused(409, "unsupported_managed_action"),
                            };
                            context.Response.StatusCode = result.Ok ? 200 : result.Status;
                            await context.Response.WriteAsJsonAsync(result.Ok ? new { message = result.Code } : (object)new { error = result.Code, errors = result.Errors });
                            return;
                        }
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

    /// <summary>An exact managed lifecycle route: its action and, for a targeted cancel, the exact decoded task id.</summary>
    internal sealed record ManagedRoute(string Action, string? TaskId);

    private static readonly HashSet<string> PostActions = new(StringComparer.OrdinalIgnoreCase)
        { "start", "stop", "restart", "cancel", "provision", "reprovision", "restart-with-version", "deprovision" };

    /// <summary>
    /// #502 review F1/F3: <c>DELETE /api/agents/{name}</c>; <c>POST /api/agents/{name}/{action}</c>;
    /// <c>POST /api/agents/{name}/cancel/{**taskId}</c> and <c>POST /api/agents/{name}/cancel_bg/{taskId}</c>, the task id
    /// decoded exactly as the ordinary endpoints decode it. Any other method, extra segment, empty segment or trailing
    /// slash is null (refused).
    /// </summary>
    internal static ManagedRoute? ParseManagedRoute(string method, string path)
    {
        if (!path.StartsWith('/') || path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal)) return null;
        var parts = path[1..].Split('/');
        if (parts.Length < 3 || !parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("agents", StringComparison.OrdinalIgnoreCase)) return null;
        if (HttpMethods.IsDelete(method)) return parts.Length == 3 ? new("deprovision", null) : null;
        if (!HttpMethods.IsPost(method) || parts.Length < 4) return null;
        var action = parts[3].ToLowerInvariant();
        if (parts.Length == 4) return PostActions.Contains(action) ? new(action, null) : null;
        if (action == "cancel_bg") return parts.Length == 5 ? new(action, Uri.UnescapeDataString(parts[4])) : null;
        if (action == "cancel") return new(action, Uri.UnescapeDataString(string.Join('/', parts[4..])));
        return null;
    }

    /// <summary>Operator bearer authentication for managed dispatch; fails closed when no operator token is configured.</summary>
    private static string? OperatorAuthFault(HttpContext context, IConfiguration configuration)
    {
        var token = configuration["Orchestrator:AuthToken"];
        if (string.IsNullOrWhiteSpace(token)) return "operator_auth_unconfigured";
        return context.Request.Headers.Authorization.ToString().Equals("Bearer " + token, StringComparison.Ordinal) ? null : "operator_auth_required";
    }
}
