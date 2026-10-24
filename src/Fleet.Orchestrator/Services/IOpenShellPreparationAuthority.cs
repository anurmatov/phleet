namespace Fleet.Orchestrator.Services;

/// <summary>
/// Outside operator selection, never an agent/request boolean. No issuer is enabled by this source checkpoint.
/// A scope binds exact immutable inputs and expires even if an RPC, lease or caller outlives it.
/// </summary>
public interface IOpenShellPreparationAuthority
{
    Task<OpenShellPreparationScope?> SelectAsync(string agentName, string intent, CancellationToken ct);
}

public sealed record OpenShellPreparationScope(int AgentId, string Intent, string? ExpectedOldRuntimeId,
    string PlanHash, DateTime Deadline, OpenShellLaunchPlan Plan);

public sealed class DenyOpenShellPreparationAuthority : IOpenShellPreparationAuthority
{
    public Task<OpenShellPreparationScope?> SelectAsync(string agentName, string intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<OpenShellPreparationScope?>(null);
    }
}
