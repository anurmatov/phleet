using System.Collections.Concurrent;

namespace Fleet.Agent.Services;

/// <summary>
/// Tracks mid-turn injection outcomes by provider so tests and status surfaces can
/// distinguish true live delivery from fallback queueing.
/// </summary>
public sealed class InjectionOutcomeCounter
{
    public const string Injected = "injected";
    /// <summary>A same-chat human message was queued instead of entering an operational turn.</summary>
    public const string NotInjectedWorkflowTurn = "not_injected_workflow_turn";
    public const string DegradedToQueue = "degraded_to_queue";
    public const string FailedThenQueued = "failed_then_queued";
    public const string DroppedAtQueueCap = "dropped_at_queue_cap";
    public const string PossibleDuplicateAfterResume = "possible_duplicate_after_resume";
    public const string MergedIntoQueue = "merged_into_queue";
    /// <summary>An injected message ran as its own provider turn and its answer was delivered at once (#369).</summary>
    public const string AnsweredAsSeparateTurn = "answered_as_separate_turn";
    /// <summary>A verified human's steering copy entered a running Relay/Bridge turn (#406).</summary>
    public const string SteeredNonHumanTurn = "steered_non_human_turn";
    /// <summary>A steering copy was not delivered (no active turn, unsupported, failed, cap, closed); the message stays queued.</summary>
    public const string SteerNotDelivered = "steer_not_delivered";
    /// <summary>A second human tried to steer a turn another (chat, user) already steered; queued only.</summary>
    public const string SteerRefusedOtherHuman = "steer_refused_other_human";
    /// <summary>An answer produced only by a steering copy was dropped, never delivered.</summary>
    public const string SteerAnswerDiscarded = "steer_answer_discarded";

    private readonly ConcurrentDictionary<(string provider, string outcome), long> _counts = new();

    public void Increment(string provider, string outcome) =>
        _counts.AddOrUpdate((provider, outcome), 1L, (_, c) => c + 1L);

    public long GetCount(string provider, string outcome) =>
        _counts.TryGetValue((provider, outcome), out var value) ? value : 0L;
}
