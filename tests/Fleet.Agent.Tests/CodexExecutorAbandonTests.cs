using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public class CodexExecutorAbandonTests
{
    [Theory]
    [InlineData("break")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task ExecuteAsync_ConsumerExit_InterruptsOnceAndNextTurnIsHealthy(string exit)
    {
        await using var server = new AbandonAppServer();
        var clock = Stopwatch.StartNew();
        var error = exit == "cancel" ? (Exception)new OperationCanceledException("consumer") : new HttpRequestException("consumer");
        async Task Consume()
        {
            await foreach (var _ in server.Executor.ExecuteAsync("first"))
            {
                if (exit == "break") break;
                throw error;
            }
        }
        if (exit == "break") await Consume();
        else Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(Consume));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Null(server.Executor.ActiveTurnIdForTests);
        Assert.False(server.Executor.RestartRequestedForTests);
        Assert.Single(server.Requests, r => (string?)r["method"] == "turn/interrupt");
        Assert.Contains(server.Logs, l => l.Contains("reason=consumer_exit") && l.Contains("drain=completed"));
        await AssertHealthyAsync(server);
        Assert.Single(server.Requests, r => (string?)r["method"] == "turn/start" && (string?)r["params"]?["input"]?[0]?["text"] == "first");
    }

    [Fact]
    public async Task ExecuteAsync_CleanupInFlight_HoldsLockAndRefusesSteering()
    {
        await using var server = new AbandonAppServer();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnInterrupt = async id => { await finish.Task; await server.NotifyAsync(AbandonAppServer.Completed(id, "ignored")); };
        var first = server.Executor.ExecuteAsync("first").GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        var cleanup = first.DisposeAsync().AsTask();
        await server.InterruptWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.Executor.TurnLockForTests.CurrentCount);
        Assert.Equal(MidTurnInjectionStatus.NoActiveTurn, (await server.Executor.TryInjectMessageAsync("correction")).Status);
        var next = CollectAsync(server.Executor.ExecuteAsync("next"));
        Assert.False(next.IsCompleted);
        Assert.Single(server.Requests, r => (string?)r["method"] == "turn/start");
        finish.SetResult();
        await cleanup;
        Assert.Equal("answer-2", Assert.Single(await next).FinalResult);
        Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/steer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupInFlight_OppositeTurnStart_WaitsAndKeepsItsOwnFrames(bool abandonedCommand)
    {
        await using var server = new AbandonAppServer();
        server.OnTurn = async id =>
        {
            await server.NotifyAsync(AbandonAppServer.Started(id));
            if (id == "turn-2")
            {
                await server.NotifyAsync(new JsonObject
                {
                    ["method"] = "item/agentMessage/delta",
                    ["params"] = new JsonObject { ["turnId"] = id, ["delta"] = "own-progress" },
                });
                await server.NotifyAsync(AbandonAppServer.Completed(id, "own-answer"));
            }
        };
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnInterrupt = async id =>
        {
            await finish.Task;
            await server.NotifyAsync(AbandonAppServer.Completed(id, "abandoned-answer"));
        };
        var first = (abandonedCommand ? server.Executor.SendCommandAsync("first-command")
            : server.Executor.ExecuteAsync("first-task")).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        var cleanup = first.DisposeAsync().AsTask();
        try
        {
            await server.InterruptWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = CollectAsync(abandonedCommand ? server.Executor.ExecuteAsync("next-task")
                : server.Executor.SendCommandAsync("next-command"));
            Assert.False(next.IsCompleted);
            Assert.Single(server.Requests, r => (string?)r["method"] is "turn/start" or "thread/shellCommand");
            Assert.Equal(0, server.Executor.TurnLockForTests.CurrentCount);
            finish.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            var progress = await next.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(progress, p => p.Summary == "own-progress");
            Assert.Equal("own-answer", Assert.Single(progress, p => p.FinalResult is not null).FinalResult);
            Assert.DoesNotContain(progress, p => p.Summary.Contains("abandoned"));
            Assert.False(server.Executor.RestartRequestedForTests);
            Assert.Null(server.Executor.CommandTurnIdForTests);
        }
        finally
        {
            finish.TrySetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MalformedCompletion_ReturnsErrorWithoutRestart(bool nonObject)
    {
        await using var server = new AbandonAppServer();
        server.OnTurn = id => server.NotifyAsync(id == "turn-1"
            ? new JsonObject { ["method"] = "turn/completed", ["params"] = new JsonObject { ["turnId"] = id, ["turn"] = nonObject ? JsonValue.Create("invalid") : null } }
            : AbandonAppServer.Completed(id, "answer-2"));
        var result = Assert.Single(await CollectAsync(server.Executor.ExecuteAsync("first")));
        Assert.Equal("Codex turn ended with a malformed completion", result.FinalResult);
        Assert.True(result.IsErrorResult);
        Assert.Null(server.Executor.ActiveTurnIdForTests);
        Assert.False(server.Executor.RestartRequestedForTests);
        Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
        await AssertHealthyAsync(server);
    }

    [Fact]
    public async Task ExecuteAsync_ProducerException_LogsReasonAndPreservesException()
    {
        await using var server = new AbandonAppServer();
        server.OnTurn = id => server.NotifyAsync(id == "turn-1"
            ? new JsonObject { ["method"] = "item/agentMessage/delta", ["params"] = new JsonObject { ["turnId"] = id, ["delta"] = 42 } }
            : AbandonAppServer.Completed(id, "answer-2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(server.Executor.ExecuteAsync("first")));
        Assert.Contains(server.Logs, l => l.Contains("reason=producer_exception"));
        Assert.False(server.Executor.RestartRequestedForTests);
        await AssertHealthyAsync(server);
    }

    [Fact]
    public async Task SendCommandAsync_EarlyDispose_ClearsBothIdsAndNextTaskIsHealthy()
    {
        await using var server = new AbandonAppServer();
        server.OnTurn = id => server.NotifyAsync(id == "turn-1" ? AbandonAppServer.Started(id) : AbandonAppServer.Completed(id, "answer-2"));
        await foreach (var _ in server.Executor.SendCommandAsync("example")) break;
        Assert.Null(server.Executor.ActiveTurnIdForTests);
        Assert.Null(server.Executor.CommandTurnIdForTests);
        Assert.Contains(server.Logs, l => l.Contains("path=run") && l.Contains("reason=consumer_exit"));
        await AssertHealthyAsync(server);
    }

    [Fact]
    public async Task SendCommandAsync_LiveTask_RefusesWithoutInterruptOrRestart()
    {
        await using var server = new AbandonAppServer();
        await using var first = server.Executor.ExecuteAsync("first").GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(server.Executor.SendCommandAsync("example")));
        Assert.Contains("refused to start a shell command", error.Message);
        Assert.False(server.Executor.RestartRequestedForTests);
        Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
        await server.NotifyAsync(AbandonAppServer.Completed("turn-1", "own"));
        Assert.True(await first.MoveNextAsync());
        Assert.Equal("own", first.Current.FinalResult);
        await first.DisposeAsync();
        await AssertHealthyAsync(server);
    }

    [Fact]
    public async Task ExecuteAsync_LiveCommand_RefusesAndCommandCompletes()
    {
        await using var server = new AbandonAppServer();
        server.OnTurn = id => server.NotifyAsync(id == "turn-1" ? AbandonAppServer.Started(id) : AbandonAppServer.Completed(id, "answer-2"));
        await using var command = server.Executor.SendCommandAsync("example").GetAsyncEnumerator();
        Assert.True(await command.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(server.Executor.ExecuteAsync("blocked")));
        Assert.False(server.Executor.RestartRequestedForTests);
        Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
        await server.NotifyAsync(AbandonAppServer.Completed("turn-1", "command-answer"));
        Assert.True(await command.MoveNextAsync());
        Assert.Equal("command-answer", command.Current.FinalResult);
        await command.DisposeAsync();
        await AssertHealthyAsync(server);
    }

    [Fact]
    public async Task ExecuteAsync_DisposeAfterNewCommandStarts_DoesNotInterruptCommand()
    {
        await using var server = new AbandonAppServer();
        server.OnTurn = id => server.NotifyAsync(id == "turn-1" ? AbandonAppServer.Completed(id, "task-answer") : AbandonAppServer.Started(id));
        var task = server.Executor.ExecuteAsync("first").GetAsyncEnumerator();
        Assert.True(await task.MoveNextAsync());
        await using var command = server.Executor.SendCommandAsync("example").GetAsyncEnumerator();
        Assert.True(await command.MoveNextAsync());
        await task.DisposeAsync();
        Assert.Equal("turn-2", server.Executor.ActiveTurnIdForTests);
        Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
        await server.NotifyAsync(AbandonAppServer.Completed("turn-2", "command-answer"));
        Assert.True(await command.MoveNextAsync());
        Assert.Equal("command-answer", command.Current.FinalResult);
    }

    [Fact]
    public async Task SendCommandAsync_DisposeOldTerminal_DoesNotAbandonNewLiveCommand()
    {
        await using var server = new AbandonAppServer();
        var first = server.Executor.SendCommandAsync("first").GetAsyncEnumerator();
        // A turn/started is needed when the terminal is the first command frame.
        server.OnTurn = async id =>
        {
            await server.NotifyAsync(AbandonAppServer.Started(id));
            if (id == "turn-1") await server.NotifyAsync(AbandonAppServer.Completed(id, "first-command"));
        };
        Assert.True(await first.MoveNextAsync());
        Assert.True(await first.MoveNextAsync());
        Assert.Equal("first-command", first.Current.FinalResult);
        await using var second = server.Executor.SendCommandAsync("second").GetAsyncEnumerator();
        Assert.True(await second.MoveNextAsync());
        await first.DisposeAsync();
        Assert.Equal("turn-2", server.Executor.ActiveTurnIdForTests);
        Assert.Equal("turn-2", server.Executor.CommandTurnIdForTests);
        Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
        await server.NotifyAsync(AbandonAppServer.Completed("turn-2", "second-command"));
        Assert.True(await second.MoveNextAsync());
        Assert.Equal("second-command", second.Current.FinalResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_SendLockUnavailable_SkipsDrainAndDefersRestartWithinBudget(bool command)
    {
        var starts = 0;
        var ledger = new TurnOriginLedger();
        await using var server = new AbandonAppServer(ledger, _ => { starts++; return StartFreshPeer(); });
        server.OnTurn = id => server.NotifyAsync(AbandonAppServer.Started(id));
        using var caller = new CancellationTokenSource();
        var first = (command ? server.Executor.SendCommandAsync("first", caller.Token)
            : server.Executor.ExecuteAsync("first", ct: caller.Token)).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        await server.Executor.SendLockForTests.WaitAsync();
        try
        {
            caller.Cancel();
            var clock = Stopwatch.StartNew();
            await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.InRange(clock.Elapsed.TotalSeconds, 2.8, 5);
            Assert.True(server.Executor.RestartRequestedForTests);
            Assert.Null(server.Executor.ActiveTurnIdForTests);
            Assert.Null(server.Executor.CommandTurnIdForTests);
            Assert.Equal(1, server.Executor.TurnLockForTests.CurrentCount);
            Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
            Assert.Contains(server.Logs, l => l.Contains("cleanupLock=send") && l.Contains("restartRequested=true"));
            Assert.Contains(ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && !i.LockHeld && i.End is null);
        }
        finally { server.Executor.SendLockForTests.Release(); }
        var next = await CollectAsync(server.Executor.ExecuteAsync("next"));
        Assert.Equal("answer-2", Assert.Single(next).FinalResult);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task SendCommandAsync_CleanupTurnLockUnavailable_DoesNotInvertLocksOrDrain()
    {
        var starts = 0;
        await using var server = new AbandonAppServer(starter: _ => { starts++; return StartFreshPeer(); });
        server.OnTurn = id => server.NotifyAsync(AbandonAppServer.Started(id));
        var first = server.Executor.SendCommandAsync("first").GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        await server.Executor.TurnLockForTests.WaitAsync();
        try
        {
            await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, server.Executor.SendLockForTests.CurrentCount);
            Assert.Null(server.Executor.ActiveTurnIdForTests);
            Assert.Null(server.Executor.CommandTurnIdForTests);
            Assert.True(server.Executor.RestartRequestedForTests);
            Assert.DoesNotContain(server.Requests, r => (string?)r["method"] == "turn/interrupt");
            Assert.Contains(server.Logs, l => l.Contains("cleanupLock=turn"));
        }
        finally { server.Executor.TurnLockForTests.Release(); }
        var next = await CollectAsync(server.Executor.ExecuteAsync("next"));
        Assert.Equal("answer-2", Assert.Single(next).FinalResult);
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("missing_stdin")]
    [InlineData("throwing_stdin")]
    [InlineData("blocked_stdin")]
    [InlineData("missing_channel")]
    [InlineData("closed_channel")]
    public async Task ExecuteAsync_DrainCannotFinish_RestartsOnceAndNeverMasksConsumerError(string fault)
    {
        var restarts = 0;
        var time = new TestTime();
        var ledger = new TurnOriginLedger(time: time);
        await using var server = new AbandonAppServer(ledger, _ => { restarts++; return StartFreshPeer(); });
        server.OnInterrupt = _ => Task.CompletedTask;
        using var pending = ledger.Pending(OutboundOrigin.Human);
        var first = server.Executor.ExecuteAsync("first").GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        var blockedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        switch (fault)
        {
            case "missing_stdin": server.Executor.SetStdinForTests(null); break;
            case "throwing_stdin": server.Executor.SetStdinForTests(new AbandonAppServer.RpcWriter(_ => throw new IOException("scripted write failure"))); break;
            case "blocked_stdin": server.Executor.SetStdinForTests(new AbandonAppServer.RpcWriter(_ => blockedWrite.Task)); break;
            case "missing_channel": server.Executor.SetNotificationChannelForTests(null); break;
            case "closed_channel":
                var closed = Channel.CreateUnbounded<JsonObject>();
                closed.Writer.TryComplete();
                server.Executor.SetNotificationChannelForTests(closed);
                break;
        }
        var clock = Stopwatch.StartNew();
        await first.DisposeAsync();
        blockedWrite.TrySetResult();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        if (fault is "timeout" or "missing_stdin" or "throwing_stdin" or "blocked_stdin")
            Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.8));
        Assert.Null(server.Executor.ActiveTurnIdForTests);
        Assert.True(server.Executor.RestartRequestedForTests);
        var expected = fault is "missing_stdin" or "throwing_stdin" or "blocked_stdin" ? "write_failed"
            : fault is "missing_channel" or "closed_channel" ? "channel_closed" : "timeout";
        Assert.Contains(server.Logs, l => l.Contains("drain=" + expected));
        Assert.Contains(ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && !i.LockHeld && i.End is null);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ToolSendAttribution.ExcludedOrigin, ledger.Attribute(time.GetUtcNow() - TimeSpan.FromSeconds(2)).Attribution);
        await server.NotifyAsync(AbandonAppServer.Frame("item/completed", "turn-1",
            new JsonObject { ["type"] = "agentMessage", ["text"] = "late orphan text" }));
        var next = await CollectAsync(server.Executor.ExecuteAsync("next"));
        Assert.Equal("answer-2", Assert.Single(next).FinalResult);
        Assert.Equal(1, restarts);
        Assert.False(server.Executor.RestartRequestedForTests);
        await server.NotifyAsync(AbandonAppServer.Completed("turn-1", "late orphan terminal"));
        var following = await CollectAsync(server.Executor.ExecuteAsync("following"));
        Assert.Equal("answer-2", Assert.Single(following).FinalResult);
        Assert.Equal(1, restarts);
        Assert.DoesNotContain(next, p => p.Summary.Contains("orphan") || p.EventType == "recovered_answer");
        Assert.DoesNotContain(ledger.SnapshotForTests(), i => i.Origin == TurnOrigin.Unknown && !i.LockHeld && i.End is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_StaleTurn_LogsRecoveryAndForcesExactlyOneRestart(bool command)
    {
        var starts = 0;
        await using var server = new AbandonAppServer(starter: _ => { starts++; return StartFreshPeer(); });
        server.Executor.SetThreadStateForTests("thread-1", "stale-turn");
        var result = await CollectAsync(command ? server.Executor.SendCommandAsync("example") : server.Executor.ExecuteAsync("task"));
        Assert.Equal("answer-2", result.Last().FinalResult);
        Assert.Equal(1, starts);
        Assert.Contains(server.Logs, l => l.Contains("codex_stale_turn_at_start") && l.Contains(command ? "path=run" : "path=task"));
        Assert.Null(server.Executor.CommandTurnIdForTests);
        var next = await CollectAsync(server.Executor.ExecuteAsync("next"));
        Assert.Equal("answer-2", Assert.Single(next).FinalResult);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyStream_LogsStreamEndedAndNextTurnIsHealthy()
    {
        var starts = 0;
        await using var server = new AbandonAppServer(starter: _ => { starts++; return StartFreshPeer(); });
        server.OnTurn = _ => { server.Executor.SetNotificationChannelForTests(null); return Task.CompletedTask; };
        Assert.Empty(await CollectAsync(server.Executor.ExecuteAsync("first")));
        Assert.Contains(server.Logs, l => l.Contains("reason=stream_ended"));
        Assert.True(server.Executor.RestartRequestedForTests);
        var next = await CollectAsync(server.Executor.ExecuteAsync("next"));
        Assert.Equal("answer-2", Assert.Single(next).FinalResult);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupWriteThrows_PreservesConsumersOriginalException()
    {
        await using var server = new AbandonAppServer();
        var original = new HttpRequestException("consumer send failed");
        var clock = Stopwatch.StartNew();
        var caught = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in server.Executor.ExecuteAsync("first"))
            {
                server.Executor.SetStdinForTests(new AbandonAppServer.RpcWriter(_ => throw new IOException("write failed")));
                throw original;
            }
        });
        Assert.Same(original, caught);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(server.Executor.RestartRequestedForTests);
    }

    [Fact]
    public async Task SendCommandAsync_DiscoveredId_ConsumerExitClearsCommandMarker()
    {
        await using var server = new AbandonAppServer();
        // No turn/started: the item frame resolves the id through the second discovery branch.
        await foreach (var _ in server.Executor.SendCommandAsync("example")) break;
        Assert.Null(server.Executor.CommandTurnIdForTests);
        Assert.Null(server.Executor.ActiveTurnIdForTests);
        await AssertHealthyAsync(server);
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    /// <summary>A fresh stdio peer exercises real startup, initialize and thread/start after recovery.</summary>
    private static Process StartFreshPeer()
    {
        var psi = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("""
            import sys, json
            for line in sys.stdin:
                q = json.loads(line)
                method = q.get('method')
                if 'id' not in q:
                    continue
                result = {}
                if method == 'thread/start':
                    result = {'thread': {'id': 'fresh-thread', 'ephemeral': True}}
                if method == 'turn/start':
                    result = {'turn': {'id': 'fresh-turn'}}
                print(json.dumps({'id': q['id'], 'result': result}), flush=True)
                if method == 'thread/shellCommand':
                    print(json.dumps({'method':'turn/started', 'params':{'turn':{'id':'fresh-turn'}}}), flush=True)
                if method in ('turn/start', 'thread/shellCommand'):
                    print(json.dumps({'method':'turn/completed', 'params':{'turn':{
                        'id':'fresh-turn', 'status':'completed', 'items':[
                        {'type':'agentMessage','text':'answer-2'}]}}}), flush=True)
            """);
        return Process.Start(psi)!;
    }

    internal static async Task AssertHealthyAsync(AbandonAppServer server)
    {
        var result = Assert.Single(await CollectAsync(server.Executor.ExecuteAsync("next")));
        Assert.Equal("answer-2", result.FinalResult);
        Assert.Null(server.Executor.ActiveTurnIdForTests);
    }
    internal static async Task<List<AgentProgress>> CollectAsync(IAsyncEnumerable<AgentProgress> stream)
    {
        var result = new List<AgentProgress>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }
}
