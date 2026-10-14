using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// The stand-in for a Pi 1.0.4 <c>pi --mode rpc</c> child (#386 §15), in the role
/// <c>CodexExecutorTests.ThreadStartCapture</c> plays for codex: it captures the
/// <see cref="ProcessStartInfo"/> the executor built (arguments and environment) and every stdin
/// frame, and answers on stdout in Pi's RPC shapes.
/// </summary>
/// <remarks>
/// <para>
/// This is a STAND-IN, not Pi. It runs in memory over pipes so a test can script each frame and
/// each delay exactly — an ack 2 s after a compaction, an <c>agent_settled</c> that never comes, a
/// process that dies mid-run — which no real Pi does on demand. What the real binary does is
/// <c>PiExecutorRealPiTests</c>' job.
/// </para>
/// <para>
/// Its defaults are a healthy Pi: <c>get_state</c> matches what the executor configured,
/// <c>prompt</c> is acked <c>started</c> and answered "OK", <c>steer</c> is queued, <c>bash</c> runs.
/// Each test overrides only the handler its row is about.
/// </para>
/// </remarks>
internal sealed class PiStandIn
{
    public const string Tag = "qwen3.8-flash-next";
    public const string BaseUrl = "http://inference-host:8000";
    public const int ContextWindow = 131072;
    public const string PopplerVersion = "24.02.0";

    /// <summary>The session file the stand-in reports. Never created on disk unless a test does.</summary>
    public string SessionFile { get; set; } = "/workspace/example/.pi/sessions/s-1.jsonl"; // hygiene-ok: corpus path

    public string SessionId { get; set; } = "s-1";

    private readonly object _gate = new();
    private readonly List<PiStandInProcess> _processes = [];
    private readonly List<ProcessStartInfo> _starts = [];
    private readonly SemaphoreSlim _started = new(0);

    /// <summary>The <c>get_state</c> data for the n-th start (0-based); the default matches the configuration.</summary>
    public Func<int, JsonObject>? StateFor { get; set; }

    public bool AnswerGetState { get; set; } = true;

    /// <summary>The thinking level a correct Pi reports, which the executor derives from Effort.</summary>
    public string ThinkingLevel { get; set; } = "medium";

    public int MessageCount { get; set; }

    public Func<PiStandInProcess, JsonObject, Task>? OnPrompt { get; set; }
    public Func<PiStandInProcess, JsonObject, Task>? OnSteer { get; set; }
    public Func<PiStandInProcess, JsonObject, Task>? OnAbort { get; set; }
    public Func<PiStandInProcess, JsonObject, Task>? OnBash { get; set; }

    /// <summary>
    /// While set and not completed, a stdin write blocks before any byte reaches the stand-in: a
    /// Pi that stopped reading its stdin (#455 §3.5).
    /// </summary>
    public TaskCompletionSource? StdinGate { get; set; }

    public IReadOnlyList<PiStandInProcess> Processes
    {
        get { lock (_gate) return _processes.ToList(); }
    }

    public IReadOnlyList<ProcessStartInfo> Starts
    {
        get { lock (_gate) return _starts.ToList(); }
    }

    public PiStandInProcess Current
    {
        get { lock (_gate) return _processes[^1]; }
    }

    /// <summary>The executor's process starter.</summary>
    public IPiProcess Start(ProcessStartInfo psi)
    {
        PiStandInProcess process;
        lock (_gate)
        {
            process = new PiStandInProcess(this, _processes.Count);
            _starts.Add(psi);
            _processes.Add(process);
        }
        process.Run();
        _started.Release();
        return process;
    }

