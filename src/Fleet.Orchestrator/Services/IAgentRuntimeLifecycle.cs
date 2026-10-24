namespace Fleet.Orchestrator.Services;

/// <summary>One entry for agent runtime operations; caller attribution never selects a backend.</summary>
public interface IAgentRuntimeLifecycle
{
    Task<ProvisionResult> ValidateAsync(string name, CancellationToken ct = default);
    Task<ProvisionResult> StartAsync(string name, CancellationToken ct = default);
    Task<ProvisionResult> StopAsync(string name, CancellationToken ct = default);
    Task<ProvisionResult> RestartAsync(string name, CancellationToken ct = default);
    Task<ProvisionResult> ProvisionAsync(string name, string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null, CancellationToken ct = default);
    Task<ProvisionResult> ReprovisionAsync(string name, string? imageOverride = null,
        IReadOnlyDictionary<string, int>? instructionVersionOverrides = null, CancellationToken ct = default);
    Task<ProvisionResult> DeprovisionAsync(string name, CancellationToken ct = default);
    Task<ProvisionPreview> PreviewAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<ProvisionResult>> ReprovisionRunningAsync(string? imageOverride = null, CancellationToken ct = default);
}
