using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services.HostedProviders;
using Fleet.Shared;
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

    // Resolved once from AgentOptions.Model and the environment — see SplitLocalModel.
    // Null provider means a frontier model, which is every agent that does not opt in.
    private readonly string? _localModelProvider;
    private readonly string _threadModel;
    private readonly string? _ossBaseUrl;
    // True when _ossBaseUrl is Agent:CodexOssBaseUrl (#382), which StartProcessAsync sets on the
    // codex child; false when it is the CODEX_OSS_BASE_URL the child inherits (legacy Env Ref).
    private readonly bool _ossBaseUrlFromConfig;
    private readonly string? _inheritedOssBaseUrl;
    private bool _ossOverrideWarningLogged;

    // #382 observability, local provider only: one window report per thread, one line per compaction.
    private bool _contextWindowReported;
    private string? _compactionTurnId;
    private int _compactedNotices;
    private int _compactionItems;

    // Hosted provider (#335), resolved once from AgentOptions.Model. Null for every agent that
    // does not name a hosted prefix, and then nothing below differs from before.
    private readonly HostedModelProvider? _hostedProvider;
    private readonly HostedProviderAdapterHost? _adapterHost;
    private bool _effortWarningLogged;

    private string? _threadId;
    private volatile string? _activeTurnId;
    private volatile string? _commandTurnId;
    private ThreadTokenUsageSnapshot? _lastTurnUsage;
    private int _messageCount;
    // Accumulates assistant text from item/completed notifications of type "agentMessage".
    // Production codex app-server delivers the reply text here — NOT in turn.items of
    // turn/completed (turn.items is always empty on the real wire protocol).
    // Reset at the start of every new turn and cleared on process stop.
    private string _currentTurnAssistantText = "";
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private volatile bool _restartRequested;
    private volatile bool _turnHasFinalAnswerPhase;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;

    // The tool-send turn ledger (#394). Null without the journal, and then nothing below records.
    // _activity belongs to the current app-server process and its stdout reader.
    private readonly TurnOriginLedger? _ledger;
    private TurnOriginLedger.ProviderActivity? _activity;

    private const string CodexBin = "codex";
    internal const string OssBaseUrlEnvVar = "CODEX_OSS_BASE_URL";
    private const string InitializedMethod = "initialized";
    private const string ThreadShellCommandMethod = "thread/shellCommand";
    private const string ClientName = "phleet";
    private const string ClientVersion = "0.1.0";
    private const int StartupRetryBudget = 3;
    private static readonly TimeSpan InterruptDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TurnSteerTimeout = TimeSpan.FromSeconds(2);

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
        ILogger<CodexExecutor> logger,
        HostedProviderAdapterHost? adapterHost = null,
        TurnOriginLedger? ledger = null)
        : this(config, telegramConfig, promptBuilder, logger, Process.Start, adapterHost: adapterHost, ledger: ledger)
    {
    }

    internal CodexExecutor(
        IOptions<AgentOptions> config,
        IOptions<TelegramOptions> telegramConfig,
        PromptBuilder promptBuilder,
        ILogger<CodexExecutor> logger,
        Func<ProcessStartInfo, Process?> processStarter,
        Func<string, string?>? environmentReader = null,
        HostedProviderAdapterHost? adapterHost = null,
        TurnOriginLedger? ledger = null)
    {
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _logger = logger;
        _normalizedAttachmentDir = Path.GetFullPath(telegramConfig.Value.AttachmentDir);
        _processStarter = processStarter;
        _ledger = ledger;

        // Resolved here and validated in EnsureProcessReadyAsync rather than thrown from the
        // constructor, so a misconfigured model surfaces as a named startup failure instead of a
        // DI resolution error with no agent name in it.
        (_localModelProvider, _threadModel) = SplitLocalModel(_config.Model);
        _inheritedOssBaseUrl = (environmentReader ?? Environment.GetEnvironmentVariable)(OssBaseUrlEnvVar);
        _ossBaseUrlFromConfig = !string.IsNullOrEmpty(_config.CodexOssBaseUrl);
        _ossBaseUrl = _ossBaseUrlFromConfig ? _config.CodexOssBaseUrl : _inheritedOssBaseUrl;

        // The hosted prefixes never overlap the local ones, so at most one of the two is set.
        if (HostedModelProviders.TryResolve("codex", _config.Model, out var hosted, out var bareModel))
        {
            _hostedProvider = hosted;
            _threadModel = bareModel;
        }
        _adapterHost = adapterHost;
    }

    /// <summary>
    /// Splits an <c>ollama/…</c> or <c>lmstudio/…</c> model string into the codex
    /// <c>modelProvider</c> id and the bare model id it names.
    /// </summary>
    /// <remarks>
    /// Any other string — including one that merely contains a slash, such as <c>owl/t-lite</c> —
    /// comes back unchanged with a null provider. That is deliberate: an unprefixed model must
    /// produce exactly the <c>thread/start</c> payload it produced before this existed.
    /// <para>
    /// The algorithm lives in <see cref="CodexLocalModelProviders.Split"/>, so the orchestrator's
    /// codex local-model check (#382 C1) and the startup gate split a model exactly as this does.
    /// </para>
    /// </remarks>
    internal static (string? Provider, string Model) SplitLocalModel(string model) =>
        CodexLocalModelProviders.Split(model);

    /// <summary>
    /// Describes the configuration fault in a codex agent whose model names a local provider but
    /// has no <c>CODEX_OSS_BASE_URL</c> to reach it, or null when the pair is sound.
    /// </summary>
    /// <remarks>
    /// Shared by the host-start check in <c>Program.cs</c> — which refuses to start the host, so
    /// the container is visibly down rather than up and unable to answer — and by
    /// <see cref="EnsureProcessReadyAsync"/>, which backstops the CLI and test paths.
    /// </remarks>
    internal static string? DescribeLocalModelFault(string model, string? ossBaseUrl)
    {
        var (provider, _) = SplitLocalModel(model);
        if (provider is null || !string.IsNullOrWhiteSpace(ossBaseUrl))
            return null;

        return $"Model '{model}' selects the local codex provider '{provider}', but "
             + $"{OssBaseUrlEnvVar} is unset or blank. Set it to the inference server's "
             + "OpenAI-compatible base URL, e.g. http://host.docker.internal:11434/v1. There is "
             + "no default: codex would resolve localhost, which inside a container is the "
             + "container itself.";
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
        // #394: held from here to the release below, never while waiting. The pending origin, or
        // unknown (warmup, the CLI).
        var turnInterval = _ledger?.OpenTurn();

        string? turnId = null;
        var abandonReason = "consumer_exit";
        var (forwardedPaths, skippedCount) = CollectImagePaths(images);

        Exception? startupError = null;

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                await EnsureProcessReadyAsync(ct);

                if (_activeTurnId is { } staleTurnId)
                {
                    // /run streams outside _turnLock. Its live id is not stale.
                    if (staleTurnId == _commandTurnId)
                        throw new InvalidOperationException("CodexExecutor: _activeTurnId non-null despite _turnLock held — state corruption.");

                    _logger.LogError("codex_stale_turn_at_start: turnId={TurnId} path=task", staleTurnId);
                    await AbandonTurnAsync(staleTurnId, "task", "stale_at_start");
                    RequestRestart();
                    await EnsureProcessReadyAsync(ct);
                }

                var startParams = new JsonObject
                {
                    ["threadId"] = _threadId!,
                    ["input"] = BuildUserInputs(task, forwardedPaths),
                };
                var codexEffort = FilterEffortForProvider(MapEffortToCodex(_config.Effort));
                if (codexEffort is not null)
                    startParams["effort"] = codexEffort;

                _logger.LogDebug("codex turn/start payload: {Payload}", startParams.ToJsonString());
                var response = await SendRequestAsync("turn/start", startParams, ct);
                var turn = response.RequireObject("turn");
                turnId = turn.RequireString("id");
                _activeTurnId = turnId;
                _turnHasFinalAnswerPhase = false;
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

            await using var stream = StreamTurnAsync(turnId!, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool moved;
                try { moved = await stream.MoveNextAsync(); }
                catch
                {
                    abandonReason = "producer_exception";
                    throw;
                }
                if (!moved)
                {
                    abandonReason = "stream_ended";
                    break;
                }
                _lastActivity = DateTimeOffset.UtcNow;
                yield return stream.Current;
                if (stream.Current.FinalResult is not null) yield break;
            }
        }
        finally
        {
            if (turnId is not null && _activeTurnId == turnId)
                await AbandonTurnAsync(turnId, "task", abandonReason);
            turnInterval?.Close();
            _turnLock.Release();
        }
    }

    public async Task<MidTurnInjectionResult> TryInjectMessageAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default)
    {
        if (_turnHasFinalAnswerPhase)
            return MidTurnInjectionResult.NoActiveTurn("Codex turn has already emitted its final answer (phase=final_answer); injecting now cannot affect the current turn's response.");

        if (_threadId is null || _activeTurnId is null || _process is null || _process.HasExited)
            return MidTurnInjectionResult.NoActiveTurn("Codex has no active turn to steer.");

        var expectedTurnId = _activeTurnId;
        var (forwardedPaths, _) = CollectImagePaths(images);
        var steerParams = new JsonObject
        {
            ["threadId"] = _threadId,
            ["expectedTurnId"] = expectedTurnId,
            ["input"] = BuildUserInputs(task, forwardedPaths),
        };

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                using var steerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                steerCts.CancelAfter(TurnSteerTimeout);
                var response = await SendRequestAsync("turn/steer", steerParams, steerCts.Token);
                var acceptedTurnId = response["turnId"]?.GetValue<string>();
                return string.Equals(acceptedTurnId, expectedTurnId, StringComparison.Ordinal)
                    ? MidTurnInjectionResult.Injected
                    : MidTurnInjectionResult.Failed($"turn/steer returned unexpected turnId '{acceptedTurnId}'.");
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (RpcErrorException ex) when (IsTurnSteerPreconditionFailure(ex))
        {
            return MidTurnInjectionResult.NoActiveTurn(ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MidTurnInjectionResult.Failed($"turn/steer timed out after {TurnSteerTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is RpcErrorException or InvalidOperationException or IOException or ObjectDisposedException)
        {
            return MidTurnInjectionResult.Failed(ex.Message);
        }
    }

    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _sendLock.WaitAsync(ct);
        // #394: a raw command is never attributed to anyone. The lock-held interval covers the
        // request; the shell command itself runs after the request was accepted and the lock
        // released, so commandInterval covers it until its own turn/completed is read below or the
        // app-server's exit is confirmed — whoever takes a lock meanwhile.
        var turnInterval = _ledger?.OpenTurn(command: true);
        TurnOriginLedger.LedgerInterval? commandInterval = null;

        Exception? commandError = null;

        try
        {
            await EnsureProcessReadyAsync(ct);

            if (_activeTurnId is { } staleTurnId)
            {
                // Never wait for _turnLock while holding _sendLock: task startup uses the
                // opposite order. A held task lock or live command keeps today's refusal.
                if (!_turnLock.Wait(0))
                    throw new InvalidOperationException("CodexExecutor refused to start a shell command while a turn is already active.");
                try
                {
                    if (staleTurnId == _commandTurnId)
                        throw new InvalidOperationException("CodexExecutor refused to start a shell command while a turn is already active.");
                    _logger.LogError("codex_stale_turn_at_start: turnId={TurnId} path=run", staleTurnId);
                    await AbandonTurnAsync(staleTurnId, "run", "stale_at_start");
                    RequestRestart();
                    await EnsureProcessReadyAsync(ct);
                }
                finally { _turnLock.Release(); }
            }

            var shellParams = new JsonObject
            {
                ["threadId"] = _threadId!,
                ["command"] = command,
            };

            // Opened before the request, so no notification of the command can precede it. A
            // request that fails any other way than an RPC refusal may still have been accepted:
            // the interval then stays open until the process ends.
            commandInterval = _ledger?.OpenCommand(_activity);

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
            // Refused, so nothing runs.
            commandInterval?.Close();
        }
        finally
        {
            turnInterval?.Close();
            _sendLock.Release();
        }

        if (commandError is not null)
        {
            yield return BuildRpcErrorProgress((RpcErrorException)commandError);
            yield break;
        }

        var abandonReason = "consumer_exit";
        string? commandTurnId = null;
        try
        {
            // Capture this enumeration's id at discovery, not from the shared live marker on
            // dispose: a new command can start after this one's terminal was yielded.
            await using var stream = StreamTurnAsync(expectedTurnId: null, ct,
                onTurnResolved: id => commandTurnId = id).GetAsyncEnumerator(ct);
            while (true)
            {
                bool moved;
                try { moved = await stream.MoveNextAsync(); }
                catch
                {
                    abandonReason = "producer_exception";
                    throw;
                }
                if (!moved)
                {
                    abandonReason = "stream_ended";
                    break;
                }
                var progress = stream.Current;
                _lastActivity = DateTimeOffset.UtcNow;

                // Close before yielding a terminal, even if the caller never resumes.
                if (progress.FinalResult is not null && !progress.IsProcessExit)
                    commandInterval?.Close();
                yield return progress;
                if (progress.FinalResult is not null) yield break;
            }
        }
        finally
        {
            try
            {
                if (commandTurnId is not null && _activeTurnId == commandTurnId)
                    await AbandonTurnAsync(commandTurnId, "run", abandonReason);
            }
            finally
            {
                if (_commandTurnId == commandTurnId)
                    _commandTurnId = null;
            }
        }
    }

    // Only a flag, applied at the next turn start: the app-server keeps running and can still send,
    // so an untracked turn's ledger interval stays open until then (#394).
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

    internal void SetNotificationChannelForTests(Channel<JsonObject>? channel) =>
        _notificationChannel = channel;

    internal void SetThreadStateForTests(string? threadId, string? activeTurnId)
    {
        _threadId = threadId;
        _activeTurnId = activeTurnId;
    }

    internal string? ActiveTurnIdForTests => _activeTurnId;
    internal string? CommandTurnIdForTests => _commandTurnId;
    internal bool RestartRequestedForTests => _restartRequested;

    internal SemaphoreSlim TurnLockForTests => _turnLock;

    internal AgentProgress? BuildItemStartedProgressForTests(JsonObject @params) =>
        BuildItemStartedProgress(@params);

    internal AgentProgress? BuildItemCompletedProgressForTests(JsonObject @params) =>
        BuildItemCompletedProgress(@params);

    internal bool TurnHasFinalAnswerPhaseForTests => _turnHasFinalAnswerPhase;

    internal void SetProcessForTests(System.Diagnostics.Process? process) => _process = process;

    internal void SetStdinForTests(StreamWriter? writer) => _stdin = writer;

    /// <summary>
    /// Starts the stdout reader over <paramref name="stdout"/>, as StartProcessAsync does for a real
    /// app-server, feeding the channel set by <see cref="SetNotificationChannelForTests"/> (or a
    /// fresh one). Cancel <paramref name="ct"/> to cancel it.
    /// </summary>
    internal Task RunStdoutReaderForTests(StreamReader stdout, CancellationToken ct = default)
    {
        var channel = _notificationChannel ??= Channel.CreateUnbounded<JsonObject>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activity = _activity = _ledger?.TrackProvider();
        var token = _readerCts.Token;
        return Task.Run(() => ReadStdoutAsync(stdout, channel.Writer, activity, token), CancellationToken.None);
    }

    // Polls _pendingRequests until a TCS appears, resolves it with the given result, and returns.
    // Use in tests to stand in for a codex app-server answering one JSON-RPC request — turn/steer
    // for the injection tests, initialize and thread/start for the startup ones.
    internal async Task<bool> WaitAndCompleteNextPendingRequestForTests(JsonObject result, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var (id, tcs) in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(id, out var found) && found.TrySetResult(new RpcOutcome(result, null)))
                    return true;
            }
            await Task.Delay(1, ct);
        }
        return false;
    }

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

    // Maps fleet effort tiers to the codex ReasoningEffort enum (none/minimal/low/medium/high/xhigh).
    // Codex has no "max" level — fleet's "max" collapses to "xhigh" (codex's ceiling). none and
    // minimal go through verbatim (#382): Qwen3.8 on codex needs none to reliably emit a final message.
    internal static string? MapEffortToCodex(string? effort) =>
        effort switch
        {
            null or "" => null,
            "none"    => "none",
            "minimal" => "minimal",
            "low"     => "low",
            "medium"  => "medium",
            "high"    => "high",
            "xhigh"   => "xhigh",
            "max"     => "xhigh", // codex ceiling; fleet "max" == codex "xhigh"
            _         => null,
        };

    /// <summary>
    /// Merges <c>model_context_window</c> into the <c>thread/start</c> <c>config</c> overrides,
    /// creating the object when absent and keeping every key already in it.
    /// </summary>
    internal static void MergeModelContextWindow(JsonObject startParams, int contextWindow)
    {
        if (startParams["config"] is not JsonObject config)
            startParams["config"] = config = new JsonObject();

        config["model_context_window"] = contextWindow;
    }

    /// <summary>
    /// D7: a hosted provider receives <c>effort</c> only when the value is in its forwarded set.
    /// Otherwise the field is omitted — never remapped — and one Warning names the value.
    /// </summary>
    internal string? FilterEffortForProvider(string? codexEffort)
    {
        if (codexEffort is null || _hostedProvider is null || _hostedProvider.ForwardsEffort(codexEffort))
            return codexEffort;

        if (!_effortWarningLogged)
        {
            _effortWarningLogged = true;
            _logger.LogWarning(
                "CodexExecutor: effort '{Effort}' is not forwarded to hosted provider {Prefix}; "
                + "sending no effort, so the provider default applies",
                codexEffort, _hostedProvider.Prefix);
        }
        return null;
    }

    /// <summary>
    /// The <c>thread/start</c> <c>config</c> overrides that define a hosted provider for this thread
    /// only (D3). Codex gets no <c>env_key</c>: the forwarder holds the key. The per-start forwarder
    /// token goes in <c>http_headers</c>, which Codex sends on every request to this provider (D10).
    /// That is the token's only route out of this process: never an environment variable
    /// (<c>env_http_headers</c>), a file or argv, which the model's shell could read.
    /// </summary>
    internal static JsonObject BuildHostedProviderConfig(HostedModelProvider provider, HostedProviderEndpoint endpoint)
    {
        var prefix = $"model_providers.{provider.CodexProviderId}.";
        return new JsonObject
        {
            [prefix + "name"] = provider.DisplayName,
            [prefix + "base_url"] = $"{endpoint.BaseAddress.GetLeftPart(UriPartial.Authority)}/{provider.Prefix}",
            [prefix + "wire_api"] = "responses",
            [prefix + "requires_openai_auth"] = false,
            [prefix + "supports_websockets"] = false,
            [prefix + "stream_idle_timeout_ms"] = 300000,
            [prefix + "request_max_retries"] = 2,
            [prefix + "stream_max_retries"] = 2,
            [prefix + "http_headers"] = new JsonObject { [HostedProviderForwarder.TokenHeader] = endpoint.Token },
        };
    }

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
        // Backstop only — the daemon and CLI hosts refuse to start on this fault, so in a
        // container it is already unreachable. It stays for the paths that construct an executor
        // without going through the host, and it fails before the retry budget is touched and
        // before codex is spawned: a configuration fault is not a transient start failure.
        if (DescribeLocalModelFault(_config.Model, _ossBaseUrl) is { } fault)
            throw new InvalidOperationException($"CodexExecutor: {fault}");

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

        // D5.5, for every model: codex runs a root shell for the model, so a key in its
        // environment is one `env` call from the transcript. entrypoint.sh already unset these
        // before exec; this is the belt to that brace.
        foreach (var keyEnvVar in HostedModelProviders.KeyEnvVars)
            psi.Environment.Remove(keyEnvVar);

        // #382: the orchestrator's URL replaces whatever the container inherited, so a stale Env Ref
        // cannot point the agent at another server. The legacy route leaves the environment as is.
        if (_localModelProvider is not null && _ossBaseUrlFromConfig)
        {
            psi.Environment[OssBaseUrlEnvVar] = _ossBaseUrl;

            // Names the variable only; the inherited value is not logged.
            if (!_ossOverrideWarningLogged
                && !string.IsNullOrWhiteSpace(_inheritedOssBaseUrl)
                && !string.Equals(_inheritedOssBaseUrl, _ossBaseUrl, StringComparison.Ordinal))
            {
                _ossOverrideWarningLogged = true;
                _logger.LogWarning(
                    "CodexExecutor: the inherited {EnvVar} is superseded by Agent:CodexOssBaseUrl; "
                    + "the Env Ref can be removed from this agent",
                    OssBaseUrlEnvVar);
            }
        }

        _process = _processStarter(psi) ?? throw new InvalidOperationException("Failed to start codex app-server");
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _notificationChannel = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        _readerCts = new CancellationTokenSource();
        var activity = _activity = _ledger?.TrackProvider();
        _ = Task.Run(() => ReadStdoutAsync(_process.StandardOutput, _notificationChannel.Writer, activity, _readerCts.Token));
        _ = Task.Run(() => ReadStderrAsync(_process.StandardError, _readerCts.Token));

        _threadId = null;
        _activeTurnId = null;
        _lastTurnUsage = null;
        _messageCount = 0;
        _logger.LogInformation("CodexExecutor: app-server started (pid {Pid})", _process.Id);

        if (_localModelProvider is not null)
        {
            _logger.LogInformation(
                "CodexExecutor: local inference — provider {Provider}, model {Model}, {EnvVar}={BaseUrl}, "
                + "source={Source}, contextWindow={ContextWindow}",
                _localModelProvider, _threadModel, OssBaseUrlEnvVar, _ossBaseUrl,
                _ossBaseUrlFromConfig ? "agent config" : "legacy env",
                (object?)_config.ContextWindow ?? "unset");
        }

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

        var startParams = new JsonObject
        {
            ["model"] = _threadModel,
        };

        // codex resolves the local provider's base URL from CODEX_OSS_BASE_URL in the app-server's
        // environment: set by StartProcessAsync from Agent:CodexOssBaseUrl, else inherited (legacy).
        if (_localModelProvider is not null)
            startParams["modelProvider"] = _localModelProvider;

        // Hosted provider (#335 D3): defined per thread through config overrides, pointed at the
        // loopback forwarder. No config.toml is written.
        if (_hostedProvider is not null)
        {
            var endpoint = await (_adapterHost?.Endpoint ?? throw new InvalidOperationException(
                $"CodexExecutor: model '{_config.Model}' selects hosted provider '{_hostedProvider.Prefix}', "
                + "but no loopback adapter is registered (Agent:HostedProvider is false)."))
                .WaitAsync(ct);
            startParams["modelProvider"] = _hostedProvider.CodexProviderId;
            startParams["config"] = BuildHostedProviderConfig(_hostedProvider, endpoint);
        }

        // #382, local provider only. codex derives its auto-compaction threshold from this window,
        // so Fleet sets no second one; cloud and hosted models keep their model-owned limits.
        if (_localModelProvider is not null && _config.ContextWindow is int contextWindow)
            MergeModelContextWindow(startParams, contextWindow);

        startParams["cwd"] = _config.WorkDir;
        startParams["approvalPolicy"] = "never";
        startParams["sandbox"] = sandboxMode;
        startParams["serviceName"] = ClientName;
        startParams["baseInstructions"] = baseInstructions;
        startParams["ephemeral"] = true;

        var threadResponse = await SendRequestAsync("thread/start", startParams, ct);

        // A codex that ignored the config overrides would silently run the thread on its default
        // provider — with an OpenAI credential this container does not hold. Fail instead.
        if (_hostedProvider is not null)
        {
            var echoed = threadResponse["modelProvider"] is JsonValue v && v.TryGetValue<string>(out var p) ? p : null;
            if (!string.Equals(echoed, _hostedProvider.CodexProviderId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"CodexExecutor: thread/start did not echo modelProvider '{_hostedProvider.CodexProviderId}' "
                    + $"(got '{echoed ?? "<none>"}'); codex did not accept the hosted provider definition.");
        }

        var thread = threadResponse.RequireObject("thread");
        _threadId = thread.RequireString("id");
        _contextWindowReported = false;
        var ephemeral = thread["ephemeral"]?.GetValue<bool>() ?? false;
        var path = thread["path"];
        if (!ephemeral || path is not null && path.GetValueKindSafe() != JsonValueKind.Null)
        {
            _logger.LogWarning(
                "CodexExecutor thread/start returned unexpected persistence state (ephemeral={Ephemeral}, path={Path})",
                ephemeral, path?.ToJsonString());
        }
    }

    private async Task ReadStdoutAsync(
        StreamReader reader, ChannelWriter<JsonObject> writer, TurnOriginLedger.ProviderActivity? activity, CancellationToken ct)
    {
        try
        {
            string? line = "";
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
                {
                    ObserveTurnActivity(activity, obj);
                    await writer.WriteAsync(obj, ct);
                }
            }

            // The loop also ends on cancellation, which StopInternalAsync reports itself once its
            // kill has completed. Only stdout EOF is a process end here (#394).
            if (line is null)
                activity?.ProcessEnded();
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

    /// <summary>
    /// #394: the reader sees every notification as it is read, whoever later consumes it, so a turn
    /// the app-server runs outside a lock-held interval — a shell command streaming after its
    /// request returned, an interrupted turn that outlived its drain — becomes visible here.
    /// </summary>
    private static void ObserveTurnActivity(TurnOriginLedger.ProviderActivity? activity, JsonObject notification)
    {
        if (activity is null || notification["method"] is not JsonValue value || !value.TryGetValue<string>(out var method))
            return;

        var @params = notification["params"] as JsonObject;

        if (method == "turn/completed")
        {
            activity.TurnEnded(TryString((@params?["turn"] as JsonObject)?["id"]));
            return;
        }

        if (method == "turn/started" || method.StartsWith("item/", StringComparison.Ordinal))
            activity.TurnContent();

        // Only /run starts a user shell command, so its turn is the command's: the command's
        // interval retires with that turn's completion even if nobody reads the stream any more.
        if (method == "item/started"
            && @params?["item"] is JsonObject item
            && TryString(item["type"]) == "commandExecution"
            && TryString(item["source"]) == "userShell"
            && TryString(@params["turnId"]) is { Length: > 0 } shellTurnId)
        {
            activity.ShellCommandTurn(shellTurnId);
        }
    }

    private static string? TryString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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
        [EnumeratorCancellation] CancellationToken ct,
        Action<string?>? onTurnResolved = null)
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
                    IsProcessExit = true,
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

            ObserveLocalContext(method, @params);

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
                    _commandTurnId = resolvedTurnId;
                    onTurnResolved?.Invoke(resolvedTurnId);
                    _turnHasFinalAnswerPhase = false;
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
                    _commandTurnId = discovered;
                    onTurnResolved?.Invoke(discovered);
                    _turnHasFinalAnswerPhase = false;
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

    /// <summary>
    /// #382, local provider only: logs the context window codex actually applied, once per thread,
    /// and each context compaction. Both channel readers call it before any turn filter, so no
    /// notification is missed. Counts only — never prompt text or the token-usage payload.
    /// </summary>
    private void ObserveLocalContext(string method, JsonObject @params)
    {
        if (_localModelProvider is null)
            return;

        if (method == "thread/tokenUsage/updated")
        {
            if (_contextWindowReported
                || (@params["tokenUsage"] as JsonObject)?["modelContextWindow"] is not JsonValue window
                || !window.TryGetValue<long>(out var reported))
            {
                return;
            }

            _contextWindowReported = true;
            _logger.LogInformation(
                "CodexExecutor: codex reports modelContextWindow {Reported} (configured {Configured})",
                reported, (object?)_config.ContextWindow ?? "unset");
            if (_config.ContextWindow is int configured && reported > configured)
            {
                _logger.LogWarning(
                    "CodexExecutor: codex did not apply model_context_window (reported {Reported}, configured {Configured})",
                    reported, configured);
            }
            return;
        }

        var isItem = method == "item/completed"
            && (@params["item"] as JsonObject)?["type"]?.GetValue<string>() == "contextCompaction";
        if (!isItem && method != "thread/compacted")
            return;

        // codex can announce one compaction twice: the deprecated thread/compacted notification and
        // a contextCompaction item. Per turn, only a rise in the higher of the two counts is new.
        var turnId = @params["turnId"]?.GetValue<string>();
        if (!string.Equals(turnId, _compactionTurnId, StringComparison.Ordinal))
            (_compactionTurnId, _compactedNotices, _compactionItems) = (turnId, 0, 0);

        var seen = Math.Max(_compactedNotices, _compactionItems);
        if (isItem)
            _compactionItems++;
        else
            _compactedNotices++;

        if (Math.Max(_compactedNotices, _compactionItems) > seen)
            _logger.LogInformation("CodexExecutor: codex compacted the thread context");
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

        if (itemType == "agentMessage")
        {
            var phase = item?["phase"]?.GetValue<string>();
            if (string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase))
                _turnHasFinalAnswerPhase = true;
            return null;
        }

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
            var phase = item?["phase"]?.GetValue<string>();
            if (string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase))
                _turnHasFinalAnswerPhase = true;
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
        // A reported terminal must clear ownership even if its payload is malformed.
        _activeTurnId = null;
        _turnHasFinalAnswerPhase = false;
        if (@params["turn"] is not JsonObject turn)
        {
            _currentTurnAssistantText = "";
            return new AgentProgress
            {
                EventType = "result",
                Summary = "Codex turn ended with a malformed completion",
                FinalResult = "Codex turn ended with a malformed completion",
                IsErrorResult = true,
                IsSignificant = true,
            };
        }
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

    private async Task<bool> InterruptTurnAsync(string turnId)
    {
        if (_stdin is null || _threadId is null)
            return false;

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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CodexExecutor failed to interrupt active turn {TurnId}", turnId);
            return false;
        }
    }

    private enum DrainOutcome { Completed, Timeout, ChannelClosed, WriteFailed }

    /// <summary>Cleanup cannot replace the consumer's original exception or replay its task.</summary>
    private async Task AbandonTurnAsync(string turnId, string path, string reason)
    {
        _activeTurnId = null;
        _turnHasFinalAnswerPhase = false;
        try
        {
            var outcome = await DrainInterruptedTurnAsync(turnId);
            if (outcome != DrainOutcome.Completed)
                RequestRestart();
            var drain = outcome switch
            {
                DrainOutcome.Completed => "completed",
                DrainOutcome.Timeout => "timeout",
                DrainOutcome.ChannelClosed => "channel_closed",
                _ => "write_failed",
            };
            _logger.LogWarning(
                "codex_turn_abandoned: turnId={TurnId} path={Path} reason={Reason} drain={Drain} restartRequested={RestartRequested}",
                turnId, path, reason, drain, _restartRequested || reason == "stale_at_start");
        }
        catch (Exception ex)
        {
            RequestRestart();
            // Even a failing diagnostic sink cannot mask the original consumer exception.
            try
            {
                _ledger?.OpenUntilTurnEnds(_activity, turnId);
                _logger.LogWarning(ex, "codex_turn_abandoned: turnId={TurnId} path={Path} reason={Reason} drain=write_failed restartRequested=true", turnId, path, reason);
            }
            catch { }
        }
    }

    private async Task<DrainOutcome> DrainInterruptedTurnAsync(string turnId)
    {
        _activeTurnId = null;
        _turnHasFinalAnswerPhase = false;
        using var drainCts = new CancellationTokenSource(InterruptDrainTimeout);
        var written = false;
        try
        {
            // Bound the write as well as notification draining; neither uses the caller's token.
            written = await InterruptTurnAsync(turnId).WaitAsync(drainCts.Token);
            if (_notificationChannel is null)
            {
                _ledger?.OpenUntilTurnEnds(_activity, turnId);
                return written ? DrainOutcome.ChannelClosed : DrainOutcome.WriteFailed;
            }
            while (true)
            {
                var notification = await _notificationChannel.Reader.ReadAsync(drainCts.Token);
                var method = notification["method"]?.GetValue<string>();
                var @params = notification["params"] as JsonObject;
                if (method is null || @params is null)
                    continue;

                ObserveLocalContext(method, @params);

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
                    _currentTurnAssistantText = "";
                    return DrainOutcome.Completed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("CodexExecutor timed out draining interrupted turn {TurnId}", turnId);
            _ledger?.OpenUntilTurnEnds(_activity, turnId);
            return written ? DrainOutcome.Timeout : DrainOutcome.WriteFailed;
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("CodexExecutor notification channel closed while draining interrupted turn {TurnId}", turnId);
            _ledger?.OpenUntilTurnEnds(_activity, turnId);
            return written ? DrainOutcome.ChannelClosed : DrainOutcome.WriteFailed;
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

                // #394: the kill has completed (or the process had already exited) — a confirmed
                // end. Cancelling the reader above is not one, and a kill that threw proves nothing.
                _activity?.ProcessEnded();
                _activity = null;
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
        _commandTurnId = null;
        _turnHasFinalAnswerPhase = false;
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

    internal static bool IsTurnSteerPreconditionFailureForTests(long code, string message) =>
        IsTurnSteerPreconditionFailure(new RpcErrorException("turn/steer", code, message, null));

    private static bool IsTurnSteerPreconditionFailure(RpcErrorException ex) =>
        string.Equals(ex.Method, "turn/steer", StringComparison.Ordinal)
        && ex.Code == -32600
        && (ex.Message.Contains("no active turn to steer", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("expected active turn id", StringComparison.OrdinalIgnoreCase));

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
