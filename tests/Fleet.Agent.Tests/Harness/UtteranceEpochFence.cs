namespace Fleet.Agent.Tests.Harness;

/// <summary>What the fence decided about an item that arrived from an asynchronous stage.</summary>
internal enum FenceOutcome
{
    /// <summary>Current epoch, call live — submit / speak / play it.</summary>
    Accepted,

    /// <summary>Current epoch but the call has ended: deliver to the text transcript, never speak it.</summary>
    TranscriptOnly,

    /// <summary>A newer utterance or synthesis superseded this one. Dropped and counted.</summary>
    DroppedStale,

    /// <summary>Audio that arrived after the call ended. Dropped and counted.</summary>
    DroppedAfterHangup,
}

/// <summary>A single fence decision, stamped from the injected clock.</summary>
internal readonly record struct FenceDecision(
    FenceOutcome Outcome, string Stage, long Epoch, DateTimeOffset At)
{
    public bool IsDrop => Outcome is FenceOutcome.DroppedStale or FenceOutcome.DroppedAfterHangup;
}

/// <summary>
/// D10 — the correctness half of barge-in, expressed as a pure, clock-injectable state machine.
///
/// <para>Latency is the comfort question; playing the WRONG audio is the correctness question. Two
/// monotonically increasing counters carry it: <see cref="UtteranceEpoch"/> advances on every
/// accepted user utterance, and <see cref="PlaybackEpoch"/> advances on every synthesis start.
/// Every STT result, terminal answer and audio buffer is tagged with the epoch current when it was
/// initiated; anything arriving with a stale epoch is dropped and counted — never played, never
/// submitted.</para>
///
/// <para><b>This lives in the test project.</b> The spike's job is to prove the rule is expressible
/// and sufficient, not to ship the component. Productionising it is a follow-up.</para>
///
/// <para>Time comes from an injected <see cref="TimeProvider"/> so barge-in ordering is
/// deterministic. There is no <c>Task.Delay</c> and no wall-clock assertion anywhere in the fence
/// or its tests (AC15, MUST NOT #13): a sleep-based ordering test is flaky by construction and
/// gets muted rather than fixed.</para>
/// </summary>
internal sealed class UtteranceEpochFence
{
    private readonly TimeProvider _time;
    private readonly List<FenceDecision> _decisions = [];

    public UtteranceEpochFence(TimeProvider timeProvider) => _time = timeProvider;

    /// <summary>Advances on every accepted user utterance. Starts at 0 — no utterance yet.</summary>
    public long UtteranceEpoch { get; private set; }

    /// <summary>Advances on every synthesis start. Starts at 0 — nothing synthesised yet.</summary>
    public long PlaybackEpoch { get; private set; }

    /// <summary>The utterance epoch the currently-synthesising playback belongs to.</summary>
    public long PlaybackUtteranceEpoch { get; private set; }

    /// <summary>False once the call has ended. A hangup never rewinds.</summary>
    public bool CallActive { get; private set; } = true;

    /// <summary>Every decision in order, each stamped from the injected clock.</summary>
    public IReadOnlyList<FenceDecision> Decisions => _decisions;

    /// <summary>Total items dropped. Exactly one increment per dropped item.</summary>
    public int Drops { get; private set; }

    public int SttDrops { get; private set; }
    public int FinalDrops { get; private set; }
    public int AudioDrops { get; private set; }

    /// <summary>Answers that were held back from speech because the call had ended.</summary>
    public int PostHangupSpeechSuppressed { get; private set; }

    /// <summary>When the most recent barge-in asked local playback to stop.</summary>
    public DateTimeOffset? LastStopPlaybackRequestedAt { get; private set; }