    /// <summary>Waits until <paramref name="count"/> processes have been started in total.</summary>
    public async Task<PiStandInProcess> WaitForStartAsync(int count, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            lock (_gate)
            {
                if (_processes.Count >= count)
                    return _processes[count - 1];
            }
            await _started.WaitAsync(cts.Token);
        }
    }

    public JsonObject DefaultState() => new()
    {
        ["model"] = new JsonObject
        {
            ["id"] = Tag,
            ["name"] = Tag,
            ["provider"] = PiLocalModel.ProviderId,
            ["api"] = "anthropic-messages",
            ["contextWindow"] = ContextWindow,
        },
        ["thinkingLevel"] = ThinkingLevel,
        ["isStreaming"] = false,
        ["isCompacting"] = false,
        ["steeringMode"] = "all",
        ["followUpMode"] = "one-at-a-time",
        ["sessionFile"] = SessionFile,
        ["sessionId"] = SessionId,
        ["autoCompactionEnabled"] = true,
        ["messageCount"] = MessageCount,
        ["pendingMessageCount"] = 0,
    };

    internal async Task DispatchAsync(PiStandInProcess process, JsonObject frame)
    {
        switch (PiExecutor.Str(frame, "type"))
        {
            case "get_state":
                if (AnswerGetState)
                    await process.RespondAsync(frame, StateFor?.Invoke(process.Index) ?? DefaultState());
                break;
            case "prompt":
                await (OnPrompt ?? DefaultPromptAsync)(process, frame);
                break;
            case "steer":
                await (OnSteer ?? ((p, f) => p.RespondAsync(f, new JsonObject { ["disposition"] = "queued" })))(process, frame);
                break;
            case "abort":
                await (OnAbort ?? ((p, f) => p.RespondAsync(f)))(process, frame);
                break;
            case "bash":
                await (OnBash ?? ((p, f) => p.RespondAsync(f, new JsonObject
                {
                    ["output"] = "ran\n",
                    ["exitCode"] = 0,
                    ["cancelled"] = false,
                    ["truncated"] = false,
                })))(process, frame);
                break;
        }
    }

    private static async Task DefaultPromptAsync(PiStandInProcess process, JsonObject frame)
    {
        await process.AckAsync(frame);
        await process.EmitAsync(PiEvents.TextRun("OK"));
    }

    /// <summary>An executor wired to this stand-in, with every deadline short unless overridden.</summary>
    public PiExecutor CreateExecutor(
        string workDir,
        string agentDir,
        PiExecutorTimings? timings = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? effort = "",
        IEnumerable<string>? allowedTools = null,
        ILogger<PiExecutor>? logger = null,
        bool imageInput = false,
        IPiLocalTools? localTools = null,
        TimeSpan? commandDeadline = null)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "agent-a",
            Role = "test",
            WorkDir = workDir,
            Provider = "pi",
            Model = Tag,
            PiBaseUrl = BaseUrl,
            ContextWindow = ContextWindow,
            Effort = effort,
            AllowedTools = (allowedTools ?? ["mcp__fleet-memory__memory_get", "mcp__fleet-temporal__notify_cto"]).ToList(),
            PiImageInput = imageInput,
        });

        return new PiExecutor(
            options,
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance),
            logger ?? NullLogger<PiExecutor>.Instance,
            new PiExecutorSettings
            {
                AgentDir = agentDir,
                ProcessStarter = Start,
                BaseEnvironment = environment ?? new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" },
                VersionProbe = _ => Task.FromResult<string?>(PiLocalModel.PinnedVersion),
                PopplerVersionProbe = _ => Task.FromResult<string?>(PopplerVersion),
                LocalTools = localTools ?? new FakePiLocalTools(),
                CommandProcessingDeadline = commandDeadline ?? PiLocalModel.CommandProcessingDeadline,
                Timings = timings ?? FastTimings,
            });
    }

    /// <summary>
    /// Short deadlines for the stand-in rows. Rows that assert a literal spec duration (the 2 s
    /// steer, the 5 s abort settle) set it explicitly.
    /// </summary>
    public static PiExecutorTimings FastTimings { get; } = new()
    {
        StartupState = TimeSpan.FromSeconds(2),
        PreviousSettle = TimeSpan.FromSeconds(1),
        AbortSettle = TimeSpan.FromSeconds(1),
        Ack = TimeSpan.FromSeconds(1),
        Steer = TimeSpan.FromSeconds(2),
        Watchdog = TimeSpan.FromSeconds(3),
        InjectedTurnStart = TimeSpan.FromMilliseconds(500),
    };
}

