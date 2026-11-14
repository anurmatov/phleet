using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface over Temporal schedules: list, describe, create, pause, unpause, trigger and delete.
/// </summary>
public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/schedules — list all schedules across all configured namespaces (cheap fields only, no N+1)
        app.MapGet("/api/schedules", async (TemporalClientRegistry temporal, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Ok(Array.Empty<object>());

            try
            {
                var clients = await temporal.GetClientsAsync(ct);
                var results = new List<object>();
                foreach (var (ns, _) in clients)
                {
                    try
                    {
                        var schedules = await temporal.ListSchedulesAsync(ns, ct);
                        results.AddRange(schedules);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error listing schedules in namespace '{Namespace}'", ns);
                    }
                }
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to list schedules: {ex.Message}" }, statusCode: 500);
            }
        });

        // GET /api/schedules/{ns}/{**id} — describe a single schedule (full detail including run-time fields)
        app.MapGet("/api/schedules/{ns}/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

            var scheduleId = Uri.UnescapeDataString(id);
            try
            {
                var desc = await temporal.DescribeScheduleAsync(ns, scheduleId, ct);
                return Results.Ok(desc);
            }
            catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to describe schedule: {ex.Message}" }, statusCode: 500);
            }
        });

        // POST /api/schedules — create a new schedule
        app.MapPost("/api/schedules", async (HttpRequest request, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

            CreateScheduleRequest? req;
            try { req = await request.ReadFromJsonAsync<CreateScheduleRequest>(); }
            catch { return Results.BadRequest(new { error = "Invalid request body" }); }

            if (req is null)                                    return Results.BadRequest(new { error = "Request body is required" });
            if (string.IsNullOrWhiteSpace(req.Namespace))       return Results.BadRequest(new { error = "namespace is required" });
            if (string.IsNullOrWhiteSpace(req.WorkflowType))    return Results.BadRequest(new { error = "workflowType is required" });
            if (string.IsNullOrWhiteSpace(req.CronExpression))  return Results.BadRequest(new { error = "cronExpression is required" });

            // Auto-generate scheduleId: {workflowType}-{8-char-guid} lowercased with dashes
            var scheduleId = string.IsNullOrWhiteSpace(req.ScheduleId)
                ? $"{req.WorkflowType!.ToLowerInvariant().Replace('_', '-')}-{Guid.NewGuid().ToString("N")[..8]}"
                : req.ScheduleId!;
            var taskQueue = string.IsNullOrWhiteSpace(req.TaskQueue) ? req.Namespace : req.TaskQueue;

            // Parse optional input JSON
            object? inputObj = null;
            if (!string.IsNullOrWhiteSpace(req.InputJson))
            {
                try { inputObj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(req.InputJson); }
                catch { return Results.BadRequest(new { error = "input is not valid JSON" }); }
            }

            try
            {
                var created = await temporal.CreateScheduleAsync(
                    req.Namespace!, scheduleId, req.WorkflowType!, taskQueue!,
                    req.CronExpression!, inputObj, req.Memo, req.Paused ?? false, ct);
                return Results.Created($"/api/schedules/{req.Namespace}/{created}",
                    new { scheduleId = created, @namespace = req.Namespace, status = "created" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to create schedule: {ex.Message}" }, statusCode: 500);
            }
        });

        // POST /api/schedules/{ns}/pause/{**id} — pause a schedule
        app.MapPost("/api/schedules/{ns}/pause/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

            var scheduleId = Uri.UnescapeDataString(id);
            try
            {
                await temporal.PauseScheduleAsync(ns, scheduleId, ct);
                return Results.Ok(new { scheduleId, status = "paused" });
            }
            catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to pause schedule: {ex.Message}" }, statusCode: 500);
            }
        });

        // POST /api/schedules/{ns}/unpause/{**id} — unpause a schedule
        app.MapPost("/api/schedules/{ns}/unpause/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

            var scheduleId = Uri.UnescapeDataString(id);
            try
            {
                await temporal.UnpauseScheduleAsync(ns, scheduleId, ct);
                return Results.Ok(new { scheduleId, status = "active" });
            }
            catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to unpause schedule: {ex.Message}" }, statusCode: 500);
            }
        });

        // POST /api/schedules/{ns}/trigger/{**id} — trigger an immediate run
        app.MapPost("/api/schedules/{ns}/trigger/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

            var scheduleId = Uri.UnescapeDataString(id);
            try
            {
                await temporal.TriggerScheduleAsync(ns, scheduleId, ct);
                return Results.Ok(new { scheduleId, status = "triggered" });
            }
            catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to trigger schedule: {ex.Message}" }, statusCode: 500);
            }
        });

        // DELETE /api/schedules/{ns}/{**id} — delete a schedule
        app.MapDelete("/api/schedules/{ns}/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

            var scheduleId = Uri.UnescapeDataString(id);
            try
            {
                await temporal.DeleteScheduleAsync(ns, scheduleId, ct);
                return Results.Ok(new { scheduleId, status = "deleted" });
            }
            catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                                       || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
            {
                return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to delete schedule: {ex.Message}" }, statusCode: 500);
            }
        });

        return app;
    }
}

internal sealed record CreateScheduleRequest(
    string? ScheduleId,
    string? Namespace,
    string? WorkflowType,
    string? TaskQueue,
    string? CronExpression,
    string? InputJson,
    string? Memo,
    bool? Paused);
