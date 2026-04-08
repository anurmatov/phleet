using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Manages a persistent `claude -p` process with streaming I/O.
/// Messages are sent via stdin NDJSON; responses stream from stdout.
/// The process stays alive between tasks, avoiding session replay overhead.
/// </summary>
public sealed class ClaudeExecutor : IAgentExecutor
{
    private readonly AgentOptions _config;
    private readonly ILogger<ClaudeExecutor> _logger;

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private string? _lastSessionId;
    private int _messageCount;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private volatile bool _restartRequested;
    private ExecutionStats? _previousCumulativeStats;

    // Continuous background stdout reader — feeds all NDJSON events into this channel.
    // Separating stdout reads from the turn read-loop prevents stale events (e.g. a
    // background subagent's task_notification + result that arrives after the main turn
    // completes) from corrupting the next turn's response stream.
    private Channel<ClaudeStreamEvent>? _eventChannel;
    private CancellationTokenSource? _readerCts;

    // Background subagent task tracking (task_started / task_progress / task_notification)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BackgroundTaskInfo> _backgroundTasks = new();

    public string? LastSessionId => _lastSessionId;
    public DateTimeOffset LastActivity => _lastActivity;

    /// <summary>
    /// Returns a snapshot of currently active background (subagent) tasks.
    /// Entries are added on task_started and removed on task_notification (completed/failed/stopped).
    /// </summary>
    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        _backgroundTasks.Values.OrderBy(t => t.StartedAt).ToList();

