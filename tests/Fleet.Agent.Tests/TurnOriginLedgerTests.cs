using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// The tool-send turn ledger, fail closed (#394 AC7). Every case runs the real
/// <see cref="TaskManager"/> against an executor with a real <see cref="SemaphoreSlim"/> turn lock,
/// on a clock the test moves, and ends with the receipt going through the consumer into the spool.
/// Times are seconds after <see cref="ReceiptRig.Base"/>.
/// </summary>
public sealed class TurnOriginLedgerTests : IDisposable
{
    private const long HumanChat = ReceiptRig.HumanDm;
    private const long RelayChat = -1002000000077;

    private readonly ReceiptRig _rig = new();
    private readonly LedgerFakeExecutor _executor;

    // Two task managers share the executor, so a task can genuinely wait on its turn lock: one task
    // manager runs one task at a time and would queue the second itself.
    private readonly TaskManager _humans;
    private readonly TaskManager _relays;

    public TurnOriginLedgerTests()
    {
        _executor = new LedgerFakeExecutor(_rig.Ledger);
        _humans = NewTaskManager(_executor, _rig.Ledger);
        _relays = NewTaskManager(_executor, _rig.Ledger);
    }

    public void Dispose() => _rig.Dispose();

    // ── lock-held intervals ──────────────────────────────────────────────────

    [Fact]
    public async Task Pure_human_window_is_decided_after_the_window_and_captured_and_the_waiting_relay_has_no_interval()
    {
        var human = await HumanAcquires("human", at: 90);
        var relay = await RelayWaits("relay", at: 95);

        await DeliverAsync(requestedAt: 100, arrivesAt: 100.1);

        _rig.At(102.2);
        Assert.Equal(0, await _rig.DecideAsync());
        Assert.Empty(_rig.Acked);

        _rig.At(102.25);
        Assert.Equal(1, await _rig.DecideAsync());
        Assert.Single(_rig.Acked);
        Assert.Equal(1, _rig.Counters.Get("tool_send_captured"));
        Assert.Equal(1, _rig.Rows);

        var only = Assert.Single(_rig.Ledger.SnapshotForTests());
        Assert.Equal((TurnOrigin.Human, ReceiptRig.T(90), (DateTimeOffset?)null), (only.Origin, only.Start, only.End));

        await EndsAsync(human, at: 110);
        await relay.Acquired.Task.WaitAsync(Timeout);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Relay && i.Start == ReceiptRig.T(110));
        await EndsAsync(relay, at: 115);

