using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Runs Google's Antigravity CLI (<c>agy</c>) for a gemini agent: one process per turn, in one kept
/// conversation, with a paid Gemini API key (<c>GEMINI_API_KEY</c>, inherited from the container).
/// </summary>
/// <remarks>
/// <para>Each turn starts
/// <c>agy --output-format stream-json --input-format stream-json --disable-slash-commands
/// --dangerously-skip-permissions --settings {…} [--model M] [--conversation ID]</c>, reads the
/// <c>init</c> event (the CLI prints it before it reads its input), writes the one user message
/// and closes the input. What was measured on the pinned CLI, and what
/// <c>tests/antigravity-cli/run.sh</c> keeps true, is in <c>docs/providers/gemini.md</c>.</para>
/// <para>The session rule is the one of CLAUDE.md "Provider sessions": the conversation id is kept
/// in <see cref="ProviderSessionFile"/> with the hash of the exact instructions, and a start
/// resumes only when everything matches. The CLI does not fail on a conversation it cannot find:
/// it starts another one and says so in <c>init</c>. That is a failed resume here, known before
/// the message is written, so the message is then a first turn of a new session.</para>
/// <para>Nothing is ever cut. A message over <see cref="MaxPromptBytes"/> and instructions the CLI
/// would not deliver whole (<see cref="GeminiInstructionFiles"/>) end the turn with an error before
/// a process is started.</para>
/// </remarks>
public sealed class GeminiExecutor : IAgentExecutor
{
    /// <summary>The Antigravity CLI as installed in the agent image.</summary>
    internal const string CliBin = "agy";

    /// <summary>The variable the CLI takes the Gemini API key from.</summary>
    internal const string ApiKeyVariable = "GEMINI_API_KEY";

    /// <summary>
    /// The CLI's own switch for its background self-update; only the value <c>true</c> works. The
    /// image sets it too. Without it a start replaces the pinned binary when a newer one is out.
    /// </summary>
    internal const string DisableAutoUpdateVariable = "AGY_CLI_DISABLE_AUTO_UPDATE";

    /// <summary>
    /// The largest message, in UTF-8 bytes, that is sent. The CLI cuts one user message at 192,000
    /// bytes including a wrapper of its own (413 bytes measured): 191,500 arrived whole and 191,600
    /// did not. A larger message is refused, never cut.
    /// </summary>
    internal const int MaxPromptBytes = 190_000;

    /// <summary>
    /// Settings for this process only; nothing is saved. <c>modelProvider</c> selects the API key
    /// instead of a Google sign-in, and the terminal sandbox is off because the container is the
    /// sandbox (with it on, every shell command fails there).
    /// </summary>
    internal const string SessionSettings = """{"modelProvider":"gemini","enableTerminalSandbox":false}""";

    /// <summary>How long a kill waits for the exit before the process is handed to a watcher.</summary>
    internal static readonly TimeSpan KillExitWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a CLI that has printed its result, or closed its output, is given to exit by
    /// itself. The turn does not wait longer for it: the result is in hand, and what is still
    /// running is killed. Whether the pinned CLI ever stays (it makes calls of its own on every
    /// turn) was not measured with a network; without one it exits at once.
    /// </summary>
    internal static readonly TimeSpan DefaultExitWait = TimeSpan.FromSeconds(30);

    private readonly AgentOptions _config;
    private readonly UsageReporter? _usage;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<GeminiExecutor> _logger;
    private readonly ProviderSessionFile _sessionFile;
    private readonly string _geminiHome;
    private readonly TimeSpan _exitWait;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;

    // Kills a CLI that is still running and reports whether its exit is confirmed (#394).
    private readonly Func<Process, Task<bool>> _terminate;

    // The conversation this executor is in, with the instructions it began with. Null before the
    // first start, after /reset and /stop, and after a resume that failed.
    private HeldSession? _session;
    private string? _sessionOrigin;
    private string? _lastSessionId;

    // A new conversation has started and no message has been written into it yet.
    private bool _newSessionLinePending;

    // Raised by /reset and /stop, so a turn that was running then does not put its session back.
    private int _sessionEpoch;

    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

    public string? LastSessionId => _lastSessionId;
    public DateTimeOffset LastActivity => _lastActivity;