/// <summary>One stand-in Pi process: three in-memory pipes, a frame log and a kill count.</summary>
internal sealed class PiStandInProcess : IPiProcess
{
    private readonly PiStandIn _owner;
    // No backpressure: an image frame (~8.5 MB) is one line the reader can only consume whole.
    private readonly Pipe _stdin = new(new PipeOptions(pauseWriterThreshold: 0));
    private readonly Pipe _stdout = new();
    private readonly Pipe _stderr = new();
    private readonly SemaphoreSlim _emitLock = new(1, 1);
    private readonly SemaphoreSlim _frameArrived = new(0);
    private readonly List<JsonObject> _frames = [];
    private readonly Dictionary<JsonObject, long> _frameTimes = new(ReferenceEqualityComparer.Instance);
    private readonly Stream _stdoutStream;
    private readonly TextReader _stderrReader;
    private readonly BrokenPipeStream _stdinStream;
    private int _exited;
    private int _kills;

    public PiStandInProcess(PiStandIn owner, int index)
    {
        _owner = owner;
        Index = index;
        _stdoutStream = _stdout.Reader.AsStream();
        _stderrReader = new StreamReader(_stderr.Reader.AsStream(), Encoding.UTF8);
        _stdinStream = new BrokenPipeStream(_stdin.Writer, () => HasExited, () => owner.StdinGate?.Task);
    }

    /// <summary>0 for the first process the stand-in started.</summary>
    public int Index { get; }

    public int Id => 0;
    public bool HasExited => Volatile.Read(ref _exited) == 1;
    public int Kills => Volatile.Read(ref _kills);
    public Stream StandardInput => _stdinStream;
    public Stream StandardOutput => _stdoutStream;
    public TextReader StandardError => _stderrReader;

    /// <summary>Every frame the executor wrote, in order.</summary>
    public IReadOnlyList<JsonObject> Frames
    {
        get { lock (_frames) return _frames.ToList(); }
    }

    /// <summary>When the stand-in read <paramref name="frame"/> (a <see cref="Stopwatch"/> timestamp).</summary>
    public long ReceivedAt(JsonObject frame)
    {
        lock (_frames)
            return _frameTimes[frame];
    }

    public IReadOnlyList<JsonObject> FramesOfType(string type) =>
        Frames.Where(f => PiExecutor.Str(f, "type") == type).ToList();

    public void Kill()
    {
        Interlocked.Increment(ref _kills);
        Exit();
    }

