using Fleet.Agent.Tests.Harness;

namespace Fleet.Agent.Tests;

/// <summary>
/// D10 / AC14–15 — the correctness half of barge-in.
///
/// <para>Latency is the comfort question; playing the WRONG audio is the correctness question.
/// Every case below drops exactly one stale item and increments the drop counter by exactly one —
/// a fence that silently swallows two is as wrong as one that plays them.</para>
///
/// <para>The fence is driven by an injected <see cref="TimeProvider"/> and there is no
/// <c>Task.Delay</c> and no wall-clock assertion in this file. A sleep-based ordering test is flaky
/// by construction and gets muted rather than fixed (MUST NOT #13).</para>
/// </summary>
public class VoiceEpochFenceTests
{
    private static (UtteranceEpochFence Fence, ManualTimeProvider Clock) Build()
    {
        var clock = new ManualTimeProvider();
        return (new UtteranceEpochFence(clock), clock);
    }

    [Fact]
    public void LateSttResultForASupersededUtteranceIsDropped()
    {
        var (fence, clock) = Build();

        var first = fence.BeginUtterance();
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var second = fence.BeginUtterance();

        // The first utterance's transcript comes back after the user has already said something
        // else. Submitting it would answer a question the user has moved on from.
        var stale = fence.AcceptTranscript(first);
        Assert.Equal(FenceOutcome.DroppedStale, stale.Outcome);
        Assert.Equal(1, fence.Drops);
        Assert.Equal(1, fence.SttDrops);

        Assert.Equal(FenceOutcome.Accepted, fence.AcceptTranscript(second).Outcome);
        Assert.Equal(1, fence.Drops);
    }

    [Fact]
    public void LateFinalAnswerForASupersededUtteranceIsDropped()
    {
        var (fence, _) = Build();

        var first = fence.BeginUtterance();
        var second = fence.BeginUtterance();

        var stale = fence.AcceptFinal(first);
        Assert.Equal(FenceOutcome.DroppedStale, stale.Outcome);
        Assert.Equal(1, fence.Drops);
        Assert.Equal(1, fence.FinalDrops);

        Assert.Equal(FenceOutcome.Accepted, fence.AcceptFinal(second).Outcome);
        Assert.Equal(1, fence.Drops);
    }

    [Fact]
    public void LateAudioChunkFromASupersededSynthesisIsDropped()
    {
        var (fence, _) = Build();

        var first = fence.BeginUtterance();
        var (_, firstPlayback) = fence.BeginSynthesis(first);

        var second = fence.BeginUtterance();
        var (_, secondPlayback) = fence.BeginSynthesis(second);
        Assert.NotEqual(firstPlayback, secondPlayback);

        // A buffer from the answer the user interrupted. Playing it is the failure this exists for.
        var stale = fence.AcceptAudio(firstPlayback);
        Assert.Equal(FenceOutcome.DroppedStale, stale.Outcome);
        Assert.Equal(1, fence.Drops);
        Assert.Equal(1, fence.AudioDrops);

        Assert.Equal(FenceOutcome.Accepted, fence.AcceptAudio(secondPlayback).Outcome);
        Assert.Equal(1, fence.Drops);
    }

    [Fact]
    public void TwoBargeInsInsideOneTurnLeaveOnlyTheNewestPlayable()
    {
        var (fence, clock) = Build();

        var first = fence.BeginUtterance();
        var (_, firstPlayback) = fence.BeginSynthesis(first);

        clock.Advance(TimeSpan.FromMilliseconds(40));
        var second = fence.BeginUtterance();
        var (_, secondPlayback) = fence.BeginSynthesis(second);

        clock.Advance(TimeSpan.FromMilliseconds(40));
        var third = fence.BeginUtterance();
        var (_, thirdPlayback) = fence.BeginSynthesis(third);

        // Both superseded streams arrive interleaved, as they do in practice.
        Assert.Equal(FenceOutcome.DroppedStale, fence.AcceptAudio(firstPlayback).Outcome);
        Assert.Equal(FenceOutcome.Accepted, fence.AcceptAudio(thirdPlayback).Outcome);
        Assert.Equal(FenceOutcome.DroppedStale, fence.AcceptAudio(secondPlayback).Outcome);

        Assert.Equal(2, fence.Drops);
        Assert.Equal(2, fence.AudioDrops);
        Assert.Equal(3, fence.UtteranceEpoch);

        // The stop request is local and recorded from the injected clock — never a round trip.
        Assert.NotNull(fence.LastStopPlaybackRequestedAt);
    }

    [Fact]
    public void HangupDuringSynthesisSuppressesAudioButStillTerminatesTheSubmission()
    {
        var (fence, _) = Build();

        var utterance = fence.BeginUtterance();
        var (_, playback) = fence.BeginSynthesis(utterance);

        fence.Hangup();

        // (a) audio that arrives after the call ended never plays...
        var audio = fence.AcceptAudio(playback);
        Assert.Equal(FenceOutcome.DroppedAfterHangup, audio.Outcome);
        Assert.Equal(1, fence.Drops);
        Assert.Equal(1, fence.AudioDrops);

        // (b) ...and the turn still terminates, with its text going to the transcript only.
        // Speaking a late answer into a room after the call ended is a hard failure, not a
        // latency nuisance.
        var final = fence.AcceptFinal(utterance);
        Assert.Equal(FenceOutcome.TranscriptOnly, final.Outcome);
        Assert.False(final.IsDrop);
        Assert.Equal(1, fence.PostHangupSpeechSuppressed);
        Assert.Equal(1, fence.Drops);
    }

    [Fact]
    public void SynthesisForASupersededUtteranceNeverStarts()
    {
        var (fence, _) = Build();

        var first = fence.BeginUtterance();
        fence.BeginUtterance();

        var (decision, _) = fence.BeginSynthesis(first);
        Assert.Equal(FenceOutcome.DroppedStale, decision.Outcome);
        Assert.Equal(0, fence.PlaybackEpoch);
        Assert.Equal(1, fence.Drops);
    }

    [Fact]
    public void EveryDecisionIsStampedFromTheInjectedClock()
    {
        var (fence, clock) = Build();
        var start = clock.GetUtcNow();

        var utterance = fence.BeginUtterance();
        fence.AcceptTranscript(utterance);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        fence.AcceptFinal(utterance);

        Assert.Equal(2, fence.Decisions.Count);
        Assert.Equal(start, fence.Decisions[0].At);
        Assert.Equal(start + TimeSpan.FromMilliseconds(250), fence.Decisions[1].At);
    }

    [Fact]
    public void TheClockIsMonotonic() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ManualTimeProvider().Advance(TimeSpan.FromMilliseconds(-1)));
}
