using Fleet.Agent.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Services;

/// <summary>The origin a <see cref="TurnOriginLedger"/> interval carries (#394).</summary>
public enum TurnOrigin { Human, Relay, Bridge, Unknown }

/// <summary>What the ledger can prove about a tool send's clock-uncertainty window.</summary>
public enum ToolSendAttribution
{
    /// <summary>Captured: only human turns touch the window (#439 D1).</summary>
    Human,

    /// <summary>Captured: a relay turn touches the window, and nothing but human and relay turns does (#439 D1).</summary>
    Relay,

    /// <summary>
    /// Not captured: no human or relay turn touches the window, part of it is uncovered next to a
    /// turn that ended abnormally, or the window starts before what the ledger still remembers.
    /// </summary>
    Unattributed,

    /// <summary>Not captured: a bridge or unknown interval touches the window.</summary>
    ExcludedOrigin,
}

/// <summary>Which step of <see cref="TurnOriginLedger.Attribute"/> decided a tool send (#439).</summary>
public enum ToolSendReason
{
    /// <summary>Human and relay intervals cover every instant of the window.</summary>
    Covered,

    /// <summary>Part of the window is interval-free, and every human or relay turn touching it is open or closed normally.</summary>
    IdleEdge,

    /// <summary>A bridge or unknown interval touches the window.</summary>
    BridgeOrUnknown,

    /// <summary>No interval of any origin touches the window.</summary>
    NoInterval,

    /// <summary>Part of the window is uncovered and a human or relay turn touching it closed abnormally.</summary>
    AbnormalClose,

    /// <summary>The window is not yet observed to its end, or starts before what the ledger still remembers.</summary>
    Horizon,
}

/// <summary>
/// One tool-send decision (#439). <see cref="RelayTouched"/> reports whether any relay interval
/// touches the window, whatever the result; it is false on the <see cref="ToolSendReason.Horizon"/> path.
/// </summary>
public readonly record struct ToolSendDecision(ToolSendAttribution Attribution, ToolSendReason Reason, bool RelayTouched);

