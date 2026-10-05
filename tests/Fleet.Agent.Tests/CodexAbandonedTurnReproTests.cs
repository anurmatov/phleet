using System.Collections.Concurrent;
using System.IO.Pipelines;
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

public class CodexAbandonedTurnReproTests
{
    [Fact]
    public async Task ExecuteAsync_ProgressSendFails_NextTaskCompletesWithoutReprovision()
    {
        var result = await RunSendFailureAsync();
        Assert.Equal(CompletionKind.Failed, result.First.Kind);
        Assert.Contains("scripted send failure", result.First.Text);
        // Before the fix this assertion prints the exact F7 state-corruption failure.
        Assert.True(result.Second.Kind == CompletionKind.Completed,
            $"Second task: {result.Second.Kind}: {result.Second.Text}");
        Assert.Contains("answer-2", result.Second.Text);
        Assert.Equal(1, result.Sink.FailedSends);
    }

    internal static async Task<ReproResult> RunSendFailureAsync()
    {
        await using var server = new AbandonAppServer();
        var sink = new ThrowOnceSink();
        var manager = new TaskManager(server.Options, server.Executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, sink: sink);
        var completions = Channel.CreateUnbounded<TaskCompletion>();
        var all = new ConcurrentQueue<TaskCompletion>();
        manager.OnTaskCompleted += (chat, text, _, _, _, _, _, kind) =>
        {
            var completion = new TaskCompletion(chat, text, kind);
            all.Enqueue(completion);
            completions.Writer.TryWrite(completion);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await manager.StartTask(101, "first", "first", isSessionTask: false);
        var first = await completions.Reader.ReadAsync(deadline.Token);
        await WaitIdleAsync(manager, 101, deadline.Token);
        await manager.StartTask(202, "second", "second", isSessionTask: false);
        var second = await completions.Reader.ReadAsync(deadline.Token);
        await WaitIdleAsync(manager, 202, deadline.Token);
        return new(first, second, all.ToArray(), sink, server.Requests.ToArray());
    }

    private static async Task WaitIdleAsync(TaskManager manager, long chat, CancellationToken ct)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (!manager.HasRunningTasks(chat)) idle.TrySetResult(); }
        manager.OnStatusChanged += Changed;
        try { Changed(); await idle.Task.WaitAsync(ct); }
        finally { manager.OnStatusChanged -= Changed; }
    }

    internal sealed record TaskCompletion(long Chat, string Text, CompletionKind Kind);
    internal sealed record ReproResult(TaskCompletion First, TaskCompletion Second,
        TaskCompletion[] All, ThrowOnceSink Sink, JsonObject[] Requests);

    internal sealed class ThrowOnceSink : IMessageSink
    {
        private int _failures;
        public int FailedSends => _failures;
        public ConcurrentQueue<(long Chat, string Text)> Sent { get; } = new();
        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        { Sent.Enqueue((chatId, text)); return Task.CompletedTask; }
        public Task SendHtmlTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            if (text.Contains("Using") && Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
                throw new HttpRequestException("scripted send failure");
            return SendTextAsync(chatId, text, ct);
        }
        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPhotoAsync(long chatId, string path, string? caption, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>Scripted JSON-RPC peer; the executor and its stdout reader are production code.</summary>
internal sealed class AbandonAppServer : IAsyncDisposable
{
    private readonly StandInProcess _process = new();
    private readonly Pipe _stdout = new();
    private readonly StreamWriter _output;
    private readonly StreamReader _input;
    private readonly Task _reader;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-abandon-" + Guid.NewGuid());
    private int _turns;
    public IOptions<AgentOptions> Options { get; }
    public CodexExecutor Executor { get; }
    public ConcurrentQueue<JsonObject> Requests { get; } = new();
    public Func<string, Task>? OnTurn { get; set; }
    public Func<string, Task>? OnInterrupt { get; set; }
    public TaskCompletionSource InterruptWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string> Logs { get; } = new();
    public StreamWriter Stdin { get; }

    public AbandonAppServer(TurnOriginLedger? ledger = null, Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? starter = null)
    {
        Directory.CreateDirectory(_directory);
        Options = Microsoft.Extensions.Options.Options.Create(new AgentOptions
        { Name = "agent1", Role = "test", Provider = "codex", WorkDir = _directory });
        var telegram = Microsoft.Extensions.Options.Options.Create(new TelegramOptions());
        Executor = new CodexExecutor(Options, telegram,
            new PromptBuilder(Options, NullLogger<PromptBuilder>.Instance), new TestLogger(Logs),
            starter ?? (_ => throw new InvalidOperationException("Unexpected process restart")), ledger: ledger);
        _output = new StreamWriter(_stdout.Writer.AsStream()) { AutoFlush = true };
        _input = new StreamReader(_stdout.Reader.AsStream());
        Stdin = new RpcWriter(HandleAsync);
        Executor.SetProcessForTests(_process.Process);
        Executor.SetStdinForTests(Stdin);
        Executor.SetThreadStateForTests("thread-1", null);
        _reader = Executor.RunStdoutReaderForTests(_input);
    }

    private async Task HandleAsync(string line)
    {
        var request = JsonNode.Parse(line)!.AsObject();
        Requests.Enqueue(request);
        var method = (string?)request["method"];
        if (method == "turn/interrupt")
        {
            var id = (string)request["params"]!["turnId"]!;
            InterruptWritten.TrySetResult();
            if (OnInterrupt is not null) await OnInterrupt(id);
            else await NotifyAsync(Completed(id, "discarded", "interrupted"));
            return;
        }
        if (method is "turn/start" or "thread/shellCommand")
        {
            var id = $"turn-{Interlocked.Increment(ref _turns)}";
            Assert.True(await Executor.WaitAndCompleteNextPendingRequestForTests(
                new JsonObject { ["turn"] = new JsonObject { ["id"] = id } }));
            if (OnTurn is not null) await OnTurn(id);
            else if (id == "turn-1") await NotifyAsync(Tool(id));
            else await NotifyAsync(Completed(id, $"answer-{_turns}"));
        }
    }

    public Task NotifyAsync(JsonObject frame) => _output.WriteLineAsync(frame.ToJsonString());
    public static JsonObject Frame(string method, string turn, JsonObject? item = null) => new()
    { ["method"] = method, ["params"] = new JsonObject { ["turnId"] = turn, ["item"] = item } };
    public static JsonObject Tool(string turn) => Frame("item/started", turn,
        new JsonObject { ["type"] = "mcpToolCall", ["tool"] = "example_tool", ["arguments"] = new JsonObject() });
    public static JsonObject Started(string turn) => new()
    { ["method"] = "turn/started", ["params"] = new JsonObject { ["turn"] = new JsonObject { ["id"] = turn } } };
    public static JsonObject Completed(string turn, string answer, string status = "completed") => new()
    {
        ["method"] = "turn/completed", ["params"] = new JsonObject
        {
            ["turn"] = new JsonObject { ["id"] = turn, ["status"] = status,
                ["items"] = new JsonArray(new JsonObject { ["type"] = "agentMessage", ["text"] = answer }) }
        }
    };

    public async ValueTask DisposeAsync()
    {
        await Executor.DisposeAsync();
        await _reader;
        _output.Dispose();
        _input.Dispose();
        // Executor owns and disposes the stand-in process.
        Directory.Delete(_directory, recursive: true);
    }

    internal sealed class RpcWriter(Func<string, Task> write) : StreamWriter(Stream.Null)
    { public override Task WriteLineAsync(string? value) => write(value!); }
    private sealed class TestLogger(ConcurrentQueue<string> messages) : ILogger<CodexExecutor>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, ex));
    }
}
