using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fleet.Agent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// #277 §10's opening requirement, and the reason <see cref="AgentHostRegistration"/> exists:
/// "a real host built from <c>Program.cs</c>'s registration graph, not a hand-assembled service
/// collection that can silently omit the dependency under test."
///
/// <para>
/// Every isolation claim in #277 is a claim about REGISTRATION — that the transport is
/// conditional, that the publisher is one instance reachable by both its concrete type and
/// <c>IHostedService</c>, that the guard fires when an owner is missing. A test that re-types the
/// registrations proves the copy is self-consistent and nothing about the process that ships. The
/// graph these tests build is the same method <c>Program.cs</c> calls, so deleting a registration
/// there fails a test here.
/// </para>
///
/// <para><b>What is substituted, and why each one is a leaf rather than part of the graph:</b>
/// <list type="bullet">
/// <item><c>IAgentExecutor</c> — otherwise the host spawns the provider CLI. The turn under test
/// is dispatch → completion → event, none of which is the executor's behaviour.</item>
/// <item><c>WarmupService</c> is removed — it calls the executor three seconds after start, which
/// would appear as a phantom turn in every assertion about what ran.</item>
/// <item><c>GroupRelayService</c>'s AMQP channel is replaced by a recorder, exactly as
/// <c>RelayCompletionPublisherTests</c> does. The service itself, its registration and its
/// wiring are real; only the socket is not.</item>
/// </list>
/// Nothing in the sink, completion, conversation or hosted-service graph is substituted or
/// re-declared. <c>OrchestratorHeartbeatService</c> is deliberately left registered: it disables
/// itself when no broker host is configured, and leaving it in keeps the hosted-service list one
/// entry closer to production.
/// </para>
/// </summary>
public sealed class ProgramRegistrationGraphTests : IDisposable
{
    private const string ClientChannel = "example-adapter";
    private const string OwnerToken = "operator-set-token";
    private const long OwnerUserId = 4242L;

    /// <summary>Any syntactically valid bot token. Never contacted — no host here starts polling.</summary>
    private const string WellFormedToken = "123456:AAHfakefakefakefakefakefakefakefakeff";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"fleet-graph-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    // ── T9: a Telegram-free host, built from the real graph ───────────────────────────────

