using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Conversations.Contracts;
using Fleet.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent;

/// <summary>
/// The agent's registration graph, in one place both <c>Program.cs</c> and the tests use.
///
/// <para>
/// This exists because of #277 §10's opening requirement: verification must run against "a real
/// host built from <c>Program.cs</c>'s registration graph, not a hand-assembled service collection
/// that can silently omit the dependency under test". Top-level statements are not callable, so as
/// long as the registrations lived inside <c>Program.cs</c> the only way to test them was to
/// re-type them in the test — which asserts that the copy is correct and says nothing about the
/// process that actually ships. Every defect #277 D-3 guards against (a publisher that is
/// registered only as <c>IHostedService</c>, a transport whose absence takes relay publication
/// with it, two publisher instances) is a REGISTRATION defect, and a re-typed collection cannot
/// see any of them.
/// </para>
///
/// <para>
/// Nothing here is conditional on being under test. The daemon graph is built exactly once, by
/// this method, and the Telegram transport's conditional registration — the behaviour S4 turns on
/// — is part of it rather than something a test arranges for itself.
/// </para>
/// </summary>
public static class AgentHostRegistration
{
    /// <summary>
    /// Options binding and the services both modes need. Called before either
    /// <see cref="AddAgentCliServices"/> or <see cref="AddAgentDaemonServices"/>.
    /// </summary>
    public static IServiceCollection AddAgentCoreServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Bind configuration sections
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.Section));
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.Section));
        services.Configure<WhisperOptions>(configuration.GetSection(WhisperOptions.Section));
        services.Configure<TtsOptions>(configuration.GetSection(TtsOptions.Section));
        services.Configure<ClientChannelOptions>(configuration.GetSection(ClientChannelOptions.Section));
        services.Configure<ConversationsOptions>(configuration.GetSection(ConversationsOptions.Section));

        // Register services
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<ClaudeExecutor>();
        services.AddSingleton<CodexExecutor>();
        services.AddSingleton<GeminiExecutor>();

        services.AddSingleton<IAgentExecutor>(sp =>
        {
            var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Provider;
            return provider switch
            {
                "codex" => sp.GetRequiredService<CodexExecutor>(),
                "gemini" => sp.GetRequiredService<GeminiExecutor>(),
                _ => sp.GetRequiredService<ClaudeExecutor>(),
            };
        });

        services.AddSingleton<IFleetConnectionState, FleetConnectionState>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<GroupRelayService>();
        services.AddSingleton<AllowlistHolder>();

        return services;
    }

    /// <summary>
    /// Startup configuration gate, run by <c>Program.cs</c> against the built host before
    /// <c>app.Run()</c>. Throws rather than let a misconfigured agent reach the run loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here for the same reason the registrations do: a gate re-typed in a test proves the
    /// copy is correct and says nothing about the process that ships.
    /// </para>
    /// <para>
    /// ⚠️ Throwing is not, by itself, enough to stop the host, and the call site owns the
    /// difference. By the time this runs, <c>builder.Build()</c> holds a foreground thread, so an
    /// unhandled main-thread exception leaves the process resident at ~100% CPU with <c>docker
    /// ps</c> reporting <c>Up</c> — measured on the real image, not inferred. <c>Program.cs</c>
    /// therefore catches and calls <c>Environment.Exit(1)</c>. Keep that catch: without it this
    /// method degrades from a gate into a wedge. The throw stays so the behaviour is assertable
    /// from a test, which <c>Environment.Exit</c> inside this method would not be — it would end
    /// the test host.
    /// </para>
    /// <para>
    /// The only fault it catches today is a codex agent whose model names a local provider
    /// (<c>ollama/…</c>, <c>lmstudio/…</c>) with no <c>CODEX_OSS_BASE_URL</c> to reach it. That is
    /// misconfiguration, not degradation — there is no endpoint. It has to stop the host, because
    /// nothing downstream will: <c>/health</c> answers ok unconditionally and
    /// <see cref="Services.WarmupService"/> swallows executor startup failures as a warning, so a
    /// lazy check leaves the container up, reported healthy, and unable to answer a single turn.
    /// </para>
    /// </remarks>
    public static void ValidateStartupConfiguration(
        IServiceProvider services, Func<string, string?>? environmentReader = null)
    {
        var agent = services.GetRequiredService<IOptions<AgentOptions>>().Value;

        if (agent.Provider != "codex")
            return;

        var readEnv = environmentReader ?? Environment.GetEnvironmentVariable;
        if (CodexExecutor.DescribeLocalModelFault(
                agent.Model, readEnv(CodexExecutor.OssBaseUrlEnvVar)) is not { } fault)
        {
            return;
        }

        // Logged before the throw so the container's last line names the cause, rather than an
        // unhandled-exception stack the operator has to read to the bottom of.
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AgentHostRegistration).FullName!)
            .LogCritical("Agent '{Agent}' cannot start: {Fault}", agent.Name, fault);

        throw new InvalidOperationException(fault);
    }

    /// <summary>CLI mode: run a single task and exit.</summary>
    public static IServiceCollection AddAgentCliServices(this IServiceCollection services)
    {
        services.AddHostedService<CliRunner>();
        return services;
    }

    /// <summary>Daemon mode: Telegram transport + services.</summary>
    public static IServiceCollection AddAgentDaemonServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // --- Outbound sink seam (#277 D-1) ---------------------------------------------------
        // The holder is dependency-free and IS the thing that breaks the circular DI
        // (AgentTransport -> TaskManager -> sink) that the transport's four self-assignments used
        // to work around. Consumers constructor-inject IMessageSink and get the holder; the
        // transport attaches itself to it during its own construction. Until then — and forever in
        // a host that never registers the transport (S4) — the holder serves NullMessageSink, so
        // omitting Telegram is a counted no-op instead of a NullReferenceException.
        services.AddSingleton<SinkSuppressionCounter>();
        services.AddSingleton<RelayCompletionCounter>();
        services.AddSingleton<MessageSinkHolder>();
        services.AddSingleton<IMessageSink>(sp => sp.GetRequiredService<MessageSinkHolder>());

        // --- Conversation event seam ---------------------------------------------------------
        // Registered before TaskManager so the publisher is available to it, and before
        // AgentTransport so RuntimeWiringService starts first (see below).
        services.AddSingleton<ConversationEventCounters>();
        services.AddSingleton<ConversationRegistry>();
        services.AddSingleton<IConversationRegistry>(sp => sp.GetRequiredService<ConversationRegistry>());
        services.AddSingleton<ConversationEventBus>();
        services.AddSingleton<IConversationEventPublisher>(sp => sp.GetRequiredService<ConversationEventBus>());
        services.AddSingleton<PrincipalBinder>();
        services.AddSingleton<ConversationIntake>();

        services.AddSingleton<TaskManager>();
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<PromptAssembler>();
        services.AddSingleton<MessageRouter>();
        services.AddSingleton<GroupBehavior>();

        // --- Completion effects (#277 D-2) ----------------------------------------------------
        // Both subscribe to TaskManager in their own constructors and must outlive the Telegram
        // transport's absence: relay/bridge publication is what workflow delegations depend on, and
        // context buffering is what a cold executor needs, on every channel.
        //
        // The AddSingleton + factory-AddHostedService pair on the publisher is load-bearing, not
        // style. A bare AddHostedService<RelayCompletionPublisher>() registers the type ONLY as
        // IHostedService, so RuntimeWiringService's constructor injection of the concrete type
        // would fail to resolve — the guard meant to make a missing publisher loud would instead
        // make the process refuse to start for an unrelated-looking reason. Resolving that SAME
        // instance through the factory keeps one object: the one whose constructor subscribed and
        // whose IsAttached the guard reads. Registering both forms independently would yield two
        // instances, two subscriptions and two relay publications (#277 MUST NOT 16, MUST NOT 4).
        services.AddSingleton<RelayCompletionPublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<RelayCompletionPublisher>());
        services.AddSingleton<CompletionContextBuffer>();

        // RuntimeWiringService is registered BEFORE AgentTransport on purpose. The host starts
        // hosted services in registration order, and this one asserts that both completion handlers
        // are already attached (they are — each subscribes in its constructor, and every constructor
        // runs before any StartAsync).
        services.AddHostedService<RuntimeWiringService>();
        services.AddHostedService<ConversationEventPump>();
        // Conditional (#277 S4). The transport is the Telegram adapter and nothing else depends on
        // it being constructed: the sink falls back to the counted no-op, relay publication and
        // context buffering have their own owners, and the startup guard checks those owners rather
        // than this class. Keying off token presence rather than an explicit setting is the open
        // question in #277 §11.1; either satisfies S4.
        //
        // A malformed token does NOT take this branch — the transport is registered and handles it
        // internally (MUST NOT 17), so startup_telegram_state can distinguish absent from malformed.
        if (!string.IsNullOrWhiteSpace(configuration[$"{TelegramOptions.Section}:{nameof(TelegramOptions.BotToken)}"]))
            services.AddHostedService<AgentTransport>();
        services.AddHttpClient();
        services.AddHttpClient("whisper", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(180);
        });
        services.AddHttpClient("tts", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddSingleton<VoiceTranscriptionService>();
        services.AddSingleton<TtsService>();
        services.AddSingleton<RichFallbackCounter>();
        services.AddSingleton<InjectionOutcomeCounter>();

        services.AddHostedService<WarmupService>();
        services.AddHostedService<OrchestratorHeartbeatService>();

        AddConversationSouthSeam(services, configuration);

        return services;
    }

    /// <summary>
    /// The agent half of the durable conversation seam (#303), registered only when it is
    /// configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is <c>Conversations:SouthBaseUrl</c>, keyed off configuration in the same
    /// conditional-registration shape the Telegram transport uses above. Absent, <b>nothing</b>
    /// here is constructed: no adapter, no consumer, no hosted service, no HTTP client. That is the
    /// whole blast-radius argument — a host that has not enabled the feature is byte-identical to
    /// one built before it existed, and <c>ConversationSouthRegistrationTests</c> asserts it by
    /// inspecting the registration graph rather than by reading this comment.
    /// </para>
    /// <para>
    /// Present but incomplete <b>fails startup</b>, with the offending key named. It must not
    /// silently degrade to disabled: an operator who configured half of it would otherwise see a
    /// healthy process beside a queue nobody drains, which is the exact state #303 exists to end.
    /// </para>
    /// </remarks>
    internal static void AddConversationSouthSeam(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ConversationsOptions.Section);
        var baseUrl = section[nameof(ConversationsOptions.SouthBaseUrl)];

        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        var options = new ConversationsOptions();
        section.Bind(options);

        ValidateConversationsOptions(
            options,
            shortName: configuration[$"{AgentOptions.Section}:{nameof(AgentOptions.ShortName)}"],
            brokerHost: configuration[$"{RabbitMqOptions.Section}:{nameof(RabbitMqOptions.Host)}"]);

        // The agent's ONE connection to this broker, exposed through the service that owns it. The
        // conversation consumer takes a channel on it rather than carrying a second connection
        // string — a credential that exists twice is one a rotation can miss.
        services.AddSingleton<IAgentBrokerConnection>(sp => sp.GetRequiredService<GroupRelayService>());

        services.AddSingleton<ConversationSouthCounters>();
        services.AddSingleton<ConversationOrdinalAllocator>();
        services.AddSingleton<ConversationSouthHandoff>();

        services.AddHttpClient<ConversationSouthClient>();

        // #308. Registered whenever the south seam is — there is no separate agent-side switch,
        // because whether attachments exist is the SERVICE's decision: a command simply arrives with
        // an `attachments` array or without one. An agent that added its own flag would be a second
        // place for the answer to live, and the two would drift.
        //
        // ⚠️ It reads Telegram:AttachmentDir for the directory but NOT Telegram:PersistAttachments.
        // The directory is shared; the switch is not (MUST NOT 15).
        services.AddSingleton<ConversationAttachmentFetcher>();

        // The adapter is registered as IChannelAdapter only. The pump resolves adapters by their
        // ChannelId, and registering two under one id is a startup failure by design.
        services.AddSingleton<IChannelAdapter, ConversationSouthAdapter>();

        // AddSingleton + factory-AddHostedService, the same load-bearing pair the relay completion
        // publisher uses. A bare AddHostedService<T>() registers the type only as IHostedService, so
        // the heartbeat's constructor injection of the concrete consumer would fail to resolve;
        // registering both forms independently would yield two consumers, two broker subscriptions
        // and two claims for every delivery.
        services.AddSingleton<ConversationSouthConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationSouthConsumer>());
        services.AddHostedService<ConversationLeaseHeartbeat>();
    }

    /// <summary>
    /// The six startup validations (#303 Feature gate). Each throws, naming the offending key.
    /// </summary>
    /// <remarks>
    /// Two of them are about keys this section does NOT own. The seam borrows the agent's identity
    /// and the agent's broker rather than restating either, so the thing that can be wrong is the
    /// borrowed value — and a check is not a second field.
    /// </remarks>
    internal static void ValidateConversationsOptions(
        ConversationsOptions options, string? shortName = null, string? brokerHost = null)
    {
        Required(options.SouthBearerToken, nameof(ConversationsOptions.SouthBearerToken));

        // Absolute AND http(s). "Absolute" alone is not enough: `Uri.TryCreate` accepts
        // `south:8082` as a URI whose scheme is "south", and on a Unix host it accepts a bare path
        // as a file URI. Both would satisfy an absoluteness check, reach `HttpClient.BaseAddress`,
        // and fail later as an unroutable request rather than at startup with the key named.
        if (!Uri.TryCreate(options.SouthBaseUrl, UriKind.Absolute, out var southUri)
            || (southUri.Scheme != Uri.UriSchemeHttp && southUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.SouthBaseUrl)} must be an "
                + "absolute http or https URI.");
        }

        // The agent's OWN short name, against the SAME pattern the service enforces on its side.
        // It is both the routing key and a queue-name segment, so a value the two sides read
        // differently means this agent binds and drains a queue nobody publishes to while the real
        // one grows — with no error anywhere. Checked here, carried nowhere: a second field for one
        // identity is what this replaced.
        if (string.IsNullOrWhiteSpace(shortName)
            || !System.Text.RegularExpressions.Regex.IsMatch(
                shortName, ConversationsOptions.AgentNamePattern))
        {
            throw new InvalidOperationException(
                $"{AgentOptions.Section}:{nameof(AgentOptions.ShortName)} must match "
                + $"{ConversationsOptions.AgentNamePattern} when "
                + $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.SouthBaseUrl)} is set. "
                + "It is this agent's queue segment on the broker, not a display name.");
        }

        // The inbound queue is consumed on the agent's existing connection, so the broker it already
        // talks to has to be configured. Absent, the feature would sit retrying an attach forever
        // against a host that was never set.
        if (string.IsNullOrWhiteSpace(brokerHost))
        {
            throw new InvalidOperationException(
                $"{RabbitMqOptions.Section}:{nameof(RabbitMqOptions.Host)} is required when "
                + $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.SouthBaseUrl)} is set. "
                + "The inbound queue is consumed on the connection the agent already holds.");
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.HeartbeatInterval)} must be "
                + "positive.");
        }

        // Validated against the SHARED constant, never a literal. A heartbeat at or above the lease
        // guarantees abandonment of every turn that outlives one lease period, and the symptom —
        // attempt_abandoned on healthy turns — reads as a store fault rather than a config error.
        if (options.HeartbeatInterval > ConversationLeaseDefaults.MaxHeartbeatInterval)
        {
            throw new InvalidOperationException(
                $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.HeartbeatInterval)} must be at "
                + $"most {ConversationLeaseDefaults.MaxHeartbeatInterval} — half the store's "
                + $"{ConversationLeaseDefaults.LeaseDuration} lease.");
        }

        if (options.Prefetch < 2)
        {
            throw new InvalidOperationException(
                $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.Prefetch)} must be at least 2. "
                + "A delivery is not acknowledged until its terminal commits, so a prefetch of one stops a "
                + "second submission ever being delivered while the first turn runs.");
        }

        static void Required(string value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{ConversationsOptions.Section}:{key} is required when "
                    + $"{ConversationsOptions.Section}:{nameof(ConversationsOptions.SouthBaseUrl)} is set. "
                    + "Half a configuration would leave a healthy process beside a queue nobody drains.");
            }
        }
    }
}