    /// <summary>The process ends on its own (a crash): stdout and stderr close.</summary>
    public void Exit()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1)
            return;
        _stdout.Writer.Complete();
        _stderr.Writer.Complete();
        _stdin.Reader.CancelPendingRead();
    }

    public void Dispose()
    {
    }

    internal void Run() => _ = Task.Run(ReadFramesAsync);

    private async Task ReadFramesAsync()
    {
        var reader = _stdin.Reader;
        while (true)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (result.IsCanceled)
                return;

            var buffer = result.Buffer;
            while (buffer.PositionOf((byte)'\n') is { } lf)
            {
                var line = Encoding.UTF8.GetString(buffer.Slice(0, lf));
                buffer = buffer.Slice(buffer.GetPosition(1, lf));
                if (JsonNode.Parse(line) is not JsonObject frame)
                    continue;

                lock (_frames)
                {
                    _frames.Add(frame);
                    _frameTimes[frame] = Stopwatch.GetTimestamp();
                }
                _frameArrived.Release();

                // Handlers run beside the reader, as Pi handles commands asynchronously: an abort
                // must be read while a prompt's scripted run is still being emitted.
                _ = Task.Run(() => _owner.DispatchAsync(this, frame));
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
                return;
        }
    }

    /// <summary>Waits for the <paramref name="occurrence"/>-th frame of a type and returns it.</summary>
    public async Task<JsonObject> WaitForFrameAsync(string type, int occurrence = 1, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var matching = FramesOfType(type);
            if (matching.Count >= occurrence)
                return matching[occurrence - 1];
            try
            {
                await _frameArrived.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No '{type}' frame #{occurrence} reached the stand-in.");
            }
        }
    }

    public async Task EmitAsync(params JsonObject[] records) => await EmitAsync((IEnumerable<JsonObject>)records);

    public async Task EmitAsync(IEnumerable<JsonObject> records)
    {
        foreach (var record in records)
            await EmitLineAsync(record.ToJsonString());
    }

    /// <summary>Writes one raw stdout line (the LF is added).</summary>
    public async Task EmitLineAsync(string line)
    {
        await _emitLock.WaitAsync();
        try
        {
            if (HasExited)
                return;
            await _stdout.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }
        finally
        {
            _emitLock.Release();
        }
    }

    public async Task EmitStderrAsync(string line)
    {
        if (!HasExited)
            await _stderr.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
    }

    public Task RespondAsync(JsonObject command, JsonObject? data = null, bool success = true, string? error = null)
    {
        var response = new JsonObject
        {
            ["id"] = PiExecutor.Str(command, "id"),
            ["type"] = "response",
            ["command"] = PiExecutor.Str(command, "type"),
            ["success"] = success,
        };
        if (data is not null)
            response["data"] = data;
        if (error is not null)
            response["error"] = error;
        return EmitAsync(response);
    }

    public Task AckAsync(JsonObject prompt, string disposition = "started") =>
        RespondAsync(prompt, new JsonObject { ["disposition"] = disposition });

    /// <summary>A stdin stream that fails like a closed pipe once the process has gone.</summary>
    private sealed class BrokenPipeStream(PipeWriter writer, Func<bool> exited, Func<Task?> gate) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            exited() ? Task.FromException(new IOException("Broken pipe")) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (exited())
                throw new IOException("Broken pipe");
            if (gate() is { } blocked)
                await blocked.WaitAsync(cancellationToken);
            if (exited())
                throw new IOException("Broken pipe");
            await writer.WriteAsync(buffer, cancellationToken);
        }
    }
}

/// <summary>Pi 1.0.4 RPC records, in the shapes <c>docs/json.md</c> and <c>rpc-mode.ts</c> define.</summary>
internal static class PiEvents
{
    public static JsonObject Of(string type) => new() { ["type"] = type };

    public static JsonObject AgentStart() => Of("agent_start");
    public static JsonObject TurnStart() => Of("turn_start");
    public static JsonObject TurnEnd() => Of("turn_end");
    public static JsonObject AgentSettled() => Of("agent_settled");

    public static JsonObject AgentEnd(bool willRetry = false) => new()
    {
        ["type"] = "agent_end",
        ["messages"] = new JsonArray(),
        ["willRetry"] = willRetry,
    };

