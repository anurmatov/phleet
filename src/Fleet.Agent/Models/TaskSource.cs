namespace Fleet.Agent.Models;

/// <summary>
/// Identifies the origin of a task for source-specific behavior
/// (e.g., IDLE suppression for check-ins, relay routing for directives).
/// </summary>
public enum TaskSource
{
    /// <summary>Regular message from Telegram (DM or group mention/reply).</summary>
    UserMessage,

    /// <summary>/new command — parallel task.</summary>
    NewCommand,

    /// <summary>Relay directive from another agent.</summary>
    Relay,

    /// <summary>Periodic check-in (debounce, proactive, supervision).</summary>
    CheckIn,

    /// <summary>Request from external agent via Fleet.Bridge.</summary>
    Bridge,
}
