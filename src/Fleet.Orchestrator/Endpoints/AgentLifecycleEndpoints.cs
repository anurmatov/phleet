using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface for what is done to an agent's container and row: restart, stop, start,
/// restart with an instruction version, reprovision and delete.
/// </summary>
/// <remarks>
/// Adapters. Stop, start, restart, restart-with-version and delete call <see cref="AgentLifecycle"/>,
/// which the matching MCP tools call too; reprovision calls
/// <see cref="ContainerProvisioningService"/>, the one implementation both surfaces already share.
/// Writes are gated by the method-based bearer middleware (<see cref="OrchestratorAuth"/>).
/// </remarks>
public static class AgentLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapAgentLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // REST: restart an agent's container via Docker API
        app.MapPost("/api/agents/{name}/restart", async (string name, AgentLifecycle lifecycle) =>
            WriteHttp.Answer(await lifecycle.RestartAsync(name),
                done => Results.Ok(new { message = $"Container '{done.ContainerName}' restart requested" })));

        // REST: stop an agent's container via Docker API
        app.MapPost("/api/agents/{name}/stop", async (string name, AgentLifecycle lifecycle) =>
            WriteHttp.Answer(await lifecycle.StopAsync(name),
                done => Results.Ok(new { message = $"Container '{done.ContainerName}' stop requested" })));

        // REST: start an agent's container via Docker API
        app.MapPost("/api/agents/{name}/start", async (string name, AgentLifecycle lifecycle) =>
            WriteHttp.Answer(await lifecycle.StartAsync(name),
                done => Results.Ok(new { message = $"Container '{done.ContainerName}' start requested" })));

        // REST: restart an agent using a specific instruction version (temporary or pinned)
        app.MapPost("/api/agents/{name}/restart-with-version", async (string name, HttpRequest request, AgentLifecycle lifecycle) =>
        {
            var body = await request.ReadFromJsonAsync<RestartWithVersionRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.InstructionName))
                return Results.BadRequest(new { error = "instructionName is required" });

            var outcome = await lifecycle.RestartWithInstructionVersionAsync(
                name, body.InstructionName, body.VersionNumber, pin: body.Pin == true);
            return WriteHttp.Answer(outcome, done => !done.Reprovision.Success
                ? Results.Problem(done.Reprovision.Message)
                : Results.Ok(new
                {
                    message = done.Pinned
                        ? $"Pinned '{done.InstructionName}' to v{done.VersionNumber} and reprovisioned '{name}'"
                        : $"Reprovisioned '{name}' with '{done.InstructionName}' v{done.VersionNumber} (temporary)",
                }));
        });

        // REST: reprovision an agent's container (deprovision + provision from DB config)
        // Optional ?image= query param overrides the DB-configured image (used by CI for PR-specific tags)
        // Optional session choice (auto | fresh), see ReadSessionChoiceAsync
        app.MapPost("/api/agents/{name}/reprovision", async (string name, HttpRequest request, ContainerProvisioningService provisioning) =>
        {
            var (session, refusal) = await ReadSessionChoiceAsync(request);
            if (refusal is not null) return refusal;

            var imageOverride = request.Query["image"].FirstOrDefault();
            var result = await provisioning.ReprovisionAsync(name, imageOverride, session: session);
            if (result.Failure == ProvisionFailure.InvalidSessionChoice)
                return Results.BadRequest(new { error = result.Message });
            return result.Success
                ? Results.Ok(new { message = result.Message })
                : Results.Problem(result.Message);
        });

        // REST: bulk reprovision all currently running agents
        // Optional ?image= query param overrides the image for all agents (e.g. bulk image upgrade)
        // Optional session choice (auto | fresh) applies to every agent it reprovisions
        app.MapPost("/api/agents/reprovision-running", async (HttpRequest request, ContainerProvisioningService provisioning) =>
        {
            var (session, refusal) = await ReadSessionChoiceAsync(request);
            if (refusal is not null) return refusal;

            var imageOverride = request.Query["image"].FirstOrDefault();
            var results = await provisioning.ReprovisionRunningAsync(imageOverride, session);
            if (results is [{ Failure: ProvisionFailure.InvalidSessionChoice } refused])
                return Results.BadRequest(new { error = refused.Message });

            var failed = results.Where(r => !r.Success).ToList();
            return failed.Count == 0
                ? Results.Ok(new { message = $"Reprovisioned {results.Count} running agent(s)", results })
                : Results.Json(
                    new { message = $"{failed.Count}/{results.Count} agent(s) failed to reprovision", results },
                    statusCode: 500);
        });

        // REST: delete an agent (deprovision container + remove DB records)
        app.MapDelete("/api/agents/{name}", async (string name, AgentLifecycle lifecycle) =>
            WriteHttp.Answer(await lifecycle.DeleteAsync(name), done => Results.Ok(new
            {
                message = done.ContainerRemoved
                    ? $"Agent '{name}' deleted and container removed"
                    : $"Agent '{name}' deleted (container was not running)",
            })));

        return app;
    }

    /// <summary>
    /// The session choice a reprovision request carries: <c>session</c> in a JSON body, or
    /// <c>?session=</c> beside <c>?image=</c>. No body, an empty one and an object without the field
    /// mean no choice, which is every request from before the choice existed. Whether the word is
    /// one of the two is the service's check; this refuses only a request that cannot be read, so
    /// that a choice the caller tried to make is never dropped in silence.
    /// </summary>
    private static async Task<(string? Session, IResult? Refusal)> ReadSessionChoiceAsync(HttpRequest request)
    {
        static (string?, IResult?) Refuse(string message) => (null, Results.BadRequest(new { error = message }));

        string? fromBody = null;
        using (var reader = new StreamReader(request.Body, leaveOpen: true))
        {
            var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { fromBody = JsonSerializer.Deserialize<ReprovisionRequest>(text, JsonSerializerOptions.Web)?.Session; }
                catch (JsonException) { return Refuse("the request body must be a JSON object, and session a string"); }
            }
        }

        var fromQuery = request.Query.TryGetValue("session", out var values) ? values.ToString() : null;
        if (fromQuery is not null && fromBody is not null && fromQuery != fromBody)
            return Refuse("session is given in the query and in the body with different values");

        return (fromQuery ?? fromBody, null);
    }
}

internal sealed record RestartWithVersionRequest(string InstructionName, int VersionNumber, bool? Pin);

/// <summary>The optional body of the two reprovision routes. <c>Session</c> is <c>auto</c> or <c>fresh</c>.</summary>
internal sealed record ReprovisionRequest(string? Session);