/// <summary>
/// Every period in which the provider may be acting, tagged with whose turn it is (#394). A tool
/// send made through a Telegram MCP tool is journaled only when this ledger proves it was made in a
/// human or workflow (relay) turn (#439).
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
/// <item><see cref="OpenCommand"/> and <see cref="OpenUntilTurnEnds"/>, for work that can outlive
/// the turn lock (a raw command, cancelled or not; an interrupted turn whose drain timed out).
/// <see cref="TurnOrigin.Unknown"/>; it ends only on that work's own terminal event or a confirmed
/// process exit, whoever holds the lock meanwhile.</item>
/// </list>
/// <para>
/// The rule (<see cref="Attribute"/>, #439) decides the window <c>W = [requestedAt − 2 s, requestedAt + 2 s]</c>.
/// A send is captured when (a) no bridge or unknown interval touches <c>W</c>, (b) at least one human
/// or relay interval does, and (c) either human and relay intervals cover every instant of <c>W</c>,
/// or every human or relay interval touching <c>W</c> is still open or <b>closed normally</b>. The
/// result is <see cref="ToolSendAttribution.Relay"/> if any relay interval touches <c>W</c>, else
/// <see cref="ToolSendAttribution.Human"/>. There is no precedence between intervals and no
/// exact-containment step. Closed intervals are kept for 10 minutes; open ones are never evicted.
/// </para>
/// <para>
/// <b>Closed normally</b> (<see cref="LedgerInterval.CloseNormally"/>) means the executor read the
/// provider's own successful terminal for that turn: Claude's current-turn <c>result</c> without
/// error, Codex's <c>turn/completed</c> with <c>status: completed</c>, a Gemini CLI that exited by
/// itself with code 0. Cancellation, timeout, kill, process death, abandon, an error terminal and every
/// other path close abnormally. So a Claude turn that hits max-turns or ends on an error
/// <c>result</c> closes abnormally, and a send whose window reaches past its edge is
/// <see cref="ToolSendAttribution.Unattributed"/>. That is deliberate, not a bug to fix: the tail of
/// a turn that did not finish cleanly is not proven idle.
/// </para>
/// <para>
/// Still excluded: any bridge or unknown contact (<c>/run</c>, warmup, injected or background provider
/// turns, a Codex interrupted turn that outlived its drain, a Gemini call cancelled without a
/// confirmed exit, handed over by <see cref="ContinueAsUnknown"/>); a window before the horizon; a
/// receipt redelivered after a restart; an uncovered part of <c>W</c> next to an abnormal close.
/// </para>
/// <para>
/// Why an interval-free part of <c>W</c> can be accepted. Every provider action that can issue an
/// MCP call leaves a record first: under the turn lock it is inside a lock-held interval; outside it
/// the stdout reader opens an unknown interval on the first turn-content event, which the CLI emits
/// before it dispatches the tool call; a Gemini call is covered from process start to confirmed exit.
/// The decision waits until <c>requestedAt + 2.25 s</c>, so a reader-opened interval is visible and
/// touches <c>W</c> as long as <b>clock skew plus reader lag stay within 2 s together</b>. Before
/// #439 a late reader only failed closed; the idle-edge path makes that combined budget a capture
/// condition. So an interval-free part of <c>W</c> means no tracked provider activity there, and the
/// send belongs to a human or relay turn touching <c>W</c>. If one of those ended abnormally the
/// uncovered part may be its cancelled tail, so it is refused. Residual, the same class as before: a
/// process the provider detached from its own stdout (a background shell child calling the MCP
/// endpoint) is never seen.
/// </para>
/// <para>
/// A human-turn send whose window merely touches an adjacent relay interval is labelled relay.
/// Capture is identical; <see cref="ToolSendDecision.RelayTouched"/> and the decision log make it visible.
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
    /// Opens a held interval (<see cref="OpenHeld"/>) for a raw command written to the provider
    /// (<c>/run</c>). The executor closes it when it reads the command's result; the stdout reader
    /// also retires it on the command's own terminal event, so a command whose caller
    /// stopped reading does not hold the exclusion until the process exits: Codex through
    /// <see cref="ProviderActivity.ShellCommandTurn"/>, Claude through
    /// <see cref="ProviderActivity.CommandResult"/>. Another turn's terminal event never does.
    /// </summary>
    public LedgerInterval OpenCommand(ProviderActivity? owner) =>
        OpenHeld(owner, bind: activity => activity.Commands.Add);

    /// <summary>
    /// Opens a held interval (<see cref="OpenHeld"/>) for a Codex turn whose id is known — an
    /// interrupted turn whose drain timed out. The reader retires it on that turn's
    /// <c>turn/completed</c>, or at once if it already read one.
    /// </summary>
    public LedgerInterval OpenUntilTurnEnds(ProviderActivity? owner, string turnId) =>
        OpenHeld(owner, bind: activity => interval =>
        {
            if (activity.CompletedTurns.Contains(turnId))
            {
                CloseLocked(interval, _time.GetUtcNow());
                return;
            }

            activity.ByTurn.TryAdd(turnId, []);
            activity.ByTurn[turnId].Add(interval);
        });

    /// <summary>
    /// Opens an <see cref="TurnOrigin.Unknown"/> interval for provider work that can outlive the
    /// turn lock: a shell command that streams after its request was accepted, a turn that was
    /// cancelled without a confirmed end. No lock, silence, timeout or other turn's terminal event
    /// closes it — only <see cref="LedgerInterval.Close"/> by the executor, the reader's
    /// correlation of this work's own terminal event (<paramref name="bind"/>), or
    /// <paramref name="owner"/>'s confirmed process end.
    /// </summary>
    /// <param name="owner">The provider process the work runs in. Null: only an explicit close ends it.</param>
    /// <param name="bind">How the reader correlates the interval with the work's own terminal event.</param>
    private LedgerInterval OpenHeld(ProviderActivity? owner, Func<ProviderActivity, Action<LedgerInterval>> bind)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var interval = new LedgerInterval(this, TurnOrigin.Unknown, now, lockHeld: false, owner);
            _intervals.Add(interval);

            // A process confirmed dead runs nothing.
            if (owner is { Ended: true })
            {
                interval.End = now;
            }
            else if (owner is not null)
            {
                owner.Held.Add(interval);
                bind(owner)(interval);
            }

            SweepLocked(now);
            return interval;
        }
    }

    /// <summary>
    /// Decides a tool send stamped <paramref name="requestedAt"/> by the publisher's clock, as of now.
    /// An interval still open counts as covering up to now. The caller waits until the agent clock
    /// has passed the end of the window; asked earlier, the answer is
    /// <see cref="ToolSendAttribution.Unattributed"/> / <see cref="ToolSendReason.Horizon"/>.
    /// </summary>
    public ToolSendDecision Attribute(DateTimeOffset requestedAt)
    {
        var from = requestedAt - ClockSkew;
        var to = requestedAt + ClockSkew;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            SweepLocked(now);

            // 1. Not yet observed to its end, or older than what is still remembered: nothing about
            //    the window can be proven.
            var horizon = Max(_startedAt, now - Retention);
            if (now < to || from < horizon)
                return new(ToolSendAttribution.Unattributed, ToolSendReason.Horizon, RelayTouched: false);

            var touching = _intervals.Where(i => i.Start <= to && (i.End ?? now) >= from).ToList();
            var relayTouched = touching.Any(i => i.Origin == TurnOrigin.Relay);

            // 2. Any bridge or unknown interval that touches the window, at any point, excludes the send.
            if (touching.Any(i => i.Origin is TurnOrigin.Bridge or TurnOrigin.Unknown))
                return new(ToolSendAttribution.ExcludedOrigin, ToolSendReason.BridgeOrUnknown, relayTouched);

            // 3. Nobody's turn touches the window.
            var turns = touching.Where(i => i.Origin is TurnOrigin.Human or TurnOrigin.Relay).OrderBy(i => i.Start).ToList();
            if (turns.Count == 0)
                return new(ToolSendAttribution.Unattributed, ToolSendReason.NoInterval, relayTouched);

            var captured = relayTouched ? ToolSendAttribution.Relay : ToolSendAttribution.Human;

            // 4. Every instant covered by the union of human and relay intervals.
            var covered = from;
            var gap = false;
            foreach (var interval in turns)
            {
                if (interval.Start > covered)
                {
                    gap = true;
                    break;
                }

                covered = Max(covered, interval.End ?? now);
                if (covered >= to) break;
            }

            if (!gap && covered >= to)
                return new(captured, ToolSendReason.Covered, relayTouched);

            // 5. An interval-free part is idle only when no turn touching the window ended abnormally:
            //    the uncovered part could otherwise be a cancelled turn's tail.
            if (turns.All(i => i.End is null || i.ClosedNormally))
                return new(captured, ToolSendReason.IdleEdge, relayTouched);

            // 6.
            return new(ToolSendAttribution.Unattributed, ToolSendReason.AbnormalClose, relayTouched);
        }
    }

    /// <summary>
    /// Hands <paramref name="interval"/> over to an <see cref="TurnOrigin.Unknown"/> interval with no
    /// gap (#439): under one lock acquisition it closes <paramref name="interval"/> abnormally at now
    /// and opens an unknown interval starting at the same instant, with no lock and no owner, which
    /// only <see cref="LedgerInterval.Close"/> ends. For work that may still be running after its
    /// task gave up on it (a Gemini CLI whose kill is not confirmed yet), so its sends are not
    /// attributed to the task. On an already-closed interval it opens nothing and returns
    /// <paramref name="interval"/>. Never throws.
    /// </summary>
    public LedgerInterval ContinueAsUnknown(LedgerInterval interval)
    {
        lock (_gate)
        {
            if (interval.End is not null) return interval;

            var now = _time.GetUtcNow();
            CloseLocked(interval, now);
            var unknown = new LedgerInterval(this, TurnOrigin.Unknown, now, lockHeld: false, owner: null);
            _intervals.Add(unknown);
            SweepLocked(now);
            return unknown;
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

    /// <summary>Every retained interval with how it closed, for tests.</summary>
    internal IReadOnlyList<(TurnOrigin Origin, DateTimeOffset? End, bool ClosedNormally)> ClosuresForTests()
    {
        lock (_gate)
            return _intervals.Select(i => (i.Origin, i.End, i.ClosedNormally)).ToList();
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
            owner.Commands.Remove(interval);
            foreach (var bound in owner.ByTurn.Values) bound.Remove(interval);
        }
        SweepLocked(now);
    }

    private void OnTurnContent(ProviderActivity activity)
    {
        lock (_gate)
        {
            // A process confirmed dead cannot start anything; a reader still draining its pipe must
            // not open an interval nothing would ever close.
            // Only the reader's own untracked interval suppresses a new one. A command's interval
            // may end on the command's terminal while this other turn is still running.
            if (activity.Ended || _openLockHeld > 0 || activity.Open is not null) return;

            var now = _time.GetUtcNow();
            var interval = new LedgerInterval(this, TurnOrigin.Unknown, now, lockHeld: false, owner: activity);
            activity.Open = interval;
            _intervals.Add(interval);
            SweepLocked(now);
        }
    }

    private void OnTurnEnded(ProviderActivity activity, string? turnId)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (activity.Open is { } open) CloseLocked(open, now);
            if (turnId is null) return;

            activity.RememberCompleted(turnId);
            if (activity.ByTurn.Remove(turnId, out var bound))
                foreach (var interval in bound.ToArray()) CloseLocked(interval, now);
        }
    }

    private void OnShellCommandTurn(ProviderActivity activity, string turnId)
    {
        lock (_gate)
        {
            // Several shell items in one turn bind once; the oldest waiting command takes the turn.
            if (activity.Ended || activity.ByTurn.ContainsKey(turnId) || activity.Commands.Count == 0) return;

            var command = activity.Commands[0];
            activity.Commands.RemoveAt(0);
            activity.ByTurn[turnId] = [command];
        }
    }

    private void OnCommandResult(ProviderActivity activity)
    {
        lock (_gate)
        {
            if (activity.Commands.Count > 0) CloseLocked(activity.Commands[0], _time.GetUtcNow());
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

    /// <summary>
    /// One interval. <see cref="Close"/> and <see cref="Dispose"/> close it abnormally;
    /// <see cref="CloseNormally"/> closes it normally (#439). All three are idempotent, and the first
    /// close wins.
    /// </summary>
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

        /// <summary>Closed by <see cref="CloseNormally"/>: the provider's own successful terminal for this turn was read.</summary>
        internal bool ClosedNormally { get; private set; }

        public void Close() => _ledger.Close(this);

        /// <summary>
        /// Call only on the provider's own successful terminal for this turn (#439 D2) — never on a
        /// cancellation, timeout, kill, process death, abandon, error or <c>/run</c>. Marks and closes
        /// the interval if it is still open; a no-op on a closed one.
        /// </summary>
        public void CloseNormally()
        {
            lock (_ledger._gate)
            {
                if (End is not null) return;

                ClosedNormally = true;
                _ledger.CloseLocked(this, _ledger._time.GetUtcNow());
            }
        }

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

        /// <summary>Intervals from <see cref="OpenHeld"/> and its variants: open until retired or the process ends.</summary>
        internal List<LedgerInterval> Held { get; } = [];

        /// <summary>Command intervals waiting for their own terminal event, oldest first.</summary>
        internal List<LedgerInterval> Commands { get; } = [];

        /// <summary>Held intervals bound to a Codex turn id, retired by that turn's completion.</summary>
        internal Dictionary<string, List<LedgerInterval>> ByTurn { get; } = new(StringComparer.Ordinal);

        /// <summary>Recently completed turn ids, so a turn bound after its completion was read retires at once.</summary>
        internal HashSet<string> CompletedTurns { get; } = new(StringComparer.Ordinal);

        private readonly Queue<string> _completedOrder = new();
        private const int CompletedTurnMemory = 256;

        internal void RememberCompleted(string turnId)
        {
            if (!CompletedTurns.Add(turnId)) return;
            _completedOrder.Enqueue(turnId);
            if (_completedOrder.Count > CompletedTurnMemory) CompletedTurns.Remove(_completedOrder.Dequeue());
        }

        /// <summary>
        /// A turn-content event was read. Opens an unknown interval when no lock-held and no
        /// untracked interval is open.
        /// </summary>
        public void TurnContent() => _ledger.OnTurnContent(this);

        /// <summary>
    /// A terminal event was read: closes the untracked interval the reader opened, if any — never
    /// one from <see cref="OpenHeld"/>.
    /// </summary>
        /// <param name="turnId">The turn that ended, when the provider names it (Codex).</param>
        public void TurnEnded(string? turnId = null) => _ledger.OnTurnEnded(this, turnId);

        /// <summary>
        /// Codex: an item of turn <paramref name="turnId"/> is a user shell command
        /// (<c>commandExecution</c> with <c>source: userShell</c>), which only <c>/run</c> starts.
        /// The oldest waiting command interval is bound to that turn and retires with it.
        /// </summary>
        public void ShellCommandTurn(string turnId) => _ledger.OnShellCommandTurn(this, turnId);

        /// <summary>
        /// Claude: a result for a message written on stdin (human or unstamped origin). Claude
        /// answers stdin messages one at a time and in order, so it belongs to the oldest waiting
        /// command — the one written before any later message.
        /// </summary>
        public void CommandResult() => _ledger.OnCommandResult(this);

        /// <summary>
        /// Confirmed termination only — stdout EOF, or a kill after <c>WaitForExitAsync</c>
        /// returned. Closes the interval and ignores anything the reader reports afterwards.
        /// </summary>
        public void ProcessEnded() => _ledger.OnProcessEnded(this);
    }
}