        Assert.Equal(1, _rig.Rows);
    }

    [Fact]
    public async Task Deferral_catches_a_relay_that_acquires_after_the_receipt_arrived()
    {
        var human = await HumanAcquires("human", at: 90);
        await DeliverAsync(requestedAt: 100, arrivesAt: 100.1);
        await EndsAsync(human, at: 101.0);
        var relay = await RelayAcquires("relay", at: 101.5);

        await DecideAsync(at: 102.25);

        Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
        await EndsAsync(relay, at: 110);
    }

    [Fact]
    public async Task Reverse_a_relay_holds_the_lock_while_the_human_waits()
    {
        var relay = await RelayAcquires("relay", at: 90);
        var human = await HumanWaits("human", at: 95);

        await DeliverAsync(requestedAt: 100, arrivesAt: 100.1);
        await DecideAsync(at: 102.25);

        Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
        Assert.DoesNotContain(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Human);

        await EndsAsync(relay, at: 110);
        await human.Acquired.Task.WaitAsync(Timeout);
        await EndsAsync(human, at: 120);
    }

    [Fact]
    public async Task Skewed_handoff_excludes_the_relay_send_and_a_human_send_with_the_same_stamp()
    {
        var human = await HumanAcquires("human", at: 90);
        await EndsAsync(human, at: 100.0);
        var relay = await RelayAcquires("relay", at: 100.1);

        // The relay's real send is at 100.5, stamped by a publisher running 1 s behind.
        await DeliverAsync(requestedAt: 99.5, arrivesAt: 100.6, messageId: 1);
        await DeliverAsync(requestedAt: 99.5, arrivesAt: 100.6, messageId: 2);
        await DecideAsync(at: 101.75);

        Assert.Equal(2, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
        await EndsAsync(relay, at: 110);
    }

    [Fact]
    public async Task A_run_command_near_a_human_turn_is_unknown_and_excludes_both_sides()
    {
        var human = await HumanAcquires("human", at: 90);
        await DeliverAsync(requestedAt: 99.8, arrivesAt: 99.9);
        await EndsAsync(human, at: 100.0);

        _rig.At(100.2);
        var run = _executor.Turn("/run status");
        var command = Task.Run(async () =>
        {
            await foreach (var _ in _executor.SendCommandAsync("/run status")) { }
        });
        await run.Acquired.Task.WaitAsync(Timeout);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && i.LockHeld && i.End is null);

        await DeliverAsync(requestedAt: 101.0, arrivesAt: 101.1);
        await EndsAsync(run, at: 103);
        await command.WaitAsync(Timeout);
        await DecideAsync(at: 103.25);

        Assert.Equal(2, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task An_injected_messages_own_turn_is_read_under_the_tasks_origin()
    {
        _executor.AcceptInjections = true;
        var human = await StartAsync(_humans, HumanChat, TaskSource.UserMessage, "human", at: 90, acquire: true, session: true);
        _rig.At(95);
        Assert.Equal(TaskDispatchOutcome.Injected, await _humans.StartTask(HumanChat, "more", "more", isSessionTask: true));

        // The turn's answer, then ReadInjectedTurnAnswersAsync takes the lock for the injected turn.
        var answers = _executor.Turn(LedgerFakeExecutor.InjectedAnswers);
        await EndsAsync(human, at: 100);
        await answers.Acquired.Task.WaitAsync(Timeout);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Human && i.Start == ReceiptRig.T(100) && i.End is null);

        await DeliverAsync(requestedAt: 102, arrivesAt: 102.1);
        await DecideAsync(at: 104.25);
        Assert.Equal(1, _rig.Rows);

        await EndsAsync(answers, at: 106);
    }

    [Fact]
    public async Task Warmup_with_no_pending_origin_is_unknown()
    {
        _rig.At(100);
        var ping = _executor.Turn("ping");
        var warmup = new WarmupService(_executor, 30, NullLogger<WarmupService>.Instance, startupDelay: TimeSpan.Zero);
        await warmup.StartAsync(CancellationToken.None);
        await ping.Acquired.Task.WaitAsync(Timeout);

        var interval = Assert.Single(_rig.Ledger.SnapshotForTests());
        Assert.Equal(TurnOrigin.Unknown, interval.Origin);

        await DeliverAsync(requestedAt: 101, arrivesAt: 101.1);
        await EndsAsync(ping, at: 102);
        await DecideAsync(at: 103.25);

        Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
        await warmup.StopAsync(CancellationToken.None);
    }

    // ── coverage ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Boundary_gap_at_the_end_of_a_turn_is_unattributed()
    {
        var human = await HumanAcquires("human", at: 90);
        await DeliverAsync(requestedAt: 99.5, arrivesAt: 99.6);
        await EndsAsync(human, at: 100.0);
        await DecideAsync(at: 101.75);

        Assert.Equal(1, _rig.Counters.Get("tool_send_unattributed"));
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task A_gap_between_two_human_turns_is_unattributed_and_the_middle_of_a_turn_is_captured()
    {
        var first = await HumanAcquires("human-a", at: 90);
        await EndsAsync(first, at: 100.0);
        await DeliverAsync(requestedAt: 100.2, arrivesAt: 100.3);
        var second = await HumanAcquires("human-b", at: 100.5);

        await DecideAsync(at: 102.45);
        Assert.Equal(1, _rig.Counters.Get("tool_send_unattributed"));
        Assert.Equal(0, _rig.Rows);

        await DeliverAsync(requestedAt: 105, arrivesAt: 105.1);
        await DecideAsync(at: 107.25);
        Assert.Equal(1, _rig.Rows);

        await EndsAsync(second, at: 110);
    }

    [Fact]
    public async Task No_interval_is_unattributed()
    {
        await DeliverAsync(requestedAt: 100, arrivesAt: 100.1);
        await DecideAsync(at: 102.25);

        Assert.Equal(1, _rig.Counters.Get("tool_send_unattributed"));
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task A_stamp_ahead_of_its_arrival_is_clock_skew_even_inside_a_human_turn()
    {
        var human = await HumanAcquires("human", at: 90);
        await DeliverAsync(requestedAt: 100.5, arrivesAt: 100.1);
        await DecideAsync(at: 105);

        Assert.Equal(1, _rig.Counters.Get("receipt_clock_skew"));
        Assert.Single(_rig.Acked);
        Assert.Equal(0, _rig.Rows);
        await EndsAsync(human, at: 110);
    }

    [Fact]
    public async Task A_window_before_the_retention_horizon_is_excluded_even_inside_an_open_human_turn()
    {
        var human = await HumanAcquires("human", at: 90);

        // A backlog delivers a receipt stamped 150 at 800: its window starts before now − 10 min.
        await DeliverAsync(requestedAt: 150, arrivesAt: 800);
        await DecideAsync(at: 800);

        Assert.Equal(1, _rig.Counters.Get("tool_send_unattributed"));
        Assert.Equal(0, _rig.Rows);
        await EndsAsync(human, at: 900);
    }

    [Fact]
    public async Task After_a_restart_a_redelivered_receipt_meets_an_empty_ledger_and_is_excluded()
    {
        // The agent restarted at 1000; the broker redelivers a receipt stamped 990.
        using var rig = new ReceiptRig(startAt: 1000);
        rig.At(1000.2);
        rig.OpenTurn(OutboundOrigin.Human);
        rig.At(1000.5);
        await rig.DeliverAsync(ReceiptRig.Receipt(990));
        rig.At(1000.5);
        await rig.DecideAsync();

        Assert.Equal(1, rig.Counters.Get("tool_send_unattributed"));
        Assert.Equal(0, rig.Rows);
    }

    [Fact]
    public async Task Closed_intervals_are_evicted_after_ten_minutes_and_open_ones_never()
    {
        _rig.At(10);
        var open = _rig.OpenTurn(OutboundOrigin.Relay);
        _rig.At(90);
        var closed = _rig.OpenTurn(OutboundOrigin.Human);
        _rig.At(100);
        closed.Close();

        _rig.At(700);
        _rig.Ledger.Sweep();
        Assert.Equal(2, _rig.Ledger.SnapshotForTests().Count);

        _rig.At(700.1);
        _rig.Ledger.Sweep();
        var left = Assert.Single(_rig.Ledger.SnapshotForTests());
        Assert.Equal(TurnOrigin.Relay, left.Origin);
        open.Close();
    }

    // ── untracked provider turns ─────────────────────────────────────────────

    [Fact]
    public async Task An_untracked_provider_turn_after_a_human_turn_excludes_both_sides()
    {
        var human = await HumanAcquires("human", at: 90);
        await DeliverAsync(requestedAt: 99.0, arrivesAt: 99.1);
        await EndsAsync(human, at: 100.0);

        _rig.At(100.3);
        _executor.Stdout("assistant");
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && !i.LockHeld && i.End is null);

        await DeliverAsync(requestedAt: 100.4, arrivesAt: 100.5);
        await DecideAsync(at: 102.65);

        Assert.Equal(2, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
    }

    [Fact]
    public async Task A_silent_unknown_turn_is_never_closed_by_time_or_by_a_later_lock()
    {
        _rig.At(100.3);
        _executor.Stdout("assistant");

        // 45 s of silence, then a human turn.
        var human = await HumanAcquires("human-a", at: 150);
        await DeliverAsync(requestedAt: 160, arrivesAt: 160.1);
        await DecideAsync(at: 162.25);
        await EndsAsync(human, at: 170);

        // Five more minutes of silence, then another.
        var later = await HumanAcquires("human-b", at: 400);
        await DeliverAsync(requestedAt: 410, arrivesAt: 410.1);
        await DecideAsync(at: 412.25);
        await EndsAsync(later, at: 420);

        Assert.Equal(2, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && i.End is null);
    }

    [Fact]
    public async Task A_human_lock_before_the_unknown_turns_terminal_is_excluded_until_the_terminal()
    {
        _rig.At(100.3);
        _executor.Stdout("system", "init");
        var human = await HumanAcquires("human", at: 101);

        await DeliverAsync(requestedAt: 105, arrivesAt: 105.1);
        await DecideAsync(at: 107.25);
        Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);

        _rig.At(108);
        _executor.Stdout("result");

        // Window 113–117: all human, no unknown.
        await DeliverAsync(requestedAt: 115, arrivesAt: 115.1);
        await DecideAsync(at: 117.25);
        Assert.Equal(1, _rig.Rows);

        await EndsAsync(human, at: 120);
    }

    [Fact]
    public async Task Stdout_eof_closes_the_unknown_turn()
    {
        _rig.At(110);
        _executor.Stdout("assistant");
        _rig.At(120);
        _executor.StdoutEof();

        var human = await HumanAcquires("human", at: 125);
        await DeliverAsync(requestedAt: 132, arrivesAt: 132.1);
        await DecideAsync(at: 134.25);
        await EndsAsync(human, at: 140);

        Assert.Equal(1, _rig.Rows);
    }

    [Fact]
    public async Task A_restart_request_does_not_close_the_unknown_turn_and_the_deferred_restart_does_once_its_kill_completes()
    {
        _rig.At(100.3);
        _executor.Stdout("assistant");
        _rig.At(101);
        _executor.RequestRestart();

        var human = await HumanAcquires("human-a", at: 110);
        await DeliverAsync(requestedAt: 120, arrivesAt: 120.1);
        await DecideAsync(at: 122.25);
        Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);

        // The turn's result at 130; the deferred restart's kill completes at 131.
        var kill = _executor.HoldNextKill();
        await EndsWithoutWaitingAsync(human, at: 130);
        await kill.Reached.Task.WaitAsync(Timeout);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && i.End is null);
        _rig.At(131);
        kill.Complete();
        await human.Released.Task.WaitAsync(Timeout);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && i.End == ReceiptRig.T(131));

        var next = await HumanAcquires("human-b", at: 135);
        await DeliverAsync(requestedAt: 142, arrivesAt: 142.1);
        await DecideAsync(at: 144.25);
        await EndsAsync(next, at: 150);

        Assert.Equal(1, _rig.Rows);
    }

    [Fact]
    public async Task A_try_stop_that_returns_false_leaves_the_unknown_turn_open()
    {
        _rig.At(100.3);
        _executor.Stdout("assistant");
        var human = await HumanAcquires("human", at: 101);

        Assert.False(await _executor.TryStopProcessAsync());

        await DeliverAsync(requestedAt: 110, arrivesAt: 110.1);
        await DecideAsync(at: 112.25);
        Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
        Assert.Equal(0, _rig.Rows);
        Assert.Contains(_rig.Ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && i.End is null);

        await EndsAsync(human, at: 120);
    }

    [Fact]
    public void A_turn_content_event_while_a_lock_is_held_opens_nothing_and_a_dead_process_reports_nothing()
    {
        _rig.At(100);
        var held = _rig.OpenTurn(OutboundOrigin.Human);
        var activity = _rig.Ledger.TrackProvider();
        activity.TurnContent();
        Assert.Single(_rig.Ledger.SnapshotForTests());
        held.Close();

        activity.ProcessEnded();
        activity.TurnContent();
        Assert.Single(_rig.Ledger.SnapshotForTests());
    }

    [Fact]
    public void An_unknown_interval_open_for_ten_minutes_warns_once_and_stays_open()
    {
        var logger = new ListLogger<TurnOriginLedger>();
        var clock = new ManualClock(ReceiptRig.Base);
        var ledger = new TurnOriginLedger(logger, clock);
        ledger.TrackProvider().TurnContent();

        clock.Set(ReceiptRig.T(600));
        ledger.Sweep();
        Assert.Empty(logger.Warnings);

        clock.Set(ReceiptRig.T(601));
        ledger.Sweep();
        ledger.Sweep();
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("journal_ledger_unknown_stuck", warning);
        Assert.Contains(ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && i.End is null);
    }

    [Fact]
    public void A_pending_origin_is_scoped_and_a_command_is_always_unknown()
    {
        var ledger = _rig.Ledger;
        Assert.Null(ledger.PendingForTests);

        using (ledger.Pending(OutboundOrigin.Bridge))
        {
            using (ledger.Pending(OutboundOrigin.Human))
                Assert.Equal(TurnOrigin.Human, ledger.PendingForTests);

            Assert.Equal(TurnOrigin.Bridge, ledger.PendingForTests);
            ledger.OpenTurn(command: true).Close();
        }

        Assert.Null(ledger.PendingForTests);
        Assert.Equal(TurnOrigin.Unknown, Assert.Single(ledger.SnapshotForTests()).Origin);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static TaskManager NewTaskManager(IAgentExecutor executor, TurnOriginLedger ledger) =>
        new(Options.Create(new AgentOptions { Name = "fleet-agent1", Role = "generic-role", WorkDir = Path.GetTempPath() }),
            executor, new SessionManager(), NullLogger<TaskManager>.Instance, ledger: ledger);

    private Task<FakeTurn> HumanAcquires(string name, double at) => StartAsync(_humans, HumanChat, TaskSource.UserMessage, name, at, acquire: true);

    private Task<FakeTurn> HumanWaits(string name, double at) => StartAsync(_humans, HumanChat, TaskSource.UserMessage, name, at, acquire: false);

    private Task<FakeTurn> RelayAcquires(string name, double at) => StartAsync(_relays, RelayChat, TaskSource.Relay, name, at, acquire: true);

    private Task<FakeTurn> RelayWaits(string name, double at) => StartAsync(_relays, RelayChat, TaskSource.Relay, name, at, acquire: false);

    private async Task<FakeTurn> StartAsync(
        TaskManager manager, long chat, TaskSource source, string name, double at, bool acquire, bool session = false)
    {
        // The previous task on this manager must have finished its bookkeeping: a task started
        // while it is still registered is queued, and the queue is drained only as a task ends.
        await Eventually(() => !manager.HasRunningTasks(chat));

        _rig.At(at);
        var turn = _executor.Turn(name);
        await manager.StartTask(chat, name, name, isSessionTask: session, source: source,
            relaySender: source == TaskSource.Relay ? "peer-agent" : null);

        if (acquire)
        {
            await turn.Acquired.Task.WaitAsync(Timeout);
        }
        else
        {
            await turn.Entered.Task.WaitAsync(Timeout);
            Assert.False(turn.Acquired.Task.IsCompleted);
        }

        return turn;
    }

    private async Task EndsAsync(FakeTurn turn, double at)
    {
        await EndsWithoutWaitingAsync(turn, at);
        await turn.Released.Task.WaitAsync(Timeout);
    }

    private Task EndsWithoutWaitingAsync(FakeTurn turn, double at)
    {
        _rig.At(at);
        turn.Finish();
        return Task.CompletedTask;
    }

    private async Task DeliverAsync(double requestedAt, double arrivesAt, long messageId = 0)
    {
        _rig.At(arrivesAt);
        await _rig.DeliverAsync(ReceiptRig.Receipt(requestedAt, messageId));
    }

    private async Task DecideAsync(double at)
    {
        _rig.At(at);
        await _rig.DecideAsync();
    }

    // ── the executors' own hooks ─────────────────────────────────────────────

    /// <summary>
    /// The ledger calls at their real sites in Claude, Codex and Gemini: lock-held intervals, the
    /// stdout readers' untracked intervals and what does and does not close them.
    /// </summary>
    public sealed class ExecutorHooks : IDisposable
    {
        private readonly ReceiptRig _rig = new();

        public void Dispose() => _rig.Dispose();

        private IReadOnlyList<(TurnOrigin Origin, DateTimeOffset Start, DateTimeOffset? End, bool LockHeld)> Intervals =>
            _rig.Ledger.SnapshotForTests();

        // ── Claude ──

        [Fact]
        public async Task Claude_opens_after_the_lock_with_the_pending_origin_and_a_waiting_call_has_none()
        {
            using var standIn = new StandInProcess();
            var claude = Claude();
            var stdin = new SignalingTextWriter();
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetProcessForTests(standIn.Process);
            claude.SetStdinForTests(stdin);
            claude.SetEventChannelForTests(events);

            _rig.At(90);
            var human = Enumerate(OutboundOrigin.Human, () => claude.ExecuteAsync("hello"));
            await stdin.WaitForWriteAsync();
            var open = Assert.Single(Intervals);
            Assert.Equal((TurnOrigin.Human, ReceiptRig.T(90), (DateTimeOffset?)null, true), open);

            _rig.At(95);
            var relay = Enumerate(OutboundOrigin.Relay, () => claude.ExecuteAsync("directive"));
            await Task.Delay(100);
            Assert.Single(Intervals);

            _rig.At(100);
            await events.Writer.WriteAsync(Result());
            await human.WaitAsync(Timeout);
            await stdin.WaitForWriteAsync();
            Assert.Contains(Intervals, i => i is { Origin: TurnOrigin.Human, End: not null } && i.End == ReceiptRig.T(100));
            Assert.Contains(Intervals, i => i.Origin == TurnOrigin.Relay && i.Start == ReceiptRig.T(100) && i.End is null);

            _rig.At(105);
            await events.Writer.WriteAsync(Result());
            await relay.WaitAsync(Timeout);
            Assert.All(Intervals, i => Assert.NotNull(i.End));
        }

        [Fact]
        public async Task Claude_warmup_is_unknown_and_run_is_unknown_even_under_a_human_origin()
        {
            using var standIn = new StandInProcess();
            var claude = Claude();
            var stdin = new SignalingTextWriter();
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetProcessForTests(standIn.Process);
            claude.SetStdinForTests(stdin);
            claude.SetEventChannelForTests(events);

            var warmup = new WarmupService(claude, 30, NullLogger<WarmupService>.Instance, startupDelay: TimeSpan.Zero);
            await warmup.StartAsync(CancellationToken.None);
            await stdin.WaitForWriteAsync();
            Assert.Equal(TurnOrigin.Unknown, Assert.Single(Intervals).Origin);
            await events.Writer.WriteAsync(Result());
            await warmup.ExecuteTask!.WaitAsync(Timeout);

            var run = Enumerate(OutboundOrigin.Human, () => claude.SendCommandAsync("/compact"));
            await stdin.WaitForWriteAsync();
            Assert.Contains(Intervals, i => i is { Origin: TurnOrigin.Unknown, LockHeld: true, End: null });
            await events.Writer.WriteAsync(Result());
            await run.WaitAsync(Timeout);
            Assert.All(Intervals, i => Assert.Equal(TurnOrigin.Unknown, i.Origin));
        }

        [Fact]
        public async Task Claude_injected_turn_answers_take_the_pending_origin()
        {
            var claude = Claude();
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetEventChannelForTests(events);
            await events.Writer.WriteAsync(new ClaudeStreamEvent { Type = "system", Subtype = "init" });

            var read = Enumerate(OutboundOrigin.Human, () => claude.ReadInjectedTurnAnswersAsync(1));
            await Eventually(() => Intervals.Count == 1);
            Assert.Equal((TurnOrigin.Human, true), (Intervals[0].Origin, Intervals[0].LockHeld));

            await events.Writer.WriteAsync(Result());
            await read.WaitAsync(Timeout);
            Assert.NotNull(Assert.Single(Intervals).End);
        }

        [Theory]
        [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""", true)]
        [InlineData("""{"type":"user","message":{"content":[{"type":"tool_result"}]}}""", true)]
        [InlineData("""{"type":"stream_event"}""", true)]
        [InlineData("""{"type":"system","subtype":"init"}""", true)]
        [InlineData("""{"type":"system","subtype":"task_started","task_id":"t1"}""", false)]
        [InlineData("""{"type":"system","subtype":"task_notification","task_id":"t1"}""", false)]
        [InlineData("""{"type":"result","subtype":"success","result":"ok"}""", false)]
        public async Task Claude_reader_opens_unknown_only_on_turn_content(string line, bool opens)
        {
            var claude = Claude();
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetEventChannelForTests(events);
            var feed = new LineFeed();
            var reader = claude.RunStdoutReaderForTests(new StreamReader(feed));

            _rig.At(100.3);
            feed.WriteLine(line);
            await events.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

            Assert.Equal(opens, Intervals.Any(i => i is { Origin: TurnOrigin.Unknown, LockHeld: false, End: null }));
            feed.End();
            await reader.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Claude_reader_closes_on_the_first_result_and_on_stdout_eof()
        {
            var claude = Claude();
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetEventChannelForTests(events);
            var feed = new LineFeed();
            var reader = claude.RunStdoutReaderForTests(new StreamReader(feed));

            async Task Read(double at, string line)
            {
                _rig.At(at);
                feed.WriteLine(line);
                await events.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            }

            await Read(100.3, Assistant);
            await Read(101, Assistant);
            Assert.Single(Intervals);

            await Read(102, """{"type":"result","subtype":"error_during_execution","is_error":true}""");
            Assert.Equal(ReceiptRig.T(102), Assert.Single(Intervals).End);

            await Read(103, """{"type":"system","subtype":"init"}""");
            Assert.Contains(Intervals, i => i.Start == ReceiptRig.T(103) && i.End is null);

            _rig.At(104);
            feed.End();
            await reader.WaitAsync(Timeout);
            Assert.Contains(Intervals, i => i.Start == ReceiptRig.T(103) && i.End == ReceiptRig.T(104));
        }

        [Fact]
        public async Task Claude_restart_request_and_reader_cancellation_do_not_close_and_a_completed_kill_does()
        {
            using var standIn = new StandInProcess();
            var claude = Claude();
            claude.SetProcessForTests(standIn.Process);
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetEventChannelForTests(events);
            var feed = new LineFeed();
            using var cancelReader = new CancellationTokenSource();
            var reader = claude.RunStdoutReaderForTests(new StreamReader(feed), cancelReader.Token);

            _rig.At(100.3);
            feed.WriteLine(Assistant);
            await events.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

            _rig.At(101);
            claude.RequestRestart();
            _rig.At(102);
            await cancelReader.CancelAsync();
            await reader.WaitAsync(Timeout);
            Assert.Null(Assert.Single(Intervals).End);

            var pid = standIn.Process.Id;
            _rig.At(103);
            await claude.StopProcessAsync();
            Assert.Equal(ReceiptRig.T(103), Assert.Single(Intervals).End);
            AssertGone(pid);
        }

        [Fact]
        public async Task Claude_try_stop_that_returns_false_does_not_close_and_the_deferred_restart_kill_does()
        {
            using var standIn = new StandInProcess();
            var claude = Claude();
            claude.SetProcessForTests(standIn.Process);
            var stdin = new SignalingTextWriter();
            claude.SetStdinForTests(stdin);
            var events = Channel.CreateUnbounded<ClaudeStreamEvent>();
            claude.SetEventChannelForTests(events);
            var feed = new LineFeed();
            var reader = claude.RunStdoutReaderForTests(new StreamReader(feed));

            _rig.At(100.3);
            feed.WriteLine(Assistant);
            await events.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            _rig.At(101);
            claude.RequestRestart();

            _rig.At(110);
            var human = Enumerate(OutboundOrigin.Human, () => claude.ExecuteAsync("hello"));
            await stdin.WaitForWriteAsync();
            Assert.False(await claude.TryStopProcessAsync());
            Assert.Contains(Intervals, i => i is { Origin: TurnOrigin.Unknown, End: null });

            // The turn's own result reaches the turn without passing the reader, so only the
            // deferred restart's kill can close the untracked interval.
            var pid = standIn.Process.Id;
            _rig.At(131);
            await events.Writer.WriteAsync(Result());
            await human.WaitAsync(Timeout);
            await reader.WaitAsync(Timeout);

            Assert.Contains(Intervals, i => i.Origin == TurnOrigin.Unknown && i.End == ReceiptRig.T(131));
            AssertGone(pid);
        }

        // ── Codex ──

        [Fact]
        public async Task Codex_opens_after_the_turn_lock_with_the_pending_origin()
        {
            using var standIn = new StandInProcess();
            var codex = Codex();
            codex.SetProcessForTests(standIn.Process);
            codex.SetStdinForTests(standIn.StandardInput);
            codex.SetThreadStateForTests("thread-1", null);
            var notes = Channel.CreateUnbounded<JsonObject>();
            codex.SetNotificationChannelForTests(notes);
            using var cts = new CancellationTokenSource(Timeout);

            _rig.At(90);
            var turn = Enumerate(OutboundOrigin.Bridge, () => codex.ExecuteAsync("hello"));
            await codex.WaitAndCompleteNextPendingRequestForTests(
                new JsonObject { ["turn"] = new JsonObject { ["id"] = "turn-1" } }, cts.Token);
            Assert.Equal((TurnOrigin.Bridge, ReceiptRig.T(90), (DateTimeOffset?)null, true), Assert.Single(Intervals));

            _rig.At(100);
            await notes.Writer.WriteAsync(TurnCompleted("turn-1"));
            await turn.WaitAsync(Timeout);
            Assert.Equal(ReceiptRig.T(100), Assert.Single(Intervals).End);
        }

        [Fact]
        public async Task Codex_run_is_unknown_and_closes_with_the_send_lock()
        {
            using var standIn = new StandInProcess();
            var codex = Codex();
            codex.SetProcessForTests(standIn.Process);
            codex.SetStdinForTests(standIn.StandardInput);
            codex.SetThreadStateForTests("thread-1", null);
            var notes = Channel.CreateUnbounded<JsonObject>();
            codex.SetNotificationChannelForTests(notes);
            using var cts = new CancellationTokenSource(Timeout);

            _rig.At(100);
            var run = Enumerate(OutboundOrigin.Human, () => codex.SendCommandAsync("ls"));
            await Eventually(() => Intervals.Count == 1);
            Assert.Equal((TurnOrigin.Unknown, true), (Intervals[0].Origin, Intervals[0].LockHeld));

            _rig.At(101);
            await codex.WaitAndCompleteNextPendingRequestForTests(new JsonObject(), cts.Token);
            await Eventually(() => Intervals[0].End is not null);
            Assert.Equal(ReceiptRig.T(101), Intervals[0].End);

            await notes.Writer.WriteAsync(new JsonObject
            {
                ["method"] = "turn/started",
                ["params"] = new JsonObject { ["turn"] = new JsonObject { ["id"] = "cmd-1" } },
            });
            await notes.Writer.WriteAsync(TurnCompleted("cmd-1"));
            await run.WaitAsync(Timeout);
        }

        [Fact]
        public async Task Codex_reader_opens_on_turn_content_closes_on_turn_completed_and_on_eof()
        {
            var codex = Codex();
            var notes = Channel.CreateUnbounded<JsonObject>();
            codex.SetNotificationChannelForTests(notes);
            var feed = new LineFeed();
            var reader = codex.RunStdoutReaderForTests(new StreamReader(feed));

            async Task Read(double at, JsonObject note)
            {
                _rig.At(at);
                feed.WriteLine(note.ToJsonString());
                await notes.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            }

            await Read(100, Note("thread/tokenUsage/updated"));
            Assert.Empty(Intervals);

            await Read(100.3, Note("item/started"));
            await Read(101, Note("item/agentMessage/delta"));
            Assert.Equal((TurnOrigin.Unknown, false, (DateTimeOffset?)null), (Intervals.Single().Origin, Intervals.Single().LockHeld, Intervals.Single().End));

            await Read(102, TurnCompleted("turn-x"));
            Assert.Equal(ReceiptRig.T(102), Assert.Single(Intervals).End);

            await Read(103, Note("turn/started"));
            _rig.At(104);
            feed.End();
            await reader.WaitAsync(Timeout);
            Assert.Contains(Intervals, i => i.Start == ReceiptRig.T(103) && i.End == ReceiptRig.T(104));
        }

        [Fact]
        public async Task Codex_restart_request_cancellation_and_a_false_try_stop_do_not_close_and_a_completed_kill_does()
        {
            var codex = Codex();
            var notes = Channel.CreateUnbounded<JsonObject>();
            codex.SetNotificationChannelForTests(notes);
            var feed = new LineFeed();
            using var cancelReader = new CancellationTokenSource();
            var reader = codex.RunStdoutReaderForTests(new StreamReader(feed), cancelReader.Token);

            _rig.At(100.3);
            feed.WriteLine(Note("item/started").ToJsonString());
            await notes.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

            _rig.At(101);
            codex.RequestRestart();
            Assert.False(await codex.TryStopProcessAsync());
            await cancelReader.CancelAsync();
            await reader.WaitAsync(Timeout);
            Assert.Null(Assert.Single(Intervals).End);

            using var standIn = new StandInProcess();
            var pid = standIn.Process.Id;
            codex.SetProcessForTests(standIn.Process);
            _rig.At(105);
            await codex.StopProcessAsync();
            Assert.Equal(ReceiptRig.T(105), Assert.Single(Intervals).End);
            AssertGone(pid);
        }

        // ── Gemini ──

        [Fact]
        public async Task Gemini_spans_each_process_from_start_to_exit_and_a_command_is_unknown()
        {
            using var standIn = new GeminiStandIn();
            var gemini = Gemini(standIn);

            _rig.At(100);
            var turn = Enumerate(OutboundOrigin.Human, () => gemini.ExecuteAsync("hello"));
            await Eventually(() => Intervals.Count == 1);
            Assert.Equal((TurnOrigin.Human, ReceiptRig.T(100), (DateTimeOffset?)null), (Intervals[0].Origin, Intervals[0].Start, Intervals[0].End));

            _rig.At(104);
            standIn.Release();
            await turn.WaitAsync(Timeout);
            Assert.Equal(ReceiptRig.T(104), Assert.Single(Intervals).End);

            var run = Enumerate(OutboundOrigin.Human, () => gemini.SendCommandAsync("/compact"));
            await run.WaitAsync(Timeout);
            Assert.Equal(TurnOrigin.Unknown, Intervals.Single(i => i.Origin != TurnOrigin.Human).Origin);
        }

        [Fact]
        public async Task Gemini_concurrent_human_and_relay_processes_exclude_the_window()
        {
            using var standIn = new GeminiStandIn();
            var gemini = Gemini(standIn);
            var humans = NewTaskManager(gemini, _rig.Ledger);
            var relays = NewTaskManager(gemini, _rig.Ledger);

            _rig.At(95);
            await humans.StartTask(HumanChat, "hello", "hello", isSessionTask: false);
            await Eventually(() => Intervals.Count == 1);
            _rig.At(96);
            await relays.StartTask(RelayChat, "directive", "directive", isSessionTask: false,
                source: TaskSource.Relay, relaySender: "peer-agent");
            await Eventually(() => Intervals.Count == 2);
            Assert.Equal([TurnOrigin.Human, TurnOrigin.Relay], Intervals.Select(i => i.Origin).Order().ToArray());

            _rig.At(100.1);
            await _rig.DeliverAsync(ReceiptRig.Receipt(100));

            _rig.At(103);
            standIn.Release();
            await Eventually(() => Intervals.All(i => i.End is not null) && !humans.HasRunningTasks(HumanChat) && !relays.HasRunningTasks(RelayChat));

            _rig.At(103.25);
            await _rig.DecideAsync();
            Assert.Equal(1, _rig.Counters.Get("tool_send_non_human"));
            Assert.Equal(0, _rig.Rows);
        }

        // ── harness ──

        private const string Assistant = """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""";

        private ClaudeExecutor Claude()
        {
            var options = Options.Create(new AgentOptions
            {
                Name = "test", Role = "test", WorkDir = _rig.Root, Provider = "claude", MaxTurns = 100,
            });
            return new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance,
                new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), _rig.Ledger);
        }

        private CodexExecutor Codex()
        {
            var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = _rig.Root });
            return new CodexExecutor(
                options, Options.Create(new TelegramOptions { AttachmentDir = Path.Combine(_rig.Root, "attachments") }),
                new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<CodexExecutor>.Instance,
                _ => null, ledger: _rig.Ledger);
        }

        private GeminiExecutor Gemini(GeminiStandIn standIn)
        {
            var options = Options.Create(new AgentOptions
            {
                Name = "test", Role = "test", WorkDir = _rig.Root, Provider = "gemini", Model = "gemini-2.5-flash",
            });
            return new GeminiExecutor(options, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance),
                NullLogger<GeminiExecutor>.Instance, standIn.Start, _rig.Ledger);
        }

        private Task Enumerate(OutboundOrigin origin, Func<IAsyncEnumerable<AgentProgress>> call) => Task.Run(async () =>
        {
            using var _ = _rig.Ledger.Pending(origin);
            await foreach (var __ in call()) { }
        });

        private static ClaudeStreamEvent Result() => new() { Type = "result", Subtype = "success", Result = "ok" };

        /// <summary>The executor killed and disposed the stand-in, so it is looked up by id.</summary>
        private static void AssertGone(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.HasExited, "the stand-in process is still running");
            }
            catch (ArgumentException)
            {
                // No such process: it exited and was reaped.
            }
        }

        private static JsonObject Note(string method) => new()
        {
            ["method"] = method,
            ["params"] = new JsonObject { ["turnId"] = "turn-x" },
        };

        private static JsonObject TurnCompleted(string turnId) => new()
        {
            ["method"] = "turn/completed",
            ["params"] = new JsonObject
            {
                ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "completed" },
            },
        };
    }

    internal static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}

/// <summary>One scripted executor call: the test sees it enter, acquire and release, and ends it.</summary>
internal sealed class FakeTurn
{
    private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Acquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Finished => _finish.Task;

    public void Finish() => _finish.TrySetResult();
}

/// <summary>A kill the test holds until it moves the clock.</summary>
internal sealed class HeldKill
{
    private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completed => _complete.Task;

    public void Complete() => _complete.TrySetResult();
}

/// <summary>
/// A provider executor reduced to its ledger contract, with the calls at the same points as the real
/// ones (whose own sites <see cref="TurnOriginLedgerTests.ExecutorHooks"/> covers): a real
/// <see cref="SemaphoreSlim"/> turn lock; the interval opened right after it is acquired and closed
/// in the finally that releases it; <see cref="SendCommandAsync"/> always unknown; a deferred
/// restart that kills after the turn's result, as Claude does; and a "stdout" whose events are
/// classified as Claude's reader classifies them, one <see cref="TurnOriginLedger.ProviderActivity"/>
/// per process.
/// </summary>
internal sealed class LedgerFakeExecutor(TurnOriginLedger ledger) : IAgentExecutor
{
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, FakeTurn> _turns = new(StringComparer.Ordinal);
    private TurnOriginLedger.ProviderActivity _activity = ledger.TrackProvider();
    private HeldKill? _heldKill;
    private volatile bool _restartRequested;

    /// <summary>The script name <see cref="ReadInjectedTurnAnswersAsync"/> runs under.</summary>
    public const string InjectedAnswers = "injected-answers";

    public FakeTurn Turn(string name) => _turns.GetOrAdd(name, _ => new FakeTurn());

    public HeldKill HoldNextKill() => _heldKill = new HeldKill();

    public bool AcceptInjections { get; set; }

    public IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default) => RunAsync(task, command: false, ct);

    public Task<MidTurnInjectionResult> TryInjectMessageAsync(
        string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default) =>
        Task.FromResult(AcceptInjections ? MidTurnInjectionResult.Injected : MidTurnInjectionResult.Unsupported);

    /// <summary>Claude's injected-turn read: its own lock acquisition, so its own interval.</summary>
    public IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(int injectedMessages, CancellationToken ct = default) =>
        RunAsync(InjectedAnswers, command: false, ct);

    public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
        RunAsync(command, command: true, ct);

    private async IAsyncEnumerable<AgentProgress> RunAsync(string name, bool command, [EnumeratorCancellation] CancellationToken ct)
    {
        var turn = Turn(name);
        turn.Entered.TrySetResult();
        await _turnLock.WaitAsync(ct);
        var interval = ledger.OpenTurn(command);
        try
        {
            turn.Acquired.TrySetResult();
            await turn.Finished.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = "done", FinalResult = "done", IsSignificant = true };

            if (_restartRequested)
            {
                _restartRequested = false;
                if (_heldKill is { } held)
                {
                    held.Reached.TrySetResult();
                    await held.Completed.WaitAsync(ct);
                }
                KillCompleted();
            }
        }
        finally
        {
            interval.Close();
            _turnLock.Release();
            turn.Released.TrySetResult();
        }
    }

    /// <summary>The process's stdout reader reads one event.</summary>
    public void Stdout(string type, string? subtype = null)
    {
        if (type == "result")
            _activity.TurnEnded();
        else if (type is "assistant" or "user" or "stream_event" || (type == "system" && subtype == "init"))
            _activity.TurnContent();
    }

    public void StdoutEof()
    {
        _activity.ProcessEnded();
        _activity = ledger.TrackProvider();
    }

    public void RequestRestart() => _restartRequested = true;

    public async Task StopProcessAsync()
    {
        await _turnLock.WaitAsync();
        try { KillCompleted(); }
        finally { _turnLock.Release(); }
    }

    public async Task<bool> TryStopProcessAsync()
    {
        if (!await _turnLock.WaitAsync(TimeSpan.Zero)) return false;
        try
        {
            KillCompleted();
            return true;
        }
        finally { _turnLock.Release(); }
    }

    private void KillCompleted()
    {
        _activity.ProcessEnded();
        _activity = ledger.TrackProvider();
    }

    public bool IsProcessWarm => false;
    public string? LastSessionId => null;
    public DateTimeOffset LastActivity => DateTimeOffset.MinValue;
    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A readable stream the test writes lines into; <see cref="End"/> is stdout EOF. Honours cancellation.</summary>
internal sealed class LineFeed : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _offset;

    public void WriteLine(string line) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));

    public void End() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null || _offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            _chunks.Reader.TryRead(out _current);
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current!.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stdin that signals each line the executor writes.</summary>
internal sealed class SignalingTextWriter : TextWriter
{
    private readonly Channel<bool> _writes = Channel.CreateUnbounded<bool>();

    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        _writes.Writer.TryWrite(true);
        return Task.CompletedTask;
    }

    public Task WaitForWriteAsync() => _writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
}

/// <summary>
/// A stand-in gemini CLI: reads the task from stdin, waits until the test releases it, prints one
/// assistant message and exits 0. POSIX-only, like <see cref="StandInProcess"/>.
/// </summary>
internal sealed class GeminiStandIn : IDisposable
{
    private const string ShellPath = "/bin/sh"; // hygiene-ok: OS stand-in binary, not provider data

    private readonly string _release = Path.Combine(Path.GetTempPath(), $"gemini-release-{Guid.NewGuid():N}");

    public Process? Start(ProcessStartInfo executorStartInfo)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ShellPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("""cat >/dev/null; while [ ! -e "$0" ]; do sleep 0.05; done; echo '{"type":"message","role":"assistant","content":"ok"}'""");
        psi.ArgumentList.Add(_release);
        return Process.Start(psi);
    }

    public void Release() => File.WriteAllText(_release, "");

    public void Dispose()
    {
        try { File.Delete(_release); } catch (IOException) { }
    }
}

/// <summary>Keeps warning messages.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _warnings = new();

    public IReadOnlyList<string> Warnings => _warnings.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) _warnings.Enqueue(formatter(state, exception));
    }
}
