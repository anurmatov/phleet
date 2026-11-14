using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>
/// The one gate every write of an agent's host port goes through, and the arithmetic of which port
/// an agent answers on.
/// </summary>
/// <remarks>
/// <para>
/// Each agent's HTTP API is published on a host port (<c>DockerService.CreateContainerAsync</c>), so
/// two agents on one port means the second container cannot start. A free port can only be chosen
/// by reading what the other agents hold, and that read and the write that follows must not
/// interleave with another writer's: two creates that both read the same highest port would both
/// store the next one, and both would be told they succeeded.
/// </para>
/// <para>
/// So a writer holds this gate from before it reads the ports until its save has COMMITTED, and
/// every writer of <see cref="Agent.HostPort"/> uses this same instance: <see cref="AgentCreator"/>
/// (REST and MCP create) and <see cref="AgentConfigWriter"/> (<c>update_agent_config host_port</c>).
/// It is a process-wide gate, not a database constraint: the orchestrator is the only process that
/// writes agent rows, and a unique index would not cover an agent with no stored port, which
/// answers on a port derived from its id.
/// </para>
/// </remarks>
public sealed class AgentHostPorts
{
    /// <summary>An agent with no stored port answers on this plus its id.</summary>
    private const int BasePort = 8080;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Waits for the gate. Dispose the result once the write has committed (or was refused);
    /// disposing twice is harmless. A cancelled wait throws and holds nothing.
    /// </summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Lease(_gate);
    }

    /// <summary>
    /// The port an agent answers on: its stored one, else <c>8080 + id</c>. The same rule as
    /// <c>ContainerProvisioningService.GetEffectiveHostPort</c>, for a row that is not loaded.
    /// </summary>
    public static int Effective(int agentId, int? storedPort) => storedPort ?? BasePort + agentId;

    /// <summary>
    /// A port above every port an agent answers on. Call it holding the gate, and keep holding it
    /// until the agent that takes the port is committed.
    /// </summary>
    public static async Task<int> NextFreeAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        var held = await db.Agents.Select(a => new { a.Id, a.HostPort }).ToListAsync(ct);
        return Math.Max(BasePort, held.Select(a => Effective(a.Id, a.HostPort)).DefaultIfEmpty(BasePort).Max()) + 1;
    }

    /// <summary>
    /// The name of another agent that answers on <paramref name="port"/>, or null when none does.
    /// Call it holding the gate.
    /// </summary>
    public static async Task<string?> FindHolderAsync(OrchestratorDbContext db, int port, int exceptAgentId, CancellationToken ct)
    {
        var others = await db.Agents.Where(a => a.Id != exceptAgentId).Select(a => new { a.Id, a.Name, a.HostPort }).ToListAsync(ct);
        return others.FirstOrDefault(a => Effective(a.Id, a.HostPort) == port)?.Name;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
