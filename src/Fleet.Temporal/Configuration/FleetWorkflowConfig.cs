namespace Fleet.Temporal.Configuration;

/// <summary>
/// Static singleton accessor for <see cref="FleetWorkflowOptions"/>. Set once at startup via
/// <see cref="Initialize"/>, then safe for Temporal workflow determinism because mutations are
/// guarded by a lock and are safe to call from the hosted config service.
/// </summary>
public static class FleetWorkflowConfig
{
    private static FleetWorkflowOptions? _instance;
    private static readonly object _lock = new();

    // GroupChatId is stored separately: it comes from FLEET_GROUP_CHAT_ID via PeerConfigClient
    // and is used only inside activities (DelegateToAgentActivity), so runtime mutation is safe.
    private static long _groupChatId;

    /// <summary>
    /// Telegram group chat ID resolved at runtime from FLEET_GROUP_CHAT_ID via PeerConfigClient.
    /// Falls back to <c>TemporalBridgeOptions.GroupChatId</c> when zero (set in compose env).
    /// </summary>
    public static long GroupChatId => Interlocked.Read(ref _groupChatId);

    /// <summary>
    /// Update GroupChatId at runtime (called from PeerConfigHostedService on config.changed).
    /// Safe to call from non-workflow code — never read inside Temporal workflow replay paths.
    /// </summary>
    public static void UpdateGroupChatId(long chatId)
    {
        Interlocked.Exchange(ref _groupChatId, chatId);
    }

    /// <summary>
    /// The initialized options instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if accessed before <see cref="Initialize"/> is called.</exception>
    public static FleetWorkflowOptions Instance =>
        _instance ?? throw new InvalidOperationException(
            "FleetWorkflowConfig has not been initialized. Call Initialize() during startup.");

    /// <summary>
    /// Initialize the singleton. May be called more than once (subsequent calls are no-ops)
    /// to allow deferred config from peer-config service.
    /// </summary>
    public static void Initialize(FleetWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lock)
        {
            _instance ??= options;
        }
    }

    /// <summary>
    /// Update CtoAgent at runtime (called from PeerConfigHostedService on config.changed).
    ///
    /// NOTE: <see cref="FleetWorkflowOptions.EscalationTarget"/> is intentionally NOT updated here.
    /// It is read inside Temporal workflow code (<see cref="Workflows.FleetWorkflowBase"/>),
    /// and live mutations during workflow replay would cause Temporal non-determinism crashes.
    /// EscalationTarget is set once at startup from appsettings / compose env and stays immutable.
    /// CtoAgent is only consumed inside activities (via UWE template {{config.CtoAgent}}), so it
    /// is safe to mutate at runtime.
    /// </summary>
    public static void UpdateCtoAgent(string newValue)
    {
        lock (_lock)
        {
            if (_instance is not null)
                _instance.CtoAgent = newValue;
        }
    }
}
