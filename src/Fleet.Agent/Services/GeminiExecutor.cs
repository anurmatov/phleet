using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Spawns a fresh `gemini` CLI process per task.
/// System prompt is delivered via a temp file pointed to by GEMINI_SYSTEM_MD.
/// Task text is written to process stdin; responses stream from stdout as NDJSON
/// (--output-format stream-json).
///
/// Fresh process per task = cross-turn history bleed is structurally impossible.
/// No module-level chat state; no session objects; no process reuse between ExecuteAsync calls.
/// Matches the subprocess-per-task isolation of ClaudeExecutor and CodexExecutor.
///
/// Auth: OAuth credentials at ~/.gemini/oauth_creds.json (writable bind mount).
/// Two-level refresh: (1) the CLI's google-auth-library refreshes tokens in-place on expiry;
/// (2) AuthTokenRefreshWorkflow provides a host-side safety net every 30 min.
///
/// Images: hint-only in v1 ([image attachment: path] injected into task text by
/// AttachmentSweeper.BuildHints). Gemini CLI v0.40.1 headless file attachment is unconfirmed.
/// PDFs: hint-only ([document attachment: path]). No built-in Read/Write tools in gemini provider.
/// MCP: HTTP/SSE transport only. stdio servers are skipped by entrypoint.sh when writing
/// ~/.gemini/settings.json. Agents must use MCP tools for all filesystem/shell operations.
/// </summary>
public sealed class GeminiExecutor : IAgentExecutor
{
    private readonly AgentOptions _config;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<GeminiExecutor> _logger;

    private string? _lastSessionId;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

    // Gemini CLI per-task spawn: no persistent process, no restart state.
    // IsProcessWarm is always false — there is no process to warm up.
    public string? LastSessionId => _lastSessionId;
    public DateTimeOffset LastActivity => _lastActivity;
    public bool IsProcessWarm => false;

    // GeminiExecutor has no background subagent task tracking (no persistent process).
    public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() =>
        Array.Empty<BackgroundTaskInfo>();

    public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public GeminiExecutor(IOptions<AgentOptions> config, PromptBuilder promptBuilder, ILogger<GeminiExecutor> logger)
    {
        _config = config.Value;
        _promptBuilder = promptBuilder;
        _logger = logger;

        _logger.LogInformation(
            "GeminiExecutor: CLI-per-task mode. " +
            "Image and PDF attachments are hint-only ([attachment: path] in task text). " +
            "MCP tools require HTTP/SSE transport — stdio servers are skipped.");
    }

    public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
        string task,
        IReadOnlyList<MessageImage>? images = null,
        IReadOnlyList<MessageDocument>? documents = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _lastActivity = DateTimeOffset.UtcNow;
        _lastSessionId = null; // no session resumption for CLI-per-task

        await foreach (var progress in RunCliAsync(task, ct))
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
        // Commands like /compact have no meaningful effect with a per-task fresh process.
        // Run the command as a regular task so the agent at least sees the input and can respond.
        _lastActivity = DateTimeOffset.UtcNow;

