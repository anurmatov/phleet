using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Manages a persistent Node.js bridge process (codex-bridge.mjs) that communicates
/// with the Codex SDK. Tasks are sent via stdin JSONL; events stream back via stdout JSONL.
/// Mirrors ClaudeExecutor's process-stays-alive pattern.
/// </summary>
public sealed class CodexExecutor : IAgentExecutor
{
    private readonly AgentOptions _config;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<CodexExecutor> _logger;

    private Process? _process;
    private StreamWriter? _stdin;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private string? _lastSessionId;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private volatile bool _restartRequested;

    private Channel<BridgeEvent>? _eventChannel;
    private CancellationTokenSource? _readerCts;

    private const string BridgePath = "/app/codex-bridge.mjs";
    private const string NodeBin = "node";

    public string? LastSessionId => _lastSessionId;
    public DateTimeOffset LastActivity => _lastActivity;
    public bool IsProcessWarm => _process is not null && !_process.HasExited && _lastSessionId is not null;

    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        Array.Empty<BackgroundTaskInfo>();

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public CodexExecutor(IOptions<AgentOptions> config, PromptBuilder promptBuilder, ILogger<CodexExecutor> logger)
    {
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);
        var hasImages = images is { Count: > 0 };

        try
        {
            if (_restartRequested || _process is null || _process.HasExited)
            {
                _restartRequested = false;
                await StartProcessAsync(ct);
            }

            var msgObj = new
            {
                type = "task",
                prompt = task,
                systemPrompt = _promptBuilder.BuildSystemPrompt(),
                model = _config.Model,
                sessionId = _lastSessionId,
            };

            await _stdin!.WriteLineAsync(JsonSerializer.Serialize(msgObj).AsMemory(), ct);
            await _stdin.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }

        if (hasImages)
        {
            yield return new AgentProgress
            {
                EventType = "warning",
                Summary = "Note: the Codex provider does not support image input — images will be ignored.",
                IsSignificant = true,
            };
        }

