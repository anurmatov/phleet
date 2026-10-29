using System.Text.Json;
using Fleet.Agent;
using Fleet.Agent.Models;
using Fleet.Bridge.Configuration;
using Fleet.Bridge.Services;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Fleet.Agent.Tests;

/// <summary>
/// #492 managed workflow delegation, agent side: a typed workflow directive is admitted only from the epoch
/// control principal on fleet.group routed to this agent with this enrollment's sandbox and epoch, runs as an
/// ordinary Temporal directive, and its reply goes out as a typed workflow reply on the bridge reply route.
/// </summary>
public sealed class ManagedStockAgentWorkflowDirectiveTests
{
    private static AsyncEventingBasicConsumer Consumer(IChannel channel) =>
        channel.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();

    private static object[][] Publishes(IChannel channel) => channel.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "BasicPublishAsync").Select(c => c.GetArguments()).ToArray();

    private static byte[] Directive(Guid epoch, string sandbox = "synthetic-sandbox", string? taskId = "wf/task-1",
        string? session = null, string sender = "temporal-bridge", DateTimeOffset? deadline = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 0, Sender = sender, Text = "synthetic task", Timestamp = DateTimeOffset.UtcNow, Type = "workflow-directive",
            CorrelationId = session ?? Guid.NewGuid().ToString("N"), TaskId = taskId, Epoch = epoch, Sandbox = sandbox,
            Deadline = deadline
        });

    [Fact]
    public async Task AdmittedDirective_RunsWithTaskId_AndRepliesOnTheBridgeRouteWithItsContext()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = new List<(string Sender, string Type, string? Correlation, string? TaskId)>();
        relay.MessageReceived += (_, sender, _, type, correlation, taskId, _, _) => received.Add((sender, type, correlation, taskId));
        await relay.InitializeAsync(default);
        var session = Guid.NewGuid().ToString("N");

        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1",
            new BasicProperties { UserId = fixture.Binding.Principal("control") }, Directive(fixture.Epoch, session: session));

        var admitted = Assert.Single(received);
        // #492 F1: the turn carries its own session, so only that session can answer it.
        Assert.Equal(("temporal-bridge", RelayMessageType.Directive, (string?)session, (string?)"wf/task-1"), admitted);

        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        var publish = Assert.Single(Publishes(fixture.Channel));
        Assert.Equal("fleet.group", publish[0]);
        Assert.Equal("bridge", publish[1]);
        Assert.Equal(fixture.Binding.Principal("workload"), publish.OfType<BasicProperties>().Single().UserId);
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)publish[4]!);
        Assert.Equal("agent1", reply.RootElement.GetProperty("Sender").GetString());
        Assert.Equal("response", reply.RootElement.GetProperty("Type").GetString());
        Assert.Equal("wf/task-1", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(fixture.Epoch, reply.RootElement.GetProperty("Epoch").GetGuid());
        Assert.Equal("synthetic-sandbox", reply.RootElement.GetProperty("Sandbox").GetString());

        // The session is consumed: a second reply for the same TaskId is not a workflow reply any more.
        Assert.Equal(0, relay.WorkflowSessionCount);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "again", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Single(Publishes(fixture.Channel));
    }

    private static void AssertWorkflowReply(byte[] body, string type, string session, Guid epoch)
    {
        using var reply = JsonDocument.Parse(body);
        Assert.Equal("agent1", reply.RootElement.GetProperty("Sender").GetString());
        Assert.Equal(type, reply.RootElement.GetProperty("Type").GetString());
        Assert.Equal("wf/task-1", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(epoch, reply.RootElement.GetProperty("Epoch").GetGuid());
        Assert.Equal("synthetic-sandbox", reply.RootElement.GetProperty("Sandbox").GetString());
    }

    // ---- #492 R5: the agent-side session survives a failed reply publication ----

    [Fact]
    public async Task FailedFirstReplyPublication_KeepsTheSession_AndTheRetryPublishesTheOriginalContextOnce()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        var outage = true;
        var published = new List<(string RoutingKey, byte[] Body)>();
        fixture.Channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (outage) return ValueTask.FromException(new IOException("synthetic publish outage"));
            published.Add(((string)call[1], ((ReadOnlyMemory<byte>)call[4]).ToArray()));
            return ValueTask.CompletedTask;
        });
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1",
            new BasicProperties { UserId = fixture.Binding.Principal("control") }, Directive(fixture.Epoch, session: session));

        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Single(Publishes(fixture.Channel)); // the broker was attempted and failed
        Assert.Empty(published);
        Assert.Equal(1, relay.WorkflowSessionCount); // retained for the retry

        outage = false;
        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        var reply = Assert.Single(published);
        Assert.Equal("bridge", reply.RoutingKey);
        AssertWorkflowReply(reply.Body, "response", session, fixture.Epoch);
        Assert.Equal(0, relay.WorkflowSessionCount); // freed only by the successful final publish

        await relay.PublishToAgentAsync("temporal-bridge", 0, "again", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Single(published);
        Assert.Equal(2, Publishes(fixture.Channel).Length); // no third broker attempt
    }

    [Fact]
    public async Task PartialResponse_DoesNotConsumeTheSession_SoTheFinalReplyCarriesTheContextToo()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1",
            new BasicProperties { UserId = fixture.Binding.Principal("control") }, Directive(fixture.Epoch, session: session));

        await relay.PublishToAgentAsync("temporal-bridge", 0, "partial", RelayMessageType.PartialResponse, correlationId: session, taskId: "wf/task-1");
        Assert.Equal(1, relay.WorkflowSessionCount);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "final", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Equal(0, relay.WorkflowSessionCount);

        var publishes = Publishes(fixture.Channel);
        Assert.Equal(2, publishes.Length);
        Assert.All(publishes, p => Assert.Equal("bridge", p[1]));
        AssertWorkflowReply(((ReadOnlyMemory<byte>)publishes[0][4]!).ToArray(), "partial-response", session, fixture.Epoch);
        AssertWorkflowReply(((ReadOnlyMemory<byte>)publishes[1][4]!).ToArray(), "response", session, fixture.Epoch);
    }

    // ---- #492: cancel and timeout free the agent-side session ----

    [Fact]
    public async Task TemporalCancel_ThroughTheRealCancelEndpoint_FreesOnlyThatSession_AndTheCancelledReplyIsNotPublished()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };
        var (session, other) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1", props, Directive(fixture.Epoch, session: session));
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/other", session: other));
        Assert.Equal(2, relay.WorkflowSessionCount);

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear(); // no baked Kestrel endpoint: an ephemeral loopback port only
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var options = Options.Create(new Fleet.Agent.Configuration.AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "codex" });
        builder.Services.AddSingleton(new Fleet.Agent.Services.TaskManager(options, Substitute.For<Fleet.Agent.Services.IAgentExecutor>(),
            new Fleet.Agent.Services.SessionManager(), NullLogger<Fleet.Agent.Services.TaskManager>.Instance));
        builder.Services.AddSingleton(relay);
        await using var app = builder.Build();
        app.UseControlGuard(stockMode: true, managedStock: true);
        app.MapCancelEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var response = await client.PostAsync("/cancel/wf%2Ftask-1", null);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(1, relay.WorkflowSessionCount); // only the cancelled task's session is freed

        await relay.PublishToAgentAsync("temporal-bridge", 0, "Task cancelled.", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Empty(Publishes(fixture.Channel));
        await relay.PublishToAgentAsync("temporal-bridge", 0, "other result", RelayMessageType.Response, correlationId: other, taskId: "wf/other");
        Assert.Equal("bridge", Assert.Single(Publishes(fixture.Channel))[1]);
    }

    [Fact]
    public async Task TimedOutWorkflow_SessionIdlePastTheTimeout_IsFreed_WhileAResentDirectiveKeepsItsSessionAlive()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var now = DateTimeOffset.UtcNow;
        relay.WorkflowClock = () => now;
        await relay.InitializeAsync(default);
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };
        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, session: session));
        var timedOut = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/timed-out", session: timedOut));

        // Temporal still waiting on wf/task-1 re-sends it (same session); wf/timed-out is never re-sent.
        now += Fleet.Agent.Services.GroupRelayService.WorkflowSessionIdleTimeout / 2;
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 3, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, session: session));
        now += Fleet.Agent.Services.GroupRelayService.WorkflowSessionIdleTimeout / 2 + TimeSpan.FromSeconds(1);

        await relay.PublishToAgentAsync("temporal-bridge", 0, "late result", RelayMessageType.Response, correlationId: timedOut, taskId: "wf/timed-out");
        Assert.Empty(Publishes(fixture.Channel));
        Assert.Equal(1, relay.WorkflowSessionCount);

        await relay.PublishToAgentAsync("temporal-bridge", 0, "synthetic result", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        var publish = Assert.Single(Publishes(fixture.Channel));
        AssertWorkflowReply(((ReadOnlyMemory<byte>)publish[4]!).ToArray(), "response", session, fixture.Epoch);
        Assert.Equal(0, relay.WorkflowSessionCount);
    }

    [Fact]
    public async Task PublishOutageLongerThanTheIdleBound_KeepsTheSessionUntilTheActivityDeadline()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var now = DateTimeOffset.UtcNow;
        relay.WorkflowClock = () => now;
        await relay.InitializeAsync(default);
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };
        var idle = Fleet.Agent.Services.GroupRelayService.WorkflowSessionIdleTimeout;
        var session = Guid.NewGuid().ToString("N");
        // The activity's own deadline is four idle bounds away; wf/legacy carries none (an older sender).
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, session: session, deadline: now + idle * 4));
        var legacy = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/legacy", session: legacy));

        // Temporal cannot publish any resend for three idle bounds (#492 X4): the deadline keeps wf/task-1.
        now += idle * 3;
        await relay.PublishToAgentAsync("temporal-bridge", 0, "late legacy result", RelayMessageType.Response, correlationId: legacy, taskId: "wf/legacy");
        Assert.Empty(Publishes(fixture.Channel));
        Assert.Equal(1, relay.WorkflowSessionCount);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "late result", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        var publish = Assert.Single(Publishes(fixture.Channel));
        AssertWorkflowReply(((ReadOnlyMemory<byte>)publish[4]!).ToArray(), "response", session, fixture.Epoch);
        Assert.Equal(0, relay.WorkflowSessionCount);

        // Past its deadline the activity no longer accepts the reply, so the session is freed.
        var expiring = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 3, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/expiring", session: expiring, deadline: now + idle * 2));
        // A resend without a later deadline never shortens the expiry already held.
        now += idle / 2;
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 4, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/expiring", session: expiring));
        now += idle + TimeSpan.FromSeconds(1); // past the resend's own idle bound, before the deadline
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 5, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/probe")); // admission runs the expiry pass
        Assert.Equal(2, relay.WorkflowSessionCount);
        now += idle / 2; // past wf/expiring's deadline; wf/probe is still within its idle bound
        await relay.PublishToAgentAsync("temporal-bridge", 0, "too late", RelayMessageType.Response, correlationId: expiring, taskId: "wf/expiring");
        Assert.Single(Publishes(fixture.Channel));
        Assert.Equal(1, relay.WorkflowSessionCount);
    }

    public static TheoryData<string> Foreign() => new()
    {
        "workload user", "no user", "foreign route", "foreign exchange", "foreign epoch", "foreign sandbox",
        "no task", "bad session", "foreign sender", "revoked",
    };

    [Theory]
    [MemberData(nameof(Foreign))]
    public async Task ForeignOrStaleDirective_IsRefused_AndNoWorkflowReplyCanBePublished(string fault)
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = 0;
        relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        await relay.InitializeAsync(default);

        var user = fault switch { "workload user" => fixture.Binding.Principal("workload"), "no user" => null, _ => fixture.Binding.Principal("control") };
        var session = Guid.NewGuid().ToString("N");
        var body = fault switch
        {
            "foreign epoch" => Directive(Guid.NewGuid(), session: session),
            "foreign sandbox" => Directive(fixture.Epoch, sandbox: "other-sandbox", session: session),
            "no task" => Directive(fixture.Epoch, taskId: null, session: session),
            "bad session" => Directive(fixture.Epoch, session: "not-a-session"),
            "foreign sender" => Directive(fixture.Epoch, sender: "agent2", session: session),
            _ => Directive(fixture.Epoch, session: session),
        };
        if (fault == "revoked") fixture.Save("revoked");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false,
            fault == "foreign exchange" ? "fleet.other" : "fleet.group", fault == "foreign route" ? "agent2" : "agent1",
            new BasicProperties { UserId = user }, body);

        Assert.Equal(0, received);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "must not publish", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Empty(Publishes(fixture.Channel));
    }

    // ---- #492 W6 lows ----

    [Fact]
    public async Task UnrepliedSessions_AreBounded_SoLaterDirectivesAreStillAdmitted_AndAnswerable()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = 0;
        relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        await relay.InitializeAsync(default);
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };

        // More unreplied (cancelled or timed-out) tasks than the bound: none may wedge the map.
        for (var i = 0; i < Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions + 5; i++)
            await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", (ulong)i + 1, false, "fleet.group", "agent1",
                props, Directive(fixture.Epoch, taskId: "wf/unreplied-" + i));
        Assert.Equal(Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions, relay.WorkflowSessionCount);

        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 9999, false, "fleet.group", "agent1",
            props, Directive(fixture.Epoch, taskId: "wf/fresh", session: session));
        Assert.Equal(Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions + 6, received);

        await relay.PublishToAgentAsync("temporal-bridge", 0, "fresh result", RelayMessageType.Response, correlationId: session, taskId: "wf/fresh");
        var publish = Assert.Single(Publishes(fixture.Channel));
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)publish[4]!);
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(Fleet.Agent.Services.GroupRelayService.MaxWorkflowSessions - 1, relay.WorkflowSessionCount); // the reply frees its entry
    }

    // ---- #492 F1/F3: a turn answers only its own session; an expired delivery is refused ----

    [Fact]
    public async Task RetriedDirectiveWithANewSession_DoesNotRetagTheRunningTurnsCompletion()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        // The fixture's relay, with a log capture so the refused completion can be awaited.
        var log = new HostedProviderForwarderTests.ListLoggerProvider();
        using var logs = LoggerFactory.Create(b => b.AddProvider(log));
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(fixture.Channel));
        await using var relay = new Fleet.Agent.Services.GroupRelayService(
            Options.Create(new Fleet.Agent.Configuration.AgentOptions { Name = "agent1", ShortName = "agent1", Role = "synthetic", WorkDir = fixture.WorkDir }),
            Options.Create(new Fleet.Agent.Configuration.RabbitMqOptions { Host = "broker.example.test" }),
            logs.CreateLogger<Fleet.Agent.Services.GroupRelayService>(), fixture.Binding)
        { Connect = (_, _) => Task.FromResult(connection) };
        await relay.InitializeAsync(default);

        // Actual intake and completion; only inference is replaced, and the first turn runs until released.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options.Create(new Fleet.Agent.Configuration.AgentOptions { Name = "agent1", Role = "synthetic", WorkDir = fixture.WorkDir });
        var executor = Substitute.For<Fleet.Agent.Services.IAgentExecutor>();
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(), Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => HeldTurn(started, release.Task));
        var manager = new Fleet.Agent.Services.TaskManager(options, executor, new Fleet.Agent.Services.SessionManager(),
            logs.CreateLogger<Fleet.Agent.Services.TaskManager>(), sink: new Fleet.Agent.Services.MessageSinkHolder());
        var telegram = Options.Create(new Fleet.Agent.Configuration.TelegramOptions());
        var behavior = new Fleet.Agent.Services.GroupBehavior(options, telegram, new Fleet.Agent.Services.AllowlistHolder(telegram), executor, relay, manager,
            new Fleet.Agent.Services.CommandDispatcher(manager, executor, options, NullLogger<Fleet.Agent.Services.CommandDispatcher>.Instance),
            new Fleet.Agent.Services.PromptAssembler(executor), NullLogger<Fleet.Agent.Services.GroupBehavior>.Instance);
        relay.MessageReceived += behavior.OnRelayMessage;
        using var publisher = new Fleet.Agent.Services.RelayCompletionPublisher(manager, relay, NullLogger<Fleet.Agent.Services.RelayCompletionPublisher>.Instance);

        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };
        var (first, retry) = (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1", props, Directive(fixture.Epoch, session: first));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Temporal retries the same TaskId with a new session while the first turn is still running.
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "agent1", props, Directive(fixture.Epoch, session: retry));
        Assert.Equal(1, relay.WorkflowSessionCount);
        // Its intake is the in-flight duplicate, so the next completion for this TaskId is the first turn's.
        Assert.True(SpinWait.SpinUntil(() => log.Lines.Any(l => l.StartsWith("Duplicate taskId=wf/task-1 ignored", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)));

        release.SetResult();
        Assert.True(SpinWait.SpinUntil(() => log.Lines.Contains("Stock broker publication denied"), TimeSpan.FromSeconds(5)));
        Assert.Empty(Publishes(fixture.Channel)); // the first turn's completion is not retagged as the retry's
        Assert.Equal(1, relay.WorkflowSessionCount); // the retry keeps its own session

        await relay.PublishToAgentAsync("temporal-bridge", 0, "retry result", RelayMessageType.Response, correlationId: retry, taskId: "wf/task-1");
        AssertWorkflowReply(((ReadOnlyMemory<byte>)Assert.Single(Publishes(fixture.Channel))[4]!).ToArray(), "response", retry, fixture.Epoch);

        // Control: an unreplaced turn's own completion goes through the same chain tagged with its session.
        var own = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 3, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/task-2", session: own));
        Assert.True(SpinWait.SpinUntil(() => Publishes(fixture.Channel).Length == 2, TimeSpan.FromSeconds(5)));
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)Publishes(fixture.Channel)[1][4]!);
        Assert.Equal("wf/task-2", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(own, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(0, relay.WorkflowSessionCount);
    }

    private static async IAsyncEnumerable<AgentProgress> HeldTurn(TaskCompletionSource started, Task release)
    {
        started.TrySetResult();
        await release;
        yield return new AgentProgress { EventType = "result", Summary = "first turn", FinalResult = "first turn" };
    }

    [Fact]
    public async Task DirectiveDeliveredAfterItsActivityDeadline_IsRefusedBeforeIntake_AndRefreshesNoSession()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        var received = 0;
        relay.MessageReceived += (_, _, _, _, _, _, _, _) => received++;
        var now = DateTimeOffset.UtcNow;
        relay.WorkflowClock = () => now;
        await relay.InitializeAsync(default);
        var props = new BasicProperties { UserId = fixture.Binding.Principal("control") };
        var idle = Fleet.Agent.Services.GroupRelayService.WorkflowSessionIdleTimeout;

        // A delivery delayed past its deadline: no turn, no session.
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 1, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, taskId: "wf/late", deadline: now - TimeSpan.FromSeconds(1)));
        Assert.Equal(0, received);
        Assert.Equal(0, relay.WorkflowSessionCount);

        // A held session is not refreshed by a delayed resend: it still expires on its original idle bound.
        var session = Guid.NewGuid().ToString("N");
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 2, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, session: session, deadline: now + idle / 2));
        Assert.Equal(1, received);
        now += idle / 2;
        await Consumer(fixture.Channel).HandleBasicDeliverAsync("owned", 3, false, "fleet.group", "agent1", props,
            Directive(fixture.Epoch, session: session, deadline: now));
        Assert.Equal(1, received);
        now += idle / 2 + TimeSpan.FromSeconds(1);
        await relay.PublishToAgentAsync("temporal-bridge", 0, "too late", RelayMessageType.Response, correlationId: session, taskId: "wf/task-1");
        Assert.Empty(Publishes(fixture.Channel));
        Assert.Equal(0, relay.WorkflowSessionCount);
    }

    [Fact]
    public async Task WorkflowSignal_FromAManagedAgent_IsRefusedByName_NotSwallowed()
    {
        using var fixture = new StockBrokerTransportTests.Fixture();
        await using var relay = fixture.Relay();
        await relay.InitializeAsync(default);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            relay.PublishWorkflowSignalAsync("wf-1", "approved", "{}"));
        Assert.StartsWith(Fleet.Agent.Services.GroupRelayService.ManagedWorkflowSignalUnsupported, refused.Message);
        Assert.Empty(Publishes(fixture.Channel));
    }
}

