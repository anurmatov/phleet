using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Provider <c>pi</c> (#386): one long-lived Pi 1.0.4 <c>pi --mode rpc</c> process per agent,
/// speaking Anthropic Messages to a local server. Commands go to stdin and responses and session
/// events come back on stdout, both as LF-framed JSON lines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The process starts lazily on the first turn. Every start rewrites the three
/// <see cref="PiRuntimeFiles"/> and must pass a <c>get_state</c> check (§4.3) within 30 s, or it is
/// killed and retried, three attempts in all. No <c>set_*</c> command is ever sent: the settings come
/// from the generated files and the arguments. A crash, a kill or <see cref="RequestRestart"/> keeps
/// the session file, so the next start resumes it with <c>--session</c>; <c>/reset</c> and
/// <c>/stop</c> delete it.
/// </para>
/// <para>
/// <b>Turns.</b> A turn waits for the previous run's <c>agent_settled</c> (a post-run compaction
/// can still be running), sends <c>prompt</c>, waits for its ack — the timer is paused while a
/// compaction is open — and maps events until <c>agent_end</c>. The answer is the run's first
/// assistant message that stopped with <c>stop</c> and called no tool; a later one came from a late
/// steer and is read by <see cref="ReadInjectedTurnAnswersAsync"/>.
/// </para>
/// <para>
/// <b>Logs</b> carry counts and names only — never prompt, answer or tool text (§12, §20).
/// </para>
/// </remarks>
public sealed class PiExecutor : IAgentExecutor
{
    /// <summary>The Pi binary, resolved on PATH.</summary>
    public const string PiBin = PiRuntimeFiles.PiBin;

    internal const int StartAttempts = 3;

    internal const string AckTimeoutText =
        "The local model did not accept the message within 10 s; it was not processed. Send it again.";
    internal const string HandledText = "Pi handled this message as a command; nothing was sent to the model";
    internal const string WatchdogText = "The local model stopped responding; the turn was aborted.";
    internal const string OverflowRecoveryFailedText = "context overflowed and automatic recovery failed — send /reset";
    internal const string ProcessExitText = "Pi exited unexpectedly; the next message resumes the session.";

    internal static string StartFailureText(string field) => $"Pi failed to start: {field}";
    internal static string UnreachableText(string? origin) => $"local model server unreachable ({origin})";
    internal static string ImagesOmittedText(int count) => $"[{count} image attachment(s) omitted: this local model is text-only]";

    private const int MaxServerTextChars = 300;
    private const int MaxToolArgsChars = 200;
    private const int StderrTailChars = 2048;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 512 };

    private readonly AgentOptions _config;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<PiExecutor> _logger;
    private readonly PiExecutorSettings _settings;
    private readonly PiExecutorTimings _timings;

    // _turnLock serialises turns (ExecuteAsync, ReadInjectedTurnAnswersAsync). _lifecycleLock
    // serialises starting and stopping the process, so /stop cannot interleave with a start.
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateLock = new();

    private volatile Generation? _current;
    private string? _sessionFile;
    private string? _sessionId;
    private string? _piVersion;
    private volatile bool _restartRequested;
    private long _nextId;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

    // The live turn, for TryInjectMessageAsync. Written by the turn's own event loop.
    private volatile Generation? _liveGeneration;
    private volatile bool _runActive;
    private volatile bool _finalSeen;

    // Final answers from late steers in a run that already answered (#369), oldest first.
    private readonly ConcurrentQueue<AgentProgress> _lateAnswers = new();

    public PiExecutor(IOptions<AgentOptions> config, PromptBuilder promptBuilder, ILogger<PiExecutor> logger)
        : this(config, promptBuilder, logger, new PiExecutorSettings())
    {
    }

    internal PiExecutor(
        IOptions<AgentOptions> config, PromptBuilder promptBuilder, ILogger<PiExecutor> logger,
        PiExecutorSettings settings)
    {
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _logger = logger;
        _settings = settings;
        _timings = settings.Timings;
    }

    public string? LastSessionId
    {
        get { lock (_stateLock) return _sessionId; }
    }

    /// <summary>When the last stdout line was received (or the last turn was sent).</summary>
    public DateTimeOffset LastActivity => _lastActivity;

    /// <summary>
    /// The process is alive and its session holds at least one turn — resumed with messages, or a
    /// run completed in this process — so PromptAssembler sends only the new message.
    /// </summary>
    public bool IsProcessWarm =>
        _current is { } gen && gen.IsAlive && (gen.InitialMessageCount > 0 || gen.CompletedRuns > 0);

    /// <summary>The session file Pi reported last; in memory only. Exposed for the tests.</summary>
    internal string? SessionFileForTests
    {
        get { lock (_stateLock) return _sessionFile; }
    }

    /// <summary>The running Pi's OS process id, or null. Exposed for the real-process tests (a crash).</summary>
    internal int? ProcessIdForTests => _current is { } gen && gen.IsAlive ? gen.Process.Id : null;

    /// <summary>The live run's answer has been seen (A11: a steer now is refused). Exposed for the tests.</summary>
    internal bool FinalSeenForTests => _finalSeen;

    /// <summary>Pi has no background subagents.</summary>
    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => Array.Empty<BackgroundTaskInfo>();

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Only sets a flag: the next turn restarts Pi with the same session.</summary>
    public void RequestRestart() => _restartRequested = true;

    // ── Turns ────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await _turnLock.WaitAsync(ct);
        try
        {
            // An answer to a late steer that nobody read is still an answer.
            while (_lateAnswers.TryDequeue(out var stale))
                yield return AsRecovered(stale);

            // Documents keep the PromptAssembler path (their paths are already in the text, and
            // Pi's read tool opens them); images are not sent to a text-only model.
            var message = WithMediaFallback(task, images);

            var (gen, startFailure) = await TryStartAsync(ct);
            if (gen is null)
            {
                yield return startFailure!;
                yield break;
            }

            // Step 1: the previous run may still be settling (post-run compaction, a late steer).
            if (gen.AwaitingSettle)
            {
                var drain = new RunState(live: false);
                var settled = await DrainUntilSettledAsync(gen, drain, _timings.PreviousSettle, ct);
                foreach (var recovered in drain.AllFinals())
                    yield return AsRecovered(recovered);

                if (!settled)
                {
                    _logger.LogWarning(
                        "pi: the previous run did not settle within {Seconds} s; aborting it before the next prompt",
                        _timings.PreviousSettle.TotalSeconds);
                    await AbortAndSettleOrKillAsync(gen, drain);
                }

                // Killed, or exited while settling: the new prompt starts it with --session.
                if (!gen.IsAlive)
                {
                    (gen, startFailure) = await TryStartAsync(ct);
                    if (gen is null)
                    {
                        yield return startFailure!;
                        yield break;
                    }
                }
            }

            // Step 2: send the prompt; DriveAsync waits for its ack and maps the run.
            var run = new RunState(live: true);
            var ackId = await SendPromptAsync(gen, message, ct);
            if (ackId is null)
            {
                yield return ProcessExited(gen, run);
                yield break;
            }

            await foreach (var progress in DriveAsync(gen, run, ackId, ct))
                yield return progress;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary>
    /// Steers the live run (A11). No live run, or its answer already seen → <c>NoActiveTurn</c>, and
    /// TaskManager queues the message. Otherwise <c>steer</c>; no response within 2 s → <c>Failed</c>.
    /// </summary>
    public async Task<MidTurnInjectionResult> TryInjectMessageAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        CancellationToken ct = default)
    {
        var gen = _liveGeneration;
        if (!_runActive || gen is null || !gen.IsAlive)
            return MidTurnInjectionResult.NoActiveTurn("Pi has no active run to steer.");
        if (_finalSeen)
            return MidTurnInjectionResult.NoActiveTurn("Pi has already produced this run's answer.");

        try
        {
            var response = await RequestAsync(gen, Command("steer", ("message", WithMediaFallback(task, images))),
                _timings.Steer, ct);
            if (IsSuccess(response))
            {
                _lastActivity = DateTimeOffset.UtcNow;
                return MidTurnInjectionResult.Injected;
            }
            return MidTurnInjectionResult.Failed($"Pi refused the steer: {Bounded(Str(response, "error"))}");
        }
        catch (TimeoutException)
        {
            return MidTurnInjectionResult.Failed(
                $"Pi did not answer the steer within {_timings.Steer.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is PiProcessExitedException or IOException or ObjectDisposedException)
        {
            return MidTurnInjectionResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// After a turn with injections, yields the answers of the further runs late steers produced
    /// (#369): first those already seen in the turn's own run, then any run Pi starts before it
    /// settles, waiting for that start for at most a short bound.
    /// </summary>
    public async IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(
        int injectedMessages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (injectedMessages <= 0)
            yield break;

        await _turnLock.WaitAsync(ct);
        try
        {
            var remaining = injectedMessages;
            while (remaining > 0 && _lateAnswers.TryDequeue(out var answer))
            {
                remaining--;
                yield return answer;
            }

            while (remaining > 0 && _current is { AwaitingSettle: true } gen)
            {
                var run = new RunState(live: false);
                if (!await WaitForLateRunAsync(gen, run, ct))
                    yield break;

                _logger.LogInformation("pi: a steered message started its own run; reading its answer");
                AgentProgress? result = null;
                await foreach (var progress in DriveAsync(gen, run, ackId: null, ct))
                {
                    if (progress.EventType == "result")
                        result = progress;
                }

                if (result is null)
                    yield break;

                remaining--;
                yield return result;
                while (remaining > 0 && _lateAnswers.TryDequeue(out var more))
                {
                    remaining--;
                    yield return more;
                }
            }
        }
        finally
        {
            _turnLock.Release();
        }
    }

    /// <summary><c>/run</c>: RPC <c>bash</c> with <c>excludeFromContext</c>, so the output never reaches the model.</summary>
    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        var (gen, startFailure) = await TryStartAsync(ct);
        if (gen is null)
        {
            yield return startFailure!;
            yield break;
        }

        yield return await RunBashAsync(gen, command, ct);
    }

    /// <summary>
    /// <c>/reset</c> and <c>/stop</c>: abort, kill, and delete the session file, so the next turn
    /// starts a new session with no <c>--session</c>.
    /// </summary>
    public async Task StopProcessAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopInternalAsync(deleteSession: true);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<bool> TryStopProcessAsync()
    {
        if (_current is not { } gen || !gen.IsAlive)
            return false;

        await StopProcessAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            // Container shutdown: the session stays where it is.
            await StopInternalAsync(deleteSession: false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    // ── Start ────────────────────────────────────────────────────────────────

    private async Task<(Generation? Gen, AgentProgress? Failure)> TryStartAsync(CancellationToken ct)
    {
        try
        {
            return (await EnsureStartedAsync(ct), null);
        }
        catch (PiStartException ex)
        {
            var text = StartFailureText(ex.Field);
            return (null, new AgentProgress
            {
                EventType = "result",
                Summary = text,
                FinalResult = text,
                IsErrorResult = true,
                IsSignificant = true,
            });
        }
    }

    private async Task<Generation> EnsureStartedAsync(CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            var current = _current;
            if (current is not null && current.IsAlive && !_restartRequested)
                return current;

            if (current is not null)
            {
                if (_restartRequested)
                    _logger.LogInformation("pi: restart requested; restarting with the same session");
                else
                    _logger.LogWarning("pi: the process is gone; restarting it with the same session");
                Kill(current);
            }
            _restartRequested = false;

            if (DescribeConfigFault() is { } configFault)
            {
                _logger.LogError("Pi failed to start: {Field}", configFault);
                throw new PiStartException(configFault);
            }

            string? lastField = null;
            string lastStderr = "";
            for (var attempt = 1; attempt <= StartAttempts; attempt++)
            {
                try
                {
                    var gen = await StartOnceAsync(ct);
                    _current = gen;
                    return gen;
                }
                catch (PiStartFault fault)
                {
                    lastField = fault.Field;
                    lastStderr = fault.StderrTail;
                    _logger.LogWarning("pi start attempt {Attempt}/{Budget} failed: {Field}",
                        attempt, StartAttempts, fault.Field);
                }
            }

            _logger.LogError("Pi failed to start: {Field}; last {Bytes} chars of stderr:\n{Stderr}",
                lastField, lastStderr.Length, lastStderr);
            throw new PiStartException(lastField!);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>The executor's own minimum: the startup gate has already checked PI1–PI4 in full.</summary>
    private string? DescribeConfigFault()
    {
        if (string.IsNullOrEmpty(_config.PiBaseUrl))
            return "Agent:PiBaseUrl";
        if (string.IsNullOrEmpty(_config.Model))
            return "Agent:Model";
        return _config.ContextWindow is null ? "Agent:ContextWindow" : null;
    }

    private async Task<Generation> StartOnceAsync(CancellationToken ct)
    {
        try
        {
            PiRuntimeFiles.WriteAll(
                _settings.AgentDir, _config.PiBaseUrl!, _config.Model, _config.ContextWindow!.Value, _config.AllowedTools,
                _settings.McpConfigPath);
        }
        catch (PiRuntimeFileWriteException ex)
        {
            throw new PiStartFault(ex.FileName, "");
        }

        string promptFile;
        try
        {
            promptFile = _promptBuilder.WriteSystemPromptFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new PiStartFault("system-prompt.md", "");
        }

        var resume = ResolveResumeSession();
        var psi = BuildStartInfo(promptFile, resume);

        IPiProcess process;
        try
        {
            process = (_settings.ProcessStarter ?? OsPiProcess.Start)(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new PiStartFault("process start", ex.Message);
        }

        var gen = new Generation(process);
        _ = Task.Run(() => ReadStdoutAsync(gen));
        _ = Task.Run(() => ReadStderrAsync(gen));

        try
        {
            JsonObject response;
            try
            {
                response = await RequestAsync(gen, Command("get_state"), _timings.StartupState, ct);
            }
            catch (TimeoutException)
            {
                throw new PiStartFault("get_state", gen.Stderr.Text);
            }
            catch (PiProcessExitedException)
            {
                throw new PiStartFault("process exited", gen.Stderr.Text);
            }

            if (!IsSuccess(response) || response["data"] is not JsonObject state)
                throw new PiStartFault("get_state", gen.Stderr.Text);

            if (DescribeStateMismatch(state, _config.Model, PiLocalModel.ThinkingArgument(_config.Effort)) is { } field)
                throw new PiStartFault(field, gen.Stderr.Text);

            // §18: no provider credential where the shell can read it. Pi has written its own empty
            // auth.json by now; anything else in it, or any mcp-auth.json, refuses the start.
            if (PiRuntimeFiles.DescribeCredentialFileFault(_settings.AgentDir) is { } credentialFile)
                throw new PiStartFault(credentialFile, gen.Stderr.Text);

            gen.InitialMessageCount = (int)(Num(state, "messageCount") ?? 0);
            ApplySessionState(gen, state, force: true);

            var version = await GetPiVersionAsync(ct);
            _logger.LogInformation(
                "pi start: version {Version} model {Model} server {Origin} context {ContextWindow} reserve {Reserve} "
                + "tail {Tail} summaryMax {SummaryMax} thinking {Thinking} session {Session}",
                version, _config.Model, _config.PiBaseUrl, _config.ContextWindow, PiLocalModel.ReserveTokens,
                PiLocalModel.KeepRecentTokens, PiLocalModel.SummaryMaxTokens,
                _config.Effort == "off" ? "off" : "on", resume is null ? "new" : "resumed");
            return gen;
        }
        catch
        {
            Kill(gen);
            throw;
        }
    }

    /// <summary>
    /// The first <c>get_state</c> field that differs from what the generated files and arguments
    /// configure, or null. Names the field, never a value.
    /// </summary>
    internal static string? DescribeStateMismatch(JsonObject state, string tag, string thinkingLevel)
    {
        if (Bool(state, "autoCompactionEnabled") != true)
            return "autoCompactionEnabled";
        if (Str(state, "steeringMode") != "all")
            return "steeringMode";
        var model = state["model"] as JsonObject;
        if (Str(model, "provider") != PiLocalModel.ProviderId)
            return "model.provider";
        if (Str(model, "id") != tag)
            return "model.id";
        if (Str(state, "thinkingLevel") != thinkingLevel)
            return "thinkingLevel";
        return null;
    }

    private ProcessStartInfo BuildStartInfo(string promptFile, string? resume)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _settings.PiBinary,
            WorkingDirectory = _config.WorkDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in PiRuntimeFiles.BuildArguments(
                     _config.Model, _config.Effort, promptFile, _config.AllowedTools, resume, _settings.ExtensionDir))
        {
            psi.ArgumentList.Add(argument);
        }

        ApplyEnvironment(psi);
        return psi;
    }

    private void ApplyEnvironment(ProcessStartInfo psi)
    {
        if (_settings.BaseEnvironment is { } environment)
        {
            psi.Environment.Clear();
            foreach (var (name, value) in environment)
                psi.Environment[name] = value;
        }

        PiRuntimeFiles.ApplyChildEnvironment(psi.Environment, _settings.AgentDir);
    }

    /// <summary>
    /// The session to resume, or null for a new one. A file that exists but is not a Pi session
    /// would make Pi exit at start, so it is dropped with a Warning (§13).
    /// </summary>
    private string? ResolveResumeSession()
    {
        string? file;
        lock (_stateLock)
            file = _sessionFile;
        if (file is null || !File.Exists(file) || IsReadableSession(file))
            return file;

        _logger.LogWarning("pi: the session file is unreadable or corrupt; starting a new session");
        TryDelete(file);
        lock (_stateLock)
        {
            _sessionFile = null;
            _sessionId = null;
        }
        return null;
    }

    private static bool IsReadableSession(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            var first = reader.ReadLine();
            return first is not null && Str(JsonNode.Parse(first, documentOptions: ParseOptions), "type") == "session";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task<string> GetPiVersionAsync(CancellationToken ct)
    {
        if (_piVersion is { } known)
            return known;

        if (_settings.VersionProbe is { } probe)
            return _piVersion = await probe(ct) ?? "unknown";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _settings.PiBinary,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            ApplyEnvironment(psi);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("no process");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timings.VersionProbe);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var version = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return _piVersion = string.IsNullOrEmpty(version) ? "unknown" : version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return _piVersion = "unknown";
        }
    }

    // ── The run loop ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <c>prompt</c> and returns its id, or null when the process is gone. The ack is routed
    /// into the event channel rather than a pending request, so it is read in stdout order: a fast
    /// run's events can never be consumed before the ack that starts it.
    /// </summary>
    private static async Task<string?> SendPromptAsync(Generation gen, string message, CancellationToken ct)
    {
        var id = $"fleet-prompt-{Guid.NewGuid():N}";
        gen.PromptAckId = id;
        try
        {
            await WriteAsync(gen, Command("prompt", ("message", message)).WithId(id), ct);
            return id;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps one run's events to progress and yields the terminal result last. With an
    /// <paramref name="ackId"/> it first waits for that prompt's ack (10 s, paused while a compaction
    /// is open); without one the run has already started (a late steer's run).
    /// </summary>
    private async IAsyncEnumerable<AgentProgress> DriveAsync(
        Generation gen, RunState run, string? ackId, [EnumeratorCancellation] CancellationToken ct)
    {
        var awaitingAck = ackId is not null;
        var ackActive = Stopwatch.StartNew();
        var ackWall = Stopwatch.StartNew();
        CancellationTokenRegistration abortOnCancel = default;
        if (!awaitingAck)
            abortOnCancel = BeginRun(gen, run, ct);

        try
        {
            while (true)
            {
                // Everything already read, in stdout order — unless the turn was cancelled: then
                // nothing more is mapped, and the cancellation rule runs.
                while (!ct.IsCancellationRequested && gen.Events.Reader.TryRead(out var evt))
                {
                    if (evt.Type == "response")
                    {
                        if (!awaitingAck || Str(evt.Json, "id") != ackId)
                            continue;

                        awaitingAck = false;
                        gen.PromptAckId = null;
                        var paused = ackWall.Elapsed - ackActive.Elapsed;
                        if (paused > _timings.AckPausedWarning)
                            _logger.LogWarning(
                                "pi: the prompt ack was paused {Seconds:0} s while a compaction ran, over {Limit:0} s",
                                paused.TotalSeconds, _timings.AckPausedWarning.TotalSeconds);

                        if (AckOutcome(gen, evt.Json) is { } early)
                        {
                            run.Terminal = true;
                            yield return early;
                            yield break;
                        }

                        abortOnCancel = BeginRun(gen, run, ct);
                        continue;
                    }

                    var wasOpen = gen.CompactionOpenSince is not null;
                    var outcome = HandleEvent(gen, run, evt);
                    if (awaitingAck)
                    {
                        // The ack timer counts only while no compaction is open.
                        var isOpen = gen.CompactionOpenSince is not null;
                        if (!wasOpen && isOpen) ackActive.Stop();
                        if (wasOpen && !isOpen) ackActive.Start();
                    }

                    foreach (var progress in run.TakeOutput())
                        yield return progress;

                    if (!awaitingAck && outcome is { } terminal)
                    {
                        run.Terminal = true;
                        yield return terminal;
                        yield break;
                    }
                }


                if (ct.IsCancellationRequested)
                {
                    await OnCancelledAsync(gen, run, awaitingAck);
                    ct.ThrowIfCancellationRequested();
                }

                // Wait for the next event, the ack, or a deadline.
                TimeSpan wait;
                bool ackDeadline;
                if (awaitingAck && gen.CompactionOpenSince is null)
                {
                    wait = _timings.Ack - ackActive.Elapsed;
                    ackDeadline = true;
                }
                else
                {
                    wait = _timings.Watchdog - gen.SinceLastLine;
                    ackDeadline = false;
                }

                var wake = wait <= TimeSpan.Zero ? Wake.Timeout : await WaitAsync(gen, wait, ct);
                switch (wake)
                {
                    case Wake.Ready:
                        continue;

                    case Wake.Exited:
                        run.Terminal = true;
                        yield return ProcessExited(gen, run);
                        yield break;

                    case Wake.Cancelled:
                        await OnCancelledAsync(gen, run, awaitingAck);
                        ct.ThrowIfCancellationRequested();
                        break;

                    case Wake.Timeout when ackDeadline:
                        _logger.LogError(
                            "pi: ack timeout — no prompt ack within {Seconds:0} s and no compaction open; "
                            + "killing the process so the prompt can never run later", _timings.Ack.TotalSeconds);
                        Kill(gen);
                        run.Terminal = true;
                        yield return ErrorResult(AckTimeoutText, run);
                        yield break;

                    case Wake.Timeout:
                        // Watchdog: no Pi event for the whole window.
                        _logger.LogWarning("pi: no event for {Seconds:0} s; the local model stopped responding",
                            _timings.Watchdog.TotalSeconds);
                        run.Terminal = true;
                        if (awaitingAck)
                        {
                            // An aborted compaction would let the unacknowledged prompt start.
                            await TrySendAbortAsync(gen);
                            Kill(gen);
                        }
                        else
                        {
                            await AbortAndSettleOrKillAsync(gen, run);
                        }
                        yield return ErrorResult(WatchdogText, run);
                        yield break;
                }
            }
        }
        finally
        {
            abortOnCancel.Dispose();
            if (run.Live)
            {
                _runActive = false;
                _liveGeneration = null;
            }

            // The consumer stopped reading a run that is still going: never leave it running
            // unobserved into the next turn.
            if (run.Started && !run.Terminal)
            {
                run.Terminal = true;
                await TrySendAbortAsync(gen);
                await SettleOrKillAsync(gen, run);
            }
        }
    }

    /// <summary>
    /// The cancellation rule (A12). Before the ack, a prompt nobody waits for must not run later, so
    /// the process is aborted and killed. After it, abort has already gone out from the token's
    /// registration; wait up to 5 s for <c>agent_settled</c>, else kill. The session survives either way.
    /// </summary>
    private async Task OnCancelledAsync(Generation gen, RunState run, bool awaitingAck)
    {
        run.Terminal = true;
        if (awaitingAck)
        {
            _logger.LogInformation("pi: turn cancelled before the prompt ack; killing the process");
            await TrySendAbortAsync(gen);
            Kill(gen);
            return;
        }

        _logger.LogInformation("pi: turn cancelled; abort sent");
        await SettleOrKillAsync(gen, run);
    }

    /// <summary>The ack's result: null to continue the run, or the terminal progress.</summary>
    private AgentProgress? AckOutcome(Generation gen, JsonObject response)
    {
        if (!IsSuccess(response))
        {
            var error = Bounded(Str(response, "error"));
            _logger.LogWarning("pi: the prompt was rejected");
            return ErrorResult($"Pi refused the message: {error}", null);
        }

        var disposition = Str(response["data"], "disposition") ?? "started";
        switch (disposition)
        {
            case "handled":
                _logger.LogInformation("pi: the prompt was handled as a command; no model run");
                return new AgentProgress
                {
                    EventType = "result",
                    Summary = HandledText,
                    FinalResult = HandledText,
                    SessionId = LastSessionId,
                    IsSignificant = true,
                };
            case "queued":
                _logger.LogWarning("pi: the prompt was queued behind a running run");
                break;
        }

        gen.AwaitingSettle = true;
        return null;
    }

    private CancellationTokenRegistration BeginRun(Generation gen, RunState run, CancellationToken ct)
    {
        run.Started = true;
        if (run.Live)
        {
            _finalSeen = false;
            _liveGeneration = gen;
            _runActive = true;
        }

        // Cancellation sends abort at once, from the cancelling thread, wherever the consumer is.
        return ct.Register(() => _ = TrySendAbortAsync(gen));
    }

    /// <summary>
    /// Applies one event to the run and the generation, appending any progress to the run's output.
    /// Returns the terminal result when this event ends the run.
    /// </summary>
    private AgentProgress? HandleEvent(Generation gen, RunState run, PiEvent evt)
    {
        var json = evt.Json;
        switch (evt.Type)
        {
            case "agent_start":
                run.AgentStarted = true;
                return null;

            case "turn_start":
                run.CallStart = evt.Timestamp;
                run.FirstDelta = null;
                return null;

            case "message_update":
            {
                var update = json["assistantMessageEvent"] as JsonObject;
                var kind = Str(update, "type");
                if (kind is "text_delta" or "thinking_delta" or "toolcall_delta" && run.FirstDelta is null)
                    run.FirstDelta = evt.Timestamp;
                if (kind == "text_delta")
                {
                    run.Output.Add(new AgentProgress
                    {
                        EventType = "assistant",
                        Summary = Str(update, "delta") ?? "",
                        IsSignificant = true,
                    });
                }
                return null;
            }

            case "message_end":
                if (json["message"] is JsonObject message && Str(message, "role") == "assistant")
                    OnAssistantMessage(gen, run, message, evt.Timestamp);
                return null;

            case "tool_execution_start":
            {
                var name = Str(json, "toolName") ?? "tool";
                var args = json["args"]?.ToJsonString() ?? "{}";
                _logger.LogInformation("pi tool_use: {Tool}", name);
                run.Output.Add(new AgentProgress
                {
                    EventType = "tool_use",
                    Summary = $"Using {name}",
                    ToolName = name,
                    ToolArgs = args.Length > MaxToolArgsChars ? args[..MaxToolArgsChars] : args,
                    IsSignificant = true,
                });
                return null;
            }

            case "tool_execution_end":
                run.Output.Add(new AgentProgress
                {
                    EventType = "tool_result",
                    Summary = ContentText(json["result"]),
                    ToolName = Str(json, "toolName"),
                    IsSignificant = false,
                });
                return null;

            case "auto_retry_start":
                _logger.LogWarning("pi auto_retry: attempt {Attempt}/{Max}, delay {DelayMs} ms",
                    Num(json, "attempt"), Num(json, "maxAttempts"), Num(json, "delayMs"));
                run.Output.Add(new AgentProgress
                {
                    EventType = "warning",
                    Summary = $"The local model call failed; Pi is retrying (attempt {Num(json, "attempt")} of {Num(json, "maxAttempts")}).",
                    IsSignificant = false,
                });
                return null;

            case "compaction_start":
                gen.CompactionOpenSince = evt.Timestamp;
                return null;

            case "compaction_end":
                OnCompactionEnd(gen, run, json, evt.Timestamp);
                return null;

            case "extension_error":
                _logger.LogWarning("pi extension_error: event {Event}", Str(json, "event"));
                return null;

            case "agent_end":
                run.AgentEnded = true;
                return run.Started && !run.Terminal && run.Final is not null ? Completed(gen, run) : null;

            case "agent_settled":
                gen.AwaitingSettle = false;
                run.Settled = true;
                if (!run.Started || run.Terminal)
                    return null;
                return run.Final is not null ? Completed(gen, run) : Failed(run);

            default:
                return null;
        }
    }

    private void OnAssistantMessage(Generation gen, RunState run, JsonObject message, long timestamp)
    {
        var usage = message["usage"] as JsonObject;
        var input = (int)(Num(usage, "input") ?? 0);
        var cacheRead = (int)(Num(usage, "cacheRead") ?? 0);
        var cacheWrite = (int)(Num(usage, "cacheWrite") ?? 0);
        var output = (int)(Num(usage, "output") ?? 0);
        run.InputTokens += input;
        run.CacheReadTokens += cacheRead;
        run.OutputTokens += output;

        var stop = Str(message, "stopReason") ?? "unknown";
        long? ttftMs = run.CallStart is { } start && run.FirstDelta is { } delta ? ElapsedMs(start, delta) : null;
        long? totalMs = run.CallStart is { } callStart ? ElapsedMs(callStart, timestamp) : null;
        _logger.LogInformation(
            "pi call: prompt={Prompt} cached={Cached} output={Output} ttft_ms={TtftMs} total_ms={TotalMs} stop={Stop}",
            input + cacheRead + cacheWrite, cacheRead, output,
            ttftMs?.ToString() ?? "n/a", totalMs?.ToString() ?? "n/a", stop);

        if (gen.PostCompaction)
        {
            gen.PostCompaction = false;
            if (ttftMs is { } ttft && ttft > _timings.PostCompactionTtftWarning.TotalMilliseconds)
                _logger.LogWarning("pi: post-compaction TTFT {TtftMs} ms is over {Limit:0} s",
                    ttft, _timings.PostCompactionTtftWarning.TotalSeconds);
        }
        run.CallStart = null;
        run.FirstDelta = null;

        var content = message["content"] as JsonArray;
        var callsTool = content?.Any(block => Str(block, "type") == "toolCall") == true;
        if (stop == "stop" && !callsTool)
        {
            var text = string.Concat(content?.Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")) ?? []);
            if (run.Final is null)
            {
                run.Final = text;
                if (run.Live)
                    _finalSeen = true;
            }
            else
            {
                run.LateFinals.Add(text);
            }
            return;
        }

        if (stop is "error" or "length" or "aborted")
        {
            run.LastStopReason = stop;
            run.LastError = Str(message, "errorMessage");
        }
    }

    private void OnCompactionEnd(Generation gen, RunState run, JsonObject json, long timestamp)
    {
        var reason = Str(json, "reason") ?? "unknown";
        var hostMs = gen.CompactionOpenSince is { } since ? ElapsedMs(since, timestamp) : (long?)null;
        gen.CompactionOpenSince = null;

        if (json["result"] is JsonObject result)
        {
            var details = result["details"] as JsonObject;
            var usage = result["usage"] as JsonObject;
            var tail = Num(details, "tailTokens");
            _logger.LogInformation(
                "pi compaction {Reason} path={Path} tokensBefore={TokensBefore} tail={Tail} splitTurn={SplitTurn} "
                + "summary={Summary} cached={Cached} ms={Ms}",
                reason, Str(details, "path") == "prefix" ? "prefix" : "default",
                Num(result, "tokensBefore")?.ToString() ?? "n/a",
                tail?.ToString() ?? "n/a",
                Bool(details, "isSplitTurn") is { } split ? (split ? "true" : "false") : "n/a",
                (Num(details, "output") ?? Num(usage, "output"))?.ToString() ?? "n/a",
                (Num(details, "cached") ?? Num(usage, "cacheRead"))?.ToString() ?? "n/a",
                (Num(details, "ms") ?? hostMs)?.ToString() ?? "n/a");

            if (tail > PiLocalModel.TailBudgetTokens)
                _logger.LogWarning("pi compaction: the kept tail of {Tail} tokens is over the {Budget} budget",
                    tail, PiLocalModel.TailBudgetTokens);

            gen.PostCompaction = true;
            if (reason == "overflow")
                run.OverflowCompacted = true;
        }
        else if (Bool(json, "aborted") == true)
        {
            _logger.LogInformation("pi compaction {Reason} aborted", reason);
        }
        else if (reason == "overflow" && !run.AgentStarted)
        {
            // Pi 1.0.4 can deliver the previous run's failed-recovery compaction_end before this
            // run's agent_start; this run has not failed.
            _logger.LogWarning("pi: a failed overflow recovery from the previous run was reported late; ignored");
        }
        else if (reason == "overflow")
        {
            run.OverflowRecoveryFailed = true;
            _logger.LogError("pi: overflow recovery failed");
        }
        else
        {
            _logger.LogError("pi: compaction failed (reason={Reason})", reason);
        }

        if (hostMs > _timings.CompactionWarning.TotalMilliseconds)
            _logger.LogWarning("pi compaction {Reason} took {Ms} ms, over {Limit:0} s",
                reason, hostMs, _timings.CompactionWarning.TotalSeconds);
    }

    private AgentProgress Completed(Generation gen, RunState run)
    {
        run.Terminal = true;
        gen.CompletedRuns++;
        foreach (var late in run.LateFinals)
            _lateAnswers.Enqueue(Result(late, run, isError: false));
        run.LateFinals.Clear();
        return Result(run.Final!, run, isError: false);
    }

    private static bool LooksLikeContextOverflow(string? error) =>
        error is not null
        && (error.Contains("context", StringComparison.OrdinalIgnoreCase)
            || error.Contains("prompt has", StringComparison.OrdinalIgnoreCase)
            || error.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || error.Contains("overflow", StringComparison.OrdinalIgnoreCase));

    /// <summary>The run ended with no answer: the §13 text for what went wrong.</summary>
    private AgentProgress Failed(RunState run)
    {
        run.Terminal = true;
        string text;
        // Pi 1.0.4 does not always emit the failed-recovery compaction_end, so a run that already
        // compacted for an overflow and then ends on a context error counts as a failed recovery.
        if (run.OverflowRecoveryFailed
            || run.LastError?.Contains("overflow recovery failed", StringComparison.OrdinalIgnoreCase) == true
            || (run.OverflowCompacted && run.LastStopReason == "error" && LooksLikeContextOverflow(run.LastError)))
        {
            text = OverflowRecoveryFailedText;
        }
        else if (run.LastStopReason == "aborted")
        {
            text = "The turn was aborted.";
        }
        else if (IsUnreachable(run.LastError))
        {
            text = UnreachableText(_config.PiBaseUrl);
        }
        else if (run.LastError is { } error)
        {
            text = $"The local model returned an error for model {_config.Model}: {Bounded(error)}";
        }
        else if (run.LastStopReason == "length")
        {
            text = $"The local model {_config.Model} stopped at its output limit without an answer.";
        }
        else
        {
            text = "Pi ended the run without an answer.";
        }

        _logger.LogWarning("pi: the run ended without an answer (stop={Stop})", run.LastStopReason ?? "none");
        return Result(text, run, isError: true);
    }

    private AgentProgress ProcessExited(Generation gen, RunState run)
    {
        _logger.LogWarning("pi: the process exited mid-run; the next turn resumes the session. Last {Bytes} chars of stderr:\n{Stderr}",
            gen.Stderr.Text.Length, gen.Stderr.Text);
        Kill(gen);
        return ErrorResult(ProcessExitText, run, processExit: true);
    }

    private AgentProgress Result(string text, RunState run, bool isError) => new()
    {
        EventType = "result",
        Summary = text,
        FinalResult = text,
        SessionId = LastSessionId,
        Stats = run.Stats(),
        IsErrorResult = isError,
        IsSignificant = true,
    };

    private AgentProgress ErrorResult(string text, RunState? run, bool processExit = false) => new()
    {
        EventType = "result",
        Summary = text,
        FinalResult = text,
        SessionId = LastSessionId,
        Stats = run?.Stats(),
        IsErrorResult = true,
        IsProcessExit = processExit,
        IsSignificant = true,
    };

    private static AgentProgress AsRecovered(AgentProgress answer) => new()
    {
        EventType = "recovered_answer",
        Summary = answer.FinalResult ?? answer.Summary,
        IsSignificant = true,
    };

    private static AgentProgress AsRecovered(string text) => new()
    {
        EventType = "recovered_answer",
        Summary = text,
        IsSignificant = true,
    };

    private static bool IsUnreachable(string? error) =>
        error is not null
        && new[]
        {
            "ECONNREFUSED", "ENOTFOUND", "EHOSTUNREACH", "ENETUNREACH", "ECONNRESET", "ETIMEDOUT", "EAI_AGAIN",
            "fetch failed", "Connection error", "socket hang up",
        }.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));

    // ── Waiting, settling, aborting ──────────────────────────────────────────

    private enum Wake { Ready, Timeout, Exited, Cancelled }

    private static async Task<Wake> WaitAsync(Generation gen, TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readable = gen.Events.Reader.WaitToReadAsync(cts.Token).AsTask();
        var delay = Task.Delay(wait, cts.Token);
        var done = await Task.WhenAny(readable, delay);
        cts.Cancel();

        if (ct.IsCancellationRequested)
            return Wake.Cancelled;
        if (done == readable)
            return readable.IsCompletedSuccessfully && !readable.Result ? Wake.Exited : Wake.Ready;
        return Wake.Timeout;
    }

    /// <summary>
    /// Reads events until <c>agent_settled</c> (true), the process exits (true: nothing is left to
    /// settle) or <paramref name="timeout"/> passes (false). Answers seen meanwhile land in
    /// <paramref name="run"/>.
    /// </summary>
    private async Task<bool> DrainUntilSettledAsync(Generation gen, RunState run, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            while (gen.Events.Reader.TryRead(out var evt))
            {
                HandleEvent(gen, run, evt);
                run.TakeOutput();
                if (evt.Type == "agent_settled")
                    return true;
            }

            var remaining = timeout - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            switch (await WaitAsync(gen, remaining, ct))
            {
                case Wake.Exited:
                    gen.AwaitingSettle = false;
                    return true;
                case Wake.Cancelled:
                    ct.ThrowIfCancellationRequested();
                    break;
                case Wake.Timeout:
                    return false;
            }
        }
    }

    /// <summary>
    /// Before a late run, waits a short bound for its <c>agent_start</c> — longer while a compaction
    /// is open, since a queued steer runs after it. False on <c>agent_settled</c> or the bound.
    /// </summary>
    private async Task<bool> WaitForLateRunAsync(Generation gen, RunState run, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            while (gen.Events.Reader.TryPeek(out var next))
            {
                if (next.Type == "agent_start")
                    return true;
                gen.Events.Reader.TryRead(out _);
                HandleEvent(gen, run, next);
                run.TakeOutput();
                if (next.Type == "agent_settled")
                    return false;
            }

            var wait = gen.CompactionOpenSince is not null
                ? _timings.Watchdog - gen.SinceLastLine
                : _timings.InjectedTurnStart - waited.Elapsed;
            if (wait <= TimeSpan.Zero)
                return false;

            switch (await WaitAsync(gen, wait, ct))
            {
                case Wake.Exited:
                case Wake.Timeout:
                    return false;
                case Wake.Cancelled:
                    ct.ThrowIfCancellationRequested();
                    break;
            }
        }
    }

    /// <summary>The cancellation rule: abort, wait up to 5 s for <c>agent_settled</c>, else kill.</summary>
    private async Task AbortAndSettleOrKillAsync(Generation gen, RunState run)
    {
        await TrySendAbortAsync(gen);
        await SettleOrKillAsync(gen, run);
    }

    private async Task SettleOrKillAsync(Generation gen, RunState run)
    {
        bool settled;
        try
        {
            settled = await DrainUntilSettledAsync(gen, run, _timings.AbortSettle, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            settled = false;
        }

        if (!settled)
        {
            _logger.LogWarning("pi: no agent_settled within {Seconds:0} s of abort; killing the process, "
                               + "the next turn resumes the session", _timings.AbortSettle.TotalSeconds);
            Kill(gen);
        }
    }

    private async Task TrySendAbortAsync(Generation gen)
    {
        if (!gen.IsAlive)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await WriteAsync(gen, Command("abort").WithId(NextId()), cts.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                       or InvalidOperationException)
        {
            // The process is already gone or wedged; the caller kills it.
        }
    }

    private void Kill(Generation gen)
    {
        if (Interlocked.Exchange(ref gen.KilledFlag, 1) == 1)
            return;

        try
        {
            gen.Process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // Already exited.
        }
        gen.Process.Dispose();

        // Only this generation: a newer one is never cleared by a stale kill.
#pragma warning disable CS0420 // a volatile field passed by reference: Interlocked is a full fence
        Interlocked.CompareExchange(ref _current, null, gen);
#pragma warning restore CS0420
    }

    private async Task StopInternalAsync(bool deleteSession)
    {
        var gen = _current;
        if (gen is not null)
        {
            await TrySendAbortAsync(gen);
            Kill(gen);
        }

        _runActive = false;
        _liveGeneration = null;
        _lateAnswers.Clear();

        if (!deleteSession)
            return;

        string? file;
        lock (_stateLock)
        {
            file = _sessionFile;
            _sessionFile = null;
            _sessionId = null;
        }

        if (file is not null)
        {
            TryDelete(file);
            _logger.LogInformation("pi: session file deleted; the next turn starts a new session");
        }
    }

    // ── /run ─────────────────────────────────────────────────────────────────

    private async Task<AgentProgress> RunBashAsync(Generation gen, string command, CancellationToken ct)
    {
        JsonObject response;
        try
        {
            response = await RequestAsync(gen,
                Command("bash", ("command", command), ("excludeFromContext", true)), timeout: null, ct);
        }
        catch (Exception ex) when (ex is PiProcessExitedException or IOException or ObjectDisposedException)
        {
            return new AgentProgress { EventType = "error", Summary = $"/run failed: {ex.Message}", IsSignificant = true };
        }

        if (!IsSuccess(response) || response["data"] is not JsonObject data)
            return new AgentProgress
            {
                EventType = "error",
                Summary = $"/run failed: {Bounded(Str(response, "error"))}",
                IsSignificant = true,
            };

        var output = Str(data, "output") ?? "";
        var exitCode = Num(data, "exitCode");
        if (exitCode is { } code and not 0)
            output += $"\n[exit code {code}]";
        if (Bool(data, "truncated") == true)
            output += "\n[output truncated]";

        return new AgentProgress
        {
            EventType = "result",
            Summary = output,
            FinalResult = output,
            SessionId = LastSessionId,
            IsSignificant = true,
        };
    }

    // ── Wire ─────────────────────────────────────────────────────────────────

    private string NextId() => $"fleet-{Interlocked.Increment(ref _nextId)}";

    private static JsonObject Command(string type, params (string Name, JsonNode? Value)[] fields)
    {
        var command = new JsonObject { ["type"] = type };
        foreach (var (name, value) in fields)
            command[name] = value;
        return command;
    }

    private static async Task WriteAsync(Generation gen, JsonObject command, CancellationToken ct)
    {
        // JsonNode escapes every control character, so a frame never contains a raw LF.
        var bytes = Encoding.UTF8.GetBytes(command.ToJsonString() + "\n");
        await gen.WriteLock.WaitAsync(ct);
        try
        {
            await gen.Process.StandardInput.WriteAsync(bytes, ct);
            await gen.Process.StandardInput.FlushAsync(ct);
        }
        finally
        {
            gen.WriteLock.Release();
        }
    }

    /// <summary>Sends a command and waits for its response. A timeout throws <see cref="TimeoutException"/>.</summary>
    private async Task<JsonObject> RequestAsync(Generation gen, JsonObject command, TimeSpan? timeout, CancellationToken ct)
    {
        var id = NextId();
        var response = gen.Register(id);
        try
        {
            await WriteAsync(gen, command.WithId(id), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is { } limit)
                cts.CancelAfter(limit);
            try
            {
                return await response.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no response to {Str(command, "type")}");
            }
        }
        catch (IOException)
        {
            throw new PiProcessExitedException();
        }
        finally
        {
            gen.Pending.TryRemove(id, out _);
        }
    }

    private async Task ReadStdoutAsync(Generation gen)
    {
        var buffer = new byte[64 * 1024];
        var line = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var read = await gen.Process.StandardOutput.ReadAsync(buffer);
                if (read == 0)
                    break;

                // Strict LF framing: U+2028/U+2029 inside a JSON string are not record ends.
                var span = buffer.AsSpan(0, read);
                int lf;
                while ((lf = span.IndexOf((byte)'\n')) >= 0)
                {
                    line.Write(span[..lf]);
                    HandleLine(gen, line.WrittenSpan);
                    line.ResetWrittenCount();
                    span = span[(lf + 1)..];
                }
                line.Write(span);
            }

            if (line.WrittenCount > 0)
                HandleLine(gen, line.WrittenSpan);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                       or InvalidOperationException)
        {
            // The process is gone.
        }
        finally
        {
            gen.ReaderDone = true;
            gen.Events.Writer.TryComplete();
            foreach (var (_, pending) in gen.Pending)
                pending.TrySetException(new PiProcessExitedException());
        }
    }

    private void HandleLine(Generation gen, ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 0 && raw[^1] == (byte)'\r')
            raw = raw[..^1];
        if (raw.IsEmpty)
            return;

        var timestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref gen.LastLineTimestamp, timestamp);
        _lastActivity = DateTimeOffset.UtcNow;

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(raw, documentOptions: ParseOptions) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is null)
        {
            // Never the content: stdout carries conversation text.
            _logger.LogWarning("pi: ignored a stdout line that is not a JSON object ({Bytes} bytes)", raw.Length);
            return;
        }

        var type = Str(json, "type");
        switch (type)
        {
            case "response":
                if (Str(json, "id") is { } ackId && ackId == gen.PromptAckId)
                    break;
                if (Str(json, "id") is { } id && gen.Pending.TryRemove(id, out var pending))
                    pending.TrySetResult(json);
                else if (!IsSuccess(json))
                    _logger.LogWarning("pi: command {Command} failed", Str(json, "command"));
                return;

            case "extension_ui_request":
                CancelExtensionDialog(gen, json);
                return;

            case "agent_settled":
                // Session file and id come from get_state after every start and every settle.
                _ = RefreshSessionStateAsync(gen);
                break;
        }

        gen.Events.Writer.TryWrite(new PiEvent(json, type, timestamp));
    }

    /// <summary>Fleet has no interactive user behind Pi: a dialog is answered "cancelled" at once.</summary>
    private void CancelExtensionDialog(Generation gen, JsonObject request)
    {
        var method = Str(request, "method");
        if (method is not ("select" or "confirm" or "input" or "editor") || Str(request, "id") is not { } id)
            return;

        _logger.LogWarning("pi: extension dialog {Method} cancelled; there is no interactive user", method);
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(gen, new JsonObject
                {
                    ["type"] = "extension_ui_response",
                    ["id"] = id,
                    ["cancelled"] = true,
                }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });
    }

    private async Task RefreshSessionStateAsync(Generation gen)
    {
        try
        {
            var response = await RequestAsync(gen, Command("get_state"), _timings.StartupState, CancellationToken.None);
            if (IsSuccess(response) && response["data"] is JsonObject state)
                ApplySessionState(gen, state, force: false);
        }
        catch (Exception ex) when (ex is TimeoutException or PiProcessExitedException or IOException
                                       or ObjectDisposedException or OperationCanceledException
                                       or InvalidOperationException)
        {
            // A refresh is best effort; the next settle or start tries again.
        }
    }

    /// <summary>Kept in memory only. A stale generation never overwrites a newer one's session.</summary>
    private void ApplySessionState(Generation gen, JsonObject state, bool force)
    {
        lock (_stateLock)
        {
            if (!force && !ReferenceEquals(_current, gen))
                return;
            if (Str(state, "sessionFile") is { Length: > 0 } file)
                _sessionFile = file;
            if (Str(state, "sessionId") is { Length: > 0 } id)
                _sessionId = id;
        }
    }

    private async Task ReadStderrAsync(Generation gen)
    {
        try
        {
            string? line;
            while ((line = await gen.Process.StandardError.ReadLineAsync()) is not null)
            {
                gen.Stderr.Append(line);
                // The extension writes WARN/INFO/ERROR lines (names and counts only).
                if (line.StartsWith("WARN ", StringComparison.Ordinal))
                    _logger.LogWarning("[pi] {Line}", line[5..]);
                else if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                    _logger.LogError("[pi] {Line}", line[6..]);
                else if (line.StartsWith("INFO ", StringComparison.Ordinal))
                    _logger.LogInformation("[pi] {Line}", line[5..]);
                else
                    _logger.LogDebug("[pi stderr] {Line}", line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                       or InvalidOperationException)
        {
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string WithMediaFallback(string task, IReadOnlyList<MessageImage>? images) =>
        images is { Count: > 0 } ? $"{task}\n\n{ImagesOmittedText(images.Count)}" : task;

    private static bool IsSuccess(JsonNode? response) => Bool(response, "success") == true;

    private static string Bounded(string? text) =>
        text is null ? "(no detail)" : text.Length > MaxServerTextChars ? text[..MaxServerTextChars] : text;

    private static string ContentText(JsonNode? result) =>
        result?["content"] is JsonArray content
            ? string.Concat(content.Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")))
            : "";

    private static long ElapsedMs(long from, long to) =>
        (long)Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string? Str(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static long? Num(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var integer))
            return integer;
        return value.TryGetValue<double>(out var real) ? (long)real : null;
    }

    internal static bool? Bool(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    // ── State ────────────────────────────────────────────────────────────────

    private readonly record struct PiEvent(JsonObject Json, string? Type, long Timestamp);

    /// <summary>One Pi process and everything bound to its lifetime.</summary>
    private sealed class Generation(IPiProcess process)
    {
        public IPiProcess Process { get; } = process;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Pending { get; } = new();
        public Channel<PiEvent> Events { get; } = Channel.CreateUnbounded<PiEvent>(new UnboundedChannelOptions
        {
            SingleWriter = true,
        });
        public StderrTail Stderr { get; } = new(StderrTailChars);
        public long LastLineTimestamp = Stopwatch.GetTimestamp();
        public int KilledFlag;
        public volatile bool ReaderDone;
        public volatile string? PromptAckId;
        public volatile bool AwaitingSettle;
        public int InitialMessageCount;
        public int CompletedRuns;
        public long? CompactionOpenSince;
        public bool PostCompaction;

        public TimeSpan SinceLastLine => Stopwatch.GetElapsedTime(Volatile.Read(ref LastLineTimestamp));

        public bool IsAlive
        {
            get
            {
                if (Volatile.Read(ref KilledFlag) == 1 || ReaderDone)
                    return false;
                try
                {
                    return !Process.HasExited;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    return false;
                }
            }
        }

        public Task<JsonObject> Register(string id)
        {
            var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[id] = tcs;
            if (ReaderDone)
                tcs.TrySetException(new PiProcessExitedException());
            return tcs.Task;
        }
    }

    /// <summary>What one run has produced so far. Owned by the loop reading it.</summary>
    private sealed class RunState(bool live)
    {
        public bool Live { get; } = live;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public List<AgentProgress> Output { get; } = [];
        public List<string> LateFinals { get; } = [];
        public string? Final;
        public bool Started;
        public bool Terminal;
        public bool AgentStarted;
        public bool AgentEnded;
        public bool Settled;
        public long? CallStart;
        public long? FirstDelta;
        public int InputTokens;
        public int CacheReadTokens;
        public int OutputTokens;
        public string? LastStopReason;
        public string? LastError;
        public bool OverflowRecoveryFailed;
        public bool OverflowCompacted;

        public List<AgentProgress> TakeOutput()
        {
            var taken = Output.ToList();
            Output.Clear();
            return taken;
        }

        public IEnumerable<string> AllFinals() =>
            Final is null ? LateFinals : LateFinals.Prepend(Final);

        public ExecutionStats Stats() => new()
        {
            InputTokens = InputTokens,
            CacheReadTokens = CacheReadTokens,
            OutputTokens = OutputTokens,
            DurationMs = (int)Clock.ElapsedMilliseconds,
        };
    }

    /// <summary>The last characters of Pi's stderr, for the start-failure log.</summary>
    private sealed class StderrTail(int capacity)
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get { lock (_text) return _text.ToString(); }
        }

        public void Append(string line)
        {
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > capacity)
                    _text.Remove(0, _text.Length - capacity);
            }
        }
    }

    private sealed class PiStartFault(string field, string stderrTail) : Exception(field)
    {
        public string Field { get; } = field;
        public string StderrTail { get; } = stderrTail;
    }
}

