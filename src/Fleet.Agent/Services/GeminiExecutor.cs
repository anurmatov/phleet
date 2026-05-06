using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Manages a persistent Node.js bridge process (gemini-bridge.mjs) that communicates
/// with the @google/genai SDK via a long-lived Chat session. Mirrors CodexExecutor.
///
/// Replaces the previous per-task `gemini -p` spawn approach (PR #133 / issue #132),
/// which re-transmitted the full system prompt and conversation history on every task.
/// The bridge holds one Chat session per agent process, accumulating context across
/// turns without the per-task token overhead. See issue #145.
///
/// Protocol (stdin/stdout JSONL) mirrors codex-bridge.mjs:
///   .NET -> bridge: {"type":"task","prompt":"...","systemPrompt":"...","model":"...",
///                    "attachments":[{"path":"...","mimeType":"..."}]}
///   bridge -> .NET: {"type":"ack"} | {"type":"turn.started"} |
///                   {"type":"item.started","itemType":"message","text":"..."} |
///                   {"type":"item.started","itemType":"tool_use",...} |
///                   {"type":"item.completed","itemType":"tool_result","text":"..."} |
///                   {"type":"turn.completed","text":"...","usage":{...},"durationMs":0} |
///                   {"type":"turn.failed","error":"..."} | {"type":"error","message":"..."}
///
/// Auth: OAuth credentials at ~/.gemini/oauth_creds.json (writable bind mount).
///   google-auth-library's OAuth2Client handles transparent token refresh and emits
///   'tokens' events to keep the credential file in sync with refreshed tokens.
///
/// MCP: bridge receives --mcp-config pointing at .mcp.json (same as codex-bridge).
///   On startup, the bridge connects to each HTTP MCP server, fetches tool definitions,
///   and routes model function calls to the appropriate server.
///
/// Attachments: file paths from AttachmentSweeper are included in the JSON envelope as
///   {path, mimeType} objects. The bridge loads them as base64 inline data parts so
///   the model can process images and PDFs natively.
/// </summary>
public sealed class GeminiExecutor : IAgentExecutor
{
    private readonly AgentOptions _config;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<GeminiExecutor> _logger;

    private Process? _process;
    private StreamWriter? _stdin;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private string? _lastSessionId;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private volatile bool _restartRequested;

    private Channel<BridgeEvent>? _eventChannel;
    private CancellationTokenSource? _readerCts;

    private const string BridgePath = "/app/gemini-bridge.mjs";
    private const string NodeBin = "node";