        await foreach (var progress in RunCliAsync(command, ct))
        {
            _lastActivity = DateTimeOffset.UtcNow;
            yield return progress;
            if (progress.FinalResult is not null) yield break;
        }
    }

    // No-ops: per-task spawn means there is no persistent process to stop or restart.
    public void RequestRestart() { }
    public Task StopProcessAsync() => Task.CompletedTask;
    public Task<bool> TryStopProcessAsync() => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Core per-task runner ──────────────────────────────────────────────────

    private async IAsyncEnumerable<AgentProgress> RunCliAsync(
        string input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Write system prompt to a temp file. GEMINI_SYSTEM_MD env var points the CLI at it.
        // The file is deleted in the finally block regardless of outcome.
        var systemPromptPath = Path.Combine(Path.GetTempPath(), $"gemini-system-{Guid.NewGuid():N}.md");
        Process? process = null;

        // Capture wall-clock start time before the process starts so DurationMs measures
        // the full task duration. (_lastActivity is updated on every yielded event, so
        // using it would measure near-zero — last-event-to-exit gap, not task duration.)
        var startTime = DateTimeOffset.UtcNow;

        var accumulator = new StringBuilder();
        var stderrLines = new StringBuilder();
        var turnStarted = false;

        try
        {
            await File.WriteAllTextAsync(systemPromptPath, _promptBuilder.BuildSystemPrompt(), ct);

            var model = string.IsNullOrWhiteSpace(_config.Model) ? "gemini-2.5-flash" : _config.Model;

            var psi = new ProcessStartInfo
            {
                FileName = "gemini",
                // --output-format stream-json: emit NDJSON events on stdout for incremental parsing.
                // -m <model>: specify model; defaults to gemini-2.5-flash if omitted.
                // --yolo (-y): suppress interactive MCP tool-call approval prompts.
                //   Without this, the CLI blocks waiting for user confirmation on every tool call
                //   — the agent hangs indefinitely in headless mode.
                Arguments = $"--output-format stream-json -m {model} --yolo",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // GEMINI_SYSTEM_MD: CLI reads this env var at startup and treats the file content
            // as the system instruction. --system-prompt-file does NOT exist in v0.40.1 (verified).
            psi.Environment["GEMINI_SYSTEM_MD"] = systemPromptPath;

            // GEMINI_CLI_TRUST_WORKSPACE: prevents the CLI from downgrading --yolo to "default"
            // approval mode when the working directory is not in its trusted-directory list.
            // Without this, every tool call blocks waiting for interactive approval, which hangs
            // indefinitely in headless mode (no human present to approve).
            psi.Environment["GEMINI_CLI_TRUST_WORKSPACE"] = "true";

            // Enforce OAuth-only auth (issue #132 MUST NOT #2). The gemini CLI auto-detects
            // GEMINI_API_KEY and prefers it over ~/.gemini/oauth_creds.json when both are
            // present. A leftover GEMINI_API_KEY in the cluster .env (e.g. from PR #129 era)
            // would silently route traffic through the API-key billing tier instead of OAuth,
            // surfacing as confusing 429 "prepayment credits depleted" errors mid-task. Strip
            // the env var here so the CLI must use OAuth.
            psi.Environment.Remove("GEMINI_API_KEY");
            psi.Environment.Remove("GOOGLE_API_KEY");

            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gemini CLI process");

            // Read stderr in background — non-fatal; logged at Warning level.
            // Collected for the turn.failed error message on non-zero exit.
            // CancellationToken.None is intentional: the outer ct cancels via process.Kill in
            // the finally block. Passing ct here would abort the drain mid-read and lose stderr
            // lines we need for the error message before WaitForExitAsync returns.
            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) is not null)
                    {
                        stderrLines.AppendLine(line);
                        _logger.LogWarning("GeminiExecutor stderr: {Line}", line);
                    }
                }
                catch { /* non-fatal */ }
            }, CancellationToken.None);

            // Write task text to stdin; close stdin to signal EOF to the CLI.
            // Task text is NOT passed as a -p argument to avoid ARG_MAX / E2BIG failure on long inputs.
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            process.StandardInput.Close();

            // Read stdout line by line and parse stream-json events.
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement ev;
                try
                {
                    ev = JsonDocument.Parse(line).RootElement;
                }
                catch (JsonException)
                {
                    // Non-JSON lines (info/warning from CLI startup) — skip, log at Warning.
                    _logger.LogWarning("GeminiExecutor: non-JSON stdout line: {Line}", line);
                    continue;
                }

                // Emit turn.started on first parseable event.
                if (!turnStarted)
                {
                    turnStarted = true;
                    yield return new AgentProgress
                    {
                        EventType = "system",
                        Summary = "Processing...",
                        IsSignificant = false,
                    };
                }

                // Map stream-json event to fleet protocol.
                var mapped = MapEvent(ev, accumulator);
                if (mapped is not null)
                    yield return mapped;
            }

            await stderrTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                var finalText = accumulator.ToString();
                yield return new AgentProgress
                {
                    EventType = "result",
                    Summary = finalText,
                    FinalResult = finalText,
                    IsSignificant = true,
                    Stats = new ExecutionStats { DurationMs = (int)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds },
                };
            }
            else
            {
                var errorMsg = stderrLines.Length > 0
                    ? stderrLines.ToString().Trim()
                    : $"gemini CLI exited with code {process.ExitCode}";
                yield return new AgentProgress
                {
                    EventType = "result",
                    Summary = errorMsg,
                    FinalResult = errorMsg,
                    IsErrorResult = true,
                    IsSignificant = true,
                };
            }
        }
        finally
        {
            // Kill any still-running process and delete the temp system-prompt file.
            try
            {
                if (process is not null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* non-fatal */ }

            process?.Dispose();

            try { File.Delete(systemPromptPath); }
            catch { /* non-fatal */ }
        }
    }

    // ── stream-json event mapping ─────────────────────────────────────────────

    /// <summary>
    /// Maps a stream-json event from `gemini --output-format stream-json` to a fleet AgentProgress.
    ///
    /// Verified against gemini CLI v0.40.1 live output (canary run 2026-05-04).
    /// Primary event shapes emitted by the v0.40.1 stream-json formatter:
    ///
    ///   {"type":"init",  "session_id":"...", "model":"..."}                          — startup; skip
    ///   {"type":"message","role":"user",      "content":"..."}                       — input echo; skip
    ///   {"type":"message","role":"assistant", "content":"...", "delta":true}         — text chunk; accumulate
    ///   {"type":"tool_call",  "name":"...", "args":{...}}                            — tool invocation
    ///   {"type":"tool_result","callId":"...", "content":"..."}                       — tool response
    ///   {"type":"result","status":"success", "stats":{...}}                          — end-of-stream; RunCliAsync handles via exit code
    ///   {"type":"result","status":"error",   "error":{"type":"...","message":"..."}} — API/CLI error
    ///
    /// Legacy / defensive fallbacks retained for forward compatibility with CLI schema changes.
    /// </summary>
    internal AgentProgress? MapEvent(JsonElement ev, StringBuilder accumulator)
    {
        // Extract the event type (if present).
        var evType = ev.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        // ── message (v0.40.1 primary format) ──────────────────────────────────
        // role=assistant: streaming text delta; role=user: input echo, skip.
        if (evType is "message")
        {
            var role = ev.TryGetProperty("role", out var rp) ? rp.GetString() : null;
            if (role is not "assistant") return null; // skip user echo and unknown roles

            var content = ev.TryGetProperty("content", out var cp) && cp.ValueKind == JsonValueKind.String
                ? cp.GetString() : null;
            if (string.IsNullOrEmpty(content)) return null;

            accumulator.Append(content);
            return new AgentProgress
            {
                EventType = "assistant",
                Summary = content,
                // Intermediate streaming chunk — suppress routing to Telegram on every delta.
                // Only the terminal FinalResult event is significant (PR #129 pattern).
                IsSignificant = false,
            };
        }

        // ── result (v0.40.1: end-of-stream or API error) ──────────────────────
        // status=success: RunCliAsync emits FinalResult from the accumulated text via exit code 0.
        // status=error:   emit an immediate error result so the turn fails fast.
        if (evType is "result")
        {
            var status = ev.TryGetProperty("status", out var sp) ? sp.GetString() : null;
            if (status is not "error") return null; // success handled by exit code path

            string? msg = null;
            if (ev.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                msg = ExtractText(errObj, "message", "error");
            msg ??= ExtractText(ev, "message", "error") ?? "Unknown error from gemini CLI";

            return new AgentProgress
            {
                EventType = "result",
                Summary = msg,
                FinalResult = msg,
                IsErrorResult = true,
                IsSignificant = true,
            };
        }

        // ── Tool call ──────────────────────────────────────────────────────────
        // Handle tool_call / toolCall / function_call event shapes.
        if (evType is "tool_call" or "toolCall" or "function_call")
        {
            var toolName = ev.TryGetProperty("name", out var n) ? n.GetString()
                : ev.TryGetProperty("toolName", out var tn) ? tn.GetString()
                : null;
            var toolArgs = ev.TryGetProperty("args", out var a) ? a.GetRawText()
                : ev.TryGetProperty("input", out var inp) ? inp.GetRawText()
                : "{}";
            return new AgentProgress
            {
                EventType = "tool_use",
                Summary = $"Using {toolName}",
                ToolName = toolName ?? "",
                ToolArgs = toolArgs,
                // Intermediate event — suppress routing to Telegram on every tool invocation.
                // Only the terminal FinalResult event is significant (PR #129 pattern).
                IsSignificant = false,
            };
        }

        // ── Tool result ────────────────────────────────────────────────────────
        if (evType is "tool_result" or "toolResult" or "function_result")
        {
            var resultText = ExtractText(ev, "content", "result", "text");
            return new AgentProgress
            {
                EventType = "tool_result",
                Summary = resultText ?? "",
                IsSignificant = false,
            };
        }

        // ── Legacy / defensive text extraction ────────────────────────────────
        // Handles hypothetical schema variations: top-level text field, raw candidates format,
        // and any future event type that carries a text payload. Belt-and-suspenders — the
        // v0.40.1 primary format is message/role=assistant above; these fire only for unknowns.
        var text = ExtractEventText(ev, evType);
        if (!string.IsNullOrEmpty(text))
        {
            accumulator.Append(text);
            return new AgentProgress
            {
                EventType = "assistant",
                Summary = text,
                IsSignificant = false,
            };
        }

        // Ignore events that carry no text payload (init, usage, heartbeats, etc.).
        return null;
    }

    /// <summary>
    /// Extracts a text payload from a stream-json event, handling multiple field naming conventions
    /// used across gemini CLI versions and the raw Gemini API response format.
    /// </summary>
    internal static string? ExtractEventText(JsonElement ev, string? evType)
    {
        // Type-specific fields first.
        if (evType is "text" or "content" or "thought" or "finalText" or "model_turn_complete")
        {
            if (ev.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
            if (ev.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
        }

        // Generic top-level text field (any event type).
        if (ev.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
            return textProp.GetString();

        // Raw Gemini API streaming format: candidates[0].content.parts[*].text
        if (ev.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var cand in cands.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var cnt)) continue;
                if (!cnt.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                        sb.Append(pt.GetString());
                }
            }
            if (sb.Length > 0) return sb.ToString();
        }

        return null;
    }

    /// <summary>Tries each field name in order and returns the first non-null string value found.</summary>
    private static string? ExtractText(JsonElement ev, params string[] fields)
    {
        foreach (var field in fields)
        {
            if (ev.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }
}
