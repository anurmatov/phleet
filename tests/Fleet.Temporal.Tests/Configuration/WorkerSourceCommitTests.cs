using Fleet.Temporal.Configuration;
using Fleet.Tests.Shared;
using Temporalio.Api.Enums.V1;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Configuration;

/// <summary>
/// #476: the bridge stamps its image's source commit as the worker build id, and only an exact
/// 40-hex commit; anything else leaves the SDK default so compiled runs stay unverified.
/// </summary>
public class WorkerSourceCommitTests
{
    private const string Commit = "8c7c9ddf87147a16556acc59483ad0a9b1e57676";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("8C7C9DDF87147A16556ACC59483AD0A9B1E57676")]
    [InlineData("8c7c9ddf87147a16556acc59483ad0a9b1e5767")]
    [InlineData("8c7c9ddf87147a16556acc59483ad0a9b1e576760")]
    [InlineData("8c7c9ddf87147a16556acc59483ad0a9b1e57676\n")]
    [InlineData(" 8c7c9ddf87147a16556acc59483ad0a9b1e57676")]
    public void NotAnExactCommit_LeavesTheDefaultBuildId(string? value)
    {
        var options = new TemporalWorkerOptions("q");

        Assert.Null(WorkerSourceCommit.Apply(options, value));
#pragma warning disable CS0618
        Assert.Null(options.BuildId);
#pragma warning restore CS0618
    }

    [Fact]
    public void ExactCommit_IsTheBuildId_AndVersioningStaysOff()
    {
        var options = new TemporalWorkerOptions("q");

        Assert.Equal(Commit, WorkerSourceCommit.Apply(options, Commit));
#pragma warning disable CS0618
        Assert.Equal(Commit, options.BuildId);
        Assert.False(options.UseWorkerVersioning);
#pragma warning restore CS0618
        Assert.Null(options.DeploymentOptions);
    }

    /// <summary>
    /// Against a real dev server: every workflow task of a run on a stamped worker records the
    /// commit, and a worker given no commit records something that is not one.
    /// </summary>
    [Fact]
    public async Task RealServer_EveryWorkflowTaskRecordsTheStampedCommit()
    {
        await using var env = await TemporalDevServerSelection.StartLocalAsync(new() { DevServerOptions = new() { LogLevel = "error" } });

        async Task<IReadOnlyList<string>> BuildIdsAsync(string queue, string? commit)
        {
            var options = new TemporalWorkerOptions(queue).AddWorkflow<TwoTaskWorkflow>();
            WorkerSourceCommit.Apply(options, commit);
            using var worker = new TemporalWorker(env.Client, options);
            return await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync((TwoTaskWorkflow wf) => wf.RunAsync(), new(queue, queue));
                await handle.GetResultAsync();
                var ids = new List<string>();
                await foreach (var e in handle.FetchHistoryEventsAsync())
                    if (e.EventType == EventType.WorkflowTaskCompleted)
#pragma warning disable CS0618
                        ids.Add(e.WorkflowTaskCompletedEventAttributes.WorkerVersion?.BuildId ?? "");
#pragma warning restore CS0618
                return ids;
            });
        }

        var stamped = await BuildIdsAsync("stamped", Commit);
        var unstamped = await BuildIdsAsync("unstamped", "unknown");

        Assert.True(stamped.Count >= 2);
        Assert.All(stamped, id => Assert.Equal(Commit, id));
        Assert.NotEmpty(unstamped);
        Assert.All(unstamped, id => Assert.Null(WorkerSourceCommit.Resolve(id)));
    }

    [Workflow]
    public sealed class TwoTaskWorkflow
    {
        [WorkflowRun]
        public async Task RunAsync() => await Workflow.DelayAsync(TimeSpan.FromMilliseconds(10));
    }
}
