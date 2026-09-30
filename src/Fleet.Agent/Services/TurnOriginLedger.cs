using Fleet.Agent.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Services;

/// <summary>The origin a <see cref="TurnOriginLedger"/> interval carries (#394).</summary>
public enum TurnOrigin { Human, Relay, Bridge, Unknown }

/// <summary>What the ledger can prove about a tool send's clock-uncertainty window.</summary>
public enum ToolSendAttribution
{
    /// <summary>Human intervals cover every instant of the window and nothing else touches it.</summary>
    Human,

    /// <summary>
    /// Part of the window is not covered by a human interval, or the window starts before what the
    /// ledger still remembers.
    /// </summary>
    Unattributed,

    /// <summary>A relay, bridge or unknown interval touches the window.</summary>
    NonHuman,
}

/// <summary>
/// Every period in which the provider may be acting, tagged with whose turn it is (#394). A tool
/// send made through a Telegram MCP tool is journaled only when this ledger proves it answered a
/// human.
/// </summary>
/// <remarks>
/// <para>
/// Three sources write it:
/// </para>
/// <list type="bullet">
/// <item><see cref="Pending"/>, set by <c>TaskManager</c> around an executor enumeration. An
/// <see cref="AsyncLocal{T}"/>, so it flows into the executor's iterator and nowhere else.</item>
/// <item><see cref="OpenTurn"/>, called by an executor <b>immediately after</b> it acquires its turn
/// lock and closed in the <c>finally</c> that releases it. Tagged with the pending origin, or
/// <see cref="TurnOrigin.Unknown"/> when there is none (warmup, the CLI) or for a raw command. A task
/// still waiting for the lock has no interval.</item>
/// <item><see cref="TrackProvider"/>, one per provider process, fed by its stdout reader. A
/// turn-content event read while no lock-held and no untracked interval is open opens an
/// <see cref="TurnOrigin.Unknown"/> interval; only the first terminal event after it, stdout EOF or
/// a completed kill closes it. Silence, a timeout, another lock acquisition, a restart request or a
/// cancelled reader never do.</item>
/// <item><see cref="OpenUntilEnded"/>, for work that can outlive the turn lock (a shell command
/// accepted before it runs, a cancelled command, an interrupted turn whose drain timed out, a
/// Gemini CLI whose kill is unconfirmed). <see cref="TurnOrigin.Unknown"/>; it ends only on that
/// work's own terminal event or a confirmed process exit, whoever holds the lock meanwhile.</item>
/// </list>
/// <para>
/// The rule (<see cref="Attribute"/>) is fail closed: <b>every</b> instant of
/// <c>[requestedAt − 2 s, requestedAt + 2 s]</c> must be covered by human intervals, and no relay,
/// bridge or unknown interval may touch it. There is no precedence between intervals and no
/// exact-containment step. Closed intervals are kept for 10 minutes; open ones are never evicted.
/// </para>
/// <para>
/// Never throws and never blocks beyond a short lock: an executor must not fail a turn because of
/// the journal.
/// </para>
/// </remarks>
public sealed class TurnOriginLedger
{
    /// <summary>
    /// The maximum clock difference allowed between <c>fleet-telegram</c> and the agent: the half
    /// width of a tool send's window.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(2);

    /// <summary>How long a closed interval is remembered after it closes.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    /// <summary>How long an unknown interval may stay open before one warning is logged.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly List<LedgerInterval> _intervals = [];
    private readonly AsyncLocal<TurnOrigin?> _pending = new();
    private readonly TimeProvider _time;
    private readonly ILogger<TurnOriginLedger> _logger;

    // Nothing before this instant is known — in particular after a restart, which is what makes a
    // redelivered receipt unprovable rather than attributed against an empty ledger by accident.
    private readonly DateTimeOffset _startedAt;
    private int _openLockHeld;

    public TurnOriginLedger(ILogger<TurnOriginLedger>? logger = null, TimeProvider? time = null)
    {
        _logger = logger ?? NullLogger<TurnOriginLedger>.Instance;
        _time = time ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
    }

    /// <summary>
    /// Sets the origin the next lock-held interval opened in this async flow takes. Dispose to
    /// restore the previous one.
    /// </summary>
    public IDisposable Pending(OutboundOrigin origin)
    {
        var previous = _pending.Value;
        _pending.Value = origin switch
        {
            OutboundOrigin.Relay => TurnOrigin.Relay,
            OutboundOrigin.Bridge => TurnOrigin.Bridge,
            _ => TurnOrigin.Human,
        };
        return new PendingScope(this, previous);
    }

