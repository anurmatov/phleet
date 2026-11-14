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
/// A task id is reused when a workflow runs a step again, so it cannot say which attempt a cancel
/// was for. Each durable delegation therefore has an execution id: minted once when its record is
/// created, kept in the state file, written on every directive published for it and on every
/// cancel issued for it. The agent compares it; the agent's side is tested against its own code
/// in <c>BridgeExecutionCancelTests</c>.
///
/// <para>
/// These are regressions for the bridge that had no such id and relied on order and a
/// thirty-second hold. They use only what that bridge had, so they can be run against it and seen
/// to fail. What the id changes on the wire, field by field, is in
/// <see cref="DelegationExecutionIdWireTests"/>.
/// </para>
/// </summary>
[Collection("fleet-workflow-config-static")]
public sealed class DurableDelegationExecutionIdTests : IDisposable
{
    private const string Agent = "reviewer-one";
    private const string TaskId = "wf-1/review";

    public DurableDelegationExecutionIdTests() => Configure("coordinator", loaded: true);

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

    private static string? ExecutionIdIn(string directive) =>
        Json(directive).TryGetProperty("ExecutionId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private static DelegationStart Attempt(int attempt, string instruction) =>
        DurableHarness.Start(instruction: instruction, budgetSeconds: 3600) with { Attempt = attempt };

    // ── regressions ───────────────────────────────────────────────────────────

    /// <summary>
    /// One id per delegation, on everything the bridge sends for it: the first directive, a
    /// re-send, a continuation (a new task id on the agent, the same delegation), and the cancel,
    /// which after a continuation names the continuation's task. The id is in the state file, so a
    /// bridge that restarts between the directive and the cancel sends the cancel with the id the
    /// directive had. Another delegation gets another id.
    /// </summary>
    [Fact]
    public async Task TheExecutionId_IsMintedOncePerDelegation_IsOnEveryDirectiveAndTheCancel_AndIsTheSameAfterARestart()
    {
        var wire = new RecordingWire();
        using var h = new DurableHarness(relay: wire.Relay());

        await h.StartAsync(Attempt(1, "do the work"));
        var id = ExecutionIdIn(wire.Published[0].Body);
        Assert.NotNull(id);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(TaskId, Json(wire.Published[0].Body).GetProperty("TaskId").GetString());

        // The five-minute re-send.
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await h.TickAsync();
        Assert.Equal(2, wire.Published.Count);
        Assert.Equal(id, ExecutionIdIn(wire.Published[1].Body));

        // A continuation of an incomplete answer.
        await h.ReplyAsync(TaskId, new AgentTaskResult("part one", "incomplete"));
        Assert.Equal(3, wire.Published.Count);
        Assert.Equal($"{TaskId}/incomplete-retry-1", Json(wire.Published[2].Body).GetProperty("TaskId").GetString());
        Assert.Equal(id, ExecutionIdIn(wire.Published[2].Body));

        // A new process: only the file.
        Assert.Contains($"\"ExecutionId\": \"{id}\"", File.ReadAllText(h.StateFile));
        h.Restart();
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        await h.TickAsync();

        var cancel = Assert.Single(wire.CancelUrls);
        Assert.Equal(
            $"http://orchestrator.invalid/api/agents/{Agent}/cancel/wf-1%2Freview%2Fincomplete-retry-1?executionId={id}",
            cancel);
        Assert.Empty(await h.PendingAsync());

        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        await h.StartAsync(Attempt(2, "do the work again"));
        var next = ExecutionIdIn(wire.Published[3].Body);
        Assert.NotNull(next);
        Assert.NotEqual(id, next);
    }

    /// <summary>
    /// The bridge restarts between issuing a cancel and delivering it. After recovery the cancel
    /// names the execution it was issued for, with the id that execution's directive carried, and
    /// the successor that arrives meanwhile has an id of its own. At a receiver that compares ids
    /// the cancel stops execution 1 and nothing else — also when the very same cancel is delivered
    /// once more after execution 2 has started, which no hold on the bridge's side can rule out.
    /// </summary>
    [Fact]
    public async Task ARestartBetweenIssuingACancelAndDeliveringIt_KeepsItsExecutionId_AndItReachesOnlyItsOwnExecution()
    {
        using var h = new DurableHarness();
        var agent = new TaskIdOnlyReceiver { ComparesExecutionIds = true };
        h.Relay.Receiver = agent;
        await h.StartAsync(Attempt(1, "first attempt"));
        var first = agent.Executions[0].ExecutionId;
        Assert.False(string.IsNullOrEmpty(first));

        // Issued: Temporal is told the activity is cancelled. Not delivered: the relay cannot send.
        h.Gateway.Heartbeat = ActivityCallOutcome.CancelRequested;
        h.Relay.Ready = false;
        await h.TickAsync();
        Assert.Single(h.Gateway.Cancelled);
        Assert.Empty(h.Relay.CancelRequests);

        h.Restart();
        h.Gateway.Heartbeat = ActivityCallOutcome.Ok;
        await h.StartAsync(Attempt(2, "second attempt"));
        h.Relay.Ready = true;
        await h.TickAsync();

        var cancel = Assert.Single(h.Relay.CancelRequests);
        Assert.Equal((Agent, TaskId, first), cancel);
        Assert.Equal([$"directive:{TaskId}→#1", $"cancel:{TaskId}→#1", $"directive:{TaskId}→#2"], agent.Log);
        var second = agent.Serving("second attempt")!;
        Assert.False(string.IsNullOrEmpty(second.ExecutionId));
        Assert.NotEqual(first, second.ExecutionId);

        // The same cancel again, late: execution 2 holds the task id now.
        agent.Cancel(cancel.Agent, cancel.TaskId, cancel.ExecutionId);

        Assert.Equal($"cancel:{TaskId}→nothing", agent.Log[^1]);
        Assert.True(second.Running);
    }
}
