using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// A later attempt of a workflow step has the task id its predecessor had, and an answer used to
/// be matched to a pending delegation by task id alone. A predecessor's answer that arrived after
/// its successor had started was therefore taken for the successor's: it completed the successor's
/// activity with the answer to another instruction. The agent now echoes on every answer the
/// execution id its task carried, and the bridge accepts an answer for a durable record only when
/// that id is the record's.
///
/// <para>
/// Through the real <see cref="TemporalRelayListener"/> reply routing and the real
/// <see cref="DurableDelegationService"/>. These are regressions for the bridge that matched
/// answers by task id; they use only what that bridge had, so they can be run against it and seen
/// to fail. What happens with an answer that names no execution, and with records and paths that
/// have no id, is in <see cref="DurableDelegationAnswerCompatibilityTests"/>.
/// </para>
/// </summary>
public sealed class DurableDelegationAnswerFenceTests
{
    private const string Agent = "reviewer-one";
    private const string TaskId = "wf-1/review";

    private sealed class Scenario : IDisposable
    {
        public DurableHarness H { get; } = new();
        public TaskIdOnlyReceiver Receiver { get; } = new() { ComparesExecutionIds = true };
        public TaskCompletionRegistry Registry { get; } = new(NullLogger<TaskCompletionRegistry>.Instance);

        /// <summary>The execution id the first attempt's directive carried, and the second's.</summary>
        public string First { get; private set; } = "";
        public string Second { get; private set; } = "";

        /// <summary>The listener of the bridge process that is running now.</summary>
        public TemporalRelayListener Listener => new(
            Options.Create(new RabbitMqOptions()), Registry, Substitute.For<ITemporalClientFactory>(),
            NullLogger<TemporalRelayListener>.Instance, H.Service);

        /// <summary>
        /// The first attempt is cancelled by its workflow and the step is run again: the second
        /// attempt is pending under the same task id, and its directive is with the agent.
        /// </summary>
        public static async Task<Scenario> SuccessorPendingAsync()
        {
            var s = new Scenario();
            s.H.Relay.Receiver = s.Receiver;

            await s.H.StartAsync(Attempt(1, "first attempt"));
            s.First = s.Receiver.Executions[0].ExecutionId ?? "";

            s.H.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
            await s.H.TickAsync();
            s.H.Gateway.Heartbeat = ActivityCallOutcome.Ok;

            await s.H.StartAsync(Attempt(2, "second attempt"));
            s.Second = s.Receiver.Serving("second attempt")?.ExecutionId ?? "";

            Assert.False(string.IsNullOrEmpty(s.First));
            Assert.False(string.IsNullOrEmpty(s.Second));
            Assert.NotEqual(s.First, s.Second);
            Assert.Equal("waiting", Assert.Single(await s.H.PendingAsync()).State);
            return s;
        }

        public void Dispose() => H.Dispose();
    }

    private static DelegationStart Attempt(int attempt, string instruction) =>
        DurableHarness.Start(instruction: instruction, budgetSeconds: 3600) with { Attempt = attempt };

    /// <summary>An answer as the agent publishes it, naming the execution its task carried.</summary>
    private static RelayMessage Answer(string taskId, string text, string executionId, string type = RelayMessageType.Response) =>
        new(0, Agent, text, DateTimeOffset.UtcNow, type, TaskId: taskId, ExecutionId: executionId);

    /// <summary>
    /// The first attempt's answer arrives after the second attempt has started. It is settled (the
    /// message is acknowledged; nothing else is waiting for it), it is not the second attempt's
    /// answer, and nothing of it reaches the log. The second attempt's own answer completes it.
    /// </summary>
    [Fact]
    public async Task APredecessorsLateAnswer_IsNotAcceptedByTheSuccessor_AndTheSuccessorsOwnAnswerCompletesIt()
    {
        using var s = await Scenario.SuccessorPendingAsync();
        var listener = s.Listener;

        Assert.True(await listener.TryRouteReplyAsync(
            Answer(TaskId, "[status: completed] ANSWER-TO-THE-FIRST-ATTEMPT", s.First)));

        Assert.Empty(s.H.Gateway.Completed);
        Assert.Equal("waiting", Assert.Single(await s.H.PendingAsync()).State);
        Assert.DoesNotContain(s.H.Log.Lines, line => line.Contains("ANSWER-TO-THE-FIRST-ATTEMPT"));

        Assert.True(await listener.TryRouteReplyAsync(
            Answer(TaskId, "[status: completed] the second attempt's answer", s.Second)));

        Assert.Equal(
            (TaskId, new AgentTaskResult("the second attempt's answer", "completed")),
            Assert.Single(s.H.Gateway.Completed));
        Assert.Empty(await s.H.PendingAsync());
    }

