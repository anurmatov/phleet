namespace Fleet.Agent.Configuration;

/// <summary>
/// Bounds and default for <see cref="AgentOptions.TurnStallTimeoutMinutes"/>: how long a running
/// turn may go without a single event from its provider process before the agent stops it.
/// </summary>
/// <remarks>
/// <para>
/// The default is deliberately long. The watchdog sees only the events an executor yields, and
/// some legitimate work is silent for a long time: a Claude Bash call can run ten minutes, a Codex
/// command runs for as long as the model asked without yielding anything until it completes, a
/// slow local model can take minutes before its first token, and an agent that calls
/// <c>temporal_get_workflow_result</c> on a running workflow waits, silently, for that workflow to
/// finish. Sixty minutes would cut the last of those. Three hours is past all of them in ordinary
/// use and still ends a wedged process the same night. Turns delegated by a workflow are bounded
/// sooner anyway: the bridge cancels them when the step times out.
/// </para>
/// <para>
/// <c>0</c> turns the watchdog off.
/// </para>
/// </remarks>
public static class TurnStallTimeout
{
    public const int DefaultMinutes = 180;

    /// <summary>One week. Anything longer is a typo, not a setting.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>Null when valid, otherwise the fault to stop the host with.</summary>
    public static string? DescribeFault(int minutes) =>
        minutes is >= 0 and <= MaxMinutes
            ? null
            : $"Agent:TurnStallTimeoutMinutes is {minutes}; it must be 0 (off) or between 1 and {MaxMinutes}.";

    /// <summary>The fixed text a stalled turn fails with. No provider or prompt text, only the limit.</summary>
    public static string FailureMessage(TimeSpan timeout) =>
        $"Task failed: no output from the model for {(int)Math.Round(timeout.TotalMinutes)} minutes, so the turn was stopped (stall watchdog).";
}
