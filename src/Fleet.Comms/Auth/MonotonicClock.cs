namespace Fleet.Comms.Auth;

/// <summary>
/// The clock every expiry decision on this boundary is made against.
///
/// <para><b>Storing an absolute deadline does not, by itself, make expiry irreversible.</b> The
/// deadline is fixed, but the value it is compared against is wall-clock time, and wall-clock time
/// moves backwards — an NTP step, a hypervisor restore, an operator correcting a drifted host. A
/// comparison against raw wall time therefore lets a credential that has already been refused
/// authenticate again, which is the one direction an auth boundary must never move.</para>
///
/// <para>Three sources are combined and the largest wins:</para>
/// <list type="number">
///   <item><description>wall-clock time, so a forward correction is honoured immediately and
///     expiry is never <i>delayed</i> by this wrapper;</description></item>
///   <item><description>the wall clock captured at construction plus <b>monotonic</b> elapsed time
///     from <see cref="TimeProvider.GetTimestamp"/>, which no clock change can move, so time keeps
///     advancing at the right rate across a backward jump rather than freezing;</description></item>
///   <item><description>a high-water mark of everything already returned, so no observer ever sees
///     this clock go backwards even between the two reads above.</description></item>
/// </list>
///
/// <para>This holds within a process. Across a restart the anchor is taken from whatever the host
/// clock then says, so <see cref="AuthService"/> additionally <b>burns</b> a credential in the store
/// the first time it is observed expired — see <c>AuthService.Rejected</c> call sites. The two
/// mechanisms cover different halves of the same rule: this one makes expiry irreversible while the
/// process lives, the burn makes it irreversible after it dies.</para>
/// </summary>
public sealed class MonotonicClock
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    // The anchor MOVES. Keeping it at construction forever is what froze this clock: a forward
    // correction would raise the returned value without moving the point elapsed time is measured
    // from, so a later rollback left the projection trailing hours behind and the high-water mark
    // held the result still until it caught up. Effective time then advanced at zero, and every
    // deadline measured against it stopped arriving.
    private DateTimeOffset _anchorWall;
    private long _anchorTimestamp;
    private DateTimeOffset _highWater;

    public MonotonicClock(TimeProvider time)
    {
        _time = time;
        _anchorWall = time.GetUtcNow();
        _anchorTimestamp = time.GetTimestamp();
        _highWater = _anchorWall;
    }

    /// <summary>The current time, guaranteed never to be earlier than any value already returned.</summary>
    public DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            var timestamp = _time.GetTimestamp();
            var projected = _anchorWall + _time.GetElapsedTime(_anchorTimestamp, timestamp);
            var wall = _time.GetUtcNow();

            if (wall > projected)
            {
                // A forward correction. Adopt it AND re-anchor, so elapsed time from here on is
                // measured from the corrected reading. Adopting the value without re-anchoring is
                // the freeze described above.
                _anchorWall = wall;
                _anchorTimestamp = timestamp;
                projected = wall;
            }

            // Redundant while the timestamp source is genuinely monotonic, which is the contract of
            // GetTimestamp. Kept because the cost is a comparison and the failure it guards — a
            // clock that goes backwards — is the one this class exists to make impossible.
            if (projected > _highWater)
                _highWater = projected;

            return _highWater;
        }
    }
}
