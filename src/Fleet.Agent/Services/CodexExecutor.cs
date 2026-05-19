using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Manages a persistent <c>codex app-server --listen stdio://</c> process and
/// speaks the JSON-RPC 2.0 protocol over stdin/stdout.
/// Warm state is bounded to the lifetime of the current process only: when the
/// process dies or is restarted, the next task starts a fresh ephemeral thread.
/// </summary>
public sealed class CodexExecutor : IAgentExecutor
{
    private readonly AgentOptions _config;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<CodexExecutor> _logger;
    private readonly string _normalizedAttachmentDir;

    private Process? _process;
    private StreamWriter? _stdin;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<RpcOutcome>> _pendingRequests = new();
    private Channel<JsonObject>? _notificationChannel;
    private CancellationTokenSource? _readerCts;
    private long _nextRequestId;

    private string? _threadId;
    private string? _activeTurnId;
    private ThreadTokenUsageSnapshot? _lastTurnUsage;
    private int _messageCount;
    // Accumulates assistant text from item/completed notifications of type "agentMessage".
    // Production codex app-server delivers the reply text here — NOT in turn.items of
    // turn/completed (turn.items is always empty on the real wire protocol).
    // Reset at the start of every new turn and cleared on process stop.
    private string _currentTurnAssistantText = "";
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private volatile bool _restartRequested;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;

    private const string CodexBin = "codex";
    private const string InitializedMethod = "initialized";
    private const string ThreadShellCommandMethod = "thread/shellCommand";
    private const string ClientName = "phleet";
    private const string ClientVersion = "0.1.0";
    private const int StartupRetryBudget = 3;
    private static readonly TimeSpan InterruptDrainTimeout = TimeSpan.FromSeconds(3);

    public string? LastSessionId => _threadId;
    public DateTimeOffset LastActivity => _lastActivity;
    public bool IsProcessWarm => _process is not null && !_process.HasExited && _messageCount > 0;

    /// <summary>
    /// Always returns empty for codex-provider agents. The codex app-server v2 protocol's
    /// <c>item/started</c>/<c>item/completed</c> notifications do not map to Claude's
    /// background-subagent task semantics. <c>/status</c> and <c>/cancel_bg</c> commands
    /// will report no background tasks for this provider.
    /// </summary>
    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        Array.Empty<BackgroundTaskInfo>();

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public CodexExecutor(
        IOptions<AgentOptions> config,
        IOptions<TelegramOptions> telegramConfig,
        PromptBuilder promptBuilder,
        ILogger<CodexExecutor> logger)
        : this(config, telegramConfig, promptBuilder, logger, Process.Start)
    {
    }

    internal CodexExecutor(
        IOptions<AgentOptions> config,
        IOptions<TelegramOptions> telegramConfig,
        PromptBuilder promptBuilder,
        ILogger<CodexExecutor> logger,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _logger = logger;
        _normalizedAttachmentDir = Path.GetFullPath(telegramConfig.Value.AttachmentDir);
        _processStarter = processStarter;
    }

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;

        // Serialize concurrent ExecuteAsync calls: the second caller waits here until
        // the first turn completes, mirroring ClaudeExecutor's _sendLock.WaitAsync pattern.
        // This replaces the previous single-flight throw (G2 deviation) with graceful queuing.
        await _turnLock.WaitAsync(ct);

        string? turnId = null;
        var (forwardedPaths, skippedCount) = CollectImagePaths(images);

        Exception? startupError = null;

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                await EnsureProcessReadyAsync(ct);

                // Sanity assert: _turnLock ensures only one ExecuteAsync runs at a time,
                // so _activeTurnId should always be null here. If not, something cleared
                // it incorrectly — treat as a hard error rather than silently proceeding.
                if (_activeTurnId is not null)
                    throw new InvalidOperationException("CodexExecutor: _activeTurnId non-null despite _turnLock held — state corruption.");

                var startParams = new JsonObject
                {
                    ["threadId"] = _threadId!,
                    ["input"] = BuildUserInputs(task, forwardedPaths),
                };

