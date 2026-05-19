namespace Fleet.Orchestrator.Helpers;

internal static class AgentPatchHelpers
{
    internal static readonly string[] ValidCodexSandboxModes =
        ["read-only", "workspace-write", "danger-full-access"];

    // Maps an incoming PATCH value to the stored CodexSandboxMode.
    // "" (empty string) → (null error, null value)  — clears the field
    // valid mode       → (null error, value)         — sets the field
    // invalid mode     → (error message, null)       — caller returns 400
    internal static (string? Error, string? Value) MapCodexSandboxMode(string input)
    {
        if (input == "")
            return (null, null);
        if (!Array.Exists(ValidCodexSandboxModes, m => m == input))
            return ($"Invalid CodexSandboxMode '{input}'. Valid values: {string.Join(", ", ValidCodexSandboxModes)}.", null);
        return (null, input);
    }
}
