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
    /// How far back a poll looks for newly closed failed runs. Failures already recorded stay in
    /// <see cref="StateFile"/> until delivered, whatever their age.
    /// </summary>
    public int LookbackHours { get; set; } = 6;

    /// <summary>
    /// Delivery attempts per failure (a failed hand-over or a notification run that closed without
    /// delivering) before it is parked. Parked records persist and stay visible.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>
    /// Durable record of every failure not yet delivered, so a restart resumes it whatever its age.
    /// Mount its directory on a volume. Empty keeps state in memory only (reported as not durable).
    /// </summary>
    public string StateFile { get; set; } = "/var/lib/fleet-temporal-bridge/workflow-failures.json";

    /// <summary>Namespace and task queue of the existing NotifyCtoWorkflow, the delivery path.</summary>
    public string NotificationNamespace { get; set; } = "fleet";

    public string NotificationTaskQueue { get; set; } = "fleet";
}