    /// <summary>
    /// A new user utterance was accepted. Returns the epoch to tag its STT request with.
    ///
    /// <para>When this happens during playback it IS the barge-in: the stop request is recorded
    /// here and is local, so it never depends on a server round trip.</para>
    /// </summary>
    public long BeginUtterance()
    {
        UtteranceEpoch++;
        if (PlaybackEpoch > 0 && PlaybackUtteranceEpoch < UtteranceEpoch)
            LastStopPlaybackRequestedAt = _time.GetUtcNow();
        return UtteranceEpoch;
    }

    /// <summary>A transcript came back from STT. Only the current utterance may be submitted.</summary>
    public FenceDecision AcceptTranscript(long utteranceEpoch)
    {
        if (utteranceEpoch != UtteranceEpoch)
        {
            SttDrops++;
            return Record(FenceOutcome.DroppedStale, "stt", utteranceEpoch);
        }
        return Record(FenceOutcome.Accepted, "stt", utteranceEpoch);
    }

    /// <summary>
    /// A terminal answer arrived for an utterance.
    ///
    /// <para>Post-hangup ownership (D10): a turn still running when the call ends must still
    /// terminate its submission, and its text goes to the transcript ONLY. Speaking a late answer
    /// into a room after the call ended is a hard failure, not a latency nuisance — so the
    /// current-epoch post-hangup case is <see cref="FenceOutcome.TranscriptOnly"/> rather than a
    /// drop, and it is counted separately from stale drops.</para>
    /// </summary>
    public FenceDecision AcceptFinal(long utteranceEpoch)
    {
        if (utteranceEpoch != UtteranceEpoch)
        {
            FinalDrops++;
            return Record(FenceOutcome.DroppedStale, "final", utteranceEpoch);
        }

        if (!CallActive)
        {
            PostHangupSpeechSuppressed++;
            return Record(FenceOutcome.TranscriptOnly, "final", utteranceEpoch);
        }

        return Record(FenceOutcome.Accepted, "final", utteranceEpoch);
    }

    /// <summary>
    /// Synthesis started for an utterance. Returns the playback epoch its audio must carry, or a
    /// stale decision when the utterance has already been superseded.
    /// </summary>
    public (FenceDecision Decision, long PlaybackEpoch) BeginSynthesis(long utteranceEpoch)
    {
        if (utteranceEpoch != UtteranceEpoch || !CallActive)
        {
            FinalDrops++;
            return (Record(
                CallActive ? FenceOutcome.DroppedStale : FenceOutcome.DroppedAfterHangup,
                "synthesis", utteranceEpoch), PlaybackEpoch);
        }

        PlaybackEpoch++;
        PlaybackUtteranceEpoch = utteranceEpoch;
        return (Record(FenceOutcome.Accepted, "synthesis", PlaybackEpoch), PlaybackEpoch);
    }

    /// <summary>An audio buffer arrived. Only the current playback epoch, on a live call, may play.</summary>
    public FenceDecision AcceptAudio(long playbackEpoch)
    {
        if (!CallActive)
        {
            AudioDrops++;
            return Record(FenceOutcome.DroppedAfterHangup, "audio", playbackEpoch);
        }

        if (playbackEpoch != PlaybackEpoch)
        {
            AudioDrops++;
            return Record(FenceOutcome.DroppedStale, "audio", playbackEpoch);
        }

        return Record(FenceOutcome.Accepted, "audio", playbackEpoch);
    }

    /// <summary>The call ended. Idempotent — a second hangup is not a second state change.</summary>
    public void Hangup() => CallActive = false;

    private FenceDecision Record(FenceOutcome outcome, string stage, long epoch)
    {
        var decision = new FenceDecision(outcome, stage, epoch, _time.GetUtcNow());
        _decisions.Add(decision);
        if (decision.IsDrop)
            Drops++;
        return decision;
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> the test advances by hand. Exists so the fence can be clock-driven
/// without adding a package reference for one test double.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Move the clock forward. Never backwards — a monotonic clock is part of the contract.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "The fence's clock is monotonic.");
        _now += delta;
    }
}