    /// <summary>
    /// T9 / P1. No bot token, so <c>AgentTransport</c> is never registered — and the host still
    /// starts and serves a client turn from <c>conversation.open</c> to <c>turn.final</c>.
    ///
    /// The mutation this fails on is "remove the NullMessageSink default (D-1)": every send the
    /// turn makes goes through the holder with nothing attached, so a null sink would throw inside
    /// the turn instead of being counted.
    /// </summary>
    [Fact]
    public async Task T9_HostWithoutABotToken_StartsAndServesAFirstPartyTurnEndToEnd()
    {
        await using var host = await StartAsync(botToken: "");

        // The premise: the transport really is absent from THIS host, not merely inert.
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), s => s is AgentTransport);

        var open = host.Services.GetRequiredService<ConversationIntake>().Open(OpenPayload());
        Assert.True(open.Success);

        await host.Services.GetRequiredService<ConversationIntake>()
            .SubmitAsync(open.RuntimeKey, "what is the answer?");

        var final = await host.Adapter.WaitForAsync(ConversationEventKind.TurnFinal);
        Assert.Equal("the answer", final.PayloadAs<TurnFinalPayload>()!.Text);
        Assert.Contains(host.Adapter.Received, e => e.Kind == ConversationEventKind.TurnStarted);
        Assert.Equal(ClientChannel, final.Identity.ChannelId);

        // The turn's outbound text went to the holder, which served NullMessageSink and counted
        // the suppression. That counter being non-zero is the evidence the send path ran at all —
        // without it this test would also pass if the turn never tried to answer.
        Assert.True(host.Services.GetRequiredService<SinkSuppressionCounter>()
            .GetSuppressed(SinkSuppressionCounter.ReasonNullSink) > 0);
    }

    /// <summary>
    /// The positive control for T9. Without it, deleting the <c>if</c> around the transport
    /// registration — or inverting it — would leave T9 green while S4 was silently unimplemented.
    /// </summary>
    [Fact]
    public async Task T9_ControlAHostWithABotToken_DoesRegisterTheTransport()
    {
        await using var host = await StartAsync(botToken: WellFormedToken, startHost: false);

        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is AgentTransport);
    }

    // ── T10 / T13b: relay completion, once, from one instance ─────────────────────────────

    /// <summary>
    /// T10 / P2. The same token-less host completes a bridge delegation and its answer reaches the
    /// broker with the correlation id intact. Asserted on the published message, not a call count.
    /// </summary>
    [Fact]
    public async Task T10_TheSameTelegramFreeHost_CompletesARelayDelegationWithACorrelationId()
    {
        await using var host = await StartAsync(botToken: "");

        host.Services.GetRequiredService<TaskManager>().RaiseTaskCompletedForTest(
            chatId: 7L, result: "delegation answer", relaySender: "bridge",
            source: TaskSource.Bridge, correlationId: "corr-1", taskId: "wf/step");

        var published = await host.Published.DequeueAsync();
        Assert.Equal("bridge", published.RoutingKey);
        Assert.Equal("corr-1", published.Payload.GetProperty("CorrelationId").GetString());
        Assert.Equal(RelayMessageType.BridgeResponse, published.Payload.GetProperty("Type").GetString());
        Assert.Contains("delegation answer", published.Payload.GetProperty("Text").GetString());
    }

    /// <summary>
    /// T13b. The publisher resolved by type and the publisher the host is running must be the SAME
    /// object, and the completion event must have exactly one relay subscriber.
    ///
    /// Reference equality alone is not enough: <c>AddSingleton</c> plus a bare
    /// <c>AddHostedService&lt;T&gt;()</c> yields two instances, two subscriptions and two relay
    /// publications, and only the second assertion sees that. Both are here because the two halves
    /// fail on different mutations — the identity check fails if the factory overload is replaced,
    /// the publish count fails if a second subscription is added anywhere.
    /// </summary>
    [Fact]
    public async Task T13b_TheHostedPublisherIsTheResolvedSingleton_AndSubscribesExactlyOnce()
    {
        await using var host = await StartAsync(botToken: "");

        var resolved = host.Services.GetRequiredService<RelayCompletionPublisher>();
        var hosted = host.Services.GetServices<IHostedService>().OfType<RelayCompletionPublisher>().ToList();

        Assert.Same(resolved, Assert.Single(hosted));

        host.Services.GetRequiredService<TaskManager>().RaiseTaskCompletedForTest(
            chatId: 7L, result: "answer", relaySender: "peer",
            source: TaskSource.Relay, taskId: "wf/step");

        await host.Published.DequeueAsync();
        // Give a second subscriber, if one existed, more than enough time to publish too.
        await Task.Delay(200);
        Assert.Empty(host.Published.Messages);
    }

    // ── T11: the guard bites on the real graph ────────────────────────────────────────────

    /// <summary>
    /// T11. Omitting <see cref="RelayCompletionPublisher"/> from the graph fails startup, naming
    /// the missing type. It must fail at DI resolution rather than at the first dropped answer.
    /// </summary>
    [Fact]
    public async Task T11_AGraphMissingTheRelayPublisher_FailsStartupNamingIt()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            botToken: "",
            mutate: services =>
            {
                foreach (var d in services.Where(IsRelayPublisherDescriptor).ToList())
                    services.Remove(d);
            }));

        Assert.Contains(nameof(RelayCompletionPublisher), Flatten(ex));
    }

    /// <summary>
    /// T11, the variant the registration comment warns about: replacing the
    /// <c>AddSingleton</c> + factory pair with a bare <c>AddHostedService&lt;T&gt;()</c>. The type
    /// is then registered ONLY as <c>IHostedService</c>, so <see cref="RuntimeWiringService"/>'s
    /// constructor injection cannot resolve it and startup fails — loudly, but for a reason that
    /// reads like an unrelated wiring bug, which is exactly why the pair is documented.
    /// </summary>
    [Fact]
    public async Task T11_ABareHostedServiceRegistration_FailsToResolveTheConcretePublisher()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            botToken: "",
            mutate: services =>
            {
                foreach (var d in services.Where(IsRelayPublisherDescriptor).ToList())
                    services.Remove(d);
                services.AddHostedService<RelayCompletionPublisher>();
            }));

        Assert.Contains(nameof(RelayCompletionPublisher), Flatten(ex));
    }

    // ── T15: one adapter per channel ──────────────────────────────────────────────────────

    /// <summary>
    /// T15 / R9. Two adapters claiming one channel id is a startup failure on the real graph, not
    /// nondeterministic routing discovered at first delivery.
    /// </summary>
    [Fact]
    public async Task T15_TwoAdaptersForOneChannelId_FailStartup()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            botToken: "",
            mutate: services => services.AddSingleton<IChannelAdapter>(new RecordingAdapter())));

        Assert.Contains(ClientChannel, Flatten(ex));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The publisher's two descriptors: the concrete singleton, and the factory-registered
    /// <c>IHostedService</c> that resolves it. It is the only factory-registered hosted service in
    /// the graph — a second one would need this filter to be narrowed rather than left to guess.
    /// </summary>
    private static bool IsRelayPublisherDescriptor(ServiceDescriptor d) =>
        d.ServiceType == typeof(RelayCompletionPublisher)
        || (d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null);

    private static string Flatten(Exception ex)
    {
        var text = new StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
            text.AppendLine(e.Message);
        return text.ToString();
    }

    private async Task<GraphHost> StartAsync(
        string botToken, Action<IServiceCollection>? mutate = null, bool startHost = true)
    {
        Directory.CreateDirectory(_workDir);

        // DisableDefaults keeps the graph deterministic: no appsettings.json from the referenced
        // project's output, no ambient environment variables. The registrations under test come
        // from AgentHostRegistration; only the values they read come from here.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = _workDir,
            ["Agent:Provider"] = "claude",
            ["Telegram:BotToken"] = botToken,
            ["Telegram:AllowedUserIds:0"] = OwnerUserId.ToString(),
            ["ClientChannel:OwnerPrincipalToken"] = OwnerToken,
            ["ClientChannel:OwnerUserId"] = OwnerUserId.ToString(),
            // No RabbitMq:Host — the broker socket is substituted below, and leaving the host
            // blank keeps the heartbeat service from dialling out.
        });
        builder.Services.AddLogging();

        // THE GRAPH UNDER TEST. Same two calls Program.cs makes, same order.
        builder.Services.AddAgentCoreServices(builder.Configuration);
        builder.Services.AddAgentDaemonServices(builder.Configuration);

        var executor = new StubExecutor("the answer");
        builder.Services.AddSingleton<IAgentExecutor>(executor);
        foreach (var d in builder.Services.Where(d => d.ImplementationType == typeof(WarmupService)).ToList())
            builder.Services.Remove(d);

        var adapter = new RecordingAdapter();
        builder.Services.AddSingleton<IChannelAdapter>(adapter);

        mutate?.Invoke(builder.Services);

        var host = builder.Build();
        var published = new PublishedMessages();

        try
        {
            // Resolving the relay before start is deliberate: the fake channel must be in place
            // before any hosted service can publish through it, and resolving the singleton here
            // is the same instance the publisher gets.
            AttachCapturingChannel(host.Services.GetRequiredService<GroupRelayService>(), published);

            if (startHost)
                await host.StartAsync();
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return new GraphHost(host, adapter, published, executor);
    }

    private ConversationOpenPayload OpenPayload() => new()
    {
        ChannelId = ClientChannel,
        PrincipalBinding = new PrincipalBinding
        {
            Scheme = PrincipalBinding.LegacyOwnerScheme,
            Value = OwnerToken,
        },
        Role = PrincipalRole.Owner,
    };

    private sealed record GraphHost(
        IHost Host, RecordingAdapter Adapter, PublishedMessages Published, StubExecutor Executor)
        : IAsyncDisposable
    {
        public IServiceProvider Services => Host.Services;

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* a host that never started has nothing to stop */ }
            Host.Dispose();
        }
    }

    /// <summary>The Phase-0 loopback adapter — the only adapter kind the protocol permits.</summary>
    private sealed class RecordingAdapter : IChannelAdapter
    {
        private readonly ConcurrentQueue<ConversationEvent> _received = new();

        public string ChannelId => ClientChannel;
        public IReadOnlyList<ConversationEvent> Received => _received.ToList();

        public Task DeliverAsync(ConversationEvent evt, CancellationToken ct)
        {
            _received.Enqueue(evt);
            return Task.CompletedTask;
        }

        public async Task<ConversationEvent> WaitForAsync(string kind)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var match = _received.FirstOrDefault(e => e.Kind == kind);
                if (match is not null)
                    return match;
                await Task.Delay(10, cts.Token);
            }
        }
    }

    private sealed class StubExecutor : IAgentExecutor
    {
        private readonly string _answer;
        private readonly ConcurrentQueue<string> _executed = new();

        public StubExecutor(string answer) => _answer = answer;

        public IReadOnlyList<string> Executed => _executed.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executed.Enqueue(task);
            await Task.Yield();
            yield return new AgentProgress
            {
                EventType = "result",
                Summary = _answer,
                FinalResult = _answer,
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default) =>
            Task.FromResult(MidTurnInjectionResult.NoActiveTurn("no active turn"));

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── capturing broker channel (same shape as RelayCompletionPublisherTests) ────────────

    private static void AttachCapturingChannel(GroupRelayService relay, PublishedMessages published)
    {
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask)
            .AndDoes(call =>
            {
                var routingKey = call.ArgAt<string>(1);
                var body = call.ArgAt<ReadOnlyMemory<byte>>(4);
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body.Span));
                published.Enqueue(new PublishedMessage(routingKey, document.RootElement.Clone()));
            });

        SetPrivateField(relay, "_publishChannel", channel);
        SetPrivateField(relay, "_initialized", true);
    }

    private static void SetPrivateField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }

    private sealed record PublishedMessage(string RoutingKey, JsonElement Payload);

    private sealed class PublishedMessages
    {
        private readonly SemaphoreSlim _available = new(0);
        public ConcurrentQueue<PublishedMessage> Messages { get; } = new();

        public void Enqueue(PublishedMessage message)
        {
            Messages.Enqueue(message);
            _available.Release();
        }

        public async Task<PublishedMessage> DequeueAsync()
        {
            var acquired = await _available.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(acquired, "No relay message was published within the timeout.");
            Assert.True(Messages.TryDequeue(out var message));
            return message!;
        }
    }
}
