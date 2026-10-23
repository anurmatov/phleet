using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Shared REST/MCP/provisioning dispatch. Pending, never-launched agents retain the existing
/// backend. No OpenShell runtime operation is enabled until its driver and admission are qualified.
/// An unknown backend or any launch history cannot fall back to Docker, even after retirement.
/// </summary>
public sealed class AgentRuntimeLifecycle(
    IServiceScopeFactory scopes, ContainerProvisioningService provisioning, DockerService docker,
    ILogger<AgentRuntimeLifecycle> logger) : IAgentRuntimeLifecycle
{
    internal const string Blocked = "compatibility_blocked: OpenShell lifecycle driver and current-supervisor admission are not qualified";

    private async Task<(Agent? Agent, ProvisionResult? Error)> ResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Name == name, ct);
            if (agent is null) return (null, ProvisionResult.Fail(name, "agent not found in DB", 404));
            // Desired default 'openshell' alone is not migration authority. Once there has been
            // a launch, resetting metadata or deleting its active slot cannot authorize downgrade.
            if (agent.ExecutionShell != "openshell" || agent.OpenShellMigrationState != "pending"
                || await db.OpenShellLaunches.AnyAsync(l => l.AgentId == agent.Id, ct))
                return (agent, ProvisionResult.Fail(name, Blocked, 422));
            if (string.IsNullOrWhiteSpace(agent.ContainerName))
                return (agent, ProvisionResult.Fail(name, "runtime ownership unavailable", 503));
            return (agent, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Agent lifecycle ownership lookup failed ({Type})", ex.GetType().Name);
            return (null, ProvisionResult.Fail(name, "runtime ownership unavailable", 503));
        }
    }

    public async Task<ProvisionResult> ValidateAsync(string name, CancellationToken ct = default)
    {
        var (_, error) = await ResolveAsync(name, ct);
        return error ?? ProvisionResult.Ok(name, "legacy backend selected; no OpenShell qualification");
    }

    public async Task<ProvisionResult> StartAsync(string name, CancellationToken ct = default) =>
        await ChangeLegacyStateAsync(name, "start", ct);
    public async Task<ProvisionResult> StopAsync(string name, CancellationToken ct = default) =>
        await ChangeLegacyStateAsync(name, "stop", ct);
    public async Task<ProvisionResult> RestartAsync(string name, CancellationToken ct = default) =>
        await ChangeLegacyStateAsync(name, "restart", ct);

    private async Task<ProvisionResult> ChangeLegacyStateAsync(string name, string operation, CancellationToken ct)
    {
        var (agent, error) = await ResolveAsync(name, ct);
        if (error is not null) return error;
        ct.ThrowIfCancellationRequested();
        var ok = operation switch
        {
            "start" => await docker.StartLegacyAgentContainerAsync(agent!.ContainerName),
            "stop" => await docker.StopLegacyAgentContainerAsync(agent!.ContainerName),
            _ => await docker.RestartLegacyAgentContainerAsync(agent!.ContainerName),
        };
        return ok ? ProvisionResult.Ok(name, $"legacy container {operation} requested; runtime readiness is not confirmed")
            : ProvisionResult.Fail(name, $"legacy container {operation} failed", 503);
    }

    public async Task<ProvisionResult> ProvisionAsync(string name, string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null, CancellationToken ct = default)
    {
        var (_, error) = await ResolveAsync(name, ct);
        return error ?? await provisioning.LegacyProvisionAsync(name, imageOverride, instructionVersionOverrides, ct);
    }

    public async Task<ProvisionResult> ReprovisionAsync(string name, string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null, CancellationToken ct = default)
    {
        var (_, error) = await ResolveAsync(name, ct);
        return error ?? await provisioning.LegacyReprovisionAsync(name, imageOverride, instructionVersionOverrides, ct);
    }

    public async Task<ProvisionResult> DeprovisionAsync(string name, CancellationToken ct = default)
    {
        var (_, error) = await ResolveAsync(name, ct);
        return error ?? await provisioning.LegacyDeprovisionAsync(name, ct);
    }

    public async Task<ProvisionPreview> PreviewAsync(string name, CancellationToken ct = default)
    {
        var (agent, error) = await ResolveAsync(name, ct);
        if (error is null) return await provisioning.LegacyPreviewAsync(name, ct);
        return new(name, agent?.ContainerName ?? "", "", new("", 0, [], [], []), null, [error.Message]);
    }

    public async Task<IReadOnlyList<ProvisionResult>> ReprovisionRunningAsync(string? imageOverride = null,
        CancellationToken ct = default)
    {
        // The legacy snapshot is never a sandbox-running snapshot. Reject mixed/unknown state
        // before invoking it rather than silently skip or wake a migrated agent.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (await db.Agents.AnyAsync(a => a.IsEnabled &&
                    (a.ExecutionShell != "openshell" || a.OpenShellMigrationState != "pending"
                     || db.OpenShellLaunches.Any(l => l.AgentId == a.Id)), ct))
                return [ProvisionResult.Fail("(all)", Blocked, 422)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Agent lifecycle snapshot failed ({Type})", ex.GetType().Name);
            return [ProvisionResult.Fail("(all)", "runtime ownership unavailable", 503)];
        }
        return await provisioning.LegacyReprovisionRunningAsync(imageOverride, ct);
    }
}
