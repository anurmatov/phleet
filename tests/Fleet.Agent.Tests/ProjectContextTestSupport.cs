using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

/// <summary>
/// Shared scaffolding for the #347 project-context tests: a throwaway content root holding
/// <c>projects/&lt;p&gt;/full.md</c>, routing blocks built from placeholders only, a capturing
/// logger, and a per-turn releasable executor that emits <c>prompt_accepted</c> like the real ones.
/// </summary>
internal static class ProjectContextTestSupport
{
    public const string ProjectA = "project-a";
    public const string ProjectB = "project-b";
    public const string Repo = "org/app";
    public const string Workflow = "ExampleWorkflow";
    public const long Chat = -100000000001;
    public const string ChatValue = "-100000000001";

    /// <summary>The header the attacher writes in front of a project's full context.</summary>
    public static string BlockHeader(string project) => $"[project context: {project} · ";

    public static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    public static ContextAttachmentRequest Request(string project = ProjectA, int version = 3, string kind = "chat") =>
        new(project, version, kind);

    public static ProjectContextRoutingOptions Routing(
        IEnumerable<string> cardProjects,
        IEnumerable<(string Kind, string Value, string Project)> routes,
        IDictionary<string, string>? fullVersions = null)
    {
        var cards = cardProjects.ToList();
        return new ProjectContextRoutingOptions
        {
            CardProjects = cards,
            FullVersions = fullVersions is null
                ? cards.ToDictionary(p => p, _ => "3")
                : new Dictionary<string, string>(fullVersions),
            Routes = routes.Select(r => new ProjectContextRouteOptions { Kind = r.Kind, Value = r.Value, Project = r.Project }).ToList(),
        };
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!condition())
            await Task.Delay(10, cts.Token);
    }
}

/// <summary>A temp directory standing in for <c>AppContext.BaseDirectory</c>.</summary>
internal sealed class ProjectContextRoot : IDisposable
{
    public ProjectContextRoot() => Directory.CreateDirectory(Path);

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pcr-{Guid.NewGuid():N}");

    public ProjectContextRoot WithFull(string project, string content)
    {
        var dir = System.IO.Path.Combine(Path, "projects", project);
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "full.md"), content);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* teardown only */ }
    }
}

/// <summary>Records every formatted log line with its level. Thread-safe: turns log from pool threads.</summary>
internal sealed class ConcurrentCapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public IEnumerable<string> At(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => _entries.Enqueue((logLevel, formatter(state, exception)));
}

/// <summary>
/// Per-turn releasable executor for the delivery and ledger tests. Each <c>ExecuteAsync</c>
/// records its input, emits <c>prompt_accepted</c> (unless told to fail first), then waits for a
/// release. Warm state, session id and compaction epoch are settable so every ledger reset rule can
/// be driven from the test.
/// </summary>
internal sealed class LedgerTestExecutor : IAgentExecutor
{
    private readonly Channel<TurnEnd> _releases = Channel.CreateUnbounded<TurnEnd>();
    private readonly ConcurrentQueue<string> _executed = new();
    private readonly ConcurrentQueue<string> _injected = new();
    private int _acceptedCount;

    public enum TurnEnd { Completed, ProcessExit }

    public IReadOnlyList<string> ExecutedTasks => [.. _executed];
    public IReadOnlyList<string> InjectedTasks => [.. _injected];
    public int AcceptedCount => Volatile.Read(ref _acceptedCount);

    public volatile bool Warm = true;
    public volatile string? SessionId = "session-1";
    public int Epoch;

    /// <summary>Per turn: true → throw before prompt_accepted (a send failure).</summary>
    public ConcurrentQueue<bool> FailBeforeAccept { get; } = new();

    /// <summary>The event emitted as acceptance. Replaceable so a test can make it look forwardable.</summary>
    public Func<AgentProgress> PromptAcceptedEvent { get; set; } = AgentProgress.PromptAccepted;

    public MidTurnInjectionResult InjectionResult { get; set; } = MidTurnInjectionResult.Injected;

    public bool IsProcessWarm => Warm;
    public string? LastSessionId => SessionId;
    public int CompactionEpoch => Volatile.Read(ref Epoch);
    public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _executed.Enqueue(task);
        if (FailBeforeAccept.TryDequeue(out var fail) && fail)
            throw new IOException("synthetic stdin write failure");

        Interlocked.Increment(ref _acceptedCount);
        yield return PromptAcceptedEvent();

        var end = await _releases.Reader.ReadAsync(ct);
        if (end == TurnEnd.ProcessExit)
        {
            // The process died: whatever it held in context is gone.
            Warm = false;
            yield return new AgentProgress
            {
                EventType = "error",
                Summary = "synthetic process exit",
                IsSignificant = true,
                IsProcessExit = true,
                IsErrorResult = true,
            };
            yield break;
        }

        // A fixed answer, never an echo of the input: the input carries the attachment, and an
        // echo would carry it back out through the answer and read as a leak that is not one.
        yield return new AgentProgress { EventType = "result", Summary = "answer", FinalResult = "answer", IsSignificant = true };
    }

    public Task<MidTurnInjectionResult> TryInjectMessageAsync(
        string task, IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
    {
        if (InjectionResult.Status == MidTurnInjectionStatus.Injected)
            _injected.Enqueue(task);
        return Task.FromResult(InjectionResult);
    }

    public void ReleaseNextTurn(TurnEnd end = TurnEnd.Completed) => _releases.Writer.TryWrite(end);

    public Task WaitForExecuteCountAsync(int expected) =>
        ProjectContextTestSupport.WaitUntilAsync(() => _executed.Count >= expected);

    public Task WaitForAcceptedCountAsync(int expected) =>
        ProjectContextTestSupport.WaitUntilAsync(() => AcceptedCount >= expected);

    public Task StopProcessAsync() => Task.CompletedTask;
    public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
    public void RequestRestart() { }
    public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