    /// <summary>
    /// Opens a lock-held interval (Gemini: a CLI process from start to exit). Call it only once the
    /// turn lock is held, and close it before the lock is released.
    /// </summary>
    /// <param name="command">
    /// A raw command (<c>/run</c>): always <see cref="TurnOrigin.Unknown"/>, whatever is pending.
    /// </param>
    public LedgerInterval OpenTurn(bool command = false)
    {
        var origin = command ? TurnOrigin.Unknown : _pending.Value ?? TurnOrigin.Unknown;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var interval = new LedgerInterval(this, origin, now, lockHeld: true, owner: null);
            _intervals.Add(interval);
            _openLockHeld++;
            SweepLocked(now);
            return interval;
        }
    }

    /// <summary>Untracked-activity bookkeeping for one provider process and its stdout reader.</summary>
    public ProviderActivity TrackProvider() => new(this);

    /// <summary>
    /// Opens an <see cref="TurnOrigin.Unknown"/> interval for provider work that can outlive the
    /// turn lock: a shell command that streams after its request was accepted, a turn that was
    /// cancelled without a confirmed end. No lock, silence, timeout or other turn's terminal event
    /// closes it — only <see cref="LedgerInterval.Close"/>, called by the executor once it has seen
    /// this work's own terminal event, or <paramref name="owner"/>'s confirmed process end.
    /// </summary>
    /// <param name="owner">The provider process the work runs in. Null: only an explicit close ends it.</param>
    public LedgerInterval OpenUntilEnded(ProviderActivity? owner)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var interval = new LedgerInterval(this, TurnOrigin.Unknown, now, lockHeld: false, owner);

            // A process confirmed dead runs nothing.
            if (owner is { Ended: true })
                interval.End = now;
            else
                owner?.Held.Add(interval);

            _intervals.Add(interval);
            SweepLocked(now);
            return interval;
        }
    }

    /// <summary>
    /// Decides a tool send stamped <paramref name="requestedAt"/> by the publisher's clock, as of now.
    /// An interval still open counts as covering up to now. The caller waits until the agent clock
    /// has passed the end of the window; asked earlier, the answer is <see cref="ToolSendAttribution.Unattributed"/>.
    /// </summary>
    public ToolSendAttribution Attribute(DateTimeOffset requestedAt)
    {
        var from = requestedAt - ClockSkew;
        var to = requestedAt + ClockSkew;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            SweepLocked(now);

            // Not yet observed to its end, or older than what is still remembered: nothing about
            // the window can be proven.
            var horizon = Max(_startedAt, now - Retention);
            if (now < to || from < horizon)
                return ToolSendAttribution.Unattributed;

            // Any non-human interval that touches the window, at any point, excludes the send.
            foreach (var interval in _intervals)
            {
                if (interval.Origin != TurnOrigin.Human
                    && interval.Start <= to && (interval.End ?? now) >= from)
                {
                    return ToolSendAttribution.NonHuman;
                }
            }

            // Every instant must be covered by the union of human intervals — a gap between two
            // human turns, or the time before or after one, is not.
            var covered = from;
            foreach (var interval in _intervals
                         .Where(i => i.Origin == TurnOrigin.Human && i.Start <= to && (i.End ?? now) >= from)
                         .OrderBy(i => i.Start))
            {
                if (interval.Start > covered)
                    return ToolSendAttribution.Unattributed;

                covered = Max(covered, interval.End ?? now);
                if (covered >= to)
                    return ToolSendAttribution.Human;
            }

            return covered >= to ? ToolSendAttribution.Human : ToolSendAttribution.Unattributed;
        }
    }

    /// <summary>Evicts expired intervals and reports stuck unknown ones. Called on a timer by the consumer.</summary>
    public void Sweep()
    {
        lock (_gate) SweepLocked(_time.GetUtcNow());
    }

    /// <summary>A copy of every retained interval, for tests.</summary>
    internal IReadOnlyList<(TurnOrigin Origin, DateTimeOffset Start, DateTimeOffset? End, bool LockHeld)> SnapshotForTests()
    {
        lock (_gate)
            return _intervals.Select(i => (i.Origin, i.Start, i.End, i.LockHeld)).ToList();
    }

    internal TurnOrigin? PendingForTests => _pending.Value;

    private void Close(LedgerInterval interval)
    {
        lock (_gate) CloseLocked(interval, _time.GetUtcNow());
    }

    private void CloseLocked(LedgerInterval interval, DateTimeOffset now)
    {
        if (interval.End is not null) return;

        interval.End = now;
        if (interval.LockHeld) _openLockHeld--;
        if (interval.Owner is { } owner)
        {
            if (ReferenceEquals(owner.Open, interval)) owner.Open = null;
            owner.Held.Remove(interval);
        }
        SweepLocked(now);
    }

    private void OnTurnContent(ProviderActivity activity)
    {
        lock (_gate)
        {
            // A process confirmed dead cannot start anything; a reader still draining its pipe must
            // not open an interval nothing would ever close.
            if (activity.Ended || _openLockHeld > 0) return;
            if (_intervals.Any(i => !i.LockHeld && i.End is null)) return;

            var now = _time.GetUtcNow();
            var interval = new LedgerInterval(this, TurnOrigin.Unknown, now, lockHeld: false, owner: activity);
            activity.Open = interval;
            _intervals.Add(interval);
            SweepLocked(now);
        }
    }

    private void OnTurnEnded(ProviderActivity activity)
    {
        lock (_gate)
        {
            if (activity.Open is { } open) CloseLocked(open, _time.GetUtcNow());
        }
    }

    private void OnProcessEnded(ProviderActivity activity)
    {
        lock (_gate)
        {
            activity.Ended = true;
            var now = _time.GetUtcNow();
            if (activity.Open is { } open) CloseLocked(open, now);
            foreach (var held in activity.Held.ToArray()) CloseLocked(held, now);
        }
    }

    private void SweepLocked(DateTimeOffset now)
    {
        _intervals.RemoveAll(i => i.End is { } end && end < now - Retention);

        foreach (var interval in _intervals)
        {
            if (interval.Origin != TurnOrigin.Unknown || interval.End is not null || interval.StuckReported) continue;
            if (now - interval.Start <= StuckAfter) continue;

            interval.StuckReported = true;
            _logger.LogWarning(
                "journal_ledger_unknown_stuck: an unknown provider interval has been open for more than {Minutes} minutes; "
                + "human tool sends are excluded until the provider turn ends or the process exits",
                (int)StuckAfter.TotalMinutes);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private sealed class PendingScope(TurnOriginLedger ledger, TurnOrigin? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ledger._pending.Value = previous;
        }
    }

    /// <summary>One interval. <see cref="Close"/> is idempotent.</summary>
    public sealed class LedgerInterval : IDisposable
    {
        private readonly TurnOriginLedger _ledger;

        internal LedgerInterval(TurnOriginLedger ledger, TurnOrigin origin, DateTimeOffset start, bool lockHeld, ProviderActivity? owner)
        {
            _ledger = ledger;
            Origin = origin;
            Start = start;
            LockHeld = lockHeld;
            Owner = owner;
        }

        internal TurnOrigin Origin { get; }
        internal DateTimeOffset Start { get; }
        internal DateTimeOffset? End { get; set; }
        internal bool LockHeld { get; }
        internal ProviderActivity? Owner { get; }
        internal bool StuckReported { get; set; }

        public void Close() => _ledger.Close(this);

        public void Dispose() => Close();
    }

    /// <summary>
    /// What one provider process's stdout reader reports. Created when the process starts; the
    /// executor calls <see cref="ProcessEnded"/> on stdout EOF or once a kill has completed.
    /// </summary>
    public sealed class ProviderActivity
    {
        private readonly TurnOriginLedger _ledger;

        internal ProviderActivity(TurnOriginLedger ledger) => _ledger = ledger;

        internal LedgerInterval? Open { get; set; }
        internal bool Ended { get; set; }

        /// <summary>Intervals from <see cref="OpenUntilEnded"/>: a terminal event does not close them.</summary>
        internal List<LedgerInterval> Held { get; } = [];

        /// <summary>
        /// A turn-content event was read. Opens an unknown interval when no lock-held and no
        /// untracked interval is open.
        /// </summary>
        public void TurnContent() => _ledger.OnTurnContent(this);

        /// <summary>
    /// A terminal event was read: closes the untracked interval the reader opened, if any — never
    /// one from <see cref="OpenUntilEnded"/>.
    /// </summary>
        public void TurnEnded() => _ledger.OnTurnEnded(this);

        /// <summary>
        /// Confirmed termination only — stdout EOF, or a kill after <c>WaitForExitAsync</c>
        /// returned. Closes the interval and ignores anything the reader reports afterwards.
        /// </summary>
        public void ProcessEnded() => _ledger.OnProcessEnded(this);
    }
}
