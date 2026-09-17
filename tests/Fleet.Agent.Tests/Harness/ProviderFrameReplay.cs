using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// L1 (D2): drive a synthetic fixture through each executor's REAL parse path and capture the
/// ordered <see cref="AgentProgress"/> it produces.
///
/// <para>The three seams are not uniform, and the matrix turns on that (D3):</para>
/// <list type="bullet">
/// <item><b>Claude</b> — the full <c>ExecuteAsync</c>, with a <see cref="StandInProcess"/> plus the
/// stdin, process and event-channel seams. Terminal ownership lives in the read loop, not in
/// <c>ParseProgress</c>, so anything short of the full loop would not observe it.</item>
/// <item><b>Codex</b> — <c>StreamTurnForTests</c> over a scripted notification channel. No process
/// at all is required for the read path.</item>
/// <item><b>Gemini</b> — <c>MapEvent</c> with a caller-owned accumulator. There is no process seam
/// and no event-channel seam below <c>ExecuteAsync</c>, so the turn-start marker and the terminal
/// are NOT observable here; they are recorded as <c>inferred</c> at L2 (D3). Opening a seam would
/// be a <c>src/</c> change, which MUST NOT #3 and #18 forbid.</item>
/// </list>
///
/// <para>A missing fixture THROWS with the fixture name. It is never caught and turned into a
/// skip: a skipped provider is an unmeasured provider reported as green.</para>
/// </summary>
internal static class ProviderFrameReplay
{
    public const string ClaudeDirectory = "claude";
    public const string CodexDirectory = "codex";
    public const string GeminiDirectory = "gemini";

