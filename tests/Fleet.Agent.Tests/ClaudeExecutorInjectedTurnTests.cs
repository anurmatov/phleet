using System.Diagnostics;
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

        public static Fixture Start(TimeSpan? startWait = null)
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/cat",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });
            var executor = new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance,
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

        /// <summary>One ExecuteAsync turn: send, then the scripted init, answer text and result.</summary>
        public async Task<List<AgentProgress>> RunTurnAsync(string message, string answer)
        {
            var turn = Task.Run(async () =>
            {
                var events = new List<AgentProgress>();
                await foreach (var p in Executor.ExecuteAsync(message, ct: _timeout.Token))
                    events.Add(p);
                return events;
            });
            // ExecuteAsync drains the channel and writes to stdin before it reads.
            await Task.Delay(100, _timeout.Token);
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
