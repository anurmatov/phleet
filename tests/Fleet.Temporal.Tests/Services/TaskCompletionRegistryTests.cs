using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// The registry's identity contract.
///
/// TaskIds are deliberately stable across Temporal retries — the XML doc on
/// <c>DelegateToAgentAsync</c> tells callers to pass a stable ID "so Temporal can safely retry
/// without re-registering". The registry did not honour that: <c>TryCancel</c> removed and
/// cancelled whatever sat under the key, so a dead attempt's cleanup took down its live
/// successor. The successor then failed in seconds reporting a 90-minute timeout it never waited
/// for, and the agent's real reply arrived to an empty registry and was dropped (issue #321).
/// </summary>
public class TaskCompletionRegistryTests
{
    private static TaskCompletionRegistry NewRegistry() =>
        new(NullLogger<TaskCompletionRegistry>.Instance);

    [Fact]
    public void TryCancel_WithTheCallersOwnSource_DoesNotCancelALaterRegistration()
    {
        // This is the defect, reproduced at its smallest. Red before compare-and-remove.
        var registry = NewRegistry();

        var first = registry.Register("wf/task");
        var second = registry.Register("wf/task");   // a Temporal retry takes the same key

        var removed = registry.TryCancel("wf/task", first);

        Assert.False(removed);                 // the key was not ours to remove
        Assert.True(first.Task.IsCanceled);    // our own source is released
        Assert.False(second.Task.IsCompleted); // ...and the live attempt is untouched
        Assert.Equal(1, registry.PendingCount);
    }

    [Fact]
    public void AfterAStaleCancel_TheLiveAttemptStillReceivesTheAgentResponse()
    {
        // The consequence that actually cost a review: with the live registration cancelled and
        // removed, the agent's reply had nowhere to land.
        var registry = NewRegistry();

        var first = registry.Register("wf/task");
        var second = registry.Register("wf/task");
        registry.TryCancel("wf/task", first);

        var delivered = registry.TryComplete("wf/task", new AgentTaskResult("the review", "completed"));

        Assert.True(delivered);
        Assert.Equal("the review", second.Task.Result.Text);
    }

    [Fact]
    public void TryCancel_WithTheCurrentRegistration_RemovesAndCancelsIt()
    {
        // The ordinary path still works — otherwise the test above would pass by never cancelling
        // anything at all.
        var registry = NewRegistry();
        var tcs = registry.Register("wf/task");

        Assert.True(registry.TryCancel("wf/task", tcs));
        Assert.True(tcs.Task.IsCanceled);
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void TryCancel_WithoutAnOwnedSource_KeepsTheUnconditionalBehaviour()
    {
        // Callers with no registration of their own (shutdown paths) still clear the key.
        var registry = NewRegistry();
        var tcs = registry.Register("wf/task");

        Assert.True(registry.TryCancel("wf/task"));
        Assert.True(tcs.Task.IsCanceled);
        Assert.False(registry.TryCancel("wf/task"));
    }

    [Fact]
    public void TryCancel_ForAnUnknownTaskId_ReportsFalseAndCancelsOnlyTheCallersSource()
    {
        var registry = NewRegistry();
        var orphan = new TaskCompletionSource<AgentTaskResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.False(registry.TryCancel("wf/never-registered", orphan));
        Assert.True(orphan.Task.IsCanceled);
    }
}