    // One process per turn: nothing stays warm. The conversation is kept by the CLI on disk.
    public bool IsProcessWarm => false;
    public bool KeepsConversation => true;
    public string? SessionOrigin => _sessionOrigin;

    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        Array.Empty<BackgroundTaskInfo>();

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public GeminiExecutor(
        IOptions<AgentOptions> config, PromptBuilder promptBuilder, ILogger<GeminiExecutor> logger,
        UsageReporter? usage = null)
        : this(config, promptBuilder, logger, Process.Start, usage: usage)
    {
    }

    internal GeminiExecutor(
        IOptions<AgentOptions> config, PromptBuilder promptBuilder, ILogger<GeminiExecutor> logger,
        Func<ProcessStartInfo, Process?> processStarter,
        Func<Process, Task<bool>>? terminate = null,
        UsageReporter? usage = null,
        string? geminiHome = null,
        TimeSpan? exitWait = null)
    {
        _exitWait = exitWait ?? DefaultExitWait;
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _usage = usage;
        _logger = logger;
        _processStarter = processStarter;
        _terminate = terminate ?? (process => ProviderChildProcess.KillAndConfirmExitAsync(process, KillExitWait));
        _geminiHome = geminiHome
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
        _sessionFile = new ProviderSessionFile(_config.WorkDir, AgentProviders.Gemini, ProviderSessionFile.Cloud, logger);
    }

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var progress in RunToResultAsync(WithAttachmentHints(task, images, documents), ct))
            yield return progress;
    }

    /// <summary>
    /// The CLI's print mode takes text only, so an attachment reaches the model as a path hint in
    /// the text (<c>[image attachment: …]</c>), which the model opens with the CLI's own
    /// <c>read_file</c> tool (images, PDFs, audio and video by path). The Telegram intake has put
    /// the hints in already; a chat image arrives without one, so any hint the text does not hold
    /// is added here. An attachment that was not persisted has no path and gets none.
    /// </summary>
    internal static string WithAttachmentHints(
        string task, IReadOnlyList<MessageImage>? images, IReadOnlyList<MessageDocument>? documents)
    {
        var missing = AttachmentSweeper.BuildHints(images ?? [], documents)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(hint => !task.Contains(hint, StringComparison.Ordinal))
            .ToList();
        return missing.Count == 0 ? task : $"{task}\n{string.Join('\n', missing)}";
    }

    public async IAsyncEnumerable<AgentProgress> SendCommandAsync(
        string command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The CLI's slash commands are off in this mode, so a command is a message like any other
        // and the model answers it.
        await foreach (var progress in RunToResultAsync(command, ct))
            yield return progress;
    }

    /// <summary>
    /// The one read loop behind a task and a command: runs the CLI, stamps the activity time on
    /// every event and stops after the terminal result.
    /// </summary>
    private async IAsyncEnumerable<AgentProgress> RunToResultAsync(
        string input, [EnumeratorCancellation] CancellationToken ct)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        await foreach (var progress in RunCliAsync(input, ct))
        {
            _lastActivity = DateTimeOffset.UtcNow;
            yield return progress;
            if (progress.FinalResult is not null) yield break;
        }
    }

    public void RequestRestart() { }

    /// <summary>
    /// /reset and /stop: the session is forgotten and its file removed, so the next turn starts a
    /// new conversation, in this container or the next. A running turn is stopped by its own
    /// cancellation, which kills its process.
    /// </summary>
    public Task StopProcessAsync()
    {
        Interlocked.Increment(ref _sessionEpoch);
        _session = null;
        _sessionOrigin = null;
        _lastSessionId = null;
        _newSessionLinePending = false;
        _sessionFile.Remove();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A credential change needs nothing here: every turn starts a process, which reads the key
    /// from the environment the container was started with. The session is kept.
    /// </summary>
    public Task<bool> TryStopProcessAsync() => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── One turn ──────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<AgentProgress> RunCliAsync(
        string input, [EnumeratorCancellation] CancellationToken ct)
    {
        var instructions = _promptBuilder.BuildSystemPrompt();
        var instructionsHash = ProviderSessionFile.HashInstructions(instructions);
        var epoch = Volatile.Read(ref _sessionEpoch);

        // The size is checked with the new-session line on it, so a resume that turns into a new
        // session at init cannot push the message over the limit after the process has started.
        var promptBytes = Encoding.UTF8.GetByteCount(PromptAssembler.WithNewSessionLine(input));
        if (promptBytes > MaxPromptBytes)
        {
            yield return Refusal(
                $"The message is {promptBytes} bytes; the Antigravity CLI takes at most {MaxPromptBytes} bytes in one message. Nothing was sent.");
            yield break;
        }

        if (GeminiInstructionFiles.Prepare(_geminiHome, _config.WorkDir, instructions) is { } instructionsFault)
        {
            _logger.LogError("Gemini turn refused: {Fault}", instructionsFault);
            yield return Refusal($"The turn was not started: {instructionsFault}. Nothing was sent.");
            yield break;
        }

        var startTime = DateTimeOffset.UtcNow;
        var usageTurn = _usage?.BeginTurn();
        var turn = new TurnState();
        Process? process = null;
        var stderrLines = new StringBuilder();
        string? failedResume = null;

        try
        {
            // At most two starts: a resume whose process ends before its init is a failed resume,
            // and the turn goes on as a new session inside this same start. Never a third.
            for (var attempt = 0; ; attempt++)
            {
                var resumeId = DecideSession(instructionsHash, failedResume);
                failedResume = null;
                stderrLines.Clear();

                var psi = BuildStartInfo(resumeId);
                process = ProviderChildProcess.Start(_processStarter, psi, "Failed to start the Antigravity CLI (agy)");

                // CancellationToken.None is intentional: the stream ends when the process does, and
                // the lines are needed for the error text of a start that failed.
                var stderrTask = ProviderChildProcess.PumpStderr(process.StandardError, line =>
                {
                    lock (stderrLines) stderrLines.AppendLine(line);
                    _logger.LogWarning("GeminiExecutor stderr: {Line}", line);
                }, CancellationToken.None);

                // The CLI prints init before it reads anything. Whatever comes first decides.
                string? conversationId = null;
                AgentProgress? early = null;
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                {
                    if (!TryParse(line, out var ev)) continue;
                    if (EventName(ev) == "init")
                    {
                        conversationId = ReadString(ev, "conversation_id");
                        break;
                    }

                    early = MapResult(ev, turn, startTime);
                    if (early is not null) break;
                }

                if (string.IsNullOrEmpty(conversationId))
                {
                    // Nothing will be written: a CLI that waits for its input gets its end.
                    try { process.StandardInput.Close(); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* already gone */ }

                    var exited = await ExitedAsync(process, stderrTask, ct);
                    var why = early?.FinalResult ?? ExitText(process, stderrLines);

                    if (resumeId is not null && attempt == 0)
                    {
                        _logger.LogWarning("Gemini resume of {SessionId} ended before init: {Why}", resumeId, why);
                        ForgetSession(epoch);
                        failedResume = "the CLI ended before its init";
                        if (exited || await _terminate(process))
                            process.Dispose();
                        else
                            _ = CloseOnExitAsync(process);
                        process = null;
                        continue;
                    }

                    CompleteUsage(usageTurn, turn, startTime);
                    yield return early ?? ErrorResult(why);
                    yield break;
                }

                var newSession = OnInit(resumeId, conversationId, instructionsHash, epoch);

                yield return new AgentProgress
                {
                    EventType = "system",
                    Summary = "Processing...",
                    IsSignificant = false,
                    SessionId = conversationId,
                };

                // One NDJSON line, then end of input: the CLI runs one turn and exits.
                var text = newSession ? PromptAssembler.WithNewSessionLine(input) : input;
                await process.StandardInput.WriteAsync(UserMessageLine(text).AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
                process.StandardInput.Close();
                if (newSession && epoch == Volatile.Read(ref _sessionEpoch))
                    _newSessionLinePending = false;

                AgentProgress? final = null;
                while (final is null && (line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
                {
                    if (!TryParse(line, out var ev)) continue;

                    final = MapResult(ev, turn, startTime);
                    if (final is not null) break;

                    foreach (var mapped in MapStep(ev, turn))
                        yield return mapped;
                }

                // A process still running after the bound is killed by the finally below.
                await ExitedAsync(process, stderrTask, ct);

                // Both ends are this turn's own terminal; only a turn that never got here has no row.
                CompleteUsage(usageTurn, turn, startTime);
                yield return final ?? ErrorResult(ExitText(process, stderrLines));
                yield break;
            }
        }
        finally
        {
            // Cancelling a turn is killing its process: print mode has no cancel message.
            var exited = true;
            if (process is not null)
            {
                try { exited = process.HasExited || await _terminate(process); }
                catch { exited = false; }
            }

            if (exited)
            {
                process?.Dispose();
            }
            else
            {
                _logger.LogWarning("GeminiExecutor: the Antigravity CLI did not confirm its exit after a kill; waiting for it in the background");
                _ = CloseOnExitAsync(process!);
            }
        }
    }

    /// <summary>
    /// Waits for the exit of a CLI that has nothing more to say, and for the rest of its error
    /// output, at most <see cref="DefaultExitWait"/>. False when it is still running then; the
    /// turn's cancellation still ends the wait at once.
    /// </summary>
    private async Task<bool> ExitedAsync(Process process, Task stderrTask, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(_exitWait);
        try
        {
            await process.WaitForExitAsync(bound.Token);
            await stderrTask.WaitAsync(bound.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "GeminiExecutor: the Antigravity CLI did not exit within {Seconds} s of its last output; it is killed",
                (int)_exitWait.TotalSeconds);
            return false;
        }
    }

    private static async Task CloseOnExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch { /* exit not confirmed */ }
        finally
        {
            process.Dispose();
        }
    }

    // ── The process ───────────────────────────────────────────────────────────

    /// <summary>
    /// The CLI's arguments. <c>Agent.Model</c> goes to <c>--model</c> as it is, and no model means
    /// no flag (the CLI's own default). There is no <c>--effort</c>: a model slug carries it
    /// (<c>gemini-4-argon-medium</c>) and the CLI refuses the flag beside such a slug.
    /// </summary>
    internal static IReadOnlyList<string> BuildArgs(string? model, string? resumeId)
    {
        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            // A message that starts with a slash is a message, not a command of the CLI.
            "--disable-slash-commands",
            // Without it print mode denies every shell command and every write in the workspace.
            "--dangerously-skip-permissions",
            "--settings", SessionSettings,
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
        }

        if (!string.IsNullOrEmpty(resumeId))
        {
            args.Add("--conversation");
            args.Add(resumeId);
        }

        return args;
    }

    internal ProcessStartInfo BuildStartInfo(string? resumeId)
    {
        var psi = ProviderChildProcess.CreateStartInfo(CliBin, _config.WorkDir, createNoWindow: false);
        foreach (var arg in BuildArgs(_config.Model, resumeId))
            psi.ArgumentList.Add(arg);

        // GEMINI_API_KEY is inherited as the container has it. The pin must hold: no self-update.
        psi.Environment[DisableAutoUpdateVariable] = "true";
        return psi;
    }

    /// <summary>The one line of input: <c>{"event":"user","message":{"content":"…"}}</c>.</summary>
    internal static string UserMessageLine(string text)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", "user");
            writer.WriteStartObject("message");
            writer.WriteString("content", text);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private string ExitText(Process process, StringBuilder stderrLines)
    {
        string text;
        lock (stderrLines) text = stderrLines.ToString().Trim();
        if (text.Length == 0)
        {
            text = process.HasExited
                ? $"The Antigravity CLI exited with code {process.ExitCode} and no result"
                : "The Antigravity CLI closed its output with no result and did not exit";
        }

        // The CLI does not print the key; this keeps it so whatever a later version prints.
        var key = Environment.GetEnvironmentVariable(ApiKeyVariable);
        return string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]", StringComparison.Ordinal);
    }

    // ── The session ───────────────────────────────────────────────────────────

    /// <summary>
    /// What this start does about the conversation: the id to resume, or null for a new one. The
    /// rule is <see cref="ProviderSessionFile.Decide"/>'s; a conversation this executor already
    /// holds is resumed while the instructions are the ones it began with.
    /// </summary>
    private string? DecideSession(string instructionsHash, string? failedResume)
    {
        string? resumeId = null;
        string reason;
        if (failedResume is not null)
        {
            reason = $"resume failed: {failedResume}";
        }
        else if (_session is { } held)
        {
            if (held.InstructionsHash == instructionsHash)
                resumeId = held.Id;
            reason = "instructions changed";
        }
        else
        {
            var decision = _sessionFile.Decide(instructionsHash);
            resumeId = decision.ResumeId;
            reason = decision.Reason;
        }

        if (resumeId is not null)
            return resumeId;

        _session = null;
        _lastSessionId = null;
        _logger.LogInformation("Gemini session new: {Reason}", reason);
        return null;
    }

    /// <summary>
    /// The CLI has said which conversation this process is in. Returns true when it is a new one,
    /// so the message about to be written is its first.
    /// </summary>
    private bool OnInit(string? resumeId, string conversationId, string instructionsHash, int epoch)
    {
        var resumed = resumeId is not null && string.Equals(resumeId, conversationId, StringComparison.Ordinal);
        if (resumeId is not null && !resumed)
        {
            // Not an error for the CLI: it could not find (or read) the conversation and began
            // another. For the fleet this is a resume that failed.
            _sessionFile.Remove();
            _session = null;
            _logger.LogInformation(
                "Gemini session new: resume failed: the CLI did not find conversation {SessionId} and started {NewSessionId}",
                resumeId, conversationId);
        }

        if (epoch != Volatile.Read(ref _sessionEpoch))
            return !resumed;

        if (resumed)
        {
            if (_session is null)
            {
                _sessionOrigin = "resumed";
                _logger.LogInformation("Gemini session resumed: {SessionId}", conversationId);
            }
        }
        else
        {
            _sessionOrigin = "new";
            _newSessionLinePending = true;
        }

        _lastSessionId = conversationId;
        if (_session is not { } held || held.Id != conversationId || held.InstructionsHash != instructionsHash)
        {
            _session = new HeldSession(conversationId, instructionsHash);
            _sessionFile.Save(conversationId, instructionsHash);
        }

        return _newSessionLinePending;
    }

    /// <summary>Drops a session whose resume failed, in memory and on disk.</summary>
    private void ForgetSession(int epoch)
    {
        _sessionFile.Remove();
        if (epoch != Volatile.Read(ref _sessionEpoch))
            return;
        _session = null;
        _lastSessionId = null;
        _sessionOrigin = null;
    }

    private sealed record HeldSession(string Id, string InstructionsHash);

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>What one turn's events have said so far.</summary>
    internal sealed class TurnState
    {
        public StringBuilder Text { get; } = new();
        public HashSet<long> AnnouncedTools { get; } = [];
        public JsonElement? Usage { get; set; }
        public long? NumTurns { get; set; }
        public string? ConversationId { get; set; }
    }

    private bool TryParse(string line, out JsonElement ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            ev = document.RootElement.Clone();
            return ev.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            _logger.LogWarning("GeminiExecutor: a line of the CLI's output is not JSON ({Length} characters)", line.Length);
            return false;
        }
    }

    private static string? EventName(JsonElement ev) => ReadString(ev, "event");

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Maps a <c>step_update</c> event of <c>agy --output-format stream-json</c> (1.5.14):
    /// <code>
    /// {"event":"step_update","step_update":{"conversation_id":…,"step_index":N,
    ///   "state":"ACTIVE|DONE|ERROR","step_type":"user_input|agent_response|tool|system_message|checkpoint|error_message",
    ///   "text_delta":…,"tool_name":…,"tool_info":{"name":…,"parameters":{…},"output":…|"error":{"type":…,"message":…}}}}
    /// </code>
    /// Text is not significant (the result delivers the answer); a tool step is announced once,
    /// significant, with its name and arguments, and its end is a tool result.
    /// </summary>
    internal static IReadOnlyList<AgentProgress> MapStep(JsonElement ev, TurnState turn)
    {
        if (EventName(ev) != "step_update"
            || !ev.TryGetProperty("step_update", out var step) || step.ValueKind != JsonValueKind.Object)
            return [];

        var state = ReadString(step, "state");
        switch (ReadString(step, "step_type"))
        {
            case "agent_response":
                var delta = ReadString(step, "text_delta");
                if (string.IsNullOrEmpty(delta)) return [];
                turn.Text.Append(delta);
                return [new AgentProgress { EventType = "assistant", Summary = delta, IsSignificant = false }];

            case "tool":
                var mapped = new List<AgentProgress>(2);
                step.TryGetProperty("tool_info", out var info);
                var index = step.TryGetProperty("step_index", out var i) && i.TryGetInt64(out var n) ? n : -1;
                if (turn.AnnouncedTools.Add(index))
                {
                    var (name, args) = ToolCall(step, info);
                    mapped.Add(new AgentProgress
                    {
                        EventType = "tool_use",
                        Summary = $"Using {name}",
                        ToolName = name,
                        ToolArgs = args,
                        IsSignificant = true,
                    });
                }

                if (state is "DONE" or "ERROR")
                {
                    mapped.Add(new AgentProgress
                    {
                        EventType = "tool_result",
                        Summary = ToolOutcome(info),
                        IsSignificant = false,
                    });
                }

                return mapped;

            default:
                return [];
        }
    }

    /// <summary>
    /// A tool's name and arguments. An MCP tool is called through the CLI's <c>call_mcp_tool</c>
    /// (<c>{ServerName, ToolName, Arguments}</c>); it is shown under the name the other providers
    /// give it, <c>mcp__server__tool</c>, with its own arguments.
    /// </summary>
    private static (string Name, string Args) ToolCall(JsonElement step, JsonElement info)
    {
        var name = ReadString(step, "tool_name") ?? ReadString(info, "name") ?? "";
        var hasParameters = info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object;
        if (!hasParameters)
            return (name, "{}");

        parameters = info.GetProperty("parameters");
        if (name == "call_mcp_tool"
            && ReadString(parameters, "ServerName") is { Length: > 0 } server
            && ReadString(parameters, "ToolName") is { Length: > 0 } tool)
        {
            var arguments = parameters.TryGetProperty("Arguments", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.GetRawText()
                : "{}";
            return ($"mcp__{server}__{tool}", arguments);
        }

        return (name, parameters.GetRawText());
    }

    private static string ToolOutcome(JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object)
            return "";
        if (ReadString(info, "output") is { } output)
            return output;
        if (info.TryGetProperty("error", out var error))
            return ReadString(error, "message") ?? (error.ValueKind == JsonValueKind.String ? error.GetString()! : "tool error");
        return "";
    }

    /// <summary>
    /// Maps the terminal event, or returns null for any other:
    /// <code>
    /// {"event":"result","result":{"conversation_id":…,"status":"SUCCESS|ERROR","response":…,"error":…,
    ///   "num_turns":N,"usage":{"input_tokens":…,"output_tokens":…,"thinking_tokens":…,"cache_read_tokens":…,"total_tokens":…}}}
    /// </code>
    /// Anything but <c>SUCCESS</c> is an error result with the CLI's own error text.
    /// </summary>
    internal static AgentProgress? MapResult(JsonElement ev, TurnState turn, DateTimeOffset startTime)
    {
        if (EventName(ev) != "result"
            || !ev.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;

        turn.ConversationId = ReadString(result, "conversation_id") is { Length: > 0 } id ? id : null;
        if (result.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            turn.Usage = usage.Clone();
        turn.NumTurns = result.TryGetProperty("num_turns", out var turns) && turns.TryGetInt64(out var count) && count >= 0 ? count : null;

        var stats = new ExecutionStats
        {
            DurationMs = (int)Math.Max(0, (DateTimeOffset.UtcNow - startTime).TotalMilliseconds),
        };

        if (ReadString(result, "status") == "SUCCESS")
        {
            var response = ReadString(result, "response");
            var text = string.IsNullOrEmpty(response) ? turn.Text.ToString() : response;
            return new AgentProgress
            {
                EventType = "result",
                Summary = text,
                FinalResult = text,
                IsSignificant = true,
                SessionId = turn.ConversationId,
                Stats = stats,
            };
        }

        var message = ReadString(result, "error") is { Length: > 0 } error
            ? error
            : "The Antigravity CLI reported an error without a message";
        return new AgentProgress
        {
            EventType = "result",
            Summary = message,
            FinalResult = message,
            IsErrorResult = true,
            IsSignificant = true,
            SessionId = turn.ConversationId,
            Stats = stats,
        };
    }

    private static AgentProgress ErrorResult(string message) => new()
    {
        EventType = "result",
        Summary = message,
        FinalResult = message,
        IsErrorResult = true,
        IsSignificant = true,
    };

    /// <summary>A turn that ends before any process starts. It has no usage row: the provider saw nothing.</summary>
    private static AgentProgress Refusal(string message) => ErrorResult(message);

    /// <summary>
    /// The usage row of a finished run. The CLI's result counts the whole conversation, across
    /// processes, so the row is cumulative per conversation; a count it did not report stays null.
    /// </summary>
    private void CompleteUsage(UsageTurn? usageTurn, TurnState turn, DateTimeOffset startTime)
    {
        if (usageTurn is null)
            return;
        var durationMs = (long)Math.Max(0, (DateTimeOffset.UtcNow - startTime).TotalMilliseconds);
        _usage!.Complete(usageTurn, ProviderUsageSnapshot.ForGemini(
            _config.Model, durationMs, turn.Usage, turn.NumTurns, turn.ConversationId ?? _lastSessionId));
    }
}
