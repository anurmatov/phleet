using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// #369: Claude CLI runs an injected message as its own turn when it arrives while the model is
/// already producing its final answer — <c>system/init</c>, then a second <c>result</c>, after
/// <see cref="ClaudeExecutor.ExecuteAsync"/> has returned on the first. Scripted stdout, fed through
/// the executor's real event channel, with /bin/cat standing in for the live process. The event
/// order mirrors what Claude Code 2.1.280 emitted against a local server: <c>result</c>, then
/// <c>system/init</c> about 10 ms later, then the turn, then a <c>result</c> with no origin.
/// </summary>
public class ClaudeExecutorInjectedTurnTests
{
    [Fact]
    public async Task SeparateTurn_ItsAnswerIsYieldedWithinOneSecond_AndTheNextSendDrainsNothing()
    {
        await using var fixture = Fixture.Start();
        var first = await fixture.RunTurnAsync("first message", "ALPHA");
        Assert.Equal(["ALPHA"], FinalResults(first));

        var read = fixture.ReadInjectedAsync(1);
        fixture.Write(Init());
        fixture.Write(Text("BRAVO"));
        var resultWrittenAt = Stopwatch.GetTimestamp();
        fixture.Write(new ClaudeStreamEvent { Type = "result", Result = "BRAVO" });
        var extra = await read;

        var answer = Assert.Single(extra);
        Assert.Equal("BRAVO", answer.Progress.FinalResult);
        Assert.False(answer.Progress.IsErrorResult);
        Assert.True(Stopwatch.GetElapsedTime(resultWrittenAt, answer.At) < TimeSpan.FromSeconds(1),
            "the second answer must be yielded as soon as its result arrives");

        // The next send finds nothing stale: no out-of-band recovered answer, and its own answer.
        var next = await fixture.RunTurnAsync("next message", "CHARLIE");
        Assert.DoesNotContain(next, p => p.EventType == "recovered_answer");
        Assert.Equal(["CHARLIE"], FinalResults(next));
    }

