using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        return services;
    }
}
