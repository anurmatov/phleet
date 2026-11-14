namespace Fleet.Orchestrator.Endpoints;

/// <summary>The body of every <c>toggle-active</c> route: instructions, project contexts, workflow definitions and repositories.</summary>
internal sealed record ToggleActiveRequest(bool IsActive);
