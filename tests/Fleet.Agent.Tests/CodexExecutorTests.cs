using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public class CodexExecutorTests
{
    private static CodexExecutor CreateExecutor(
        string attachmentDir = "/workspace/attachments",
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? processStarter = null,
        ILogger<CodexExecutor>? logger = null)
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/workspace",
        });
        var telegramOptions = Options.Create(new TelegramOptions
        {
            AttachmentDir = attachmentDir,
        });
        var promptBuilder = new PromptBuilder(agentOptions, NullLogger<PromptBuilder>.Instance);
        var resolvedLogger = logger ?? NullLogger<CodexExecutor>.Instance;
        return processStarter is null
            ? new CodexExecutor(agentOptions, telegramOptions, promptBuilder, resolvedLogger)
            : new CodexExecutor(agentOptions, telegramOptions, promptBuilder, resolvedLogger, processStarter);
    }

    /// <summary>Captures log messages for assertion in tests.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task EnsureProcessReady_StartFailureExhaustsRetryBudget()
    {
        var attempts = 0;
        var executor = CreateExecutor(processStarter: _ =>
        {
            attempts++;
            return null;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.EnsureProcessReadyForTestsAsync());

        Assert.Contains("after 3 attempts", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StreamTurn_IgnoresStaleTurnNotifications()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-new");

        await channel.Writer.WriteAsync(TurnCompleted("turn-old", "stale"));
        await channel.Writer.WriteAsync(TurnStarted("turn-new"));
        await channel.Writer.WriteAsync(TurnCompleted("turn-new", "fresh"));
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-new"));

        Assert.Equal(2, progress.Count);
        Assert.Equal("system", progress[0].EventType);
        Assert.Equal("result", progress[1].EventType);
        Assert.Equal("fresh", progress[1].FinalResult);
        Assert.Null(executor.ActiveTurnIdForTests);
    }

    [Fact]
    public async Task StreamTurn_ProcessCrashMidTurn_ReturnsErrorAndClearsActiveTurn()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-1"));

        var final = Assert.Single(progress);
        Assert.True(final.IsErrorResult);
        Assert.Contains("exited unexpectedly", final.FinalResult);
        Assert.Null(executor.ActiveTurnIdForTests);
    }

    [Fact]
    public async Task StreamTurn_Cancelled_DrainsInterruptedTurnAndClearsActiveTurn()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");

        await channel.Writer.WriteAsync(TokenUsage("turn-1", 12, 7));
        await channel.Writer.WriteAsync(TurnCompleted("turn-1", "", "interrupted"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in executor.StreamTurnForTests("turn-1", cts.Token))
            {
            }
        });

        Assert.Null(executor.ActiveTurnIdForTests);
        Assert.False(channel.Reader.TryRead(out _));
    }

    private static async Task<List<AgentProgress>> CollectAsync(IAsyncEnumerable<AgentProgress> progressStream)
    {
        var progress = new List<AgentProgress>();
        await foreach (var item in progressStream)
            progress.Add(item);
        return progress;
    }

    private static JsonObject TurnStarted(string turnId) =>
        new()
        {
            ["method"] = "turn/started",
            ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject
                {
                    ["id"] = turnId,
                },
            },
        };

    private static JsonObject TurnCompleted(string turnId, string assistantText, string status = "completed") =>
        new()
        {
            ["method"] = "turn/completed",
            ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject
                {
                    ["id"] = turnId,
                    ["status"] = status,
                    ["durationMs"] = 1,
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "agentMessage",
                            ["text"] = assistantText,
                        },
                    },
                },
            },
        };

    // Real-wire protocol: text arrives via item/completed(agentMessage) before turn/completed.
    // turn.items in turn/completed is always empty on the live codex app-server.
    [Fact]
    public async Task StreamTurn_AgentMessageViaItemCompleted_UsesAccumulatedText()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");

        await channel.Writer.WriteAsync(TurnStarted("turn-1"));
        await channel.Writer.WriteAsync(ItemCompleted("turn-1", "agentMessage", "hello from codex"));
        // turn/completed with no items — matches real wire protocol
        await channel.Writer.WriteAsync(TurnCompletedNoItems("turn-1"));
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-1"));

        var final = progress.Last(p => p.FinalResult is not null);
        Assert.Equal("hello from codex", final.FinalResult);
        Assert.False(final.IsErrorResult);
    }

    // Fallback: when no item/completed(agentMessage) was received, use turn.items.
    // This matches the test fixture shape and guards against future protocol changes.
    [Fact]
    public async Task StreamTurn_AgentMessageInTurnItems_FallsBackToExtractAssistantText()
    {
        var executor = CreateExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("thread-1", "turn-1");

        // TurnCompleted puts agentMessage in turn.items — the fallback path
        await channel.Writer.WriteAsync(TurnCompleted("turn-1", "fallback text"));
        channel.Writer.TryComplete();

        var progress = await CollectAsync(executor.StreamTurnForTests("turn-1"));

        var final = progress.Single(p => p.FinalResult is not null);
        Assert.Equal("fallback text", final.FinalResult);
        Assert.False(final.IsErrorResult);
    }

    private static JsonObject TokenUsage(string turnId, int inputTokens, int outputTokens) =>
        new()
        {
            ["method"] = "thread/tokenUsage/updated",
            ["params"] = new JsonObject
            {
                ["turnId"] = turnId,
                ["tokenUsage"] = new JsonObject
                {
                    ["last"] = new JsonObject
                    {
                        ["inputTokens"] = inputTokens,
                        ["outputTokens"] = outputTokens,
                    },
                },
            },
        };

    // Matches the real wire protocol: item/completed with the given item type and text.
    private static JsonObject ItemCompleted(string turnId, string itemType, string text) =>
        new()
        {
            ["method"] = "item/completed",
            ["params"] = new JsonObject
            {
                ["turnId"] = turnId,
                ["item"] = new JsonObject
                {
                    ["type"] = itemType,
                    ["text"] = text,
                },
            },
        };

    // turn/completed with no items array — matches the real wire protocol where the
    // assistant text arrives via item/completed(agentMessage) before turn/completed.
    private static JsonObject TurnCompletedNoItems(string turnId, string status = "completed") =>
        new()
        {
            ["method"] = "turn/completed",
            ["params"] = new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject
                {
                    ["id"] = turnId,
                    ["status"] = status,
                    ["durationMs"] = 1,
                    // Intentionally no "items" key — production codex app-server omits it.
                },
            },
        };

    // Concurrent ExecuteAsync calls must queue rather than throw. We hold _turnLock externally,
    // start an ExecuteAsync iteration in the background (which blocks waiting for the lock),
    // then release — verifying the second caller got the lock and proceeded (failing on process
    // startup as expected, but NOT throwing the old "refused to start a second turn" error).
    [Fact]
    public async Task ExecuteAsync_ConcurrentCalls_AreSerialized()
    {
        var executor = CreateExecutor(processStarter: _ => null);

        // Hold the turn lock externally so the background call has to wait.
        await executor.TurnLockForTests.WaitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? caughtEx = null;
        var backgroundTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in executor.ExecuteAsync("task", ct: cts.Token)) { }
            }
            catch (Exception ex)
            {
                caughtEx = ex;
            }
        });

        // Give the background task time to start and reach _turnLock.WaitAsync.
        await Task.Delay(50);
        Assert.False(backgroundTask.IsCompleted, "ExecuteAsync should be waiting on _turnLock");

        // Release the lock — the background call should now proceed (and fail on process startup).
        executor.TurnLockForTests.Release();
        await backgroundTask;

        // The failure must be the startup-exhaustion error, not the old single-flight throw.
        Assert.NotNull(caughtEx);
        Assert.IsType<InvalidOperationException>(caughtEx);
        Assert.DoesNotContain("refused to start a second turn", caughtEx.Message);
        Assert.Contains("3 attempts", caughtEx.Message);
    }

    // BuildItemStartedProgress must emit a [codex tool_use:...] log line for every tool item,
    // mirroring ClaudeExecutor's tool-call logger for observability parity.
    [Fact]
    public void BuildItemStartedProgress_LogsToolUse()
    {
        var capturer = new CapturingLogger<CodexExecutor>();
        var executor = CreateExecutor(logger: capturer);

        var @params = new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["type"] = "commandExecution",
                ["command"] = "Bash",
                ["args"] = new JsonArray { "ls", "-la" },
            },
        };

        var progress = executor.BuildItemStartedProgressForTests(@params);

        Assert.NotNull(progress);
        Assert.Equal("tool_use", progress.EventType);

        Assert.Contains(capturer.Messages, m => m.Contains("[codex tool_use:Bash"));
    }
}
