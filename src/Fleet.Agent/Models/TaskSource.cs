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

    /// <summary>
    /// Human-originated group-chat batch dispatched after the debounce timer fires.
    /// Unlike <see cref="CheckIn"/> (proactive timer sweep and welcome DM), this source
    /// is eligible for same-chat mid-turn injection and uses the peek/commit pattern
    /// for pending images and the watermark so that images that arrive after the snapshot
    /// are never silently consumed by a failed-enqueue commit.
    /// </summary>
    DebouncedGroupBatch,
}