        await foreach (var progress in StreamEventsAsync(ct))
        {
            _lastActivity = DateTimeOffset.UtcNow;
            yield return progress;
            if (progress.FinalResult is not null) yield break;
        }
    }

    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);

        try
        {
            if (_process is null || _process.HasExited)
                await StartProcessAsync(ct);

            var msgObj = new
            {
                type = "command",
                prompt = command,
                sessionId = _lastSessionId,
            };

            await _stdin!.WriteLineAsync(JsonSerializer.Serialize(msgObj).AsMemory(), ct);
            await _stdin.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }

        await foreach (var progress in StreamEventsAsync(ct))
        {
            _lastActivity = DateTimeOffset.UtcNow;
            yield return progress;
            if (progress.FinalResult is not null) yield break;
        }
    }

    public void RequestRestart() => _restartRequested = true;

    public async Task StopProcessAsync()
    {
        await StopInternalAsync();
    }

    public async Task<bool> TryStopProcessAsync()
    {
        if (_process is null) return false;
        await StopInternalAsync();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _ = StopInternalAsync();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Internals ---

    private Task StartProcessAsync(CancellationToken ct)
    {
        StopReaderAsync();

        var mcpConfigPath = Path.Combine(_config.WorkDir, ".mcp.json");

        var sandboxMode = string.IsNullOrWhiteSpace(_config.CodexSandboxMode)
            ? "danger-full-access"
            : _config.CodexSandboxMode;

        var psi = new ProcessStartInfo
        {
            FileName = NodeBin,
            Arguments = $"{BridgePath} --mcp-config {mcpConfigPath}",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
        };
        psi.Environment["CODEX_SANDBOX_MODE"] = sandboxMode;

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start codex-bridge.mjs");
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false };

        _eventChannel = Channel.CreateUnbounded<BridgeEvent>(new UnboundedChannelOptions { SingleReader = true });
        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadStdoutAsync(_process.StandardOutput, _eventChannel.Writer, _readerCts.Token));

        _logger.LogInformation("CodexExecutor: bridge process started (pid {Pid})", _process.Id);
        return Task.CompletedTask;
    }

    private async Task ReadStdoutAsync(StreamReader reader, ChannelWriter<BridgeEvent> writer, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                BridgeEvent? ev = null;
                try { ev = JsonSerializer.Deserialize<BridgeEvent>(line, BridgeEvent.JsonOptions); } catch { }
                if (ev is not null) await writer.WriteAsync(ev, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "CodexExecutor stdout reader stopped"); }
        finally { writer.TryComplete(); }
    }

    private async IAsyncEnumerable<AgentProgress> StreamEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_eventChannel is null) yield break;

        await foreach (var ev in _eventChannel.Reader.ReadAllAsync(ct))
        {
            var progress = MapEvent(ev);
            if (progress is null) continue;
            yield return progress;
            if (progress.FinalResult is not null) yield break;
        }
    }

    private AgentProgress? MapEvent(BridgeEvent ev) => ev.Type switch
    {
        "ack" => new AgentProgress
        {
            EventType = "system",
            Summary = "Connected",
            IsSignificant = false,
            SessionId = ev.SessionId,
        },
        "thread.started" => new AgentProgress
        {
            EventType = "system",
            Summary = "Thread started",
            IsSignificant = false,
        },
        "turn.started" => new AgentProgress
        {
            EventType = "system",
            Summary = "Processing...",
            IsSignificant = false,
        },
        "item.started" when ev.ItemType == "message" => new AgentProgress
        {
            EventType = "assistant",
            Summary = ev.Text ?? "",
            IsSignificant = true,
        },
        "item.started" when ev.ItemType == "tool_use" => new AgentProgress
        {
            EventType = "tool_use",
            Summary = $"Using {ev.ToolName}",
            ToolName = ev.ToolName,
            ToolArgs = ev.ToolArgs,
            IsSignificant = true,
        },
        "item.completed" when ev.ItemType == "tool_result" => new AgentProgress
        {
            EventType = "tool_result",
            Summary = ev.Text ?? "",
            IsSignificant = false,
        },
        "turn.completed" => BuildTurnCompleted(ev),
        "turn.failed" => new AgentProgress
        {
            EventType = "result",
            Summary = ev.Error ?? "Turn failed",
            FinalResult = ev.Error ?? "Turn failed",
            IsErrorResult = true,
            IsSignificant = true,
        },
        "error" => new AgentProgress
        {
            EventType = "result",
            Summary = ev.Message ?? "Error",
            FinalResult = ev.Message ?? "Error",
            IsErrorResult = true,
            IsSignificant = true,
        },
        _ => null,
    };

    private AgentProgress BuildTurnCompleted(BridgeEvent ev)
    {
        if (ev.SessionId is not null) _lastSessionId = ev.SessionId;

        var stats = new ExecutionStats
        {
            InputTokens = ev.Usage?.InputTokens ?? 0,
            OutputTokens = ev.Usage?.OutputTokens ?? 0,
            DurationMs = ev.DurationMs,
        };

        return new AgentProgress
        {
            EventType = "result",
            Summary = ev.Text ?? "",
            FinalResult = ev.Text ?? "",
            SessionId = ev.SessionId,
            Stats = stats,
            IsSignificant = true,
        };
    }

    private void StopReaderAsync()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;
        _eventChannel = null;
    }

    private async Task StopInternalAsync()
    {
        StopReaderAsync();

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill();
                await _process.WaitForExitAsync();
            }
            catch { }

            _process.Dispose();
            _process = null;
        }

        _stdin?.Dispose();
        _stdin = null;
    }

    // --- Bridge event deserialization ---

    private sealed class BridgeEvent
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public string? Type { get; set; }
        public string? SessionId { get; set; }
        public string? ItemType { get; set; }
        public string? Text { get; set; }
        public string? ToolName { get; set; }
        public string? ToolArgs { get; set; }
        public string? Error { get; set; }
        public string? Message { get; set; }
        public int DurationMs { get; set; }
        public BridgeUsage? Usage { get; set; }
    }

    private sealed class BridgeUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}
