using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface over Temporal workflows: the running list, cancel, terminate, restart, history,
/// recent failures and completions, the workflow types that can be started, and starting one. Also
/// the small lookups the same views need: signals, namespaces and search attributes.
/// </summary>
/// <remarks>Sending a signal is <see cref="WorkflowSignalEndpoints"/>.</remarks>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        // REST: active workflows across all Temporal namespaces
        app.MapGet("/api/workflows", (WorkflowStore workflows) =>
            Results.Ok(workflows.GetAll()));

        // REST: cancel a running Temporal workflow (graceful shutdown)
        app.MapPost("/api/workflows/{ns}/cancel/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

            var runId = request.Query["runId"].FirstOrDefault();
            var workflowId = Uri.UnescapeDataString(id);

            try
            {
                var ok = await temporal.CancelWorkflowAsync(ns, workflowId, runId);
                return ok
                    ? Results.Ok(new { message = $"Workflow '{workflowId}' cancel requested" })
                    : Results.NotFound(new { error = $"No client for namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to cancel workflow: {ex.Message}" }, statusCode: 500);
            }
        });

        // REST: terminate a running Temporal workflow (force kill)
        app.MapPost("/api/workflows/{ns}/terminate/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

            var runId = request.Query["runId"].FirstOrDefault();
            var workflowId = Uri.UnescapeDataString(id);

            try
            {
                var ok = await temporal.TerminateWorkflowAsync(ns, workflowId, runId);
                return ok
                    ? Results.Ok(new { message = $"Workflow '{workflowId}' terminated" })
                    : Results.NotFound(new { error = $"No client for namespace '{ns}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to terminate workflow: {ex.Message}" }, statusCode: 500);
            }
        });

        // REST: restart a Temporal workflow — reads original type + input from history, starts fresh execution
        app.MapPost("/api/workflows/{ns}/restart/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

            var runId = request.Query["runId"].FirstOrDefault();
            var terminateExisting = request.Query["terminateExisting"].FirstOrDefault() == "true";
            var workflowId = Uri.UnescapeDataString(id);

            try
            {
                var (newWorkflowId, newRunId) = await temporal.RestartWorkflowAsync(ns, workflowId, runId, terminateExisting);
                return Results.Ok(new { message = $"Workflow restarted as '{newWorkflowId}'", newWorkflowId, newRunId });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to restart workflow: {ex.Message}" }, statusCode: 500);
            }
        });

        // REST: workflow execution history for detail view — GET /api/workflows/{ns}/history/{**id}
        // Uses /history/ prefix before the catch-all so ASP.NET Core routing resolves correctly.
        app.MapGet("/api/workflows/{ns}/history/{**id}", async (string ns, string id, string? runId, TemporalClientRegistry temporal, CancellationToken ct) =>
        {
            if (!temporal.IsConfigured)
                return Results.Problem("Temporal is not configured on this orchestrator");

            var workflowId = Uri.UnescapeDataString(id);
            try
            {
                var events = await temporal.FetchWorkflowEventsAsync(ns, workflowId, runId: runId, ct);
                return Results.Ok(events);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Error fetching workflow history: {ex.Message}");
            }
        });

        // REST: recently failed/terminated/canceled workflows (last 1 hour by default)
        app.MapGet("/api/workflows/failures", async (TemporalClientRegistry temporal) =>
        {
            if (!temporal.IsConfigured)
                return Results.Ok(Array.Empty<object>());

            var failures = await temporal.ListRecentFailuresAsync(TimeSpan.FromHours(1));
            return Results.Ok(failures);
        });

        // REST: recently closed workflows — GET /api/workflows/completed?hours=24
        app.MapGet("/api/workflows/completed", async (TemporalClientRegistry temporal, int? hours) =>
        {
            if (!temporal.IsConfigured)
                return Results.Ok(Array.Empty<object>());

            var h = Math.Min(hours ?? 24, 72);
            var closed = await temporal.ListRecentlyClosedAsync(TimeSpan.FromHours(h));
            return Results.Ok(closed);
        });

        // REST: workflow type registry — hardcoded C# types merged with active UWE DB types
        app.MapGet("/api/workflow-types", async (IServiceScopeFactory scopeFactory) =>
        {
            var types = new List<object>(WorkflowTypeRegistry.HardcodedTypes
                .Select(t => new { t.Name, t.Description, t.Namespace, t.TaskQueue, t.InputSchema }));

            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetService<Fleet.Orchestrator.Data.OrchestratorDbContext>();
                if (db is not null)
                {
                    var hardcodedNames = new HashSet<string>(WorkflowTypeRegistry.HardcodedTypes.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                    var uweTypes = await db.WorkflowDefinitions
                        .Where(d => d.IsActive)
                        .AsNoTracking()
                        .Select(d => new { d.Name, Description = "UWE: " + d.Name, d.Namespace, d.TaskQueue, d.Definition })
                        .ToListAsync();
                    var inputExprRegex = new System.Text.RegularExpressions.Regex(@"\{\{input\.([A-Za-z0-9_]+)\}\}");
                    var filtered = uweTypes.Where(t => !hardcodedNames.Contains(t.Name));
                    foreach (var t in filtered)
                    {
                        var matches = inputExprRegex.Matches(t.Definition);
                        var fields = matches.Select(m => m.Groups[1].Value).Distinct().ToList();
                        string? inputSchema = null;
                        if (fields.Count > 0)
                        {
                            var props = string.Join(",", fields.Select(f => $"\"{f}\":{{\"type\":\"string\"}}"));
                            inputSchema = $"{{\"type\":\"object\",\"properties\":{{{props}}}}}";
                        }
                        if (t.Name == "UweHumanInputWorkflow")
                            inputSchema = """{"type":"object","additionalProperties":false,"required":["Question","Context","AnswerMode"],"properties":{"Question":{"type":"string","minLength":1},"Context":{"type":"string","minLength":1},"AnswerMode":{"enum":["choice","text","approve-reject"]},"Choices":{"type":"array"}}}""";
                        types.Add(new { t.Name, t.Description, t.Namespace, t.TaskQueue, InputSchema = inputSchema });
                    }
                }
            }
            catch
            {
                // DB unavailable — return hardcoded only
            }

            return Results.Ok(types);
        });

        // REST: start a new Temporal workflow
        app.MapPost("/api/workflows/start", async (HttpRequest request, TemporalClientRegistry temporal, IServiceScopeFactory scopeFactory) =>
        {
            if (!temporal.IsConfigured)
                return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

            using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
            var root = body.RootElement;

            if (!root.TryGetProperty("workflowType", out var wfTypeEl) || wfTypeEl.GetString() is not string workflowType || string.IsNullOrWhiteSpace(workflowType))
                return Results.BadRequest(new { error = "workflowType is required" });

            if (!root.TryGetProperty("namespace", out var nsEl) || nsEl.GetString() is not string @namespace || string.IsNullOrWhiteSpace(@namespace))
                return Results.BadRequest(new { error = "namespace is required" });

            var taskQueue = root.TryGetProperty("taskQueue", out var tqEl) ? tqEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(taskQueue)) taskQueue = @namespace;

            var customId    = root.TryGetProperty("workflowId", out var idEl) ? idEl.GetString() : null;
            var workflowId  = string.IsNullOrWhiteSpace(customId)
                ? $"{workflowType}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                : customId!;

            string? inputJson = null;
            if (root.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                inputJson = inputEl.GetRawText();
                // Treat {} as no input
                if (inputJson.Replace(" ", "") == "{}") inputJson = null;
            }

            try
            {
                var newId = await temporal.StartWorkflowAsync(workflowType, workflowId, @namespace, taskQueue, inputJson);
                return Results.Ok(new { workflowId = newId, workflowNamespace = @namespace });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to start workflow: {ex.Message}" }, statusCode: 500);
            }
        });

        // REST: signal registry — static mapping of workflow type → available signals
        app.MapGet("/api/signals", () => Results.Ok(SignalRegistry.All));

        app.MapGet("/api/namespaces", (TemporalClientRegistry temporal) =>
        {
            if (!temporal.IsConfigured) return Results.Ok(Array.Empty<string>());
            return Results.Ok(temporal.GetNamespaces());
        });

        app.MapGet("/api/search-attributes", () =>
            Results.Ok(Fleet.Shared.SearchAttributeTypeRegistry.GetNames()));

        return app;
    }
}
