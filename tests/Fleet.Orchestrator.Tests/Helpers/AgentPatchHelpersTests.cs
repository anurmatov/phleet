using Fleet.Orchestrator.Helpers;

namespace Fleet.Orchestrator.Tests.Helpers;

// Covers the CodexSandboxMode clear-to-null mapping used by the PATCH /api/agents/{name}/config
// handler. The mapping logic is extracted to AgentPatchHelpers.MapCodexSandboxMode so that it
// can be verified independently without spinning up the full HTTP stack.
//
// Key invariant tested here: the frontend sends "" (empty string) to clear the field, and the
// handler stores null (not ""). This is the correct fix for the silent-drop bug where the old
// code sent JSON null from the frontend — null is skipped by the `is not null` guard and the
// field was never actually cleared.

public class AgentPatchHelpersTests
{
    // ── MapCodexSandboxMode ────────────────────────────────────────────────────

    [Fact]
    public void MapCodexSandboxMode_EmptyString_ClearsToNull()
    {
        // Frontend sends "" to clear — backend must store null (not "").
        var (error, value) = AgentPatchHelpers.MapCodexSandboxMode("");

        Assert.Null(error);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("workspace-write")]
    [InlineData("danger-full-access")]
    public void MapCodexSandboxMode_ValidMode_ReturnsMode(string mode)
    {
        var (error, value) = AgentPatchHelpers.MapCodexSandboxMode(mode);

        Assert.Null(error);
        Assert.Equal(mode, value);
    }

    [Fact]
    public void MapCodexSandboxMode_InvalidMode_ReturnsError()
    {
        var (error, value) = AgentPatchHelpers.MapCodexSandboxMode("sandbox-unknown");

        Assert.NotNull(error);
        Assert.Contains("sandbox-unknown", error);
        Assert.Null(value);
    }
}
