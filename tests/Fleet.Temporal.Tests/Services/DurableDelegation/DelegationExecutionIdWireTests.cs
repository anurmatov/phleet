using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Temporalio.Testing;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// What a durable delegation's execution id changes on the wire, and what it leaves alone, read
/// from the real <see cref="DelegationRelay"/> and the real in-process activity over a recording
/// broker and orchestrator.
/// </summary>
[Collection("fleet-workflow-config-static")]
public sealed class DelegationExecutionIdWireTests : IDisposable
{
    private const string Agent = "reviewer-one";
    private const string TaskId = "wf-1/review";
    private static readonly string[] DirectiveFields =
        ["ChatId", "Sender", "Text", "Timestamp", "Type", "CorrelationId", "TaskId", "WorkflowId", "SignalName", "Repo"];

    public DelegationExecutionIdWireTests() => Configure("coordinator", loaded: true);

    public void Dispose() => Configure(null, loaded: false);

    // The real relay sends nothing until the bridge has its configuration.
    private static void Configure(string? ctoAgent, bool loaded)
    {
        typeof(FleetWorkflowConfig).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, ctoAgent is null ? null : new FleetWorkflowOptions { CtoAgent = ctoAgent });
        typeof(FleetWorkflowConfig).GetField("_peerConfigLoaded", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, loaded ? 1 : 0);
    }

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static string? ExecutionIdOn(string cancelUrl)
    {
        var match = Regex.Match(cancelUrl, @"[?&]executionId=([^&]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static DelegationStart Attempt(int attempt, string instruction) =>
        DurableHarness.Start(instruction: instruction, budgetSeconds: 3600) with { Attempt = attempt };

    // ── what the id changes on the wire, and what it does not ─────────────────

    /// <summary>
    /// The directive of a delegation that has an id is the directive it would have been without
    /// one, plus the field, last. An agent that does not know the field reads the rest as before.
    /// </summary>
    [Fact]
    public async Task ADirectiveWithAnExecutionId_IsTheSameDirective_PlusOneField()
    {
        var wire = new RecordingWire();
        var relay = wire.Relay();
        PendingDelegation Record(string executionId) => new()
        {
            TaskId = TaskId, CurrentTaskId = TaskId, Agent = Agent, CurrentInstruction = "do the work",
            Repo = "owner/name", WorkflowId = "wf-1", WorkflowType = "example-workflow", ExecutionId = executionId,
        };

        await relay.PublishDirectiveAsync(Record(""), default);
        await relay.PublishDirectiveAsync(Record("0123456789abcdef0123456789abcdef"), default);

        var without = Json(wire.Published[0].Body);
        var with = Json(wire.Published[1].Body);
        Assert.Equal(DirectiveFields, without.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal([.. DirectiveFields, "ExecutionId"], with.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("0123456789abcdef0123456789abcdef", with.GetProperty("ExecutionId").GetString());
        foreach (var field in DirectiveFields.Where(f => f != "Timestamp"))
            Assert.Equal(without.GetProperty(field).GetRawText(), with.GetProperty(field).GetRawText());
    }

    /// <summary>
    /// With the switch off nothing changes: the in-process activity publishes the same ten fields
    /// and no execution id, and its cancel has no query.
    /// </summary>
    [Fact]
    public async Task TheInProcessPath_SendsNoExecutionId()
    {
        var wire = new RecordingWire();
        var registry = new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance);
        var activity = new DelegateToAgentActivity(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { AgentTimeoutSeconds = 30, OrchestratorUrl = "http://orchestrator.invalid" }),
            registry, wire.Broker, wire.Http, NullLogger<DelegateToAgentActivity>.Instance);
        using var cancel = new CancellationTokenSource();

        var run = new ActivityEnvironment { CancellationTokenSource = cancel }
            .RunAsync(() => activity.DelegateToAgentAsync(Agent, "do the work", TaskId));
        await DurableHarness.WaitForAsync(() => wire.Published.Count == 1, "the activity published its directive");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(DirectiveFields, Json(wire.Published[0].Body).EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("ExecutionId", wire.Published[0].Body);
        Assert.Equal($"http://orchestrator.invalid/api/agents/{Agent}/cancel/wf-1%2Freview", Assert.Single(wire.CancelUrls));
    }

    /// <summary>
    /// A record written before the id existed has none, and is not given one: its agent task was
    /// started by a directive without it, so a cancel with a freshly made id would match nothing.
    /// It goes on sending what it always sent, and its cancel is matched by task id.
    /// </summary>
    [Fact]
    public async Task ARecordFromBeforeTheId_SendsWhatItAlwaysSent()
    {
        var wire = new RecordingWire();
        using var h = new DurableHarness(relay: wire.Relay());
        await h.StartAsync(Attempt(1, "do the work"));
        var file = File.ReadAllText(h.StateFile);
        var stripped = Regex.Replace(file, "\\s*\"ExecutionId\": \"[0-9a-f]{32}\",", "");
        Assert.NotEqual(file, stripped);
        File.WriteAllText(h.StateFile, stripped);

        h.Restart();
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await h.TickAsync();
        Assert.Equal(2, wire.Published.Count);
        Assert.Equal(DirectiveFields, Json(wire.Published[1].Body).EnumerateObject().Select(p => p.Name).ToArray());

        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        await h.TickAsync();
        Assert.Equal($"http://orchestrator.invalid/api/agents/{Agent}/cancel/wf-1%2Freview", Assert.Single(wire.CancelUrls));
    }

    /// <summary>
    /// The broadcast fallback tells the agent to stop everything it is doing, whenever it reads the
    /// message, which cannot be held to one execution. A delegation that has an id is therefore
    /// cancelled through the orchestrator or not at all; one that has none keeps the fallback.
    /// </summary>
    [Fact]
    public async Task ACancelThatNamesAnExecution_IsNeverTurnedIntoTheBroadcast()
    {
        PendingDelegation Record(string executionId) => new()
        {
            TaskId = TaskId, CurrentTaskId = TaskId, Agent = Agent, WorkflowId = "wf-1",
            WorkflowType = "example-workflow", ExecutionId = executionId,
        };

        var unreachable = new RecordingWire { OrchestratorAnswers = null };
        Assert.Equal(CancelDelivery.Unknown, await unreachable.Relay().SendCancelAsync(Record("execution-1")));
        Assert.Single(unreachable.CancelUrls);
        Assert.Empty(unreachable.Published);

        var noUrl = new RecordingWire();
        Assert.Equal(CancelDelivery.Unknown, await noUrl.Relay(orchestratorUrl: "").SendCancelAsync(Record("execution-1")));
        Assert.Empty(noUrl.CancelUrls);
        Assert.Empty(noUrl.Published);

        // Without an id, as before: the broadcast.
        var legacy = new RecordingWire { OrchestratorAnswers = null };
        Assert.Equal(CancelDelivery.Unknown, await legacy.Relay().SendCancelAsync(Record("")));
        Assert.Contains("/cancel all", Assert.Single(legacy.Published).Body);
    }

    /// <summary>
    /// What the orchestrator and the agent answer does not change what the bridge does with a
    /// cancel: a 2xx is settled whether or not the id was compared. An agent or an orchestrator
    /// from before the id answers without it, the cancel was then matched by task id, and the
    /// bridge's own order and hold are what protect a successor there.
    /// </summary>
    [Theory]
    [InlineData("""{"cancelled":true,"taskId":"wf-1/review","executionId":"execution-1","outcome":"cancelled"}""")]
    [InlineData("""{"cancelled":false,"taskId":"wf-1/review","executionId":"execution-1","outcome":"other_execution"}""")]
    [InlineData("""{"cancelled":false,"taskId":"wf-1/review","executionId":"execution-1","outcome":"not_found"}""")]
    [InlineData("""{"cancelled":true,"taskId":"wf-1/review"}""")]
    [InlineData("""{"message":"Cancel requested for task 'wf-1/review' on agent 'reviewer-one'"}""")]
    [InlineData("not json")]
    public async Task AnyAnswerToACancelThatNamedAnExecution_IsSettled(string answer)
    {
        var wire = new RecordingWire
        {
            OrchestratorAnswers = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) },
        };
        var record = new PendingDelegation
        {
            TaskId = TaskId, CurrentTaskId = TaskId, Agent = Agent, WorkflowId = "wf-1",
            WorkflowType = "example-workflow", ExecutionId = "execution-1",
        };

        Assert.Equal(CancelDelivery.Settled, await wire.Relay().SendCancelAsync(record));
        Assert.Equal("execution-1", ExecutionIdOn(Assert.Single(wire.CancelUrls)));
    }
}
