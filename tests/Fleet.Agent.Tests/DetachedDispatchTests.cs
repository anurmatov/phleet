using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// A dispatch nobody awaits must still be observed: when it throws, the message is gone, and the
/// only thing left to find is the log line.
/// </summary>
public class DetachedDispatchTests
{
    private const string Prompt = "PROMPT-TEXT-THAT-MUST-NOT-BE-LOGGED";
    private const string Display = "DISPLAY-TEXT-THAT-MUST-NOT-BE-LOGGED";

    [Fact]
    public async Task StartTaskDetached_WhenTheDispatchThrows_LogsIdentifiersAndLeavesTheAgentUsable()
    {
        var logger = new SnapshotLogger<TaskManager>();
        var executor = new AnsweringExecutor();
        var manager = BuildManager(executor, logger);
        manager.DispatchCheckpointForTest = checkpoint =>
        {
            if (checkpoint == "admission_idle") throw new InvalidOperationException("synthetic");
        };

        manager.StartTaskDetached(77, Prompt, Display, isSessionTask: true,
            source: TaskSource.Relay, relaySender: "peer", taskId: "wf-1/step");

        var fault = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("Task dispatch failed and the message was dropped", fault.Message);
        Assert.Contains("chat=77", fault.Message);
        Assert.Contains("source=Relay", fault.Message);
        Assert.Contains("taskId=wf-1/step", fault.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(Prompt) || e.Message.Contains(Display));

        // The failed dispatch holds nothing: no running turn, no task id, and the next one runs.
        Assert.False(manager.HasRunningTasks(77));
        Assert.Equal(0, manager.ActiveTaskIdCountForTest);
        manager.DispatchCheckpointForTest = null;
        Assert.Equal(TaskDispatchOutcome.Ran,
            await manager.StartTask(77, "next", "next", isSessionTask: true,
                source: TaskSource.Relay, relaySender: "peer", taskId: "wf-1/step"));
    }

    [Fact]
    public void StartTaskDetached_WhenTheDispatchSucceeds_LogsNoError()
    {
        var logger = new SnapshotLogger<TaskManager>();
        var manager = BuildManager(new AnsweringExecutor(), logger);

        manager.StartTaskDetached(77, Prompt, Display, isSessionTask: true);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void NoCallSiteDiscardsADispatch()
    {
        // `_ = _taskManager.StartTask(...)` is how a failed dispatch used to disappear. A caller
        // that cannot await uses StartTaskDetached; one that can, awaits.
        var root = AgentSourceRoot();
        var discarded = new Regex(@"_\s*=\s*_?[A-Za-z]*[Tt]askManager\.StartTask\(", RegexOptions.Compiled);
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => discarded.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string AgentSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Fleet.Agent")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Fleet.Agent");
    }

    private static TaskManager BuildManager(IAgentExecutor executor, ILogger<TaskManager> logger)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test",
            Role = "test",
            WorkDir = "/tmp",
            Provider = "claude",
        });
        return new TaskManager(options, executor, new SessionManager(), logger, sink: Substitute.For<IMessageSink>());
    }

    /// <summary>Safe to read while a background turn is still logging.</summary>
    private sealed class SnapshotLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class AnsweringExecutor : IAgentExecutor
    {
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new AgentProgress { EventType = "result", Summary = "ok", FinalResult = "ok" };
        }

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
