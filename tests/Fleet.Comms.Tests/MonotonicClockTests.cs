using Fleet.Comms.Auth;

namespace Fleet.Comms.Tests;

/// <summary>
/// The clock every expiry comparison on this boundary runs against.
///
/// <para>These are the unit-level statement of the property the lifecycle tests assert end to end:
/// a stored deadline is only as irreversible as the value it is compared with, and wall-clock time
/// is not irreversible.</para>
/// </summary>
public class MonotonicClockTests
{
    [Fact]
    public void ItTracksTheWallClockWhileTheWallClockOnlyMovesForward()
    {
        var time = new TestTimeProvider();
        var clock = new MonotonicClock(time);
        var start = clock.GetUtcNow();

        time.Advance(TimeSpan.FromMinutes(7));

        Assert.Equal(start + TimeSpan.FromMinutes(7), clock.GetUtcNow());
    }

    [Fact]
    public void ABackwardWallClockJumpNeverMovesItBack()
    {
        var time = new TestTimeProvider();
        var clock = new MonotonicClock(time);

        time.Advance(TimeSpan.FromMinutes(20));
        var peak = clock.GetUtcNow();

        time.SetBackwards(TimeSpan.FromMinutes(5));
        Assert.Equal(peak, clock.GetUtcNow());

        // Far enough back that the wall reading precedes construction — a restored snapshot.
        time.SetBackwards(TimeSpan.FromHours(2));
        Assert.Equal(peak, clock.GetUtcNow());
    }

    [Fact]
    public void AfterABackwardJumpItKeepsAdvancingAtTheMonotonicRate()
    {
        var time = new TestTimeProvider();
        var clock = new MonotonicClock(time);

        time.Advance(TimeSpan.FromMinutes(20));
        var peak = clock.GetUtcNow();

        // The host is corrected backwards, then ten real minutes pass. Time must keep moving, or a
        // credential issued during the skew would carry a deadline that never arrives.
        time.SetBackwards(TimeSpan.FromMinutes(5));
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(peak + TimeSpan.FromMinutes(10), clock.GetUtcNow());
    }

    /// <summary>
    /// A forward correction is honoured immediately. The wrapper exists to stop expiry being
    /// undone, not to delay it — clamping a forward jump would keep a credential alive past its
    /// deadline, which is the same failure in the other direction.
    /// </summary>
    [Fact]
    public void AForwardWallClockJumpIsHonouredImmediately()
    {
        var time = new TestTimeProvider();
        var clock = new MonotonicClock(time);
        var start = clock.GetUtcNow();

        time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(start + TimeSpan.FromHours(3), clock.GetUtcNow());
    }
}
