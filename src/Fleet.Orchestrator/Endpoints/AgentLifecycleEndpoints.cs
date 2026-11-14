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
        app.MapPost("/api/agents/{name}/reprovision", async (string name, HttpRequest request, ContainerProvisioningService provisioning) =>
        {
            var imageOverride = request.Query["image"].FirstOrDefault();
            var result = await provisioning.ReprovisionAsync(name, imageOverride);
            return result.Success
                ? Results.Ok(new { message = result.Message })
                : Results.Problem(result.Message);
        });

        // REST: bulk reprovision all currently running agents
        // Optional ?image= query param overrides the image for all agents (e.g. bulk image upgrade)
        app.MapPost("/api/agents/reprovision-running", async (HttpRequest request, ContainerProvisioningService provisioning) =>
        {
            var imageOverride = request.Query["image"].FirstOrDefault();
            var results = await provisioning.ReprovisionRunningAsync(imageOverride);

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
}

internal sealed record RestartWithVersionRequest(string InstructionName, int VersionNumber, bool? Pin);