    public static JsonObject Usage(int input = 120, int output = 8, int cacheRead = 0) => new()
    {
        ["input"] = input,
        ["output"] = output,
        ["cacheRead"] = cacheRead,
        ["cacheWrite"] = 0,
        ["totalTokens"] = input + output + cacheRead,
        ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 },
    };

    public static JsonObject TextDelta(string delta) => new()
    {
        ["type"] = "message_update",
        ["usage"] = Usage(),
        ["assistantMessageEvent"] = new JsonObject { ["type"] = "text_delta", ["contentIndex"] = 0, ["delta"] = delta },
    };

    public static JsonObject ToolCallDelta(string delta) => new()
    {
        ["type"] = "message_update",
        ["usage"] = Usage(),
        ["assistantMessageEvent"] = new JsonObject { ["type"] = "toolcall_delta", ["contentIndex"] = 0, ["delta"] = delta },
    };

    public static JsonObject Assistant(JsonArray content, string stopReason, JsonObject? usage = null, string? errorMessage = null)
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["api"] = "anthropic-messages",
            ["provider"] = PiLocalModel.ProviderId,
            ["model"] = PiStandIn.Tag,
            ["usage"] = usage ?? Usage(),
            ["stopReason"] = stopReason,
            ["timestamp"] = 1,
        };
        if (errorMessage is not null)
            message["errorMessage"] = errorMessage;
        return new JsonObject { ["type"] = "message_end", ["message"] = message };
    }

    public static JsonObject FinalText(string text, JsonObject? usage = null) =>
        Assistant(new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }, "stop", usage);

    public static JsonObject ToolCallMessage(string id, string name, JsonObject args) =>
        Assistant(new JsonArray
        {
            new JsonObject { ["type"] = "toolCall", ["id"] = id, ["name"] = name, ["arguments"] = args },
        }, "toolUse");

    public static JsonObject ErrorMessage(string error, string stopReason = "error") =>
        Assistant(new JsonArray(), stopReason, errorMessage: error);

    public static JsonObject ToolStart(string id, string name, JsonObject args) => new()
    {
        ["type"] = "tool_execution_start",
        ["toolCallId"] = id,
        ["toolName"] = name,
        ["args"] = args,
    };

    public static JsonObject ToolEnd(string id, string name, string text, bool isError = false) => new()
    {
        ["type"] = "tool_execution_end",
        ["toolCallId"] = id,
        ["toolName"] = name,
        ["result"] = new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["details"] = new JsonObject(),
        },
        ["isError"] = isError,
    };

    public static JsonObject CompactionStart(string reason = "threshold") => new()
    {
        ["type"] = "compaction_start",
        ["reason"] = reason,
    };

    public static JsonObject CompactionEnd(string reason = "threshold", int tailTokens = 4096, bool willRetry = false) => new()
    {
        ["type"] = "compaction_end",
        ["reason"] = reason,
        ["result"] = new JsonObject
        {
            ["summary"] = "summary",
            ["firstKeptEntryId"] = "e-7",
            ["tokensBefore"] = 115000,
            ["estimatedTokensAfter"] = 30000,
            ["usage"] = Usage(input: 206, output: 390, cacheRead: 114000),
            ["details"] = new JsonObject
            {
                ["path"] = "prefix",
                ["prompt"] = 114206,
                ["cached"] = 114000,
                ["output"] = 390,
                ["ms"] = 21000,
                ["isSplitTurn"] = false,
                ["tailTokens"] = tailTokens,
            },
        },
        ["aborted"] = false,
        ["willRetry"] = willRetry,
    };

    public static JsonObject AutoRetryStart(int attempt = 1) => new()
    {
        ["type"] = "auto_retry_start",
        ["attempt"] = attempt,
        ["maxAttempts"] = 3,
        ["delayMs"] = 2000,
        ["errorMessage"] = "529 overloaded",
    };

    /// <summary>A whole one-call run that answers <paramref name="text"/> and settles.</summary>
    public static IEnumerable<JsonObject> TextRun(string text, bool settle = true)
    {
        yield return AgentStart();
        yield return TurnStart();
        yield return TextDelta(text);
        yield return FinalText(text);
        yield return TurnEnd();
        yield return AgentEnd();
        if (settle)
            yield return AgentSettled();
    }
}

/// <summary>A thread-safe capturing logger: the executor logs from its reader threads.</summary>
internal sealed class PiTestLogger : ILogger<PiExecutor>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
            _entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>A temporary directory, deleted on dispose.</summary>
internal sealed class PiTempDirectory : IDisposable
{
    public PiTempDirectory(string prefix = "pi-test")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