/// <summary>Pi did not start after every attempt; <see cref="Field"/> names what failed, never a value.</summary>
public sealed class PiStartException(string field) : InvalidOperationException(PiExecutor.StartFailureText(field))
{
    public string Field { get; } = field;
}

/// <summary>The Pi process exited before a command was answered.</summary>
public sealed class PiProcessExitedException() : IOException("The Pi process exited.");

/// <summary>Seams and limits for <see cref="PiExecutor"/>; the defaults are production's.</summary>
internal sealed class PiExecutorSettings
{
    /// <summary><c>PI_CODING_AGENT_DIR</c>, where the three files are written.</summary>
    public string AgentDir { get; init; } = PiLocalModel.AgentDir;

    public string ExtensionDir { get; init; } = PiLocalModel.ExtensionDir;

    /// <summary>The MCP config the extension reads (<c>fleet-pi.json</c> <c>mcpConfigPath</c>).</summary>
    public string McpConfigPath { get; init; } = PiLocalModel.McpConfigPath;

    public string PiBinary { get; init; } = PiExecutor.PiBin;

    public PiExecutorTimings Timings { get; init; } = new();

    /// <summary>Starts the process from the start info the executor built; null starts a real one.</summary>
    public Func<ProcessStartInfo, IPiProcess>? ProcessStarter { get; init; }