    /// <summary>Absolute path of a fixture in the test output tree.</summary>
    public static string FixturePath(string provider, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", provider, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{provider}/{fileName}' was not found at '{path}'. " +
                "If the whole provider directory is missing, the Fixtures copy item in " +
                "Fleet.Agent.Tests.csproj has not been widened to Fixtures\\**\\* and every " +
                "fixture-backed test is passing vacuously.",
                path);
        }
        return path;
    }

    /// <summary>Non-blank lines of a JSON-Lines / NDJSON fixture, in file order.</summary>
    public static IReadOnlyList<string> ReadLines(string provider, string fileName) =>
        File.ReadAllLines(FixturePath(provider, fileName))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    // ── Claude ───────────────────────────────────────────────────────────────

    /// <summary>Deserialize a Claude NDJSON fixture into the executor's own frame type.</summary>
    public static IReadOnlyList<ClaudeStreamEvent> ReadClaudeFrames(string fileName) =>
        ReadLines(ClaudeDirectory, fileName)
            .Select(line => JsonSerializer.Deserialize<ClaudeStreamEvent>(line)
                ?? throw new InvalidOperationException($"Fixture line in claude/{fileName} deserialized to null."))
            .ToList();

    /// <summary>
    /// Run a full Claude turn over a stand-in process, feeding the fixture frames through the
    /// event channel the real read loop consumes.
    /// </summary>
    public static async Task<IReadOnlyList<AgentProgress>> ReplayClaudeAsync(
        string fileName, CancellationToken ct = default) =>
        await ReplayClaudeFramesAsync(ReadClaudeFrames(fileName), ct);

    /// <inheritdoc cref="ReplayClaudeAsync(string, CancellationToken)"/>
    public static async Task<IReadOnlyList<AgentProgress>> ReplayClaudeFramesAsync(
        IReadOnlyList<ClaudeStreamEvent> frames, CancellationToken ct = default)
    {
        await using var replay = new ClaudeReplay();
        return await replay.RunTurnAsync(frames, ct);
    }

    /// <summary>
    /// A live Claude executor bound to a stand-in process, for scenarios that need to act on the
    /// turn while it runs (steering, the final-answer gate, drain-preserve, restart).
    /// </summary>
    public sealed class ClaudeReplay : IAsyncDisposable
    {
        private readonly StandInProcess _process = new();
        private readonly Channel<ClaudeStreamEvent> _events = Channel.CreateUnbounded<ClaudeStreamEvent>();
        private readonly SignalingTextWriter _stdin = new();

        public ClaudeReplay(int maxTurns = 100)
        {
            var options = Options.Create(new AgentOptions
            {
                Name = "agent-a",
                Role = "test",
                WorkDir = "/workspace/example", // hygiene-ok: the fixture-corpus workspace root
                Provider = "claude",
                MaxTurns = maxTurns,
            });
            Executor = new ClaudeExecutor(
                options, NullLogger<ClaudeExecutor>.Instance,
                new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
            Executor.SetProcessForTests(_process.Process);
            Executor.SetStdinForTests(_stdin);
            Executor.SetEventChannelForTests(_events);
        }

        public ClaudeExecutor Executor { get; }

        /// <summary>Completes once the executor has written the turn's prompt to stdin.</summary>
        public Task WaitForPromptWriteAsync(CancellationToken ct) => _stdin.WaitForWriteAsync(ct);

        /// <summary>Hand a frame to the executor's read loop.</summary>
        public ValueTask FeedAsync(ClaudeStreamEvent frame, CancellationToken ct) =>
            _events.Writer.WriteAsync(frame, ct);

        /// <summary>Start a turn without waiting for it, so a caller can steer it mid-flight.</summary>
        public Task<IReadOnlyList<AgentProgress>> StartTurnAsync(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                var progress = new List<AgentProgress>();
                await foreach (var item in Executor.ExecuteAsync("synthetic fixture turn", ct: ct))
                    progress.Add(item);
                return (IReadOnlyList<AgentProgress>)progress;
            }, ct);
        }

        public async Task<IReadOnlyList<AgentProgress>> RunTurnAsync(
            IReadOnlyList<ClaudeStreamEvent> frames, CancellationToken ct)
        {
            var turn = StartTurnAsync(ct);
            await WaitForPromptWriteAsync(ct);
            foreach (var frame in frames)
                await FeedAsync(frame, ct);
            return await turn;
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            _process.Dispose();
        }
    }

    /// <summary>
    /// A stdin writer that signals each write. Extracted alongside the stand-in so both replay
    /// paths can wait for the executor to reach its write rather than sleeping.
    /// </summary>
    internal sealed class SignalingTextWriter : TextWriter
    {
        private readonly Channel<bool> _writes = Channel.CreateUnbounded<bool>();

        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>Every line the executor wrote, in order. This is how an injected frame is observed.</summary>
        public List<string> Lines { get; } = [];

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (Lines) Lines.Add(buffer.ToString());
            _writes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }

        public Task WaitForWriteAsync(CancellationToken cancellationToken) =>
            _writes.Reader.ReadAsync(cancellationToken).AsTask();
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    /// <summary>Deserialize a Codex JSON-Lines fixture into app-server notification objects.</summary>
    public static IReadOnlyList<JsonObject> ReadCodexFrames(string fileName) =>
        ReadLines(CodexDirectory, fileName)
            .Select(line => JsonNode.Parse(line) as JsonObject
                ?? throw new InvalidOperationException($"Fixture line in codex/{fileName} is not a JSON object."))
            .ToList();

    /// <summary>
    /// Stream a Codex turn over a scripted notification channel. No process is started: the codex
    /// read path consumes the channel directly.
    /// </summary>
    public static async Task<IReadOnlyList<AgentProgress>> ReplayCodexAsync(
        string fileName, string turnId = "t_1", CancellationToken ct = default) =>
        await ReplayCodexFramesAsync(ReadCodexFrames(fileName), turnId, ct);

    /// <inheritdoc cref="ReplayCodexAsync(string, string, CancellationToken)"/>
    public static async Task<IReadOnlyList<AgentProgress>> ReplayCodexFramesAsync(
        IReadOnlyList<JsonObject> frames, string turnId = "t_1", CancellationToken ct = default)
    {
        await using var executor = CreateCodexExecutor();
        var channel = Channel.CreateUnbounded<JsonObject>();
        executor.SetNotificationChannelForTests(channel);
        executor.SetThreadStateForTests("t_thread_1", turnId);

        foreach (var frame in frames)
            await channel.Writer.WriteAsync(frame, ct);

        var progress = new List<AgentProgress>();
        await foreach (var item in executor.StreamTurnForTests(turnId, ct))
            progress.Add(item);
        return progress;
    }

    /// <summary>A codex executor configured for replay. Never starts a provider process.</summary>
    public static CodexExecutor CreateCodexExecutor()
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "agent-a",
            Role = "test",
            WorkDir = "/workspace/example", // hygiene-ok: the fixture-corpus workspace root
            Provider = "codex",
        });
        var telegramOptions = Options.Create(new TelegramOptions
        {
            AttachmentDir = "/workspace/example/attachments", // hygiene-ok: fixture-corpus path
        });
        return new CodexExecutor(
            agentOptions, telegramOptions,
            new PromptBuilder(agentOptions, NullLogger<PromptBuilder>.Instance),
            NullLogger<CodexExecutor>.Instance);
    }

    // ── Gemini ───────────────────────────────────────────────────────────────

    /// <summary>Parse a Gemini JSON-Lines fixture into stream events.</summary>
    public static IReadOnlyList<JsonElement> ReadGeminiFrames(string fileName) =>
        ReadLines(GeminiDirectory, fileName)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    /// <summary>
    /// Drive Gemini frames through <c>MapEvent</c> with a caller-owned accumulator.
    ///
    /// <para>The returned accumulator text is what <c>ExecuteAsync</c> would hand to its terminal
    /// <c>result</c> — so the terminal's CONTENT is observable here even though the terminal
    /// EVENT is not. Only two steps stay unobserved: accumulator → <c>FinalResult</c>, and the
    /// <c>ExitCode</c> success-vs-error branch. That is exactly what <c>inferred</c> is for (D3).</para>
    /// </summary>
    public static (IReadOnlyList<AgentProgress> Progress, string Accumulated) ReplayGemini(string fileName) =>
        ReplayGeminiFrames(ReadGeminiFrames(fileName));

    /// <inheritdoc cref="ReplayGemini(string)"/>
    public static (IReadOnlyList<AgentProgress> Progress, string Accumulated) ReplayGeminiFrames(
        IReadOnlyList<JsonElement> frames)
    {
        var executor = CreateGeminiExecutor();
        var accumulator = new StringBuilder();
        var progress = new List<AgentProgress>();
        foreach (var frame in frames)
        {
            var mapped = executor.MapEvent(frame, accumulator);
            if (mapped is not null)
                progress.Add(mapped);
        }
        return (progress, accumulator.ToString());
    }

    /// <summary>A gemini executor configured for replay. Never starts a provider process.</summary>
    public static GeminiExecutor CreateGeminiExecutor()
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "agent-a",
            Role = "test",
            WorkDir = "/workspace/example", // hygiene-ok: the fixture-corpus workspace root
            Provider = "gemini",
            Model = "gemini-2.5-flash",
        });
        return new GeminiExecutor(
            options, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance),
            NullLogger<GeminiExecutor>.Instance);
    }

    /// <summary>
    /// The turn-start marker Gemini synthesises inline in <c>ExecuteAsync</c> on the first
    /// parseable line. Reproduced here — faithfully, and labelled — because no seam exposes it.
    /// Every row built on it is <c>inferred</c>.
    /// </summary>
    public static AgentProgress GeminiInferredTurnStart() => new()
    {
        EventType = "system",
        Summary = "Processing...",
        IsSignificant = false,
    };

    /// <summary>
    /// The terminal Gemini builds after <c>WaitForExitAsync</c> from the accumulated text and the
    /// process exit code. Reproduced here, and <c>inferred</c> for the same reason.
    /// </summary>
    public static AgentProgress GeminiInferredTerminal(string accumulated, int exitCode = 0) =>
        exitCode == 0
            ? new AgentProgress
            {
                EventType = "result",
                Summary = accumulated,
                FinalResult = accumulated,
                IsSignificant = true,
            }
            : new AgentProgress
            {
                EventType = "result",
                Summary = $"gemini CLI exited with code {exitCode}",
                FinalResult = $"gemini CLI exited with code {exitCode}",
                IsErrorResult = true,
                IsSignificant = true,
            };
}
