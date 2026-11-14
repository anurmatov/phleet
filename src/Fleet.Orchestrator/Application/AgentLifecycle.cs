using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>The container a lifecycle request acted on.</summary>
public sealed record ContainerAction(string ContainerName);

/// <summary>
/// A reprovision with one instruction held at a chosen version. <c>PreviousCurrentVersion</c> is
/// the version the instruction pointed at before this call.
/// </summary>
public sealed record VersionedRestart(
    string InstructionName,
    int VersionNumber,
    bool Pinned,
    int PreviousCurrentVersion,
    ProvisionResult Reprovision);

/// <summary>A deleted agent, and whether a container was there to remove.</summary>
public sealed record AgentDeleted(string Name, bool ContainerRemoved);

/// <summary>
/// The one implementation of what can be done to an agent's container and row: stop, start,
/// restart, restart with a chosen instruction version, and delete. The REST routes and the MCP
/// tools are adapters over it. Provision, deprovision and reprovision already have one
/// implementation, <see cref="ContainerProvisioningService"/>, which both surfaces call directly.
/// </summary>
public sealed class AgentLifecycle(
    IServiceScopeFactory scopes,
    AgentRegistry registry,
    DockerService docker,
    ContainerProvisioningService provisioning,
    OperatorStops operatorStops,
    IAclChangeNotifier aclNotifier)
{
    /// <summary>
    /// Stops the agent's container. A stop someone asked for is not an agent going down, so it is
    /// recorded for the push notifier whichever surface asked.
    /// </summary>
    public async Task<WriteOutcome<ContainerAction>> StopAsync(string agentName)
    {
        var agent = registry.Get(agentName);
        if (agent is null)
            return WriteFaults.NotFound("agent_not_registered", $"Agent '{agentName}' not found in registry");

        var containerName = agent.ContainerName ?? agentName;
        if (!await docker.StopContainerAsync(containerName))
            return WriteFaults.Failed("docker_failed", $"Failed to stop container '{containerName}' — check orchestrator logs");

        operatorStops.MarkStopped(agentName);
        return WriteOutcome<ContainerAction>.Ok(new ContainerAction(containerName));
    }

    public async Task<WriteOutcome<ContainerAction>> StartAsync(string agentName)
    {
        var agent = registry.Get(agentName);
        if (agent is null)
            return WriteFaults.NotFound("agent_not_registered", $"Agent '{agentName}' not found in registry");

        var containerName = agent.ContainerName ?? agentName;
        if (!await docker.StartContainerAsync(containerName))
            return WriteFaults.Failed("docker_failed", $"Failed to start container '{containerName}' — check orchestrator logs");

        return WriteOutcome<ContainerAction>.Ok(new ContainerAction(containerName));
    }

    /// <summary>
    /// Restarts the agent's container. The registry (live state) names it first; the database is
    /// the fallback, which is what reaches a stopped or crashed agent.
    /// </summary>
    public async Task<WriteOutcome<ContainerAction>> RestartAsync(string agentName)
    {
        var containerName = registry.Get(agentName)?.ContainerName;
        if (containerName is null)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            var stored = db is null ? null : await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == agentName);
            containerName = stored?.ContainerName;
        }

        if (containerName is null)
            return WriteFaults.NotFound("agent_not_found", $"Agent '{agentName}' not found in registry or DB");

        if (!await docker.RestartContainerAsync(containerName))
            return WriteFaults.Failed("docker_failed", $"Failed to restart container '{containerName}' — check orchestrator logs");

        return WriteOutcome<ContainerAction>.Ok(new ContainerAction(containerName));
    }

    /// <summary>What Docker says about a container now. Read-only; null when it cannot say.</summary>
    public Task<string?> DescribeContainerAsync(string containerName) => docker.GetContainerStatusAsync(containerName);

    /// <summary>
    /// Reprovisions an agent with one instruction at a chosen version. Pinned, the instruction's
    /// current version is changed first and every later reprovision uses it; otherwise the stored
    /// version is untouched and only this container gets the override.
    /// </summary>
    public async Task<WriteOutcome<VersionedRestart>> RestartWithInstructionVersionAsync(
        string agentName, string instructionName, int versionNumber, bool pin)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        if (string.IsNullOrWhiteSpace(instructionName))
            return WriteFaults.Invalid("instruction_name_required", "instructionName is required");

        if (!await db.Agents.AsNoTracking().AnyAsync(a => a.Name == agentName))
            return WriteFaults.AgentNotFound(agentName);

        var instruction = await db.Instructions.Include(i => i.Versions).FirstOrDefaultAsync(i => i.Name == instructionName);
        if (instruction is null)
            return WriteFaults.NotFound("instruction_not_found", $"Instruction '{instructionName}' not found");

        if (instruction.Versions.All(v => v.VersionNumber != versionNumber))
            return new WriteFault(WriteFaultKind.NotFound, "instruction_version_not_found",
                $"Version {versionNumber} not found for instruction '{instructionName}'")
            {
                Detail = instruction.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.VersionNumber).ToList(),
            };

        var previous = instruction.CurrentVersion;
        ProvisionResult result;
        if (pin)
        {
            instruction.CurrentVersion = versionNumber;
            await db.SaveChangesAsync();
            result = await provisioning.ReprovisionAsync(agentName);
        }
        else
        {
            result = await provisioning.ReprovisionAsync(agentName,
                instructionVersionOverrides: new Dictionary<string, int> { [instructionName] = versionNumber });
        }

        return WriteOutcome<VersionedRestart>.Ok(new VersionedRestart(instructionName, versionNumber, pin, previous, result));
    }

    /// <summary>
    /// Deletes an agent: its container (best effort — none is fine), its row with every related
    /// row, the memory access its project assignments granted, and its registry entry.
    /// </summary>
    public async Task<WriteOutcome<AgentDeleted>> DeleteAsync(string agentName)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var agent = await db.Agents
            .Include(a => a.Tools)
            .Include(a => a.Projects)
            .Include(a => a.McpEndpoints)
            .Include(a => a.Networks)
            .Include(a => a.EnvRefs)
            .Include(a => a.TelegramUsers)
            .Include(a => a.TelegramGroups)
            .Include(a => a.Instructions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Name == agentName);

        if (agent is null)
            return WriteFaults.AgentNotFound(agentName);

        var deprovision = await provisioning.DeprovisionAsync(agentName);

        db.Agents.Remove(agent);
        await db.SaveChangesAsync();

        // agent_project_access is keyed by name, not by a foreign key, so deleting the agent leaves
        // its rows behind. Deleting an agent unassigns every project, so the same hook applies: the
        // rows it created go, an operator's manual rows and any wildcard stay.
        await AgentProjectAccessSync.SyncAndBroadcastAsync(db, aclNotifier, agent.Name, []);

        // No ghost in the dashboard.
        registry.Remove(agentName);

        return WriteOutcome<AgentDeleted>.Ok(new AgentDeleted(agentName, deprovision.Success));
    }
}
