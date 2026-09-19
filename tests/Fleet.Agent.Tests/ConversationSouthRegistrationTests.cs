using Fleet.Agent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Agent.Tests;

/// <summary>
/// The feature gate and the five startup validations (#303 AC5, AC7).
/// </summary>
/// <remarks>
/// <para>
/// Asserted against <see cref="AgentHostRegistration.AddConversationSouthSeam"/> — the same method
/// <c>Program.cs</c> reaches through <c>AddAgentDaemonServices</c> — rather than a re-typed
/// collection. Every claim here is a claim about registration, and a re-typed collection proves the
/// copy is self-consistent and nothing about the process that ships.
/// </para>
/// <para>
/// A note on what AC5's third assertion says, and what it deliberately does not. The broker queue is
/// declared and bound by the PUBLISHING service, on purpose, so that a submission published before
/// the agent ever started is still there when it first attaches. "No queue exists" is therefore an
/// assertion about the producer's behaviour, not the agent's. What the agent owns is: it declares
/// nothing, binds nothing and attaches no consumer — which is what is asserted below, structurally,
/// by the absence of the consumer from the graph.
/// </para>
/// </remarks>
public sealed class ConversationSouthRegistrationTests
{
    private const string ValidUrl = "http://south.invalid:8082";

    /// <summary>The agent's own short name — the seam's identity, borrowed rather than restated.</summary>
    private static readonly string ShortNameKey =
        $"{AgentOptions.Section}:{nameof(AgentOptions.ShortName)}";

