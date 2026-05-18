using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public class CodexExecutorTests
{
    private static CodexExecutor CreateExecutor(
        string attachmentDir = "/workspace/attachments",
        Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? processStarter = null)
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
        return processStarter is null
            ? new CodexExecutor(agentOptions, telegramOptions, promptBuilder, NullLogger<CodexExecutor>.Instance)
            : new CodexExecutor(agentOptions, telegramOptions, promptBuilder, NullLogger<CodexExecutor>.Instance, processStarter);
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
}