    /// <summary>
    /// Request cancellation of a specific background subagent task by sending a TaskStop
    /// command to the Claude process via stdin.
    /// Returns false if the task ID is not in the active set.
    /// Uses a non-blocking tryacquire on _sendLock so the cancel endpoint never times out
    /// behind an active execution turn. If the lock is held, the task_stop message is written
    /// directly (stdin writes are thread-safe) and we return true immediately.
    /// </summary>
    public async Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default)
    {
        if (!_backgroundTasks.ContainsKey(taskId))
            return false;

        if (_process is null || _process.HasExited)
            return false;

        var message = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "task_stop",
            task_id = taskId,
        });

        _logger.LogInformation("Sending task_stop for background task {TaskId}", taskId);

        if (await _sendLock.WaitAsync(TimeSpan.Zero))
        {
            // Lock acquired — write under the lock as normal.
            try
            {
                await _stdin!.WriteLineAsync(message.AsMemory(), ct);
                await _stdin.FlushAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }
        else
        {
            // Lock is held by an active execution turn. Write directly — StreamWriter
            // internal writes on a UTF-8 pipe are effectively atomic for single-line
            // NDJSON messages; the worst case is interleaved bytes on the pipe, which
            // Claude's stream-json parser handles gracefully. This avoids a timeout.
            await _stdin!.WriteLineAsync(message.AsMemory(), ct);
            await _stdin.FlushAsync();
        }

        return true;
    }

    /// <summary>
    /// True when the process is alive and has already processed at least one message,
    /// meaning Claude has the conversation context in memory and callers can skip
    /// re-sending group buffers and other redundant context.
    /// </summary>
    public bool IsProcessWarm => _process is not null && !_process.HasExited && _messageCount > 0;

    private readonly PromptBuilder _promptBuilder;

    public ClaudeExecutor(IOptions<AgentOptions> config, ILogger<ClaudeExecutor> logger, PromptBuilder promptBuilder)
    {
        _config = config.Value;
        _logger = logger;
        _promptBuilder = promptBuilder;
    }

    /// <summary>
    /// Send a message to the persistent Claude process, streaming progress events.
    /// The process is started automatically if not running, or restarted with --resume if crashed.
    /// </summary>
    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        byte[]? imageBytes = null,
        string? imageMimeType = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);

        try
        {
            string message;
            if (imageBytes is not null && imageBytes.Length > 0)
            {
                var base64 = Convert.ToBase64String(imageBytes);
                var mime = imageMimeType ?? "image/jpeg";
                var content = new object[]
                {
                    new { type = "image", source = new { type = "base64", media_type = mime, data = base64 } },
                    new { type = "text", text = task },
                };
                message = JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { role = "user", content }
                });
            }
            else
            {
                message = JsonSerializer.Serialize(new
                {
                    type = "user",
                    message = new { role = "user", content = task }
                });
            }

            var attempt = 0;

            while (attempt < 2)
            {
                attempt++;
                EnsureProcess();

                // Discard any events buffered between the previous turn and now
                // (e.g. background subagent task_notification + stale result events).
                // System events were already processed inline by the background reader.
                DrainStaleTurnEvents();

                // Send message to stdin
                try
                {
                    var messageBytes = Encoding.UTF8.GetByteCount(message);
                    _logger.LogInformation("Sending message to claude ({Bytes} bytes, message #{Count})",
                        messageBytes, _messageCount + 1);
                    if (messageBytes > 10_000)
                        _logger.LogWarning("Large input detected ({Size} bytes, message #{Num})",
                            messageBytes, _messageCount + 1);
                    await _stdin!.WriteLineAsync(message.AsMemory(), ct);
                    await _stdin.FlushAsync();
                }
                catch (IOException) when (attempt < 2)
                {
                    _logger.LogWarning("Write to stdin failed (attempt {Attempt}), restarting process", attempt);
                    await KillProcessAsync();
                    continue;
                }

                // Read response events until "result" — events come from the background
                // reader via the channel rather than directly from stdout.
                var needsRetry = false;

                while (!ct.IsCancellationRequested)
                {
                    ClaudeStreamEvent? evt;
                    try
                    {
                        evt = await _eventChannel!.Reader.ReadAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Task cancelled — kill process so it doesn't continue mid-response
                        await KillProcessAsync();
                        throw;
                    }
                    catch (ChannelClosedException)
                    {
                        // Channel closed = background reader exited = process died mid-response
                        evt = null;
                    }

                    if (evt is null)
                    {
                        // Process died mid-response
                        if (attempt < 2)
                        {
                            _logger.LogWarning("Process died mid-response (attempt {Attempt}), restarting with --resume", attempt);
                            await KillProcessAsync();
                            needsRetry = true;
                            break;
                        }

                        _logger.LogError("Process died after retry, giving up");
                        yield return new AgentProgress
                        {
                            IsSignificant = true,
                            Summary = "Claude process died unexpectedly",
                            EventType = "error",
                        };
                        yield break;
                    }

                    // System events were already processed for side effects (background task
                    // tracking) by the background reader; calling ParseProgress again is safe
                    // because all mutations are idempotent (ConcurrentDictionary set/remove).
                    // We still call it here to get the AgentProgress value for yielding.

                    var progress = ParseProgress(evt);

                    // Detect max-turns exhaustion: NumTurns == MaxTurns means
                    // Claude used all allocated turns and may have stopped mid-task
                    if (evt.Type == "result" && !progress.IsErrorResult && evt.NumTurns >= _config.MaxTurns)
                    {
                        progress = new AgentProgress
                        {
                            IsSignificant = progress.IsSignificant,
                            Summary = progress.Summary,
                            EventType = progress.EventType,
                            ToolName = progress.ToolName,
                            FinalResult = progress.FinalResult,
                            SessionId = progress.SessionId,
                            Stats = progress.Stats,
                            IsErrorResult = true,
                        };
                    }

                    yield return progress;

                    // "result" event means this response is complete — process stays alive
                    if (evt.Type == "result")
                    {
                        if (_restartRequested)
                        {
                            _restartRequested = false;
                            _logger.LogInformation("Performing deferred restart after task completion");
                            await KillProcessAsync();
                            _messageCount = 0;
                            _previousCumulativeStats = null;
                        }

                        _messageCount++;
                        var stats = ParseStats(evt);
                        if (stats is not null)
                        {
                            yield return new AgentProgress
                            {
                                IsSignificant = false,
                                Summary = "Execution stats",
                                EventType = "stats",
                                Stats = stats,
                            };
                        }
                        yield break;
                    }
                }

                if (!needsRetry) break;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Stop the persistent process (used by /reset and shutdown).
    /// Next call to ExecuteAsync will start a fresh process.
    /// </summary>
    public async Task StopProcessAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            await KillProcessAsync();
            _messageCount = 0;
            _previousCumulativeStats = null;
            _lastSessionId = null;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<bool> TryStopProcessAsync()
    {
        if (!await _sendLock.WaitAsync(TimeSpan.Zero))
            return false;
        try
        {
            await KillProcessAsync();
            _messageCount = 0;
            _previousCumulativeStats = null;
            _lastSessionId = null;
            return true;
        }
        finally { _sendLock.Release(); }
    }

    public void RequestRestart()
    {
        _restartRequested = true;
        _logger.LogInformation("Process restart requested (deferred until current task completes)");
    }

    public async ValueTask DisposeAsync()
    {
        await KillProcessAsync();
        _sendLock.Dispose();
    }

    // --- Stdout channel helpers ---

    /// <summary>
    /// Drain any events buffered in <see cref="_eventChannel"/> that arrived between
    /// turns (e.g. background subagent task_notification + result events emitted after
    /// the main turn completed). System events were already processed inline by the
    /// background reader; non-system stale events — in particular stale "result" events
    /// emitted when a background subtask completes — are discarded here so they cannot
    /// terminate the next turn's read loop prematurely.
    /// Must be called while holding <see cref="_sendLock"/>.
    /// </summary>
    private void DrainStaleTurnEvents()
    {
        if (_eventChannel is null) return;
        var discarded = 0;
        while (_eventChannel.Reader.TryRead(out var stale))
        {
            if (stale.Type != "system")
                discarded++;
        }
        if (discarded > 0)
            _logger.LogInformation("Drained {Count} stale non-system event(s) from stdout channel before new turn", discarded);
    }

    /// <summary>
    /// Long-running background task for the lifetime of the claude process.
    /// Reads every NDJSON line from stdout and writes the parsed event to
    /// <see cref="_eventChannel"/>.  System events are also processed inline so
    /// <see cref="_backgroundTasks"/> stays accurate between turns (the heartbeat
    /// reads it independently of any active turn).
    /// When the process exits or the token is cancelled, the channel writer is
    /// completed so any blocked ReadAsync in ExecuteAsync/SendCommandAsync returns.
    /// </summary>
    private async Task ReadStdoutLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _stdout!.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null) break; // process exited
                if (string.IsNullOrWhiteSpace(line)) continue;

                ClaudeStreamEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<ClaudeStreamEvent>(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug("Skipping non-JSON stdout line: {Line} ({Error})", line, ex.Message);
                    continue;
                }

                if (evt is null) continue;

                if (evt.SessionId is not null)
                    _lastSessionId = evt.SessionId;

                // Process system events (background task lifecycle) immediately so
                // _backgroundTasks is current even when no turn is active.
                if (evt.Type == "system")
                    ParseProgress(evt);

                // Buffer for the active turn's read loop. Stale events that land
                // between turns will be drained by DrainStaleTurnEvents().
                _eventChannel!.Writer.TryWrite(evt);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background stdout reader exited unexpectedly");
        }
        finally
        {
            _eventChannel?.Writer.TryComplete();
            _logger.LogDebug("Background stdout reader stopped");
        }
    }

    // --- Process lifecycle ---

    private void EnsureProcess()
    {
        if (_process is not null && !_process.HasExited)
            return;

        // Process died or first start — clean up old state
        var resumeId = _lastSessionId;
        _process?.Dispose();
        _process = null;
        _stdin = null;
        _stdout = null;
        _messageCount = 0;
        _previousCumulativeStats = null;

        var args = BuildArgs(resumeId);
        _logger.LogInformation("Starting persistent claude process: {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = args,
            WorkingDirectory = _config.WorkDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process");

        _stdin = _process.StandardInput;
        _stdin.AutoFlush = false;
        _stdout = _process.StandardOutput;

        // Read stderr in background for logging (lives for process lifetime)
        var stderr = _process.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await stderr.ReadLineAsync(CancellationToken.None);
                    if (line is null) break;
                    _logger.LogWarning("[claude stderr] {Line}", line);
                }
            }
            catch (ObjectDisposedException) { }
        });

        // Start continuous stdout reader — all NDJSON events flow through the channel
        // so stale between-turn events never corrupt the next turn's read loop.
        _readerCts = new CancellationTokenSource();
        _eventChannel = Channel.CreateUnbounded<ClaudeStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = Task.Run(() => ReadStdoutLoopAsync(_readerCts.Token));

        if (resumeId is not null)
            _logger.LogInformation("Restarted claude process with --resume {SessionId} (PID {Pid})", resumeId, _process.Id);
        else
            _logger.LogInformation("Started new claude process (PID {Pid})", _process.Id);
    }

    private async Task KillProcessAsync()
    {
        if (_process is null) return;

        // Cancel the background reader first so it stops reading from the pipe
        // before we close the process (avoids ObjectDisposedException races).
        if (_readerCts is not null)
        {
            await _readerCts.CancelAsync();
            _readerCts.Dispose();
            _readerCts = null;
        }
        _eventChannel = null;

        // Clear stale background task entries — the new process has no knowledge of
        // tasks that were running in the old process and will never emit task_notification
        // for them. Leaving them in would cause /status and the dashboard to show
        // zombie tasks indefinitely.
        _backgroundTasks.Clear();

        try
        {
            if (!_process.HasExited)
            {
                _logger.LogInformation("Killing claude process (PID {Pid})", _process.Id);
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { }
        catch (SystemException) { }

        _process.Dispose();
        _process = null;
        _stdin = null;
        _stdout = null;
    }

    // --- Argument building ---

    private string BuildArgs(string? resumeSessionId)
    {
        var sb = new StringBuilder();

        sb.Append("-p ");

        if (resumeSessionId is not null)
            sb.Append($"--resume \"{EscapeArg(resumeSessionId)}\" ");

        sb.Append("--input-format stream-json --output-format stream-json --verbose ");

        if (_config.AllowedTools.Count > 0)
        {
            var tools = string.Join(",", _config.AllowedTools);
            sb.Append($"--allowedTools \"{tools}\" ");
        }

        sb.Append($"--model {_config.Model} ");
        sb.Append($"--max-turns {_config.MaxTurns} ");
        sb.Append($"--permission-mode {_config.PermissionMode} ");

        if (!string.IsNullOrWhiteSpace(_config.Effort))
            sb.Append($"--effort {_config.Effort} ");

        if (!string.IsNullOrWhiteSpace(_config.JsonSchema))
            sb.Append($"--json-schema \"{EscapeArg(_config.JsonSchema)}\" ");

        if (!string.IsNullOrWhiteSpace(_config.AgentsJson))
            sb.Append($"--agents \"{EscapeArg(_config.AgentsJson)}\" ");

        // Load MCP config from workspace if it exists
        var mcpConfigPath = Path.Combine(_config.WorkDir, ".mcp.json");
        if (File.Exists(mcpConfigPath))
        {
            sb.Append($"--mcp-config \"{EscapeArg(mcpConfigPath)}\" ");
        }

        var systemPrompt = _promptBuilder.BuildSystemPrompt();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append($"--append-system-prompt \"{EscapeArg(systemPrompt)}\" ");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Send a raw command (e.g. /compact) directly to the claude process stdin.
    /// The command is NOT wrapped in stream-json — it's sent as plain text,
    /// which claude CLI interprets as a slash command.
    /// </summary>
    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);

        try
        {
            if (_process is null || _process.HasExited)
            {
                yield return new AgentProgress
                {
                    IsSignificant = true,
                    Summary = "No running process. Send a task first.",
                    EventType = "error",
                };
                yield break;
            }

            _logger.LogInformation("Sending command to claude stdin: {Command}", command);

            // Discard any stale between-turn events before writing (same as ExecuteAsync).
            DrainStaleTurnEvents();

            // Wrap as a user message containing the slash command
            var message = JsonSerializer.Serialize(new
            {
                type = "user",
                message = new { role = "user", content = command }
            });
            await _stdin!.WriteLineAsync(message.AsMemory(), ct);
            await _stdin.FlushAsync();

            // Read response events until "result" — via the channel, not _stdout directly.
            while (!ct.IsCancellationRequested)
            {
                ClaudeStreamEvent? evt;
                try
                {
                    evt = await _eventChannel!.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (ChannelClosedException) { break; } // process died

                var progress = ParseProgress(evt);
                yield return progress;

                if (evt.Type == "result")
                {
                    _messageCount++;
                    var stats = ParseStats(evt);
                    if (stats is not null)
                    {
                        yield return new AgentProgress
                        {
                            IsSignificant = false,
                            Summary = "Execution stats",
                            EventType = "stats",
                            Stats = stats,
                        };
                    }
                    yield break;
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // --- Event parsing ---

    /// <summary>
    /// Extract token usage stats from the result event's ExtensionData.
    /// Returns per-turn deltas (subtracting previous cumulative stats),
    /// with CumulativeInputTokens set for ctx% calculation.
    /// </summary>
    private ExecutionStats? ParseStats(ClaudeStreamEvent evt)
    {
        if (evt.ExtensionData is null) return null;

        var cumulative = new ExecutionStats();
        var found = false;

        // Try modelUsage first (has contextWindow)
        if (evt.ExtensionData.TryGetValue("modelUsage", out var modelUsageObj)
            && modelUsageObj is JsonElement modelUsage && modelUsage.ValueKind == JsonValueKind.Object)
        {
            // modelUsage is keyed by model name — take the first entry
            foreach (var model in modelUsage.EnumerateObject())
            {
                var m = model.Value;
                cumulative.InputTokens = m.TryGetProperty("inputTokens", out var it) ? it.GetInt32() : 0;
                cumulative.OutputTokens = m.TryGetProperty("outputTokens", out var ot) ? ot.GetInt32() : 0;
                cumulative.CacheReadTokens = m.TryGetProperty("cacheReadInputTokens", out var cr) ? cr.GetInt32() : 0;
                cumulative.CacheCreationTokens = m.TryGetProperty("cacheCreationInputTokens", out var cc) ? cc.GetInt32() : 0;
                cumulative.ContextWindow = m.TryGetProperty("contextWindow", out var cw) ? cw.GetInt32() : 0;
                cumulative.CostUsd = m.TryGetProperty("costUSD", out var cost) ? cost.GetDecimal() : 0;
                found = true;
                break;
            }
        }

        if (evt.ExtensionData.TryGetValue("total_cost_usd", out var totalCostObj)
            && totalCostObj is JsonElement totalCost && totalCost.TryGetDecimal(out var tc))
        {
            cumulative.CostUsd = tc;
            found = true;
        }

        if (evt.ExtensionData.TryGetValue("duration_ms", out var durationObj)
            && durationObj is JsonElement duration && duration.TryGetInt32(out var dm))
        {
            cumulative.DurationMs = dm;
            found = true;
        }

        if (evt.NumTurns is not null)
            cumulative.NumTurns = evt.NumTurns.Value;

        if (!found) return null;

        // Compute per-turn deltas
        var prev = _previousCumulativeStats;
        var delta = new ExecutionStats
        {
            InputTokens = cumulative.InputTokens - (prev?.InputTokens ?? 0),
            OutputTokens = cumulative.OutputTokens - (prev?.OutputTokens ?? 0),
            CacheReadTokens = cumulative.CacheReadTokens - (prev?.CacheReadTokens ?? 0),
            CacheCreationTokens = cumulative.CacheCreationTokens - (prev?.CacheCreationTokens ?? 0),
            CostUsd = cumulative.CostUsd - (prev?.CostUsd ?? 0),
            ContextWindow = cumulative.ContextWindow,
            DurationMs = cumulative.DurationMs,
            NumTurns = cumulative.NumTurns,
        };

        _previousCumulativeStats = cumulative;
        return delta;
    }

    private AgentProgress ParseProgress(ClaudeStreamEvent evt)
    {
        // Handle system events — route subtype-specific ones before the generic case
        if (evt.Type == "system")
        {
            switch (evt.Subtype)
            {
                case "task_started":
                    if (evt.TaskId is not null)
                    {
                        var info = new BackgroundTaskInfo
                        {
                            TaskId      = evt.TaskId,
                            Description = evt.Description ?? evt.TaskId,
                            TaskType    = evt.TaskType    ?? "unknown",
                            StartedAt   = DateTimeOffset.UtcNow,
                        };
                        _backgroundTasks[evt.TaskId] = info;
                        _logger.LogInformation("Background task started: {TaskId} ({TaskType}) — {Description}",
                            evt.TaskId, info.TaskType, info.Description);
                    }
                    return new AgentProgress
                    {
                        IsSignificant = false,
                        Summary       = $"Background task started: {evt.Description ?? evt.TaskId}",
                        EventType     = evt.Type,
                        SessionId     = evt.SessionId,
                    };

                case "task_progress":
                    if (evt.TaskId is not null && _backgroundTasks.TryGetValue(evt.TaskId, out var progressTask))
                        progressTask.Summary = evt.TaskSummary;
                    return new AgentProgress
                    {
                        IsSignificant = false,
                        Summary       = $"Background task progress: {evt.TaskSummary ?? evt.TaskId}",
                        EventType     = evt.Type,
                        SessionId     = evt.SessionId,
                    };

                case "task_notification":
                    if (evt.TaskId is not null)
                        _backgroundTasks.TryRemove(evt.TaskId, out _);
                    _logger.LogInformation("Background task {Status}: {TaskId} — {Summary}",
                        evt.TaskStatus, evt.TaskId, evt.TaskSummary);
                    return new AgentProgress
                    {
                        IsSignificant = false,
                        Summary       = $"Background task {evt.TaskStatus}: {evt.TaskSummary ?? evt.TaskId}",
                        EventType     = evt.Type,
                        SessionId     = evt.SessionId,
                    };

                case "init":
                    _logger.LogDebug("System event: init (TaskId={TaskId})", evt.TaskId);
                    return new AgentProgress
                    {
                        IsSignificant = false,
                        Summary       = "Session initialized",
                        EventType     = evt.Type,
                        SessionId     = evt.SessionId,
                    };

                case "api_retry":
                    // Claude CLI emits this when it backs off and retries upstream API calls.
                    // Benign; just note it at debug and keep streaming.
                    _logger.LogDebug("System event: api_retry (TaskId={TaskId})", evt.TaskId);
                    return new AgentProgress
                    {
                        IsSignificant = false,
                        Summary       = "API retry",
                        EventType     = evt.Type,
                        SessionId     = evt.SessionId,
                    };

                default:
                    if (evt.Subtype is not null)
                        _logger.LogDebug("Unhandled system event subtype: {Subtype} (TaskId={TaskId})", evt.Subtype, evt.TaskId);
                    return new AgentProgress
                    {
                        IsSignificant = false,
                        Summary       = $"System event: {evt.Subtype ?? "unknown"}",
                        EventType     = evt.Type,
                        SessionId     = evt.SessionId,
                    };
            }
        }

        return evt.Type switch
        {
            "assistant" => ParseAssistantEvent(this, evt),

            "result" => new AgentProgress
            {
                IsSignificant = true,
                Summary = !string.IsNullOrEmpty(evt.Result)
                    ? TruncateText(evt.Result, 500)
                    : "Task completed",
                EventType = evt.Type,
                FinalResult = evt.Result,
                SessionId = evt.SessionId,
                IsErrorResult = evt.IsError == true,
                StructuredOutput = evt.StructuredOutput.HasValue
                    ? evt.StructuredOutput.Value.GetRawText()
                    : null,
            },

            _ => new AgentProgress
            {
                IsSignificant = false,
                Summary = $"Event: {evt.Type}",
                EventType = evt.Type,
            },
        };
    }

    private static AgentProgress ParseAssistantEvent(ClaudeExecutor self, ClaudeStreamEvent evt)
    {
        var blocks = evt.Message?.Content;
        if (blocks is null or [])
            return new AgentProgress { IsSignificant = false, Summary = "Assistant (empty)", EventType = evt.Type };

        // Check for tool_use blocks
        var toolUse = blocks.FirstOrDefault(b => b.Type == "tool_use");
        if (toolUse is not null)
        {
            var argsJson = toolUse.Input is not null
                ? TruncateText(JsonSerializer.Serialize(toolUse.Input, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), 500)
                : "{}";
            self._logger.LogInformation("Tool call: {ToolName}({Args})", toolUse.Name ?? "unknown", argsJson);

            return new AgentProgress
            {
                IsSignificant = true,
                Summary = DescribeToolUse(toolUse.Name ?? "unknown", toolUse.Input),
                EventType = evt.Type,
                ToolName = toolUse.Name,
                ToolArgs = argsJson,
            };
        }

        // Extract text blocks
        var text = string.Join("\n", blocks.Where(b => b.Type == "text" && b.Text is not null).Select(b => b.Text!));
        if (!string.IsNullOrEmpty(text))
        {
            return new AgentProgress
            {
                IsSignificant = true,
                Summary = TruncateText(text, 500),
                EventType = evt.Type,
                FinalResult = text,
            };
        }

        return new AgentProgress { IsSignificant = false, Summary = "Assistant event", EventType = evt.Type };
    }

    private static string DescribeToolUse(string name, Dictionary<string, object>? input)
    {
        string? GetInput(string key) =>
            input is not null && input.TryGetValue(key, out var val) ? val?.ToString() : null;

        switch (name)
        {
            case "Read":
                if (GetInput("file_path") is { } readPath) return $"Reading {readPath}";
                break;
            case "Write":
                if (GetInput("file_path") is { } writePath) return $"Writing {writePath}";
                break;
            case "Edit":
                if (GetInput("file_path") is { } editPath) return $"Editing {editPath}";
                break;
            case "Bash":
                if (GetInput("command") is { } cmd)
                {
                    cmd = cmd.ReplaceLineEndings(" ").Trim();
                    if (cmd.Length > 80) cmd = cmd[..80] + "...";
                    return $"Running: {cmd}";
                }
                break;
            case "Grep":
                if (GetInput("pattern") is { } grepPattern) return $"Searching for \"{grepPattern}\"";
                break;
            case "Glob":
                if (GetInput("pattern") is { } globPattern) return $"Finding files: {globPattern}";
                break;
            case "WebFetch":
                if (GetInput("url") is { } url) return $"Fetching {url}";
                break;
            case "WebSearch":
                if (GetInput("query") is { } query) return $"Searching web: \"{query}\"";
                break;
            case "Task":
            case "Agent":
                if (GetInput("description") is { } desc) return $"Agent: {desc}";
                break;
            case "TodoWrite":
                return "Updating task list";
        }

        // MCP tools: mcp__serverName__toolName -> "Using toolName"
        if (name.StartsWith("mcp__"))
        {
            var lastSep = name.LastIndexOf("__");
            if (lastSep > 4)
                return $"Using {name[(lastSep + 2)..]}";
        }

        return $"Using tool: {name}";
    }

    private static string EscapeArg(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string TruncateText(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
