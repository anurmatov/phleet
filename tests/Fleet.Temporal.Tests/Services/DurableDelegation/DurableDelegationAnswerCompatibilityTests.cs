using System.Text.RegularExpressions;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// Agents and the bridge deploy separately, so an answer does not always name an execution, and a
/// delegation does not always have one. These pin each combination; the case the execution id
/// exists for is in <see cref="DurableDelegationAnswerFenceTests"/>.
///
/// <list type="table">
/// <item><term>record has an id, answer names it</term><description>accepted</description></item>
/// <item><term>record has an id, answer names another</term><description>not accepted</description></item>
/// <item><term>record has an id, answer names none</term><description>accepted with a warning;
/// not accepted when <c>RequireAnswerExecutionId</c> is set</description></item>
/// <item><term>record has no id (written before it existed)</term><description>accepted by task
/// id, whatever the answer names</description></item>
/// <item><term>in-memory path (switch off, or no durable record)</term><description>by task id;
/// an id on the answer is ignored</description></item>
/// </list>
/// </summary>
public sealed class DurableDelegationAnswerCompatibilityTests
{
    private const string Agent = "reviewer-one";
    private const string TaskId = "wf-1/review";
    private static readonly AgentTaskResult Done = new("all done", "completed");

    private static TemporalRelayListener Listener(DurableDelegationService? durable, TaskCompletionRegistry? registry = null) => new(
        Options.Create(new RabbitMqOptions()), registry ?? new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
        Substitute.For<ITemporalClientFactory>(), NullLogger<TemporalRelayListener>.Instance, durable);

    private static RelayMessage Answer(string? executionId, string text = "[status: completed] all done") =>
        new(0, Agent, text, DateTimeOffset.UtcNow, RelayMessageType.Response, TaskId: TaskId, ExecutionId: executionId);

    private static async Task<string> StartAsync(DurableHarness h)
    {
        var receiver = new TaskIdOnlyReceiver { ComparesExecutionIds = true };
        h.Relay.Receiver = receiver;
        await h.StartAsync(DurableHarness.Start());
        return receiver.Executions[0].ExecutionId!;
    }

    /// <summary>
    /// An agent from before the id answers without one. Refusing that would leave every
    /// delegation to such an agent to run out its budget, so by default it is accepted, by task id
    /// as before, and the log says so each time, with identifiers only. This is the remaining
    /// risk for older agents: their late answer can still be taken for a successor's.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatNamesNoExecution_IsAcceptedWithAWarning_ByDefault()
    {
        using var h = new DurableHarness();
        var id = await StartAsync(h);
        h.Log.Lines.Clear();

        Assert.True(await Listener(h.Service).TryRouteReplyAsync(Answer(null, "[status: completed] ANSWER-TEXT")));

        Assert.Equal((TaskId, new AgentTaskResult("ANSWER-TEXT", "completed")), Assert.Single(h.Gateway.Completed));
        var warning = Assert.Single(h.Log.Lines, line => line.Contains("names no execution"));
        Assert.StartsWith("Warning:", warning);
        Assert.Contains($"taskId={TaskId} from {Agent}", warning);
        Assert.Contains(id, warning);
        Assert.DoesNotContain(h.Log.Lines, line => line.Contains("ANSWER-TEXT"));
    }

    /// <summary>
    /// Once every agent echoes the id an operator can say so, and an answer without one is then
    /// treated like an answer from another execution. The bridge does not decide this by itself:
    /// an answer does not say how old the agent is.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatNamesNoExecution_IsNotAccepted_WhenTheOperatorRequiresOne()
    {
        using var h = new DurableHarness(configure: o => o.RequireAnswerExecutionId = true);
        var id = await StartAsync(h);

        Assert.Equal(DurableReplyOutcome.OtherExecution, await h.ReplyAsync(TaskId, Done));
        // Settled for the listener too: acknowledged, and not offered to anything else.
        Assert.True(await Listener(h.Service).TryRouteReplyAsync(Answer(null)));
        Assert.Empty(h.Gateway.Completed);
        Assert.Equal("waiting", Assert.Single(await h.PendingAsync()).State);

        Assert.True(await Listener(h.Service).TryRouteReplyAsync(Answer(id)));
        Assert.Equal((TaskId, Done), Assert.Single(h.Gateway.Completed));
    }

    [Fact]
    public async Task AnAnswerThatNamesAnotherExecution_IsNotAccepted_AndSaysSoWithIdentifiersOnly()
    {
        using var h = new DurableHarness();
        var id = await StartAsync(h);
        h.Log.Lines.Clear();

        Assert.Equal(
            DurableReplyOutcome.OtherExecution,
            await h.Service.TryHandleReplyAsync(TaskId, new AgentTaskResult("ANSWER-TEXT", "completed"), default, "another-execution", Agent));

        Assert.Empty(h.Gateway.Completed);
        var line = Assert.Single(h.Log.Lines);
        Assert.Contains($"taskId={TaskId} from {Agent} names execution another-execution", line);
        Assert.Contains(id, line);
        Assert.DoesNotContain("ANSWER-TEXT", line);
    }

    /// <summary>
    /// A record written before the id existed has none. It was matched by task id then and still
    /// is, whether or not the agent has since started naming executions.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("some-execution")]
    public async Task ARecordFromBeforeTheId_AcceptsAnAnswerByTaskId(string? answerExecutionId)
    {
        using var h = new DurableHarness(configure: o => o.RequireAnswerExecutionId = true);
        await h.StartAsync(DurableHarness.Start());
        var file = File.ReadAllText(h.StateFile);
        var stripped = Regex.Replace(file, "\\s*\"ExecutionId\": \"[0-9a-f]{32}\",", "");
        Assert.NotEqual(file, stripped);
        File.WriteAllText(h.StateFile, stripped);
        h.Restart();

        Assert.True(await Listener(h.Service).TryRouteReplyAsync(Answer(answerExecutionId)));

        Assert.Equal((TaskId, Done), Assert.Single(h.Gateway.Completed));
    }

    /// <summary>
    /// The in-memory path knows a delegation by its task id. An execution id on an answer for it
    /// is ignored like any field it does not know: with the switch off, and with the switch on
    /// when the delegation is not a durable record.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheInMemoryPath_IgnoresAnExecutionIdOnAnAnswer(bool durableOn)
    {
        using var h = new DurableHarness(enabled: durableOn);
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var waiting = registry.Register(TaskId);

        Assert.True(await Listener(h.Service, registry).TryRouteReplyAsync(Answer("some-execution")));

        Assert.Equal(Done, await waiting.Task);
        Assert.Empty(h.Gateway.Calls);
    }
}
