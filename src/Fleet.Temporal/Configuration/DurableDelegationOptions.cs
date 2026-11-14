namespace Fleet.Temporal.Configuration;

/// <summary>
/// Settings for <see cref="Services.DurableDelegationService"/>. Off by default: with it off,
/// <see cref="Activities.DelegateToAgentActivity"/> waits for the agent inside one activity
/// execution exactly as it always did.
/// </summary>
public sealed class DurableDelegationOptions
{
    public const string Section = "DurableDelegation";

    /// <summary>
    /// When true a delegation is recorded in <see cref="StateFile"/> and its activity is completed
    /// from that record, so it survives a bridge restart. Read once at start.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Every delegation still waiting for its agent. Mount its directory on a volume. It holds the
    /// instructions sent and any partial answers received, so it is as sensitive as the workflows
    /// are. Empty keeps the records in memory only, which survives nothing.
    /// </summary>
    public string StateFile { get; set; } = "/var/lib/fleet-temporal-bridge/pending-delegations.json";

    /// <summary>
    /// Seconds between passes over the pending delegations. Each pass heartbeats every one of
    /// them, so this must stay well under the shortest <c>HeartbeatTimeout</c> a workflow schedules
    /// a delegation with (90 seconds today).
    /// </summary>
    public int TickSeconds { get; set; } = 15;

    /// <summary>
    /// What to do with an answer that names no execution when the delegation it is for has an
    /// execution id.
    /// <para>
    /// By default (true) it is refused, like an answer that names another execution: acknowledged,
    /// logged, and nothing changes. An answer is then never matched by task id alone, so a
    /// predecessor's late answer cannot be taken for its successor's. The price is the rollout
    /// order: an agent on an image from before the id sends every answer without one, so a
    /// delegation to such an agent never completes and ends when its budget runs out. Upgrade
    /// every agent before turning durable delegation on.
    /// </para>
    /// <para>
    /// Set to false to accept such an answer by task id, with a warning each time. That keeps
    /// older agents working and reopens the hole for them. The bridge cannot tell an older agent
    /// from a newer one by an answer, so it does not decide this for itself.
    /// </para>
    /// </summary>
    public bool RequireAnswerExecutionId { get; set; } = true;
}
