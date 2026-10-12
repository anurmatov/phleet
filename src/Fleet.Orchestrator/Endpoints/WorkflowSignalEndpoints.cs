using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

public sealed record WorkflowSignalGate(string Status, string? Phase, string? GateRef);

/// <summary>The existing Temporal singleton is used in production; endpoint tests substitute only IO.</summary>
public interface IWorkflowSignalClient
{
    bool IsConfigured { get; }
    Task<WorkflowSignalGate> DescribeSignalGateAsync(string ns, string id, string runId, CancellationToken ct);
    Task<bool> SignalWorkflowAsync(string ns, string id, string? runId, string signal, string payload, CancellationToken ct);
}

public static class WorkflowSignalEndpoints
{
    public static WebApplication MapWorkflowSignalEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workflows/{ns}/signal/{**id}", SendAsync);
        return app;
    }

    private sealed record SignalRequest(string? SignalName, string? WorkflowType, string? Payload, string? RunId, string? ExpectedGateRef);

    public static async Task<IResult> SendAsync(string ns, string id, HttpRequest request, IWorkflowSignalClient temporal)
    {
        SignalRequest? body;
        try { body = await request.ReadFromJsonAsync<SignalRequest>(request.HttpContext.RequestAborted); }
        catch (JsonException) { return Results.BadRequest(new { error = "invalid_json" }); }
        if (body is null || string.IsNullOrWhiteSpace(body.SignalName))
            return Results.BadRequest(new { error = "signalName is required" });
        var signalName = body.SignalName;
        var known = string.IsNullOrWhiteSpace(body.WorkflowType) ? null : SignalRegistry.Get(body.WorkflowType);
        // Omitting or inventing workflowType cannot bypass a reserved name's gate binding.
        var definition = SignalRegistry.All.Values.SelectMany(x => x)
            .FirstOrDefault(s => s.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase) && s.BindAttribute == "GateRef");
        var payload = body.Payload ?? "{}";
        var workflowId = Uri.UnescapeDataString(id);
        if (definition is not null && string.IsNullOrWhiteSpace(body.RunId))
            return Results.BadRequest(new { error = "run_required" });
        if (known is not null && !known.Any(s => s.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = $"Signal '{signalName}' is not registered for workflow type '{body.WorkflowType}'" });
        if (!temporal.IsConfigured)
            return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

        if (definition is not null)
        {
            WorkflowSignalGate gate;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try { gate = await temporal.DescribeSignalGateAsync(ns, workflowId, body.RunId!, deadline.Token).WaitAsync(deadline.Token); }
            catch (Exception) { return Results.Json(new { error = "describe_unavailable" }, statusCode: 503); }
            if (gate.Status != "Running")
                return Results.Json(new { error = "stale_run" }, statusCode: 409);
            var current = string.IsNullOrEmpty(gate.GateRef) ? null : gate.GateRef;
            var expected = string.IsNullOrEmpty(body.ExpectedGateRef) ? null : body.ExpectedGateRef;
            if (current is null && expected is null)
            {
                if (definition.Name == "acceptance-decision")
                    return Results.Json(new { error = "no_gate" }, statusCode: 409);
                // Legacy merge waits deliberately bypass Phase and preserve the payload bytes.
            }
            else
            {
                if (current is null)
                    return StaleGate(expected, current);
                if (definition.ValidPhases is not null && !definition.ValidPhases.Contains(gate.Phase, StringComparer.Ordinal))
                    return Results.Json(new { error = "stale_phase" }, statusCode: 409);
                if (expected is null || !string.Equals(expected, current, StringComparison.Ordinal))
                    return StaleGate(expected, current);
                JsonObject? parsed;
                try { parsed = JsonNode.Parse(payload) as JsonObject; }
                catch (JsonException) { return Results.BadRequest(new { error = "invalid_payload" }); }
                if (parsed is null) return Results.BadRequest(new { error = "invalid_payload" });
                // Do not leave a case-variant Ref that another consumer could prefer.
                foreach (var key in parsed.Select(p => p.Key).Where(k => k.Equals("Ref", StringComparison.OrdinalIgnoreCase)).ToArray())
                    parsed.Remove(key);
                parsed["Ref"] = current;
                payload = parsed.ToJsonString();
            }
        }
        try
        {
            var ok = await temporal.SignalWorkflowAsync(ns, workflowId, body.RunId, signalName, payload, request.HttpContext.RequestAborted);
            return ok ? Results.Ok(new { message = $"Signal '{signalName}' sent to workflow '{workflowId}'" })
                : Results.NotFound(new { error = $"No client for namespace '{ns}'" });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = $"Failed to send signal: {ex.Message}" }, statusCode: 500);
        }
    }
    private static IResult StaleGate(string? expected, string? current)
        => Results.Json(new { error = "stale_gate", expectedGateRef = expected, currentGateRef = current }, statusCode: 409);
}