    /// <summary>
    /// The same for a partial answer, which would otherwise have become the start of the second
    /// attempt's answer and sent a continuation to the agent; and for the first attempt's answer
    /// to its own continuation, whose task id is the one the second attempt's continuation has.
    /// </summary>
    [Fact]
    public async Task APredecessorsLatePartialAnswer_IsNotAddedToTheSuccessorsAnswer_AndStartsNoContinuation()
    {
        using var s = await Scenario.SuccessorPendingAsync();
        var listener = s.Listener;
        var published = s.H.Relay.Published.Count;

        Assert.True(await listener.TryRouteReplyAsync(
            Answer(TaskId, "half of the first attempt", s.First, RelayMessageType.PartialResponse)));
        await s.H.SettleAsync();

        Assert.Equal(published, s.H.Relay.Published.Count);
        Assert.Equal(TaskId, Assert.Single(await s.H.PendingAsync()).CurrentTaskId);

        // The second attempt's own partial answer is continued.
        Assert.True(await listener.TryRouteReplyAsync(
            Answer(TaskId, "own part", s.Second, RelayMessageType.PartialResponse)));
        await s.H.SettleAsync();
        Assert.Equal($"{TaskId}/incomplete-retry-1", s.H.Relay.Published[^1].TaskId);

        // The first attempt had a continuation too, and its answer arrives now.
        Assert.True(await listener.TryRouteReplyAsync(
            Answer($"{TaskId}/incomplete-retry-1", "[status: completed] the rest of the first attempt", s.First)));
        Assert.Empty(s.H.Gateway.Completed);

        Assert.True(await listener.TryRouteReplyAsync(
            Answer($"{TaskId}/incomplete-retry-1", "[status: completed] own rest", s.Second)));
        Assert.Equal(new AgentTaskResult("own part\nown rest", "completed"), Assert.Single(s.H.Gateway.Completed).Result);
    }

    /// <summary>
    /// Across a bridge restart the record still knows which execution it is, so the first
    /// attempt's answer is still not accepted. Temporal cannot be told anything at that moment, so
    /// an accepted answer would be kept in the record and delivered later: it is not kept either.
    /// The second attempt's own answer is, and after another restart that is what Temporal gets.
    /// </summary>
    [Fact]
    public async Task AcrossARestart_APredecessorsAnswerIsNotAccepted_AndIsNotKeptForTemporal()
    {
        using var s = await Scenario.SuccessorPendingAsync();
        s.H.Restart();
        s.H.Gateway.Complete = ActivityCallOutcome.Unavailable;

        Assert.True(await s.Listener.TryRouteReplyAsync(
            Answer(TaskId, "[status: completed] the first attempt's answer", s.First)));
        Assert.Equal("waiting", Assert.Single(await s.H.PendingAsync()).State);

        Assert.True(await s.Listener.TryRouteReplyAsync(
            Answer(TaskId, "[status: completed] the second attempt's answer", s.Second)));
        Assert.Equal("answer-recorded", Assert.Single(await s.H.PendingAsync()).State);

        s.H.Restart();
        s.H.Gateway.Complete = ActivityCallOutcome.Ok;
        await s.H.TickAsync();

        Assert.Equal(
            (TaskId, new AgentTaskResult("the second attempt's answer", "completed")),
            Assert.Single(s.H.Gateway.Completed));
        Assert.Empty(await s.H.PendingAsync());
    }
}