    /// <summary>The broker the agent already talks to. No second connection string exists.</summary>
    private static readonly string BrokerHostKey =
        $"{RabbitMqOptions.Section}:{nameof(RabbitMqOptions.Host)}";

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(Qualify(p.Key), p.Value)))
            .Build();

    /// <summary>
    /// Bare names belong to the <c>Conversations</c> section; anything already carrying a section
    /// prefix is passed through, so a test can drive <c>Agent:ShortName</c> and <c>RabbitMq:Host</c>
    /// through the same helper.
    /// </summary>
    private static string Qualify(string key) =>
        key.Contains(':', StringComparison.Ordinal) ? key : $"{ConversationsOptions.Section}:{key}";

    private static IConfiguration FullyConfigured(params (string Key, string? Value)[] overrides)
    {
        var pairs = new Dictionary<string, string?>
        {
            [nameof(ConversationsOptions.SouthBaseUrl)] = ValidUrl,
            [nameof(ConversationsOptions.SouthBearerToken)] = "a-token",
            [ShortNameKey] = "example-agent",
            [BrokerHostKey] = "broker.invalid",
        };

        foreach (var (key, value) in overrides)
            pairs[key] = value;

        return Config([.. pairs.Select(p => (p.Key, p.Value))]);
    }

    private static IServiceCollection Register(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        AgentHostRegistration.AddConversationSouthSeam(services, configuration);
        return services;
    }

    // ── AC5: disabled is byte-identical ──────────────────────────────────────

    /// <summary>
    /// With no <c>SouthBaseUrl</c>, NOTHING is registered. Not the adapter, not the consumer, not the
    /// heartbeat, not an HTTP client, not even the counters.
    /// </summary>
    /// <remarks>
    /// This is the whole blast-radius argument. An agent that has not enabled the feature must be
    /// indistinguishable from one built before the feature existed — and "registered but idle" is
    /// not that: an idle consumer still holds a broker connection and an HTTP client, and still has
    /// a code path that can be entered by a configuration change nobody reviewed.
    /// </remarks>
    [Fact]
    public void WithNoSouthBaseUrlNothingIsRegistered()
    {
        var services = Register(Config((nameof(ConversationsOptions.SouthBaseUrl), null)));

        Assert.Empty(services);
    }

    /// <summary>A whitespace-only value is absent, not present-and-blank.</summary>
    [Fact]
    public void AWhitespaceSouthBaseUrlIsTreatedAsAbsent()
    {
        var services = Register(Config((nameof(ConversationsOptions.SouthBaseUrl), "   ")));

        Assert.Empty(services);
    }

    /// <summary>
    /// The adapter, the consumer and the heartbeat are all absent from the disabled graph — named
    /// individually, so deleting one registration from the enabled path cannot be masked by another.
    /// </summary>
    [Fact]
    public void TheDisabledGraphContainsNoneOfTheNewServiceTypes()
    {
        var services = Register(Config((nameof(ConversationsOptions.SouthBaseUrl), null)));

        foreach (var type in new[]
                 {
                     typeof(ConversationSouthAdapter), typeof(ConversationSouthConsumer),
                     typeof(ConversationLeaseHeartbeat), typeof(ConversationSouthClient),
                     typeof(ConversationSouthHandoff), typeof(ConversationOrdinalAllocator),
                     typeof(ConversationSouthCounters),
                 })
        {
            Assert.DoesNotContain(services, d =>
                d.ServiceType == type || d.ImplementationType == type);
        }
    }

    // ── the enabled graph ────────────────────────────────────────────────────

    [Fact]
    public void AValidConfigurationRegistersTheWholeSeam()
    {
        var services = Register(FullyConfigured());

        Assert.Contains(services, d => d.ImplementationType == typeof(ConversationSouthAdapter)
                                       && d.ServiceType == typeof(IChannelAdapter));
        Assert.Contains(services, d => d.ServiceType == typeof(ConversationSouthConsumer));
        Assert.Contains(services, d => d.ImplementationType == typeof(ConversationLeaseHeartbeat));
        Assert.Contains(services, d => d.ServiceType == typeof(ConversationSouthHandoff));
        Assert.Contains(services, d => d.ServiceType == typeof(ConversationOrdinalAllocator));
        Assert.Contains(services, d => d.ServiceType == typeof(ConversationSouthCounters));
        Assert.Contains(services, d => d.ServiceType == typeof(ConversationSouthClient));
    }

    /// <summary>
    /// The consumer is registered as a singleton AND resolved through a factory for its hosted
    /// registration, so both reach ONE instance.
    /// </summary>
    /// <remarks>
    /// The mutation this fails on is <c>AddHostedService&lt;ConversationSouthConsumer&gt;()</c>.
    /// That registers the type only as <c>IHostedService</c>: the heartbeat's constructor injection
    /// of the concrete consumer would fail to resolve, and registering both forms independently
    /// would yield two consumers — two broker subscriptions, and two claims for every delivery.
    /// </remarks>
    [Fact]
    public void TheConsumerIsOneInstanceReachableByBothItsConcreteTypeAndIHostedService()
    {
        var services = Register(FullyConfigured());

        var concrete = Assert.Single(services, d => d.ServiceType == typeof(ConversationSouthConsumer));
        Assert.Equal(ServiceLifetime.Singleton, concrete.Lifetime);

        var hosted = services.Where(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && d.ImplementationFactory is not null).ToList();

        Assert.NotEmpty(hosted);

        // A factory registration carries no ImplementationType, which is exactly how the one-instance
        // shape is distinguishable from the two-instance one.
        Assert.DoesNotContain(services, d =>
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && d.ImplementationType == typeof(ConversationSouthConsumer));
    }

    // ── AC7: the six startup validations ─────────────────────────────────────

    /// <summary>
    /// Validation 1 — a present <c>SouthBaseUrl</c> with any other required key missing or blank
    /// fails startup, naming the key.
    /// </summary>
    /// <remarks>
    /// It must NOT silently degrade to disabled. An operator who configured half of it would
    /// otherwise see a healthy process beside a queue nobody drains, which is the exact state #303
    /// exists to end — and the one a "be lenient" reading of this branch would reintroduce.
    /// </remarks>
    [Theory]
    [InlineData(nameof(ConversationsOptions.SouthBearerToken))]
    [InlineData("Agent:ShortName")]
    [InlineData("RabbitMq:Host")]
    public void AMissingRequiredKeyFailsStartupAndNamesTheKey(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured((key, null))));

        Assert.Contains(LeafOf(key), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ConversationsOptions.SouthBearerToken))]
    [InlineData("Agent:ShortName")]
    [InlineData("RabbitMq:Host")]
    public void ABlankRequiredKeyFailsStartupToo(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured((key, "   "))));

        Assert.Contains(LeafOf(key), error.Message, StringComparison.Ordinal);
    }

    private static string LeafOf(string key) => key.Split(':')[^1];

    /// <summary>Validation 2 — the base URL must be absolute.</summary>
    [Theory]
    [InlineData("south:8082")]
    [InlineData("/deliveries:claim")]
    [InlineData("not a url")]
    public void ANonAbsoluteSouthBaseUrlFailsStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured((nameof(ConversationsOptions.SouthBaseUrl), value))));

        Assert.Contains(nameof(ConversationsOptions.SouthBaseUrl), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validation 3 — the agent's own <c>ShortName</c> must match the pattern the SERVICE enforces.
    /// </summary>
    /// <remarks>
    /// It is both the routing key and a queue-name segment. A value the two sides read differently
    /// means this agent binds and drains a queue nobody publishes to while the real one grows — with
    /// no error anywhere, which is the worst shape a configuration fault can take. Checked here and
    /// carried nowhere: the seam borrows the identity the agent already has rather than taking a
    /// second field for it, which is the divergence this check is about.
    /// </remarks>
    [Theory]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    [InlineData("ünïcode")]
    public void AShortNameOutsideThePatternFailsStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured((ShortNameKey, value))));

        Assert.Contains(nameof(AgentOptions.ShortName), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("agent-1")]
    [InlineData("agent_1")]
    [InlineData("A0")]
    public void AShortNameInsideThePatternStarts(string value) =>
        Register(FullyConfigured((ShortNameKey, value)));

    /// <summary>Validation 4 — a non-positive heartbeat interval fails startup.</summary>
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:30")]
    public void ANonPositiveHeartbeatIntervalFailsStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured((nameof(ConversationsOptions.HeartbeatInterval), value))));

        Assert.Contains(nameof(ConversationsOptions.HeartbeatInterval), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validation 5 — a heartbeat above half the lease bound fails startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A heartbeat at or above the lease guarantees abandonment of every turn that outlives one
    /// lease period, and the symptom — <c>attempt_abandoned</c> on healthy turns — reads as a store
    /// fault rather than a configuration error. Half, not "less than", so a single lost renewal is
    /// survivable.
    /// </para>
    /// <para>
    /// Validated against <see cref="ConversationLeaseDefaults.MaxHeartbeatInterval"/>, never a
    /// literal. The mutation this fails on is re-introducing a hard-coded 120 seconds on this side —
    /// two literals drift, and the drift is silent.
    /// </para>
    /// </remarks>
    [Fact]
    public void AHeartbeatAboveHalfTheLeaseBoundFailsStartup()
    {
        var tooLong = ConversationLeaseDefaults.MaxHeartbeatInterval + TimeSpan.FromSeconds(1);

        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured(
                (nameof(ConversationsOptions.HeartbeatInterval), tooLong.ToString()))));

        Assert.Contains(nameof(ConversationsOptions.HeartbeatInterval), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeartbeatExactlyAtHalfTheLeaseBoundIsAccepted() =>
        Register(FullyConfigured(
            (nameof(ConversationsOptions.HeartbeatInterval),
                ConversationLeaseDefaults.MaxHeartbeatInterval.ToString())));

    /// <summary>
    /// The agent's bound and the store's default come from ONE constant.
    /// </summary>
    /// <remarks>
    /// The agent cannot read the service's configured lease at runtime, so the only thing standing
    /// between the two sides is that they compile against the same number.
    /// </remarks>
    [Fact]
    public void TheLeaseBoundIsShared()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), ConversationLeaseDefaults.LeaseDuration);
        Assert.Equal(
            ConversationLeaseDefaults.LeaseDuration / 2,
            ConversationLeaseDefaults.MaxHeartbeatInterval);
    }

    /// <summary>
    /// A prefetch of one is refused.
    /// </summary>
    /// <remarks>
    /// A delivery is not acknowledged until its terminal commits. With a prefetch of one, a second
    /// submission could never be delivered while the first turn ran — so <c>injected</c> and
    /// <c>queued</c>, the two dispositions this whole seam exists to report to a client, could never
    /// occur. The failure would look like a feature that simply never triggers.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void APrefetchBelowTwoFailsStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Register(FullyConfigured((nameof(ConversationsOptions.Prefetch), value))));

        Assert.Contains(nameof(ConversationsOptions.Prefetch), error.Message, StringComparison.Ordinal);
    }

    /// <summary>A fully valid configuration starts — the positive control for every case above.</summary>
    [Fact]
    public void AValidConfigurationDoesNotThrow()
    {
        var services = Register(FullyConfigured());

        Assert.NotEmpty(services);
    }
}