                var response = await SendRequestAsync("turn/start", startParams, ct);
                var turn = response.RequireObject("turn");
                turnId = turn.RequireString("id");
                _activeTurnId = turnId;
                _lastTurnUsage = null;
                _currentTurnAssistantText = "";
            }
            catch (RpcErrorException ex)
            {
                // Catch all RPC errors per G6 mapping table. Session errors trigger a cold
                // restart so the next task begins with a fresh thread.
                LogRpcError(ex);
                if (ex.IsSessionError) RequestRestart();
                startupError = ex;
            }
            finally
            {
                _sendLock.Release();
            }

            if (startupError is not null)
            {
                yield return BuildRpcErrorProgress((RpcErrorException)startupError);
                yield break;
            }

            if (skippedCount > 0)
            {
                yield return new AgentProgress
                {
                    EventType = "warning",
                    Summary = $"Codex: {skippedCount} image(s) skipped — no persisted file path or file not found.",
                    IsSignificant = true,
                };
            }

            await foreach (var progress in StreamTurnAsync(turnId!, ct))
            {
                _lastActivity = DateTimeOffset.UtcNow;
                yield return progress;
                if (progress.FinalResult is not null) yield break;
            }
        }
        finally
        {
            _turnLock.Release();
        }
    }

    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);

        Exception? commandError = null;

        try
        {
            await EnsureProcessReadyAsync(ct);

            // Same single-flight guard as ExecuteAsync — see comment there.
            if (_activeTurnId is not null)
                throw new InvalidOperationException("CodexExecutor refused to start a shell command while a turn is already active.");

            var shellParams = new JsonObject
            {
                ["threadId"] = _threadId!,
                ["command"] = command,
            };

            // Intentional: `thread/shellCommand` is the v2 thread-scoped shell entrypoint.
            // It preserves shell syntax (pipes, redirects, quoting) unlike `command/exec`.
            await SendRequestAsync(ThreadShellCommandMethod, shellParams, ct);
            _lastTurnUsage = null;
            _currentTurnAssistantText = "";
        }
        catch (RpcErrorException ex)
        {
            LogRpcError(ex);
            if (ex.IsSessionError) RequestRestart();
            commandError = ex;
        }
        finally
        {
            _sendLock.Release();
        }

        if (commandError is not null)
        {
            yield return BuildRpcErrorProgress((RpcErrorException)commandError);
            yield break;
        }

        await foreach (var progress in StreamTurnAsync(expectedTurnId: null, ct))
        {
            _lastActivity = DateTimeOffset.UtcNow;
            yield return progress;
            if (progress.FinalResult is not null) yield break;
        }
    }

    public void RequestRestart() => _restartRequested = true;

    public async Task StopProcessAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            await StopInternalAsync();
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
            if (_process is null && _threadId is null)
                return false;

            await StopInternalAsync();
            return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopInternalAsync();
        _sendLock.Dispose();
        _turnLock.Dispose();
    }

    internal Task EnsureProcessReadyForTestsAsync(CancellationToken ct = default) =>
        EnsureProcessReadyAsync(ct);

    internal IAsyncEnumerable<AgentProgress> StreamTurnForTests(string? expectedTurnId, CancellationToken ct = default) =>
        StreamTurnAsync(expectedTurnId, ct);

    internal void SetNotificationChannelForTests(Channel<JsonObject> channel) =>
        _notificationChannel = channel;

    internal void SetThreadStateForTests(string? threadId, string? activeTurnId)
    {
        _threadId = threadId;
        _activeTurnId = activeTurnId;
    }

    internal string? ActiveTurnIdForTests => _activeTurnId;

    internal SemaphoreSlim TurnLockForTests => _turnLock;

    internal AgentProgress? BuildItemStartedProgressForTests(JsonObject @params) =>
        BuildItemStartedProgress(@params);

    internal (List<string> ForwardedPaths, int SkippedCount) CollectImagePaths(IReadOnlyList<MessageImage>? images)
    {
        if (images is not { Count: > 0 })
            return ([], 0);

        var forwarded = new List<string>(images.Count);
        var skipped = 0;

        foreach (var img in images)
        {
            if (string.IsNullOrEmpty(img.FilePath))
            {
                skipped++;
                continue;
            }

            string normalized;
            try { normalized = Path.GetFullPath(img.FilePath); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CodexExecutor: image path '{Path}' is invalid — skipping", img.FilePath);
                skipped++;
                continue;
            }

            // Only allow files inside AttachmentDir. This blocks path-prefix tricks and
            // traversal like `/attachments/../secret` after full-path normalization.
            if (!normalized.StartsWith(_normalizedAttachmentDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(normalized, _normalizedAttachmentDir, StringComparison.Ordinal))
            {
                _logger.LogError(
                    "CodexExecutor: image path '{Path}' is outside AttachmentDir '{Dir}' — skipping",
                    img.FilePath, _normalizedAttachmentDir);
                skipped++;
                continue;
            }

            if (!File.Exists(img.FilePath))
            {
                skipped++;
                continue;
            }

            forwarded.Add(img.FilePath);
        }

        return (forwarded, skipped);
    }

    internal static string NormalizeSandboxMode(string? sandboxMode) =>
        sandboxMode switch
        {
            null or "" => "danger-full-access",
            "read-only" => "read-only",
            "workspace-write" => "workspace-write",
            "danger-full-access" => "danger-full-access",
            _ => "danger-full-access",
        };

    internal static JsonArray BuildUserInputs(string task, IReadOnlyList<string> imagePaths)
    {
        var inputs = new JsonArray();
        foreach (var path in imagePaths)
        {
            inputs.Add(new JsonObject
            {
                ["type"] = "localImage",
                ["path"] = path,
            });
        }

        inputs.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = task,
            // The v2 UserInput schema defines text_elements with default [].
            ["text_elements"] = new JsonArray(),
        });

        return inputs;
    }

    private async Task EnsureProcessReadyAsync(CancellationToken ct)
    {
        Exception? lastError = null;

        // 3-strike budget is in-memory and resets on each call (not on container restart).
        // This is intentional for now: the budgeted exhaustion gate is primarily a guard
        // against tight boot-loop crashes within a single agent lifetime, not across
        // container restarts. Each call gets a fresh 3 attempts.
        for (var attempt = 1; attempt <= StartupRetryBudget; attempt++)
        {
            try
            {
                if (_restartRequested || _process is null || _process.HasExited)
                {
                    _restartRequested = false;
                    await StopInternalAsync();
                    await StartProcessAsync(ct);
                }

                if (_threadId is null)
                    await InitializeThreadAsync(ct);

                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "CodexExecutor startup attempt {Attempt}/{Budget} failed", attempt, StartupRetryBudget);
                await StopInternalAsync();
            }
        }

        throw new InvalidOperationException(
            $"CodexExecutor startup failed after {StartupRetryBudget} attempts: {lastError?.Message}",
            lastError);
    }

    private Task StartProcessAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CodexBin,
            Arguments = "app-server --listen stdio://",
            WorkingDirectory = _config.WorkDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _process = _processStarter(psi) ?? throw new InvalidOperationException("Failed to start codex app-server");
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _notificationChannel = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadStdoutAsync(_process.StandardOutput, _notificationChannel.Writer, _readerCts.Token));
        _ = Task.Run(() => ReadStderrAsync(_process.StandardError, _readerCts.Token));

        _threadId = null;
        _activeTurnId = null;
        _lastTurnUsage = null;
        _messageCount = 0;
        _logger.LogInformation("CodexExecutor: app-server started (pid {Pid})", _process.Id);
        return Task.CompletedTask;
    }

    private async Task InitializeThreadAsync(CancellationToken ct)
    {
        var initResult = await SendRequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = ClientName,
                ["title"] = "Phleet",
                ["version"] = ClientVersion,
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = false,
            },
        }, ct);

        _logger.LogDebug(
            "CodexExecutor initialized (codexHome={Home}, platform={Family}/{Os})",
            initResult["codexHome"]?.ToString(),
            initResult["platformFamily"]?.ToString(),
            initResult["platformOs"]?.ToString());

        await SendNotificationAsync(InitializedMethod, null, ct);

        var systemPromptPath = _promptBuilder.WriteSystemPromptFile();
        var baseInstructions = await File.ReadAllTextAsync(systemPromptPath, ct);
        var sandboxMode = NormalizeSandboxMode(_config.CodexSandboxMode);

        var threadResponse = await SendRequestAsync("thread/start", new JsonObject
        {
            ["model"] = _config.Model,
            ["cwd"] = _config.WorkDir,
            ["approvalPolicy"] = "never",
            ["sandbox"] = sandboxMode,
            ["serviceName"] = ClientName,
            ["baseInstructions"] = baseInstructions,
            ["ephemeral"] = true,
        }, ct);

        var thread = threadResponse.RequireObject("thread");
        _threadId = thread.RequireString("id");
        var ephemeral = thread["ephemeral"]?.GetValue<bool>() ?? false;
        var path = thread["path"];
        if (!ephemeral || path is not null && path.GetValueKindSafe() != JsonValueKind.Null)
        {
            _logger.LogWarning(
                "CodexExecutor thread/start returned unexpected persistence state (ephemeral={Ephemeral}, path={Path})",
                ephemeral, path?.ToJsonString());
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader, ChannelWriter<JsonObject> writer, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "CodexExecutor: malformed JSON-RPC line from app-server: {Line}", line);
                    continue;
                }

                if (node is not JsonObject obj)
                    continue;

                if (TryGetRequestId(obj, out var requestId))
                {
                    // _pendingRequests is only written from this loop (single reader) and
                    // from CancellationToken registrations via TryRemove. ConcurrentDictionary
                    // makes both paths safe. Do not add a second stdout reader without
                    // revisiting this assumption.
                    if (_pendingRequests.TryRemove(requestId, out var tcs))
                        tcs.TrySetResult(new RpcOutcome(obj["result"] as JsonObject, obj["error"] as JsonObject));
                    continue;
                }

                if (obj["method"] is JsonValue)
                    await writer.WriteAsync(obj, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CodexExecutor stdout reader stopped");
        }
        finally
        {
            FailPendingRequests(new InvalidOperationException("Codex app-server stdout reader stopped before the request completed."));
            writer.TryComplete();
        }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.LogWarning("[codex stderr] {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task<JsonObject> SendRequestAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        if (_stdin is null)
            throw new InvalidOperationException("CodexExecutor stdin is not available.");

        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<RpcOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };

        if (@params is not null)
            message["params"] = @params;

        await _stdin.WriteLineAsync(message.ToJsonString());

        using var registration = ct.Register(() =>
        {
            if (_pendingRequests.TryRemove(id, out var pending))
                pending.TrySetCanceled(ct);
        });

        var outcome = await tcs.Task;
        if (outcome.Error is not null)
        {
            var rpcError = RpcErrorException.From(method, outcome.Error);
            // Auth failures request a restart here so the token is re-read on next attempt.
            // Full error logging and G6 classification happen in the caller catch blocks.
            if (rpcError.IsAuthFailure)
                RequestRestart();
            throw rpcError;
        }

        return outcome.Result ?? new JsonObject();
    }

    private async Task SendNotificationAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        if (_stdin is null)
            throw new InvalidOperationException("CodexExecutor stdin is not available.");

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (@params is not null)
            message["params"] = @params;

        await _stdin.WriteLineAsync(message.ToJsonString());
    }

    private async IAsyncEnumerable<AgentProgress> StreamTurnAsync(
        string? expectedTurnId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_notificationChannel is null)
            yield break;

        var resolvedTurnId = expectedTurnId;

        while (true)
        {
            JsonObject? notification = null;
            var channelClosed = false;
            try
            {
                notification = await _notificationChannel.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                if (resolvedTurnId is not null)
                    await DrainInterruptedTurnAsync(resolvedTurnId);
                throw;
            }
            catch (ChannelClosedException)
            {
                channelClosed = true;
            }

            if (channelClosed)
            {
                _activeTurnId = null;
                yield return new AgentProgress
                {
                    EventType = "result",
                    Summary = "Codex app-server exited unexpectedly",
                    FinalResult = "Codex app-server exited unexpectedly",
                    IsErrorResult = true,
                    IsSignificant = true,
                };
                yield break;
            }

            if (notification is null)
                continue;

            var method = notification["method"]?.GetValue<string>();
            var @params = notification["params"] as JsonObject;
            if (method is null || @params is null)
                continue;

            if (method == "thread/tokenUsage/updated")
            {
                var turnId = @params["turnId"]?.GetValue<string>();
                if (resolvedTurnId is not null && turnId == resolvedTurnId)
                    _lastTurnUsage = ParseTokenUsage(@params["tokenUsage"] as JsonObject);
                continue;
            }

            if (method == "turn/started")
            {
                var turn = @params["turn"] as JsonObject;
                var startedTurnId = turn?["id"]?.GetValue<string>();
                if (resolvedTurnId is null)
                {
                    resolvedTurnId = startedTurnId;
                    _activeTurnId = resolvedTurnId;
                }

                if (startedTurnId == resolvedTurnId)
                {
                    yield return new AgentProgress
                    {
                        EventType = "system",
                        Summary = "Processing...",
                        IsSignificant = false,
                    };
                }
                continue;
            }

            if (resolvedTurnId is null)
            {
                var discovered = @params["turnId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(discovered))
                {
                    resolvedTurnId = discovered;
                    _activeTurnId = discovered;
                }
            }

            if (!AppliesToTurn(@params, resolvedTurnId))
                continue;

            var progress = MapNotification(method, @params, resolvedTurnId!);
            if (progress is not null)
            {
                yield return progress;
                if (progress.FinalResult is not null)
                    yield break;
            }
        }
    }

    private AgentProgress? MapNotification(string method, JsonObject @params, string turnId)
    {
        return method switch
        {
            "thread/started" => new AgentProgress
            {
                EventType = "system",
                Summary = "Connected",
                SessionId = _threadId,
                IsSignificant = false,
            },
            "item/agentMessage/delta" => new AgentProgress
            {
                EventType = "assistant",
                Summary = @params["delta"]?.GetValue<string>() ?? "",
                IsSignificant = true,
            },
            "item/started" => BuildItemStartedProgress(@params),
            "item/completed" => BuildItemCompletedProgress(@params),
            "turn/completed" => BuildTurnCompletedProgress(@params, turnId),
            _ => null,
        };
    }

    private AgentProgress? BuildItemStartedProgress(JsonObject @params)
    {
        var item = @params["item"] as JsonObject;
        var itemType = item?["type"]?.GetValue<string>();

        AgentProgress? progress = itemType switch
        {
            "commandExecution" => new AgentProgress
            {
                EventType = "tool_use",
                Summary = $"Using {item?["command"]?.GetValue<string>() ?? "command"}",
                ToolName = item?["command"]?.GetValue<string>(),
                ToolArgs = item?["args"]?.ToJsonString() ?? item?["command"]?.GetValue<string>() ?? "{}",
                IsSignificant = true,
            },
            "mcpToolCall" => new AgentProgress
            {
                EventType = "tool_use",
                Summary = $"Using {item?["tool"]?.GetValue<string>() ?? "mcp tool"}",
                ToolName = item?["tool"]?.GetValue<string>(),
                ToolArgs = item?["arguments"]?.ToJsonString(),
                IsSignificant = true,
            },
            "dynamicToolCall" => new AgentProgress
            {
                EventType = "tool_use",
                Summary = $"Using {item?["tool"]?.GetValue<string>() ?? "tool"}",
                ToolName = item?["tool"]?.GetValue<string>(),
                ToolArgs = item?["arguments"]?.ToJsonString(),
                IsSignificant = true,
            },
            _ => null,
        };

        if (progress is not null)
        {
            var toolName = progress.ToolName ?? itemType ?? "unknown";
            var rawArgs = progress.ToolArgs ?? "";
            var argsPreview = rawArgs.Length > 200 ? rawArgs[..200] + "…" : rawArgs;
            _logger.LogInformation("[codex tool_use:{Tool}] {Args}", toolName, argsPreview);
        }

        return progress;
    }

    private AgentProgress? BuildItemCompletedProgress(JsonObject @params)
    {
        var item = @params["item"] as JsonObject;
        var itemType = item?["type"]?.GetValue<string>();

        if (itemType == "agentMessage")
        {
            // Production codex app-server delivers the assistant's full reply text here —
            // NOT inside turn.items of the subsequent turn/completed notification.
            // Accumulate it so BuildTurnCompletedProgress can return the correct FinalResult.
            // Streaming deltas are already surfaced via item/agentMessage/delta, so no
            // additional AgentProgress event is emitted here.
            var assistantText = item?["text"]?.GetValue<string>();
            if (assistantText is not null)
                _currentTurnAssistantText = assistantText;
            return null;
        }

        var text = itemType switch
        {
            "commandExecution" => item?["aggregatedOutput"]?.GetValue<string>() ?? "",
            "mcpToolCall" => ExtractMcpToolResultText(item),
            "dynamicToolCall" => ExtractDynamicToolResultText(item),
            _ => null,
        };

        return text is null ? null : new AgentProgress
        {
            EventType = "tool_result",
            Summary = text,
            IsSignificant = false,
        };
    }

    private AgentProgress BuildTurnCompletedProgress(JsonObject @params, string turnId)
    {
        var turn = @params.RequireObject("turn");
        var status = turn["status"]?.GetValue<string>() ?? "failed";
        // Primary: text accumulated from item/completed(agentMessage) notifications.
        // Fallback: scan turn.items in case a future protocol version re-populates it.
        var finalText = string.IsNullOrEmpty(_currentTurnAssistantText)
            ? ExtractAssistantText(turn)
            : _currentTurnAssistantText;
        _currentTurnAssistantText = "";
        var durationMs = turn["durationMs"]?.GetValue<int?>() ?? 0;
        var stats = _lastTurnUsage is null
            ? new ExecutionStats { DurationMs = durationMs }
            : new ExecutionStats
            {
                InputTokens = _lastTurnUsage.InputTokens,
                OutputTokens = _lastTurnUsage.OutputTokens,
                DurationMs = durationMs,
            };

            _activeTurnId = null;

        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            _messageCount++;
            return new AgentProgress
            {
                EventType = "result",
                Summary = finalText,
                FinalResult = finalText,
                SessionId = _threadId,
                Stats = stats,
                IsSignificant = true,
            };
        }

        var error = turn["error"] as JsonObject;
        var errorMessage = error?["message"]?.GetValue<string>() ?? (status == "interrupted" ? "Interrupted" : "Codex turn failed");
        return new AgentProgress
        {
            EventType = "result",
            Summary = errorMessage,
            FinalResult = errorMessage,
            SessionId = _threadId,
            Stats = stats,
            IsErrorResult = true,
            IsSignificant = true,
        };
    }

    private async Task InterruptTurnAsync(string turnId)
    {
        if (_stdin is null || _threadId is null)
            return;

        try
        {
            var id = Interlocked.Increment(ref _nextRequestId);
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "turn/interrupt",
                ["params"] = new JsonObject
                {
                    ["threadId"] = _threadId,
                    ["turnId"] = turnId,
                },
            };

            await _stdin.WriteLineAsync(message.ToJsonString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CodexExecutor failed to interrupt active turn {TurnId}", turnId);
        }
    }

    private async Task DrainInterruptedTurnAsync(string turnId)
    {
        _activeTurnId = null;
        await InterruptTurnAsync(turnId);

        if (_notificationChannel is null)
            return;

        using var drainCts = new CancellationTokenSource(InterruptDrainTimeout);

        try
        {
            while (true)
            {
                var notification = await _notificationChannel.Reader.ReadAsync(drainCts.Token);
                var method = notification["method"]?.GetValue<string>();
                var @params = notification["params"] as JsonObject;
                if (method is null || @params is null)
                    continue;

                if (method == "thread/tokenUsage/updated")
                {
                    var updateTurnId = @params["turnId"]?.GetValue<string>();
                    if (updateTurnId == turnId)
                        _lastTurnUsage = ParseTokenUsage(@params["tokenUsage"] as JsonObject);
                    continue;
                }

                if (!AppliesToTurn(@params, turnId))
                    continue;

                if (method == "turn/completed")
                {
                    _ = BuildTurnCompletedProgress(@params, turnId);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("CodexExecutor timed out draining interrupted turn {TurnId}", turnId);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("CodexExecutor notification channel closed while draining interrupted turn {TurnId}", turnId);
        }
    }

    private async Task StopInternalAsync()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch { }

            _process.Dispose();
            _process = null;
        }

        FailPendingRequests(new InvalidOperationException("Codex app-server stopped before the request completed."));

        _stdin?.Dispose();
        _stdin = null;
        _notificationChannel = null;
        _threadId = null;
        _activeTurnId = null;
        _lastTurnUsage = null;
        _currentTurnAssistantText = "";
        _messageCount = 0;
    }

    private void FailPendingRequests(Exception ex)
    {
        foreach (var (id, tcs) in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(id, out var pending))
                pending.TrySetException(ex);
            else
                tcs.TrySetException(ex);
        }
    }

    // G6 error mapping table — classify RpcErrorException and return structured AgentProgress.
    private AgentProgress BuildRpcErrorProgress(RpcErrorException ex)
    {
        if (ex.IsAuthFailure)
            return new AgentProgress
            {
                EventType = "result",
                Summary = "Codex authentication expired; restart requested while refreshed credentials are applied.",
                FinalResult = $"Codex authentication expired: {ex.Message}",
                IsErrorResult = true,
                IsSignificant = true,
            };

        if (ex.IsSessionError)
            return new AgentProgress
            {
                EventType = "result",
                Summary = "Codex session not found; cold restart requested for next task.",
                FinalResult = $"Codex session error: {ex.Message}",
                IsErrorResult = true,
                IsSignificant = true,
            };

        if (ex.IsContextError)
            return new AgentProgress
            {
                EventType = "result",
                Summary = "Codex context window or turn limit exceeded.",
                FinalResult = $"Codex context error: {ex.Message}",
                IsErrorResult = true,
                IsSignificant = true,
            };

        return new AgentProgress
        {
            EventType = "result",
            Summary = $"Codex RPC error ({ex.Method}): {ex.Message}",
            FinalResult = $"Codex RPC error: {ex.Message}",
            IsErrorResult = true,
            IsSignificant = true,
        };
    }

    private void LogRpcError(RpcErrorException ex)
    {
        if (ex.IsAuthFailure)
            _logger.LogWarning(
                "Codex auth error from {Method}; restart requested (statusCode={StatusCode}, action={Action})",
                ex.Method, ex.StatusCode, ex.Action ?? "none");
        else if (ex.IsSessionError)
            _logger.LogWarning("Codex session error from {Method}: {Message}", ex.Method, ex.Message);
        else if (ex.IsContextError)
            _logger.LogWarning("Codex context error from {Method}: {Message}", ex.Method, ex.Message);
        else
            _logger.LogError("Codex RPC error from {Method} (code={Code}): {Message}", ex.Method, ex.Code, ex.Message);
    }

    private static bool TryGetRequestId(JsonObject obj, out long id)
    {
        id = 0;
        if (obj["id"] is not JsonValue value)
            return false;

        try
        {
            id = value.GetValue<long>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AppliesToTurn(JsonObject @params, string? turnId) =>
        turnId is not null && string.Equals(@params["turnId"]?.GetValue<string>(), turnId, StringComparison.Ordinal)
        || turnId is not null && string.Equals((@params["turn"] as JsonObject)?["id"]?.GetValue<string>(), turnId, StringComparison.Ordinal);

    private static ThreadTokenUsageSnapshot? ParseTokenUsage(JsonObject? tokenUsage)
    {
        var last = tokenUsage?["last"] as JsonObject;
        if (last is null)
            return null;

        return new ThreadTokenUsageSnapshot(
            last["inputTokens"]?.GetValue<int>() ?? 0,
            last["outputTokens"]?.GetValue<int>() ?? 0);
    }

    private static string ExtractAssistantText(JsonObject turn)
    {
        if (turn["items"] is not JsonArray items)
            return "";

        string finalText = "";
        foreach (var itemNode in items)
        {
            if (itemNode is not JsonObject item)
                continue;

            if (item["type"]?.GetValue<string>() == "agentMessage")
                finalText = item["text"]?.GetValue<string>() ?? finalText;
        }

        return finalText;
    }

    private static string ExtractMcpToolResultText(JsonObject? item)
    {
        var result = item?["result"] as JsonObject;
        if (result?["content"] is JsonArray content)
        {
            var parts = new List<string>(content.Count);
            foreach (var partNode in content)
            {
                if (partNode is JsonObject part && part["text"] is JsonValue text)
                    parts.Add(text.GetValue<string>());
            }
            return string.Join("", parts);
        }

        return item?["error"]?["message"]?.GetValue<string>() ?? "";
    }

    private static string ExtractDynamicToolResultText(JsonObject? item)
    {
        if (item?["contentItems"] is not JsonArray items)
            return item?["success"]?.GetValue<bool>() == true ? "Tool completed." : "";

        var parts = new List<string>(items.Count);
        foreach (var partNode in items)
        {
            if (partNode is JsonObject part && part["text"] is JsonValue text)
                parts.Add(text.GetValue<string>());
        }
        return string.Join("", parts);
    }

    private sealed record RpcOutcome(JsonObject? Result, JsonObject? Error);

    private sealed record ThreadTokenUsageSnapshot(int InputTokens, int OutputTokens);

    private sealed class RpcErrorException(string method, long code, string message, JsonNode? errorData) : Exception(message)
    {
        public string Method { get; } = method;
        public long Code { get; } = code;
        public JsonNode? ErrorData { get; } = errorData;
        public string? ErrorCode { get; init; }
        public int? StatusCode { get; init; }
        public string? Action { get; init; }
        public bool IsAuthFailure =>
            string.Equals(ErrorCode, "Auth", StringComparison.OrdinalIgnoreCase)
            && StatusCode == 401
            && string.Equals(Action, "relogin", StringComparison.OrdinalIgnoreCase);

        // Thread-not-found: triggers cold restart so the next task starts a fresh thread.
        public bool IsSessionError =>
            (ErrorCode?.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ?? false)
            || Message.Contains("thread", StringComparison.OrdinalIgnoreCase) && Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

        // Context-window / turn-limit exceeded.
        public bool IsContextError =>
            (ErrorCode?.Contains("ContextWindow", StringComparison.OrdinalIgnoreCase) ?? false)
            || (ErrorCode?.Contains("TurnLimit", StringComparison.OrdinalIgnoreCase) ?? false)
            || Message.Contains("context window", StringComparison.OrdinalIgnoreCase);

        public static RpcErrorException From(string method, JsonObject error)
        {
            var code = error["code"]?.GetValue<long>() ?? 0;
            var message = error["message"]?.GetValue<string>() ?? "JSON-RPC error";
            var data = error["data"];
            var dataObject = data as JsonObject;
            return new RpcErrorException(method, code, message, data)
            {
                ErrorCode = dataObject?["errorCode"]?.GetValue<string>(),
                StatusCode = dataObject?["statusCode"]?.GetValue<int?>(),
                Action = dataObject?["action"]?.GetValue<string>(),
            };
        }
    }
}

file static class JsonNodeExtensions
{
    public static JsonObject RequireObject(this JsonObject obj, string propertyName) =>
        obj[propertyName] as JsonObject ?? throw new InvalidOperationException($"Expected object property '{propertyName}'.");

    public static string RequireString(this JsonObject obj, string propertyName) =>
        obj[propertyName]?.GetValue<string>() ?? throw new InvalidOperationException($"Expected string property '{propertyName}'.");

    public static JsonValueKind? GetValueKindSafe(this JsonNode? node)
    {
        if (node is null)
            return null;

        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.ValueKind;
    }
}
