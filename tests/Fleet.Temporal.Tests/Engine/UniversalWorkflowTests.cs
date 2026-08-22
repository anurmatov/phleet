using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// Unit tests for <see cref="UniversalWorkflow"/> engine-level helpers.
/// These cover logic that is independent of the Temporal runtime.
/// </summary>
public sealed class UniversalWorkflowTests
{
    // -----------------------------------------------------------------------
    // ResolveOutputVar — the variable stored when a delegate step completes
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveOutputVar_IdleResult_EmptyText_ReturnsStatusPrefix()
    {
        // When an agent answers IDLE the Text field is empty (everything after
        // "[status: idle]" is stripped by ParseAgentResult). Storing an empty
        // string in outputVar causes templates like {{vars.x | default: '...'}}
        // to fire their fallback, silently masking the IDLE completion as a
        // failure.  ResolveOutputVar must return a non-empty sentinel instead.
        var result = new AgentTaskResult("", "idle");

        var value = UniversalWorkflow.ResolveOutputVar(result);

        Assert.Equal("[status: idle]", value);
        Assert.False(string.IsNullOrEmpty(value),
            "outputVar must be non-empty after an IDLE completion so default filters don't fire");
    }

    [Fact]
    public void ResolveOutputVar_CompletedResult_ReturnsText()
    {
        var result = new AgentTaskResult("agent output text", "completed");
        Assert.Equal("agent output text", UniversalWorkflow.ResolveOutputVar(result));
    }

    [Fact]
    public void ResolveOutputVar_FailedResult_EmptyText_ReturnsEmptyText()
    {
        // Only the "idle" status gets the sentinel — failed stays as-is so
        // callers can distinguish failed-with-no-output from idle.
        var result = new AgentTaskResult("", "failed");
        Assert.Equal("", UniversalWorkflow.ResolveOutputVar(result));
    }

    [Fact]
    public void ResolveOutputVar_IdleResult_NonEmptyText_ReturnsText()
    {
        // If text is somehow non-empty on an idle result, honour it verbatim.
        var result = new AgentTaskResult("trailing text", "idle");
        Assert.Equal("trailing text", UniversalWorkflow.ResolveOutputVar(result));
    }
}