    public string? LastSessionId => _lastSessionId;
    public DateTimeOffset LastActivity => _lastActivity;
    // Bridge is warm once the process is running — mirrors CodexExecutor.
    public bool IsProcessWarm => _process is not null && !_process.HasExited;

    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        Array.Empty<BackgroundTaskInfo>();

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public GeminiExecutor(
        IOptions<AgentOptions> config,
        PromptBuilder promptBuilder,
        ILogger<GeminiExecutor> logger)
    {
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _logger = logger;

        _logger.LogInformation(
            "GeminiExecutor: persistent bridge mode (gemini-bridge.mjs). " +
            "System prompt delivered once per session; context accumulates across tasks. " +
            "MCP tools via HTTP servers in .mcp.json. " +
            "Attachments (image/PDF) encoded as inline base64 in the JSON envelope.");
    }

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);

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
                model = string.IsNullOrWhiteSpace(_config.Model) ? "gemini-2.5-flash" : _config.Model,
                attachments = BuildAttachments(images, documents),
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

    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Commands (/compact etc.) are forwarded as tasks; the persistent chat session
        // handles them naturally without needing a separate code path.
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

    // ── Internals ─────────────────────────────────────────────────────────────

    private Task StartProcessAsync(CancellationToken ct)
    {
        StopReader();

        var mcpConfigPath = Path.Combine(_config.WorkDir, ".mcp.json");

        var psi = new ProcessStartInfo
        {
            FileName = NodeBin,
            // Use ArgumentList (not Arguments string) so paths with spaces are quoted safely.
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WorkingDirectory = _config.WorkDir,
        };

        // Enforce OAuth-only auth: strip API key env vars that would bypass OAuth
        // and silently use the API-key billing tier (same invariant as entrypoint.sh).
        psi.ArgumentList.Add(BridgePath);
        psi.ArgumentList.Add("--mcp-config");
        psi.ArgumentList.Add(mcpConfigPath);

        psi.Environment.Remove("GEMINI_API_KEY");
        psi.Environment.Remove("GOOGLE_API_KEY");

        // Force IPv4-first DNS — generativelanguage.googleapis.com publishes AAAA records;
        // Docker bridge networks typically lack IPv6 egress; Happy Eyeballs picks IPv6
        // first and ETIMEDOUT before reaching the API. Same fix as CodexExecutor.
        var existingNodeOpts = psi.Environment.TryGetValue("NODE_OPTIONS", out var no) ? no ?? "" : "";
        psi.Environment["NODE_OPTIONS"] = string.IsNullOrEmpty(existingNodeOpts)
            ? "--dns-result-order=ipv4first"
            : existingNodeOpts + " --dns-result-order=ipv4first";

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gemini-bridge.mjs");
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            { AutoFlush = false };

        _eventChannel = Channel.CreateUnbounded<BridgeEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() =>
            ReadStdoutAsync(_process.StandardOutput, _eventChannel.Writer, _readerCts.Token));

        _logger.LogInformation("GeminiExecutor: bridge process started (pid {Pid})", _process.Id);
        return Task.CompletedTask;
    }

    private async Task ReadStdoutAsync(
        StreamReader reader,
        ChannelWriter<BridgeEvent> writer,
        CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                BridgeEvent? ev = null;
                try { ev = JsonSerializer.Deserialize<BridgeEvent>(line, BridgeEvent.JsonOptions); }
                catch { /* skip malformed lines */ }
                if (ev is not null) await writer.WriteAsync(ev, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "GeminiExecutor stdout reader stopped"); }
        finally { writer.TryComplete(); }
    }

    private async IAsyncEnumerable<AgentProgress> StreamEventsAsync(
        [EnumeratorCancellation] CancellationToken ct)
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

    internal AgentProgress? MapEvent(BridgeEvent ev) => ev.Type switch
    {
        "ack" => new AgentProgress
        {
            EventType = "system",
            Summary = "Connected",
            IsSignificant = false,
            SessionId = ev.SessionId,
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
            // Intermediate streaming chunk — suppress Telegram routing per PR #129 pattern.
            IsSignificant = false,
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

        return new AgentProgress
        {
            EventType = "result",
            Summary = ev.Text ?? "",
            FinalResult = ev.Text ?? "",
            SessionId = ev.SessionId,
            Stats = new ExecutionStats
            {
                InputTokens  = ev.Usage?.InputTokens  ?? 0,
                OutputTokens = ev.Usage?.OutputTokens ?? 0,
                DurationMs   = ev.DurationMs,
            },
            IsSignificant = true,
        };
    }

    // ── Attachment helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the attachment descriptor list for the JSON envelope. The bridge reads each
    /// file by path and encodes it as base64 inline data. Files without a persisted
    /// FilePath (PersistAttachments=false or size-limit exceeded) are omitted silently.
    /// </summary>
    private static IReadOnlyList<object>? BuildAttachments(
        IReadOnlyList<MessageImage>? images,
        IReadOnlyList<MessageDocument>? documents)
    {
        var list = new List<object>();

        if (images is not null)
        {
            foreach (var img in images)
            {
                if (string.IsNullOrEmpty(img.FilePath)) continue;
                list.Add(new { path = img.FilePath, mimeType = img.MimeType ?? "image/jpeg" });
            }
        }

        if (documents is not null)
        {
            foreach (var doc in documents)
            {
                if (string.IsNullOrEmpty(doc.FilePath)) continue;
                list.Add(new { path = doc.FilePath, mimeType = doc.MimeType ?? "application/pdf" });
            }
        }

        return list.Count > 0 ? list : null;
    }

    // ── Process lifecycle ─────────────────────────────────────────────────────

    private void StopReader()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;
        _eventChannel = null;
    }

    private async Task StopInternalAsync()
    {
        StopReader();

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill();
                await _process.WaitForExitAsync();
            }
            catch { /* non-fatal */ }

            _process.Dispose();
            _process = null;
        }

        _stdin?.Dispose();
        _stdin = null;
    }

    // ── Bridge event deserialization ──────────────────────────────────────────

    internal sealed class BridgeEvent
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        [JsonPropertyName("type")]       public string?     Type       { get; set; }
        [JsonPropertyName("sessionId")]  public string?     SessionId  { get; set; }
        [JsonPropertyName("itemType")]   public string?     ItemType   { get; set; }
        [JsonPropertyName("text")]       public string?     Text       { get; set; }
        [JsonPropertyName("toolName")]   public string?     ToolName   { get; set; }
        [JsonPropertyName("toolArgs")]   public string?     ToolArgs   { get; set; }
        [JsonPropertyName("error")]      public string?     Error      { get; set; }
        [JsonPropertyName("message")]    public string?     Message    { get; set; }
        [JsonPropertyName("durationMs")] public int         DurationMs { get; set; }
        [JsonPropertyName("usage")]      public BridgeUsage? Usage     { get; set; }
    }

    internal sealed class BridgeUsage
    {
        [JsonPropertyName("inputTokens")]  public int InputTokens  { get; set; }
        [JsonPropertyName("outputTokens")] public int OutputTokens { get; set; }
    }
}
