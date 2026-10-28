namespace Fleet.Temporal.Configuration;

/// <summary>
/// Settings for <see cref="Services.WorkflowFailureNotifier"/> (#488). Off by default: turning it on
/// is an operator decision, and a fresh install with it off behaves exactly as before.
/// </summary>
public sealed class WorkflowFailureNotifierOptions
{
    public const string Section = "WorkflowFailureNotifier";

    /// <summary>When false the notifier does not poll and sends nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Agent that receives the notifications. Empty means the configured coordinator,
    /// <c>FLEET_CTO_AGENT</c>. Neither set means notifications are held, never guessed.
    /// </summary>
    public string CoordinatorAgent { get; set; } = "";

    /// <summary>Seconds between polls of the configured namespaces.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How far back a poll looks for closed failed runs. Bounds what a restart re-examines; it must
    /// stay below the notification namespace's retention, which is what remembers sent notices.
    /// </summary>
    public int LookbackHours { get; set; } = 6;

    /// <summary>Send attempts per failure before it is parked as an error until the next restart.</summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>Namespace and task queue of the existing NotifyCtoWorkflow, the delivery path.</summary>
    public string NotificationNamespace { get; set; } = "fleet";

    public string NotificationTaskQueue { get; set; } = "fleet";
}
