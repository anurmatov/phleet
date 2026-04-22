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
    /// </summary>
    public static void UpdateCtoAgent(string newValue)
    {
        lock (_lock)
        {
            if (_instance is not null)
            {
                _instance.CtoAgent = newValue;
                _instance.EscalationTarget = newValue;
            }
        }
    }
}