    [Fact]
    public async Task Absorbed_NoTurnStarts_YieldsNothingWithinTheBound()
    {
        await using var fixture = Fixture.Start(startWait: TimeSpan.FromMilliseconds(300));
        await fixture.RunTurnAsync("first message", "combined answer");

        var started = Stopwatch.GetTimestamp();
        var extra = await fixture.ReadInjectedAsync(1);

        Assert.Empty(extra);
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(250), $"returned before the bound ({elapsed})");
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"a missing turn must not hold the chat ({elapsed})");
    }

    [Fact]
    public async Task NoInjection_ReturnsAtOnceAndTouchesNothing()
    {
        await using var fixture = Fixture.Start();
        await fixture.RunTurnAsync("first message", "answer");
        fixture.Write(Init());

        var started = Stopwatch.GetTimestamp();
        var extra = await fixture.ReadInjectedAsync(0);

        Assert.Empty(extra);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(200));
        Assert.True(fixture.Channel.Reader.TryPeek(out var left) && left.Subtype == "init",
            "with no injection the channel is not read");
    }

    [Fact]
    public async Task AStrayNonTurnEvent_IsLeftForTheStaleDrain()
    {
        await using var fixture = Fixture.Start();
        await fixture.RunTurnAsync("first message", "answer");
        fixture.Write(Text("late text"));

        var started = Stopwatch.GetTimestamp();
        var extra = await fixture.ReadInjectedAsync(1);

        Assert.Empty(extra);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1), "not a turn start: stop at once");
        fixture.Executor.DrainStaleTurnEventsForTests();
        Assert.Equal("late text", fixture.Executor.PreservedDrainedAnswerTextForTests);
    }

    [Fact]
    public async Task ProcessExit_EndsTheWait()
    {
        await using var fixture = Fixture.Start(startWait: TimeSpan.FromSeconds(30));
        await fixture.RunTurnAsync("first message", "answer");

        var read = fixture.ReadInjectedAsync(1);
        fixture.Channel.Writer.TryComplete();
        var extra = await read.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(extra);
    }

    [Fact]
    public async Task ANonHumanResult_IsNotDeliveredAsAnAnswer()
    {
        await using var fixture = Fixture.Start(startWait: TimeSpan.FromMilliseconds(300));
        await fixture.RunTurnAsync("first message", "answer");

        var read = fixture.ReadInjectedAsync(1);
        fixture.Write(Init());
        fixture.Write(new ClaudeStreamEvent
        {
            Type = "result",
            Result = "background task finished",
            Origin = new ClaudeMessageOrigin { Kind = "task-notification" },
        });
        var extra = await read;

        Assert.Empty(extra);
    }

    [Fact]
    public async Task TwoSeparateTurns_AreBothYielded_InOrder()
    {
        await using var fixture = Fixture.Start();
        await fixture.RunTurnAsync("first message", "ALPHA");

        var read = fixture.ReadInjectedAsync(2);
        foreach (var answer in new[] { "BRAVO", "CHARLIE" })
        {
            fixture.Write(Init());
            fixture.Write(Text(answer));
            fixture.Write(new ClaudeStreamEvent { Type = "result", Result = answer });
        }
        var extra = await read;

        Assert.Equal(["BRAVO", "CHARLIE"], extra.Select(e => e.Progress.FinalResult));
    }

    [Fact]
    public async Task InitialTurn_DelayedExecutionStillReceivesItsScriptedAnswer()
    {
        await using var fixture = Fixture.Start();
        // Reproduce a worker that starts after the fixture's former 100 ms guess.
        var result = await fixture.RunTurnAsync("delayed message", "ANSWER", Task.Delay(250));
        Assert.Equal(["ANSWER"], FinalResults(result));
    }

    // ── #429: foreground tool calls, the final-answer gate and teardown races ──

    private static ClaudeStreamEvent ToolCall(string? parent = null) => new()
    {
        Type = "assistant",
        ParentToolUseId = parent,
        Message = new ClaudeMessage { Content = [new ClaudeContentBlock { Type = "tool_use", Name = "Bash", Id = "x" }] },
    };

    private static Process StartCat() => Process.Start(new ProcessStartInfo
    {
        FileName = "/bin/cat",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    })!;

    private static ClaudeExecutor BuildExecutor(ClaudeExecutorLogRecorder? logs = null)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });
        return new ClaudeExecutor(options, logs ?? (Microsoft.Extensions.Logging.ILogger<ClaudeExecutor>)NullLogger<ClaudeExecutor>.Instance,
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
    }

    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static void AssertClosed(ClaudeExecutor executor)
    {
        Assert.True(executor.TurnCommittedToFinalAnswerForTests);
        Assert.True(executor.CurrentTurnResultSeenForTests);
    }

    private static int KillWarnings(ClaudeExecutorLogRecorder logs) =>
        logs.Warnings.Count(line => line.StartsWith("Kill proceeding without the stdin lock", StringComparison.Ordinal));

    /// <summary>S2 through the real event channel: steering is accepted while a foreground tool runs.</summary>
    [Fact]
    public async Task NarratedForegroundTools_SteerDuringATool_IsInjected()
    {
        await using var fixture = Fixture.Start();
        var turn = Task.Run(async () =>
        {
            var events = new List<AgentProgress>();
            await foreach (var p in fixture.Executor.ExecuteAsync("task", ct: fixture.Timeout))
                events.Add(p);
            return events;
        });
        Assert.NotNull(await fixture.ReadEchoAsync());

        fixture.Write(Init());
        for (var call = 1; call <= 3; call++)
        {
            fixture.Write(Text($"Running call {call} now."));
            fixture.Write(ToolCall());
        }
        await fixture.WaitForParsedAsync();

        var steer = await fixture.Executor.TryInjectMessageAsync("steer");
        Assert.Equal(MidTurnInjectionStatus.Injected, steer.Status);
        Assert.Equal(3, fixture.Executor.GateReopenCountForTests);

        fixture.Write(Text("All done."));
        fixture.Write(new ClaudeStreamEvent { Type = "result", Result = "All done." });
        Assert.Equal(["All done."], FinalResults(await turn));
        AssertClosed(fixture.Executor);
    }

    /// <summary>AC4 (S6): the extra turn's init and tool call in the #370 window do not reopen.</summary>
    [Fact]
    public async Task ExtraTurnToolCallAfterTheResult_DoesNotReopenTheGate()
    {
        await using var fixture = Fixture.Start();
        await fixture.RunTurnAsync("first message", "ANSWER");

        var read = fixture.ReadInjectedAsync(1);
        fixture.Write(Init());
        fixture.Write(ToolCall());
        await fixture.WaitForParsedAsync();

        Assert.True(fixture.Executor.TurnCommittedToFinalAnswerForTests);
        Assert.Equal(0, fixture.Executor.GateReopenCountForTests);
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, (await fixture.Executor.TryInjectMessageAsync("late")).Status);

        fixture.Write(Text("EXTRA"));
        fixture.Write(new ClaudeStreamEvent { Type = "result", Result = "EXTRA" });
        Assert.Single(await read);
    }

    /// <summary>AC8 (S11): /run closes both flags, and its tool call cannot reopen them.</summary>
    [Fact]
    public async Task RunCommand_ToolCall_DoesNotReopen_InjectionRefused()
    {
        await using var fixture = Fixture.Start();
        await fixture.RunTurnAsync("first message", "ANSWER");

        var command = Task.Run(async () =>
        {
            var events = new List<AgentProgress>();
            await foreach (var p in fixture.Executor.SendCommandAsync("/run echo hi", fixture.Timeout))
                events.Add(p);
            return events;
        });
        Assert.NotNull(await fixture.ReadEchoAsync());
        AssertClosed(fixture.Executor);

        fixture.Write(ToolCall());
        await fixture.WaitForParsedAsync();

        AssertClosed(fixture.Executor);
        Assert.Equal(0, fixture.Executor.GateReopenCountForTests);
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, (await fixture.Executor.TryInjectMessageAsync("during run")).Status);

        fixture.Write(new ClaudeStreamEvent { Type = "result", Result = "ran" });
        await command;
        AssertClosed(fixture.Executor);
    }

    /// <summary>AC7 (S10): after a kill both flags stay closed until the next turn start.</summary>
    [Fact]
    public async Task Kill_ClosesBothFlags_UntilTheNextTurnStart()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(new StringWriter());
            executor.OpenTurnForTests();
            executor.ParseProgressForTests(Text("narration"));
            executor.ParseProgressForTests(ToolCall());
            Assert.False(executor.TurnCommittedToFinalAnswerForTests);

            await executor.KillProcessForTestsAsync();

            AssertClosed(executor);
            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, (await executor.TryInjectMessageAsync("x")).Status);
            Assert.Contains("Mid-turn injection refused: gate_closed", logs.Information);

            executor.OpenTurnForTests();
            Assert.False(executor.TurnCommittedToFinalAnswerForTests);
            Assert.False(executor.CurrentTurnResultSeenForTests);
            Assert.Equal(0, executor.GateReopenCountForTests);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7: the early <c>_process is null</c> return also closes, and clears the marker.</summary>
    [Fact]
    public async Task Kill_WithNoProcess_ClosesBothFlags()
    {
        var executor = BuildExecutor();
        executor.OpenTurnForTests();

        await executor.KillProcessForTestsAsync();

        AssertClosed(executor);
        Assert.Equal(0, executor.TeardownInProgressForTests);
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, (await executor.TryInjectMessageAsync("x")).Status);
    }

    /// <summary>AC7 (S10b): a process that exited on its own is refused as not running.</summary>
    [Fact]
    public async Task ProcessExitedWithoutAKill_RefusedAsProcessNotRunning()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        try
        {
            process.Kill();
            await process.WaitForExitAsync();
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(new StringWriter());
            executor.OpenTurnForTests();

            var result = await executor.TryInjectMessageAsync("x");

            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
            Assert.Contains("Mid-turn injection refused: process_not_running", logs.Information);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7b: an injection from inside the kill's cleanup is refused, nothing written.</summary>
    [Fact]
    public async Task InjectionDuringKillCleanup_RefusedByTheGate()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            executor.OpenTurnForTests();
            MidTurnInjectionResult? duringCleanup = null;
            executor.KillCleanupStartingForTests = () =>
                duringCleanup = executor.TryInjectMessageAsync("x").GetAwaiter().GetResult();

            await executor.KillProcessForTestsAsync();

            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, duringCleanup!.Status);
            Assert.Contains("Mid-turn injection refused: gate_closed", logs.Information);
            Assert.Equal("", stdin.ToString());
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7b (S13b): an injection waiting on the lock while a kill runs is never Injected.</summary>
    [Fact]
    public async Task InjectionWaitingOnTheLock_DuringAKill_NeverInjected()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        executor.KillStdinLockWait = TimeSpan.FromMilliseconds(100);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            executor.OpenTurnForTests();

            await executor.StdinWriteLockForTests.WaitAsync();
            var injection = executor.TryInjectMessageAsync("x");
            await executor.KillProcessForTestsAsync();
            executor.StdinWriteLockForTests.Release();
            var result = await injection;

            Assert.NotEqual(MidTurnInjectionStatus.Injected, result.Status);
            Assert.Contains(result.Status, new[] { MidTurnInjectionStatus.NoActiveTurn, MidTurnInjectionStatus.Failed });
            Assert.Equal(1, KillWarnings(logs));
            Assert.Equal("", stdin.ToString());
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7b (S13a): a kill after the early checks, before the lock.</summary>
    [Fact]
    public async Task KillAfterEarlyChecks_RefusedUnderTheLock()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            executor.OpenTurnForTests();
            executor.InjectionCheckpointForTests = async name =>
            {
                if (name == "early_checks_passed")
                    await executor.KillProcessForTestsAsync();
            };

            var result = await executor.TryInjectMessageAsync("x");

            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
            Assert.Contains("Mid-turn injection refused: gate_closed_under_lock", logs.Information);
            Assert.Equal("", stdin.ToString());
            Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>
    /// AC7d (S13c, S13d): a kill at a checkpoint while the injection holds the lock. The kill's
    /// bound expires (one Warning), it tears down, and the injection is refused as not running.
    /// </summary>
    [Theory]
    [InlineData("under_lock_gate_passed")]
    [InlineData("captured")]
    public async Task KillWhileHoldingTheLock_RefusedAsProcessNotRunning(string checkpoint)
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        executor.KillStdinLockWait = TimeSpan.FromMilliseconds(100);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            executor.OpenTurnForTests();
            executor.InjectionCheckpointForTests = async name =>
            {
                if (name == checkpoint)
                    await executor.KillProcessForTestsAsync();
            };

            var result = await executor.TryInjectMessageAsync("x");

            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
            Assert.Contains("Mid-turn injection refused: process_not_running", logs.Information);
            Assert.Equal("", stdin.ToString());
            Assert.Equal(1, KillWarnings(logs));
            Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7d (S13e): a kill after validation, writing to the real stdin of the dying process.</summary>
    [Fact]
    public async Task KillAfterValidation_WriteToTheRealStdin_Failed()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        executor.KillStdinLockWait = TimeSpan.FromMilliseconds(100);
        var process = StartCat();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);
            executor.OpenTurnForTests();
            var generationBefore = executor.TeardownGenerationForTests;
            executor.InjectionCheckpointForTests = async name =>
            {
                if (name == "validated")
                    await executor.KillProcessForTestsAsync();
            };

            var result = await executor.TryInjectMessageAsync("x");

            Assert.Equal(MidTurnInjectionStatus.Failed, result.Status);
            Assert.True(executor.TeardownGenerationForTests > generationBefore);
            Assert.Equal(1, KillWarnings(logs));
            Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7d (S16): an injection after DisposeAsync disposed the lock.</summary>
    [Fact]
    public async Task DisposeAfterEarlyChecks_RefusedAsProcessNotRunning_NothingEscapes()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            executor.OpenTurnForTests();
            executor.InjectionCheckpointForTests = async name =>
            {
                if (name == "early_checks_passed")
                    await executor.DisposeAsync();
            };

            var result = await executor.TryInjectMessageAsync("x");

            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
            Assert.Contains("Mid-turn injection refused: process_not_running", logs.Information);
            Assert.Equal("", stdin.ToString());
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7d: an uncontended kill takes the lock at once, with no Warning.</summary>
    [Fact]
    public async Task UncontendedKill_NoWarning_LockReleased()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(new StringWriter());

            await executor.KillProcessForTestsAsync();

            Assert.Equal(0, KillWarnings(logs));
            Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
        }
        finally { KillQuietly(process); }
    }

    public enum TeardownPastTheBound { ConcurrentDispose, KillOnly, CompletesBeforeDisposal }

    /// <summary>
    /// AC7e (S17): the injection holds the lock with its write blocked past the kill's bound. The
    /// write then completes successfully, and is still reported Failed, never Injected.
    /// </summary>
    [Theory]
    [InlineData(TeardownPastTheBound.ConcurrentDispose)]
    [InlineData(TeardownPastTheBound.KillOnly)]
    [InlineData(TeardownPastTheBound.CompletesBeforeDisposal)]
    public async Task TeardownWinsPastTheLockBound_WriteReportedFailed(TeardownPastTheBound mode)
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        executor.KillStdinLockWait = TimeSpan.FromMilliseconds(100);
        var process = StartCat();
        var writer = new BlockingTextWriter { BlockWrite = true };
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(writer);
            executor.OpenTurnForTests();
            var generationBefore = executor.TeardownGenerationForTests;

            var injection = executor.TryInjectMessageAsync("x");
            await writer.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            MidTurnInjectionResult result;
            switch (mode)
            {
                case TeardownPastTheBound.ConcurrentDispose:
                    await executor.DisposeAsync();
                    writer.Release();
                    result = await injection;
                    break;
                case TeardownPastTheBound.KillOnly:
                    await executor.KillProcessForTestsAsync();
                    writer.Release();
                    result = await injection;
                    break;
                default:
                    MidTurnInjectionResult? whileKillRuns = null;
                    executor.KillProceedingWithoutLockForTests = async () =>
                    {
                        writer.Release();
                        whileKillRuns = await injection;
                    };
                    await executor.KillProcessForTestsAsync();
                    result = whileKillRuns!;
                    break;
            }

            Assert.Equal(MidTurnInjectionStatus.Failed, result.Status);
            Assert.Contains("Mid-turn injection failed: teardown_during_write", logs.Information);
            Assert.Equal(generationBefore + 1, executor.TeardownGenerationForTests);
            Assert.Equal(1, KillWarnings(logs));
            AssertClosed(executor);
            if (mode != TeardownPastTheBound.ConcurrentDispose)
                Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>
    /// AC7f (S18): final text parsed while the flush is blocked. The bytes reached a live process,
    /// so the result is Injected — a Failed here would queue a second copy.
    /// </summary>
    [Fact]
    public async Task FinalTextDuringABlockedFlush_StillInjected_OneLine()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        var writer = new BlockingTextWriter { BlockFlush = true };
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(writer);
            executor.OpenTurnForTests();
            var generationBefore = executor.TeardownGenerationForTests;

            var injection = executor.TryInjectMessageAsync("x");
            await writer.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            executor.ParseProgressForTests(Text("Here is the answer."));
            writer.Release();
            var result = await injection;

            Assert.Equal(MidTurnInjectionStatus.Injected, result.Status);
            Assert.Single(writer.Lines);
            Assert.True(executor.TurnCommittedToFinalAnswerForTests);
            Assert.Equal(generationBefore, executor.TeardownGenerationForTests);
            Assert.DoesNotContain(logs.Information, line => line.Contains("teardown_during_write", StringComparison.Ordinal));
            Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>
    /// AC7g cases 1–2 (S19): a kill that starts after the injection's under-lock checks, paused
    /// before its lock wait. The injection writes into the still-live process and is Failed.
    /// </summary>
    [Theory]
    [InlineData("under_lock_gate_passed")]
    [InlineData("captured")]
    public async Task KillStartingAfterTheUnderLockChecks_WriteReportedFailed(string checkpoint)
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            executor.OpenTurnForTests();
            var generationBefore = executor.TeardownGenerationForTests;

            var injectionHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseInjection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.InjectionCheckpointForTests = async name =>
            {
                if (name != checkpoint) return;
                injectionHeld.TrySetResult();
                await releaseInjection.Task;
            };
            var killHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseKill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.KillBeforeLockWaitForTests = async () =>
            {
                killHeld.TrySetResult();
                await releaseKill.Task;
            };

            var injection = Task.Run(() => executor.TryInjectMessageAsync("x"));
            await injectionHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var kill = Task.Run(executor.KillProcessForTestsAsync);
            await killHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));

            releaseInjection.SetResult();
            var result = await injection;

            Assert.Equal(MidTurnInjectionStatus.Failed, result.Status);
            Assert.Contains("Mid-turn injection failed: teardown_during_write", logs.Information);
            Assert.True(Lines(stdin).Length <= 1);

            releaseKill.SetResult();
            await kill;

            Assert.Equal(1, executor.StdinWriteLockForTests.CurrentCount);
            AssertClosed(executor);
            Assert.Equal(generationBefore + 1, executor.TeardownGenerationForTests);
            Assert.Equal(0, executor.TeardownInProgressForTests);
            Assert.Equal(0, KillWarnings(logs));
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7g case 3 (S19b): a concurrent turn start cannot clear a running teardown.</summary>
    [Fact]
    public async Task TurnStartDuringAKill_InjectionRefusedAsTeardownInProgress()
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        var process = StartCat();
        var stdin = new StringWriter();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(stdin);
            var killHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseKill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.KillBeforeLockWaitForTests = async () =>
            {
                killHeld.TrySetResult();
                await releaseKill.Task;
            };

            var kill = Task.Run(executor.KillProcessForTestsAsync);
            await killHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            executor.OpenTurnForTests();

            var result = await executor.TryInjectMessageAsync("x");

            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
            Assert.Contains("Mid-turn injection refused: teardown_in_progress", logs.Information);
            Assert.Equal("", stdin.ToString());

            releaseKill.SetResult();
            await kill;
            Assert.Equal(0, executor.TeardownInProgressForTests);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>AC7g case 4: the marker returns to zero on the early return and on a throw.</summary>
    [Fact]
    public async Task KillThatThrowsInCleanup_ClearsTheMarker_FlagsStayClosed()
    {
        var executor = BuildExecutor();
        var process = StartCat();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(new StringWriter());
            executor.OpenTurnForTests();
            executor.KillCleanupStartingForTests = () => throw new InvalidOperationException("cleanup failed");

            await Assert.ThrowsAsync<InvalidOperationException>(executor.KillProcessForTestsAsync);

            Assert.Equal(0, executor.TeardownInProgressForTests);
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    public enum ClosedBeforeRecovery { Kill, ResultWithoutFinalText }

    /// <summary>
    /// AC7c (S14/S15): between EnsureProcess and the turn start, ExecuteAsync may yield a recovered
    /// answer and hand the consumer control. The gate is closed there, so no injection can be
    /// written ahead of the task.
    /// </summary>
    [Theory]
    [InlineData(ClosedBeforeRecovery.Kill)]
    [InlineData(ClosedBeforeRecovery.ResultWithoutFinalText)]
    public async Task RecoveredAnswerYield_BeforeTheTurnStart_RefusesInjection(ClosedBeforeRecovery closedBy)
    {
        var logs = new ClaudeExecutorLogRecorder();
        var executor = BuildExecutor(logs);
        if (closedBy == ClosedBeforeRecovery.Kill)
        {
            await executor.KillProcessForTestsAsync();
        }
        else
        {
            executor.OpenTurnForTests();
            executor.ParseProgressForTests(new ClaudeStreamEvent { Type = "result", Subtype = "error_max_turns", IsError = true });
        }

        var process = StartCat();
        var stdin = new StringWriter();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ClaudeStreamEvent>();
        try
        {
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(TextWriter.Synchronized(stdin));
            executor.SetEventChannelForTests(channel);
            channel.Writer.TryWrite(Text("stale answer from the previous turn"));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var turn = executor.ExecuteAsync("the task", ct: timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await turn.MoveNextAsync());
            Assert.Equal("recovered_answer", turn.Current.EventType);

            var result = await executor.TryInjectMessageAsync("early steer");
            Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, result.Status);
            Assert.Contains("Mid-turn injection refused: gate_closed", logs.Information);
            Assert.Equal("", stdin.ToString());

            var next = turn.MoveNextAsync().AsTask();
            while (stdin.ToString().Length == 0)
                await Task.Delay(10, timeout.Token);
            var first = Assert.Single(Lines(stdin));
            Assert.Contains("the task", first, StringComparison.Ordinal);
            Assert.False(executor.TurnCommittedToFinalAnswerForTests);
            Assert.False(executor.CurrentTurnResultSeenForTests);

            channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "answer" });
            Assert.True(await next);
            while (await turn.MoveNextAsync()) { }
            AssertClosed(executor);
        }
        finally { KillQuietly(process); }
    }

    /// <summary>
    /// A stdin stand-in whose line write and/or flush signal entry and then wait to be released,
    /// so a test can hold an injection mid-write (AC7e) or mid-flush (AC7f).
    /// </summary>
    private sealed class BlockingTextWriter : TextWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _buffer = new();

        public bool BlockWrite { get; init; }
        public bool BlockFlush { get; init; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Encoding Encoding => Encoding.UTF8;

        public string[] Lines
        {
            get { lock (_buffer) return _buffer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries); }
        }

        public void Release() => _release.TrySetResult();

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            WriteEntered.TrySetResult();
            if (BlockWrite)
                await _release.Task;
            lock (_buffer) _buffer.Append(buffer).Append(Environment.NewLine);
        }

        public override async Task FlushAsync()
        {
            FlushEntered.TrySetResult();
            if (BlockFlush)
                await _release.Task;
        }
    }

    private static List<string?> FinalResults(IEnumerable<AgentProgress> events) =>
        events.Where(p => p.FinalResult is not null).Select(p => p.FinalResult).ToList();

    private static ClaudeStreamEvent Init() => new() { Type = "system", Subtype = "init" };

    private static ClaudeStreamEvent Text(string text) => new()
    {
        Type = "assistant",
        Message = new ClaudeMessage { Content = [new ClaudeContentBlock { Type = "text", Text = text }] },
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));

        public ClaudeExecutor Executor { get; }
        public Channel<ClaudeStreamEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<ClaudeStreamEvent>();

        private Fixture(Process process, ClaudeExecutor executor)
        {
            _process = process;
            Executor = executor;
        }

        public static Fixture Start(TimeSpan? startWait = null, ClaudeExecutorLogRecorder? logs = null)
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/cat",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });
            var executor = new ClaudeExecutor(options, logs ?? (Microsoft.Extensions.Logging.ILogger<ClaudeExecutor>)NullLogger<ClaudeExecutor>.Instance,
                new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
            if (startWait is { } wait)
                executor.InjectedTurnStartWait = wait;
            var fixture = new Fixture(process, executor);
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);
            executor.SetEventChannelForTests(fixture.Channel);
            return fixture;
        }

        public void Write(ClaudeStreamEvent evt) => Channel.Writer.TryWrite(evt);

        /// <summary>The next line /bin/cat echoes back: what the executor wrote to stdin.</summary>
        public async Task<string?> ReadEchoAsync() => await _process.StandardOutput.ReadLineAsync(_timeout.Token);

        /// <summary>Waits until the turn's reader has taken every scripted event and parsed it.</summary>
        public async Task WaitForParsedAsync()
        {
            while (Channel.Reader.Count > 0)
                await Task.Delay(10, _timeout.Token);
            await Task.Delay(50, _timeout.Token);
        }

        public CancellationToken Timeout => _timeout.Token;

        /// <summary>One ExecuteAsync turn: send, then the scripted init, answer text and result.</summary>
        public async Task<List<AgentProgress>> RunTurnAsync(string message, string answer, Task? beforeSend = null)
        {
            var turn = Task.Run(async () =>
            {
                if (beforeSend is not null) await beforeSend;
                var events = new List<AgentProgress>();
                await foreach (var p in Executor.ExecuteAsync(message, ct: _timeout.Token))
                    events.Add(p);
                return events;
            });
            // /bin/cat echoes the actual send only after ExecuteAsync drained stale events.
            // A fixed delay can inject the answer before a busy worker starts, losing it to the drain.
            Assert.NotNull(await _process.StandardOutput.ReadLineAsync(_timeout.Token));
            Write(Init());
            Write(Text(answer));
            Write(new ClaudeStreamEvent { Type = "result", Result = answer });
            return await turn;
        }

        public Task<List<(AgentProgress Progress, long At)>> ReadInjectedAsync(int injected) => Task.Run(async () =>
        {
            var answers = new List<(AgentProgress, long)>();
            await foreach (var p in Executor.ReadInjectedTurnAnswersAsync(injected, _timeout.Token))
                answers.Add((p, Stopwatch.GetTimestamp()));
            return answers;
        });

        public async ValueTask DisposeAsync()
        {
            _timeout.Dispose();
            try { _process.Kill(); } catch (InvalidOperationException) { }
            _process.Dispose();
            await Task.CompletedTask;
        }
    }
}
