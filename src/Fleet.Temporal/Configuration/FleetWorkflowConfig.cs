namespace Fleet.Temporal.Configuration;

/// <summary>
/// Static singleton accessor for <see cref="FleetWorkflowOptions"/>. Set once at startup via
/// <see cref="Initialize"/>, then safe for Temporal workflow determinism because it is immutable
/// after initialization.
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
    /// Initialize the singleton. Must be called exactly once during application startup.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if called more than once.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="options"/> is null.</exception>
    public static void Initialize(FleetWorkflowOptions options)
    {
        lock (_lock)
        {
            if (_instance is not null)
                throw new InvalidOperationException(
                    "FleetWorkflowConfig has already been initialized.");
            _instance = options ?? throw new ArgumentNullException(nameof(options));
        }
    }
}