    /// <summary>The environment the child inherits; null is this process's own.</summary>
    public IReadOnlyDictionary<string, string>? BaseEnvironment { get; init; }

    /// <summary>The Pi version for the start line; null runs <c>pi --version</c> once.</summary>
    public Func<CancellationToken, Task<string?>>? VersionProbe { get; init; }
}

/// <summary>Every deadline the executor keeps, injectable so the tests run in seconds (A14, A14b).</summary>
internal sealed record PiExecutorTimings
{
    public TimeSpan StartupState { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PreviousSettle { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan AbortSettle { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Ack { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Steer { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Watchdog { get; init; } = TimeSpan.FromSeconds(900);
    public TimeSpan InjectedTurnStart { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan AckPausedWarning { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CompactionWarning { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PostCompactionTtftWarning { get; init; } = TimeSpan.FromSeconds(65);
    public TimeSpan VersionProbe { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>The Pi child as the executor uses it: three streams, an exit check and a kill.</summary>
internal interface IPiProcess : IDisposable
{
    /// <summary>The OS process id; 0 for an in-memory stand-in.</summary>
    int Id { get; }

    bool HasExited { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    TextReader StandardError { get; }

    /// <summary>Kills the process and everything it started (the model's shell commands).</summary>
    void Kill();
}

/// <summary>A real OS process.</summary>
internal sealed class OsPiProcess(Process process) : IPiProcess
{
    public static IPiProcess Start(ProcessStartInfo psi) =>
        new OsPiProcess(Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {psi.FileName}"));

    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public Stream StandardInput => process.StandardInput.BaseStream;
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public TextReader StandardError => process.StandardError;

    public void Kill()
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    public void Dispose() => process.Dispose();
}

file static class JsonObjectExtensions
{
    /// <summary>The command with <c>id</c> first, as Pi's own client writes it.</summary>
    public static JsonObject WithId(this JsonObject command, string id)
    {
        var framed = new JsonObject { ["id"] = id };
        foreach (var (name, value) in command.ToList())
        {
            command.Remove(name);
            framed[name] = value;
        }
        return framed;
    }
}
