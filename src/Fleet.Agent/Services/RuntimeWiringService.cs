using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// Owns the runtime wiring that must happen whether or not a Telegram bot token is configured
/// (D6.2).
///
/// Before this existed, relay initialization and the completion-callback attachment lived inside
/// the Telegram transport's <c>ExecuteAsync</c>, AFTER its early return for a missing token. A
/// token-less process therefore had no Telegram, no relay consumer and no completion callback —
/// so an adapter started that way would look alive while every workflow answer silently vanished.
///
/// This service owns ONLY the relay subscription, relay initialization and shutdown-token
/// distribution. The completion effects are attached in the constructors of
/// <see cref="RelayCompletionPublisher"/> and <see cref="CompletionContextBuffer"/> (#277 D-2);
/// this service verifies both are attached before consumption starts.
/// </summary>
public sealed class RuntimeWiringService : IHostedService
{
    private readonly GroupRelayService _relay;
    private readonly GroupBehavior _groupBehavior;
    private readonly MessageRouter _router;
    private readonly RelayCompletionPublisher _relayCompletions;
    private readonly CompletionContextBuffer _contextBuffer;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RuntimeWiringService> _logger;

    public RuntimeWiringService(
        GroupRelayService relay,
        GroupBehavior groupBehavior,
        MessageRouter router,
        RelayCompletionPublisher relayCompletions,
        CompletionContextBuffer contextBuffer,
        IHostApplicationLifetime lifetime,
        ILogger<RuntimeWiringService> logger)
    {
        _relay = relay;
        _groupBehavior = groupBehavior;
        _router = router;
        _relayCompletions = relayCompletions;
        _contextBuffer = contextBuffer;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. Guard. The host materializes every IHostedService — running every constructor —
        //    before calling any StartAsync, so both components' constructor-time subscriptions
        //    have already happened by now. This check exists so the implementation does not
        //    silently DEPEND on that framework detail: if the ordering assumption ever breaks,
        //    startup fails loudly instead of running a process that drops every relay answer.
        //
        //    It checks each component BY IDENTITY, not TaskManager.HasCompletionSubscriber. That
        //    property is true when ANY handler is attached, and since #277 D-2 split the single
        //    handler into two owners it would be satisfied by CompletionContextBuffer alone —
        //    while every workflow answer vanished. A boolean "someone is listening" is the exact
        //    silent-loss defect this guard was added to prevent (#277 MUST NOT 21).
        //
        //    Both are constructor-injected by concrete type, so a missing REGISTRATION fails at DI
        //    resolution with the type name in it, before StartAsync; this check covers the
        //    registered-but-unsubscribed case.
        if (!_relayCompletions.IsAttached || !_contextBuffer.IsAttached)
        {
            throw new InvalidOperationException(
                "RuntimeWiringService started before the completion handlers were attached to " +
                "TaskManager (RelayCompletionPublisher.IsAttached=" + _relayCompletions.IsAttached +
                ", CompletionContextBuffer.IsAttached=" + _contextBuffer.IsAttached + "). " +
                "Relay and bridge answers would be silently dropped. Both must be constructed " +
                "before this service starts.");
        }

        // 2. Attach the relay subscriber BEFORE consumption starts.
        //
        //    This is the one intentional behaviour change in this work, and it is strictly a race
        //    fix. GroupRelayService.InitializeAsync starts a consumer and only then does the
        //    caller subscribe; the consumer dispatches under autoAck, so a directive arriving in
        //    that window was acknowledged and dropped. Subscribing first closes it.
        if (_relay.IsEnabled)
            _relay.MessageReceived += _groupBehavior.OnRelayMessage;

        // 3. Now start consuming.
        await _relay.InitializeAsync(_lifetime.ApplicationStopping);

        // 4. Distribute the shutdown token.
        //
        //    ApplicationStopping, NOT StartAsync's cancellationToken. The latter aborts STARTUP
        //    and never fires on a normal shutdown, so passing it here would hand GroupBehavior a
        //    token that never cancels and leave debounce timers running past stop.
        _groupBehavior.SetShutdownToken(_lifetime.ApplicationStopping);
        _router.SetShutdownToken(_lifetime.ApplicationStopping);

        _logger.LogInformation(
            "Runtime wiring complete (relay {RelayState})",
            _relay.IsEnabled ? "enabled" : "disabled");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_relay.IsEnabled)
            _relay.MessageReceived -= _groupBehavior.OnRelayMessage;
        return Task.CompletedTask;
    }
}
