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
/// Images / PDFs / Audio: native multimodal via gemini-cli's @-reference resolver.
/// Each attachment is staged to a per-task temp directory; the working directory is
/// set to that dir and "@./filename.ext" refs are passed via -p. The CLI's
/// handleAtCommand pipeline reads the binary, MIME-detects it, and emits inlineData
/// parts to the model. Mechanism verified against gemini-cli v0.40.1 (issue #15532).
/// Video is not supported by the CLI; hint-only fallback for unknown extensions.
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
            "Multimodal (image/PDF/audio) attachments are passed natively via gemini-cli's @-reference resolver. " +
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

        await foreach (var progress in RunCliAsync(task, images, documents, ct))
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

        await foreach (var progress in RunCliAsync(command, images: null, documents: null, ct: ct))
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
        IReadOnlyList<MessageImage>? images,
        IReadOnlyList<MessageDocument>? documents,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Write system prompt to a temp file. GEMINI_SYSTEM_MD env var points the CLI at it.
        // The file is deleted in the finally block regardless of outcome.
        var systemPromptPath = Path.Combine(Path.GetTempPath(), $"gemini-system-{Guid.NewGuid():N}.md");

        // Per-task attachment staging dir. Each accepted image/PDF/audio is copied here so the
        // gemini CLI's @-resolver can read it relative to the working directory. The dir is
        // deleted in finally regardless of outcome (no leftover binary content).
        var attachmentDir = Path.Combine(Path.GetTempPath(), $"gemini-attach-{Guid.NewGuid():N}");

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

            // Stage attachments and build the @-reference prompt fragment. CWD is set to
            // attachmentDir so "@./<file>" resolves correctly inside gemini-cli's
            // handleAtCommand. Files are copied (not symlinked) for portability.
            Directory.CreateDirectory(attachmentDir);
            var atRefs = StageAttachments(images, documents, attachmentDir);

            var model = string.IsNullOrWhiteSpace(_config.Model) ? "gemini-2.5-flash" : _config.Model;

            // Prompt assembly:
            //   - The user's task text goes on stdin (avoids ARG_MAX for long prompts).
            //   - The @-references go in the -p argument. handleAtCommand only resolves @-syntax
            //     present in the -p value (verified empirically on v0.40.1). The CLI combines
            //     stdin + -p before sending to the model, so the model sees both: task text
            //     plus the inlineData from each attachment.
            //   - When there are no attachments, omit -p entirely; the CLI reads stdin normally.
            var argList = new List<string>
            {
                "--output-format", "stream-json",
                "-m", model,
                "--yolo",
            };
            if (atRefs.Count > 0)
            {
                argList.Add("-p");
                argList.Add("Attachments to consider: " + string.Join(" ", atRefs));
            }

            var psi = new ProcessStartInfo
            {
                FileName = "gemini",
                // --output-format stream-json: emit NDJSON events on stdout for incremental parsing.
                // -m <model>: specify model; defaults to gemini-2.5-flash if omitted.
                // --yolo (-y): suppress interactive MCP tool-call approval prompts.
                //   Without this, the CLI blocks waiting for user confirmation on every tool call
                //   — the agent hangs indefinitely in headless mode.
                // -p (optional): when attachments are present, carries the @-reference fragment
                //   that triggers handleAtCommand's binary inlineData path.
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = attachmentDir,
            };
            foreach (var a in argList) psi.ArgumentList.Add(a);

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

            // Force IPv4-first DNS resolution. cloudcode-pa.googleapis.com publishes both
            // A and AAAA records; Node's default Happy-Eyeballs picks IPv6 first. Docker
            // bridge networks have no IPv6 egress by default, so the CLI hangs with
            // ETIMEDOUT on every loadCodeAssist call before even reaching the model.
            // Preserves any caller-provided NODE_OPTIONS by appending.
            var existingNodeOpts = psi.Environment.TryGetValue("NODE_OPTIONS", out var no) ? no ?? "" : "";
            psi.Environment["NODE_OPTIONS"] = string.IsNullOrEmpty(existingNodeOpts)
                ? "--dns-result-order=ipv4first"
                : existingNodeOpts + " --dns-result-order=ipv4first";

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
            // Kill any still-running process and delete the temp system-prompt file + attachment dir.
            try
            {
                if (process is not null && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* non-fatal */ }

            process?.Dispose();

            try { File.Delete(systemPromptPath); }
            catch { /* non-fatal */ }

            try
            {
                if (Directory.Exists(attachmentDir))
                    Directory.Delete(attachmentDir, recursive: true);
            }
            catch { /* non-fatal */ }
        }
    }

    // ── Attachment staging ────────────────────────────────────────────────────

    /// <summary>
    /// Copies eligible image / PDF / audio attachments into <paramref name="stageDir"/>
    /// using stable random filenames that preserve their extension. Returns the list of
    /// "@./filename.ext" references to inject into the gemini -p argument.
    ///
    /// gemini-cli's handleAtCommand (v0.40.1) routes @-paths through ReadManyFilesTool,
    /// which detects MIME by extension and emits inlineData parts for image/pdf/audio.
    /// Files lacking a recognized extension are skipped (the CLI would skip them anyway,
    /// per the "asset file was not explicitly requested by name or extension" branch in
    /// the CLI source). Video is not in the CLI's supported set; skipped with a warning.
    ///
    /// Files without a persisted FilePath (Telegram:PersistAttachments disabled or
    /// size-limit exceeded) are skipped — there's nothing on disk to stage.
    /// </summary>
    private List<string> StageAttachments(
        IReadOnlyList<MessageImage>? images,
        IReadOnlyList<MessageDocument>? documents,
        string stageDir)
    {
        var refs = new List<string>();

        if (images is { Count: > 0 })
        {
            foreach (var img in images)
            {
                if (string.IsNullOrEmpty(img.FilePath) || !File.Exists(img.FilePath))
                {
                    _logger.LogWarning("GeminiExecutor: image attachment has no persisted FilePath — skipped");
                    continue;
                }
                var stagedName = StageOne(img.FilePath, stageDir, img.MimeType);
                if (stagedName is not null) refs.Add($"@./{stagedName}");
            }
        }

        if (documents is { Count: > 0 })
        {
            foreach (var doc in documents)
            {
                if (string.IsNullOrEmpty(doc.FilePath) || !File.Exists(doc.FilePath))
                {
                    _logger.LogWarning("GeminiExecutor: document attachment has no persisted FilePath — skipped");
                    continue;
                }
                var stagedName = StageOne(doc.FilePath, stageDir, doc.MimeType);
                if (stagedName is not null) refs.Add($"@./{stagedName}");
            }
        }

        return refs;
    }

    /// <summary>
    /// Copies a single attachment to the staging dir with an extension preserved from
    /// either the source path or, as fallback, the MIME type. Returns the staged basename
    /// (relative to stageDir), or null if the attachment type is unsupported by the CLI.
    /// </summary>
    private string? StageOne(string srcPath, string stageDir, string? mimeType)
    {
        var srcExt = Path.GetExtension(srcPath).ToLowerInvariant();
        if (string.IsNullOrEmpty(srcExt)) srcExt = MimeToExtension(mimeType);

        // Gemini CLI's @-resolver supports image, pdf, audio. Video and unknowns are
        // dropped — the CLI would refuse them anyway with a "not explicitly requested"
        // skip. Logged so the operator can see why an attachment didn't reach the model.
        if (!IsSupportedExtension(srcExt))
        {
            _logger.LogWarning(
                "GeminiExecutor: attachment {Path} (ext={Ext}, mime={Mime}) is not a supported media type — skipped",
                srcPath, srcExt, mimeType);
            return null;
        }

        var stagedName = $"{Guid.NewGuid():N}{srcExt}";
        var dstPath = Path.Combine(stageDir, stagedName);
        File.Copy(srcPath, dstPath, overwrite: false);
        return stagedName;
    }

    private static string MimeToExtension(string? mimeType) => (mimeType ?? "").ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "application/pdf" => ".pdf",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/aac" => ".aac",
        "audio/ogg" => ".ogg",
        _ => "",
    };

    private static bool IsSupportedExtension(string ext) => ext switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" => true,
        ".pdf" => true,
        ".mp3" or ".wav" or ".aac" or ".aiff" or ".aif" or ".ogg" or ".flac" or ".m4a" => true,
        _ => false,
    };

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
                // Tool calls ARE significant — TaskManager gates them via the
                // user-side SuppressToolMessages flag and a 1-in-5 sampling
                // counter. Matches ClaudeExecutor's MapAssistantEvent behavior.
                // (Streaming text deltas remain IsSignificant=false; only the
                // terminal FinalResult event delivers the assistant response.)
                IsSignificant = true,
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
