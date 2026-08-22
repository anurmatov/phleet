using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// Verifies that a turn the executor flagged as an error is never reported as a success.
///
/// The gap these cover (issue #225): a "result" event with IsError set but no text fell
/// through to the "Done! (no text output)" branch, which invoked OnTaskCompleted with
/// isError=false. A dead executor — expired auth after a host power-off, a crashed
/// app-server — therefore looked identical to a healthy idle one at every layer above.
/// </summary>
public class TaskFailureReportingTests
{
    private static async IAsyncEnumerable<AgentProgress> YieldResult(string? finalResult, bool isError)
    {
        yield return new AgentProgress
        {
            Summary = finalResult ?? "result",
            EventType = "result",
            FinalResult = finalResult,
            IsErrorResult = isError,
        };
        await Task.CompletedTask;
    }

    private static TaskManager BuildManager(IAgentExecutor executor, IMessageSink sink)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp" });
        var tm = new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        tm.Sink = sink;
        return tm;
    }

    /// <summary>Runs one task to completion and returns what OnTaskCompleted reported.</summary>
    private static async Task<(string Text, bool IsError)> RunTask(long chatId, string? finalResult, bool isError)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldResult(finalResult, isError));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);

        var completed = new TaskCompletionSource<(string, bool)>();
        tm.OnTaskCompleted += (_, text, _, _, err, _, _) => completed.TrySetResult((text, err));

        _ = tm.StartTask(chatId: chatId, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.UserMessage);

        return await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ErrorResult_WithNoOutput_IsReportedAsFailure()
    {
        var (text, isError) = await RunTask(chatId: 101, finalResult: null, isError: true);

        Assert.True(isError, "a turn the executor flagged as an error must not complete with isError=false");
        Assert.DoesNotContain("Done!", text);
        Assert.Contains("Task failed", text);
    }

    [Fact]
    public async Task ErrorResult_WithNoOutput_TellsTheUserItFailed()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldResult(null, isError: true));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);

        var completed = new TaskCompletionSource();
        tm.OnTaskCompleted += (_, _, _, _, _, _, _) => completed.TrySetResult();

        _ = tm.StartTask(chatId: 102, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.UserMessage);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sink.Received().SendTextAsync(
            102,
            Arg.Is<string>(s => s.Contains("Task failed") && !s.Contains("Done!")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanResult_WithNoOutput_StillReportsSuccess()
    {
        var (text, isError) = await RunTask(chatId: 103, finalResult: null, isError: false);

        Assert.False(isError);
        Assert.Contains("Done!", text);
    }

    [Fact]
    public async Task ErrorResult_WithText_DoesNotClaimTheCauseWasATurnLimit()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => YieldResult("Invalid API key · Please run /login", isError: true));

        var sink = Substitute.For<IMessageSink>();
        var tm = BuildManager(executor, sink);

        var completed = new TaskCompletionSource();
        tm.OnTaskCompleted += (_, _, _, _, _, _, _) => completed.TrySetResult();

        _ = tm.StartTask(chatId: 104, task: "ping", displayText: "ping",
            isSessionTask: false, source: TaskSource.UserMessage);

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The marker is appended for every IsErrorResult cause — auth failure, RPC error,
        // crash — so it must not name one of them.
        await sink.Received().SendTextAsync(
            104,
            Arg.Is<string>(s => s.Contains("[incomplete") && !s.Contains("hit limit")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanResult_WithText_CarriesNoIncompleteMarker()
    {
        var (text, isError) = await RunTask(chatId: 105, finalResult: "all done", isError: false);

        Assert.False(isError);
        Assert.DoesNotContain("[incomplete", text);
    }
}