/// <summary>
/// #492 managed workflow delegation, Bridge side: the managed lane (sole consumer of the epoch reply queue)
/// admits a typed workflow reply only with the workload principal and this lane's agent, sandbox and epoch, and
/// forwards it to the Temporal receiver route on the ordinary connection; anything else is refused.
/// </summary>
public sealed class ManagedStockAgentWorkflowReplyBridgeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-wf-bridge-").FullName;
    private readonly IChannel _ordinary = Substitute.For<IChannel>();
    private readonly IChannel _lane = Substitute.For<IChannel>();
    private readonly Guid _epoch = Guid.NewGuid();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Vhost => "stock-" + _epoch.ToString("N");

    private async Task<BridgeRelayService> StartAsync(TimeSpan? retryDelay = null, TimeSpan? confirmTimeout = null)
    {
        File.WriteAllText(Path.Combine(_dir, "agent1.json"), JsonSerializer.Serialize(new StockEnrollmentAuthority
        {
            State = "active", Agent = "agent1", SandboxId = "sandbox-1", Epoch = _epoch, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
            AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
            MemoryHost = "memory.example.test", BrokerHost = "broker.example.test"
        }));
        File.WriteAllText(Path.Combine(_dir, "agent1.control.password"), "control-agent1");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StockOpenShell:AuthorityDirectory"] = _dir }).Build();
        var ordinary = Substitute.For<IConnection>();
        ordinary.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(_ordinary));
        var bridge = new BridgeRelayService(Options.Create(new BridgeOptions { TargetAgent = "agent1", BridgeChatId = 7 }),
            Options.Create(new RabbitMqOptions { Host = "broker.example.test" }), NullLogger<BridgeRelayService>.Instance, configuration)
        { Connect = (_, _) => Task.FromResult(ordinary) };
        bridge.Lanes!.ForwardRetryDelay = retryDelay ?? TimeSpan.Zero;
        if (confirmTimeout is { } bound) bridge.ForwardConfirmTimeout = bound;
        bridge.Lanes!.Connect = (factory, _) =>
        {
            Assert.Equal(Vhost + "-control", factory.UserName);
            var connection = Substitute.For<IConnection>();
            connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(_lane));
            return Task.FromResult(connection);
        };
        await bridge.InitializeAsync(default);
        return bridge;
    }

    private static object[][] Publishes(IChannel channel) => channel.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == "BasicPublishAsync").Select(c => c.GetArguments()).ToArray();

    private Task Deliver(string? user, string sender = "agent1", Guid? epoch = null, string sandbox = "sandbox-1",
        string? session = null, string? taskId = "wf/task-1", string routingKey = "bridge", ulong tag = 1, bool redelivered = false)
    {
        var consumer = _lane.ReceivedCalls().SelectMany(c => c.GetArguments()).OfType<AsyncEventingBasicConsumer>().Single();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ChatId = 7, Sender = sender, Text = "synthetic result", Timestamp = DateTimeOffset.UtcNow, Type = "response",
            CorrelationId = session ?? Guid.NewGuid().ToString("N"), TaskId = taskId, Epoch = epoch ?? _epoch, Sandbox = sandbox
        });
        return consumer.HandleBasicDeliverAsync("owned", tag, redelivered, "fleet.group", routingKey, new BasicProperties { UserId = user }, body);
    }

    private static int Calls(IChannel channel, string method) => channel.ReceivedCalls().Count(c => c.GetMethodInfo().Name == method);

    [Fact]
    public async Task AdmittedReply_IsForwardedWithItsContext_ToTheTemporalReceiverOnTheOrdinaryConnection()
    {
        await using var bridge = await StartAsync();
        var session = Guid.NewGuid().ToString("N");
        await Deliver(Vhost + "-workload", session: session);

        var forward = Assert.Single(Publishes(_ordinary));
        Assert.Equal("fleet.group", forward[0]);
        Assert.Equal("temporal-bridge", forward[1]);
        using var reply = JsonDocument.Parse((ReadOnlyMemory<byte>)forward[4]!);
        Assert.Equal("agent1", reply.RootElement.GetProperty("Sender").GetString());
        Assert.Equal("wf/task-1", reply.RootElement.GetProperty("TaskId").GetString());
        Assert.Equal(session, reply.RootElement.GetProperty("CorrelationId").GetString());
        Assert.Equal(_epoch, reply.RootElement.GetProperty("Epoch").GetGuid());
        Assert.Equal("sandbox-1", reply.RootElement.GetProperty("Sandbox").GetString());
        Assert.Empty(Publishes(_lane)); // the lane never publishes a reply back into the epoch vhost
        Assert.True((bool)forward[2]!); // mandatory and confirmed: an unrouted forward fails instead of vanishing
        await _lane.Received(1).BasicAckAsync(1, false, Arg.Any<CancellationToken>()); // acked only after the forward
    }

    [Fact]
    public async Task ForwardOutage_RetainsTheReplyOnTheSameConsumer_AndRecoveryDeliversItOnce()
    {
        var outage = true;
        var forwarded = 0;
        _ordinary.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (outage) return ValueTask.FromException(new IOException("synthetic forward outage"));
            forwarded++;
            return ValueTask.CompletedTask;
        });
        await using var bridge = await StartAsync();
        var session = Guid.NewGuid().ToString("N");

        await Deliver(Vhost + "-workload", session: session, tag: 1);
        await _lane.Received(1).BasicNackAsync(1, false, true, Arg.Any<CancellationToken>()); // requeued, not lost
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
        Assert.Equal(0, Calls(_lane, "BasicRejectAsync"));

        outage = false; // the broker redelivers the retained reply to the same (sole) consumer
        await Deliver(Vhost + "-workload", session: session, tag: 2, redelivered: true);
        Assert.Equal(1, forwarded);
        await _lane.Received(1).BasicAckAsync(2, false, Arg.Any<CancellationToken>());
        Assert.Equal(1, Calls(_lane, "BasicNackAsync"));
        Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync");
        Assert.Contains(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "BasicConsumeAsync" && Equals(c.GetArguments()[1], false));
    }

    [Fact]
    public async Task PreparedReplyQueue_IsRedeclaredIdentically_AndAReplyQueuedBeforeTheLaneOpensIsForwardedOnce()
    {
        // The orchestrator prepares fleet.agent.bridge (durable, non-exclusive, non-auto-delete, no arguments)
        // and its fleet.group/bridge binding before activation (#492 R3/F8), so a reply published before this
        // lane opens is queued; the lane's redeclare must match exactly (no PRECONDITION_FAILED).
        await using var bridge = await StartAsync();
        var declare = Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "QueueDeclareAsync").GetArguments();
        Assert.Equal(new object?[] { "fleet.agent.bridge", true, false, false, null }, declare.Take(5).ToArray());
        var bind = Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "QueueBindAsync").GetArguments();
        Assert.Equal(new object?[] { "fleet.agent.bridge", "fleet.group", "bridge", null }, bind.Take(4).ToArray());
        var exchange = Assert.Single(_lane.ReceivedCalls(), c => c.GetMethodInfo().Name == "ExchangeDeclareAsync").GetArguments();
        Assert.Equal(new object?[] { "fleet.group", "direct", true, false, null }, exchange.Take(5).ToArray());

        // The broker delivers the reply it retained to the sole consumer as soon as the lane opens.
        await Deliver(Vhost + "-workload", tag: 1);
        Assert.Single(Publishes(_ordinary));
        await _lane.Received(1).BasicAckAsync(1, false, Arg.Any<CancellationToken>());
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));
    }

    [Fact]
    public async Task UnconfirmedForward_TimesOut_AndItsBackoffRunsOffTheConsumerCallback_ThenRequeues()
    {
        _ordinary.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
            Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask(Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(5)))); // no confirm
        await using var bridge = await StartAsync(retryDelay: TimeSpan.FromSeconds(2), confirmTimeout: TimeSpan.FromMilliseconds(100));

        // The confirm wait is bounded (#492 V5), and the callback returns before the backoff elapses.
        await Deliver(Vhost + "-workload", tag: 1).WaitAsync(TimeSpan.FromSeconds(1.5));
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
        // The lane keeps consuming meanwhile: the next delivery is settled while the first awaits its backoff.
        await Deliver(Vhost + "-control", tag: 2).WaitAsync(TimeSpan.FromSeconds(1.5));
        await _lane.Received(1).BasicRejectAsync(2, false, Arg.Any<CancellationToken>());
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));

        for (var i = 0; i < 100 && Calls(_lane, "BasicNackAsync") == 0; i++) await Task.Delay(50);
        await _lane.Received(1).BasicNackAsync(1, false, true, Arg.Any<CancellationToken>()); // bounded delay, then requeued
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
    }

    [Fact]
    public async Task ForeignReplies_AreRefused_AndNeverForwarded()
    {
        await using var bridge = await StartAsync();
        await Deliver(Vhost + "-control");                                     // not the workload principal
        await Deliver(null);                                                   // no validated user-id
        await Deliver(Vhost + "-workload", sender: "agent2");                  // foreign agent
        await Deliver(Vhost + "-workload", epoch: Guid.NewGuid());             // foreign epoch
        await Deliver(Vhost + "-workload", sandbox: "sandbox-2");              // foreign sandbox
        await Deliver(Vhost + "-workload", session: "not-a-session");          // no session nonce
        await Deliver(Vhost + "-workload", taskId: null);                      // no TaskId
        await Deliver(Vhost + "-workload", routingKey: "agent1");              // foreign route
        Assert.Empty(Publishes(_ordinary));
        // Refused replies are rejected without requeue: never redelivered, never acked as forwarded.
        await _lane.Received(8).BasicRejectAsync(1, false, Arg.Any<CancellationToken>());
        Assert.Equal(0, Calls(_lane, "BasicNackAsync"));
        Assert.Equal(0, Calls(_lane, "BasicAckAsync"));
    }
}
