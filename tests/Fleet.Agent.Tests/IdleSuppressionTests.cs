using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// Verifies that the IDLE marker is suppressed for all task sources,
/// not just CheckIn. Covers the fix for issue #158.
/// </summary>
public class IdleSuppressionTests
{
    private static async IAsyncEnumerable<AgentProgress> YieldFinalResult(string? finalResult)
    {
        yield return new AgentProgress
        {
            Summary = "result",
            EventType = "result",
            FinalResult = finalResult,
        };
        await Task.CompletedTask;
    }

    private static TaskManager BuildManager(IAgentExecutor executor, IMessageSink sink)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", MaxConcurrentTasks = 5 });
        var tm = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        tm.Sink = sink;
        return tm;
    }

    /// <summary>Waits until the TaskManager reports no running tasks for the given chatId.</summary>
    private static async Task WaitForIdle(TaskManager tm, long chatId)
    {
        var tcs = new TaskCompletionSource();
        tm.OnStatusChanged += () =>
        {
            if (!tm.HasRunningTasks(chatId))
                tcs.TrySetResult();
        };
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IdleResult_FromUserMessageSource_IsNotSentToChat()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldFinalResult("IDLE"));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);
        var idle = WaitForIdle(tm, chatId: 1);

        tm.StartTask(chatId: 1, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.UserMessage);

        await idle;

        await sink.DidNotReceive().SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("IDLE")]
    [InlineData("  IDLE  ")]
    public async Task IdleResult_CaseAndWhitespaceVariants_AreAllSuppressed(string idleVariant)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldFinalResult(idleVariant));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);
        var idle = WaitForIdle(tm, chatId: 2);

        tm.StartTask(chatId: 2, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.UserMessage);

        await idle;

        await sink.DidNotReceive().SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonIdleResult_FromUserMessageSource_IsSentToChat()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldFinalResult("hello from agent"));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);
        var idle = WaitForIdle(tm, chatId: 3);

        tm.StartTask(chatId: 3, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.UserMessage);

        await idle;

        await sink.Received(1).SendTextAsync(3L,
            Arg.Is<string>(s => s.Contains("hello from agent")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckIn_IdleResult_IsStillSuppressed()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldFinalResult("IDLE"));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);
        var idle = WaitForIdle(tm, chatId: 4);

        tm.StartTask(chatId: 4, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.CheckIn);

        await idle;

        await sink.DidNotReceive().SendTextAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
