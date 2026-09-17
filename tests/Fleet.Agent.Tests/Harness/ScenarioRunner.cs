using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Protocol;

namespace Fleet.Agent.Tests.Harness;

/// <summary>What one (provider × scenario) cell observed, rendered in the D6 cell grammar.</summary>
internal sealed record ScenarioObservation(
    string Frames,
    string Progress,
    string ClientEvents,
    IReadOnlyList<ConversationEvent> RawEvents)
{
    /// <summary>The typing heartbeat, excluded from the ordered cell but asserted separately.</summary>
    public int TypingHeartbeats => RawEvents.Count(MatrixCells.IsTypingHeartbeat);
}

/// <summary>
/// Runs a named scenario against a named provider and renders what it observed (D4).
///
/// <para>One place owns the mechanics so the matrix, the scenario tests and the gap rows all
/// measure the same thing. A scenario that cannot be observed through a provider's seam is
/// recorded as an ABSENCE — the test asserts the capability is missing, so a provider silently
/// gaining it turns the row red and forces a matrix update.</para>
/// </summary>
internal static class ScenarioRunner
{
    /// <summary>Per-provider scenarios, in matrix order (D4).</summary>
    public static readonly IReadOnlyList<string> PerProviderScenarios =
        ["S1", "S2", "S3", "S4", "S5", "S6", "S9", "S10", "S13", "S14", "S16"];

    /// <summary>
    /// Cross-cutting capability rows. These are not turn scenarios; they record the three
    /// per-provider facts the Background section requires the harness to confirm or refute.
    /// </summary>
    public static readonly IReadOnlyList<string> GapScenarios = ["G1", "G2"];

    /// <summary>Codex-only: the <c>commandExecution</c> → <c>ToolName</c> leak.</summary>
    public const string CodexLeakScenario = "G3";

    /// <summary>Every (provider, scenario) pair the mapping tests assert.</summary>
    public static IEnumerable<(string Provider, string Scenario)> AllRows()
    {
        foreach (var provider in CapabilityMatrix.Providers)
        {
            foreach (var scenario in PerProviderScenarios)
                yield return (provider, scenario);
            foreach (var scenario in GapScenarios)
                yield return (provider, scenario);
        }
        yield return (CapabilityMatrix.Codex, CodexLeakScenario);
    }

    public static Task<ScenarioObservation> RunAsync(string provider, string scenario, CancellationToken ct) =>
        (provider, scenario) switch
        {
            (_, "S1") => OrdinaryTurnAsync(provider, ct),
            (_, "S2") => ToolOnlyTurnAsync(provider, ct),
            (_, "S3") => SteerDuringTurnAsync(provider, ct),
            (_, "S4") => SteerAfterFinalAnswerAsync(provider, ct),
            (_, "S5") => QueuedCoalescingAsync(ct, queuedParts: 2),
            (_, "S6") => QueuedCoalescingAsync(ct, queuedParts: QueuedMessage.MaxParts + 1),
            (_, "S9") => ExecutorErrorTurnAsync(provider, ct),
            (_, "S10") => ProcessExitWithPendingInjectionAsync(ct),
            (_, "S13") => NestedSubagentAsync(provider, ct),
            (_, "S14") => StaleAnswerPreservedAsync(provider, ct),
            (_, "S16") => RestartBetweenTurnsAsync(provider, ct),
            (_, "G1") => ToolCompletionAsync(provider, ct),
            (_, "G2") => IncrementalAssistantTextAsync(provider, ct),
            (CapabilityMatrix.Codex, CodexLeakScenario) => CommandExecutionToolNameAsync(ct),
            _ => throw new ArgumentOutOfRangeException(
                nameof(scenario), $"No runner for {provider}/{scenario}."),
        };

    // ── L1: per-provider frame replay ────────────────────────────────────────

    /// <summary>The per-provider fixture file name for a logical corpus entry.</summary>
    private static string FixtureName(string provider, string stem) =>
        provider == CapabilityMatrix.Claude ? $"{stem}.ndjson" : $"{stem}.jsonl";

    /// <summary>
    /// Replay a fixture through the provider's real parse path and render frames + progress.
    ///
    /// <para>Gemini's turn-start marker and terminal are constructed inline in
    /// <c>ExecuteAsync</c> with no seam beneath it (D3), so they are reproduced here and the row
    /// is <c>inferred</c>. Nothing else on the Gemini path is synthesised.</para>
    /// </summary>
    private static async Task<(string Frames, IReadOnlyList<AgentProgress> Progress)> ReplayAsync(
        string provider, string stem, CancellationToken ct, int geminiExitCode = 0)
    {
        var fileName = FixtureName(provider, stem);
        switch (provider)
        {
            case CapabilityMatrix.Claude:
            {
                var frames = ProviderFrameReplay.ReadClaudeFrames(fileName);
                var progress = await ProviderFrameReplay.ReplayClaudeFramesAsync(frames, ct);
                return (MatrixCells.Frames(frames.Select(ClaudeDiscriminator)), progress);
            }
            case CapabilityMatrix.Codex:
            {
                var frames = ProviderFrameReplay.ReadCodexFrames(fileName);
                var progress = await ProviderFrameReplay.ReplayCodexFramesAsync(frames, ct: ct);
                return (MatrixCells.Frames(frames.Select(CodexDiscriminator)), progress);
            }
            case CapabilityMatrix.Gemini:
            {
                var frames = ProviderFrameReplay.ReadGeminiFrames(fileName);
                var (mapped, accumulated) = ProviderFrameReplay.ReplayGeminiFrames(frames);
                var progress = new List<AgentProgress> { ProviderFrameReplay.GeminiInferredTurnStart() };
                progress.AddRange(mapped);
                progress.Add(ProviderFrameReplay.GeminiInferredTerminal(accumulated, geminiExitCode));
                return (MatrixCells.Frames(frames.Select(GeminiDiscriminator)), progress);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.");
        }
    }

    private static string ClaudeDiscriminator(ClaudeStreamEvent evt) => evt.Type switch
    {
        "system" => $"system/{evt.Subtype ?? "unknown"}",
        "assistant" when evt.Message?.Content?.Any(b => b.Type == "tool_use") == true => "assistant/tool_use",
        "assistant" when evt.ParentToolUseId is not null => "assistant/text(nested)",
        "assistant" => "assistant/text",
        "user" => "user/tool_result",
        "result" when evt.IsError == true => "result(is_error)",
        _ => evt.Type,
    };

    private static string CodexDiscriminator(JsonObject notification)
    {
        var method = notification["method"]?.GetValue<string>() ?? "unknown";
        if (!method.StartsWith("item/", StringComparison.Ordinal))
            return method;

        var itemType = (notification["params"] as JsonObject)?["item"] as JsonObject;
        return itemType?["type"]?.GetValue<string>() ?? method;
    }

    private static string GeminiDiscriminator(JsonElement frame)
    {
        var type = frame.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
        if (type == "message" && frame.TryGetProperty("role", out var role))
            return $"message/{role.GetString()}";
        if (type == "result" && frame.TryGetProperty("status", out var status))
            return $"result/{status.GetString()}";
        return type;
    }

    // ── L2: runtime projection ───────────────────────────────────────────────

    /// <summary>
    /// Replay an <see cref="AgentProgress"/> sequence through the real runtime and capture what a
    /// client adapter observed, in delivery order.
    /// </summary>
    public static async Task<IReadOnlyList<ConversationEvent>> ProjectAsync(
        IReadOnlyList<AgentProgress> progress, CancellationToken ct)
    {
        var executor = new ScriptedExecutor(progress);
        var harness = ConversationHarness.Build(executor);
        var (key, identity) = harness.OpenClientConversation();
        await harness.Manager.StartTask(key, "task", "task", isSessionTask: true, identity: identity);
        return await harness.PumpUntilTerminalAsync(ct);
    }

    private static ScenarioObservation Observe(
        string frames, IReadOnlyList<AgentProgress> progress, IReadOnlyList<ConversationEvent> events) =>
        new(frames, MatrixCells.Progress(progress), MatrixCells.ClientEvents(events), events);

    private static ScenarioObservation ObserveAbsence(
        IReadOnlyList<ConversationEvent> events) =>
        new(MatrixCells.Empty, MatrixCells.Empty, MatrixCells.ClientEvents(events), events);

    // ── Scenarios ────────────────────────────────────────────────────────────

    private static async Task<ScenarioObservation> OrdinaryTurnAsync(string provider, CancellationToken ct)
    {
        var (frames, progress) = await ReplayAsync(provider, "ordinary-turn", ct);
        return Observe(frames, progress, await ProjectAsync(progress, ct));
    }

    private static async Task<ScenarioObservation> ToolOnlyTurnAsync(string provider, CancellationToken ct)
    {
        var (frames, progress) = await ReplayAsync(provider, "tool-only-turn", ct);
        return Observe(frames, progress, await ProjectAsync(progress, ct));
    }

    private static async Task<ScenarioObservation> ExecutorErrorTurnAsync(string provider, CancellationToken ct)
    {
        var (frames, progress) = await ReplayAsync(provider, "executor-error-turn", ct, geminiExitCode: 1);
        return Observe(frames, progress, await ProjectAsync(progress, ct));
    }

    private static async Task<ScenarioObservation> NestedSubagentAsync(string provider, CancellationToken ct)
    {
        if (provider != CapabilityMatrix.Claude)
        {
            // Absence, asserted rather than skipped: neither provider has a nested-turn concept,
            // so there is no frame whose nesting the runtime could carry to a client.
            AssertNoNestedTurnConcept(provider);
            return ObserveAbsence([]);
        }

        var (frames, progress) = await ReplayAsync(provider, "nested-subagent", ct);
        return Observe(frames, progress, await ProjectAsync(progress, ct));
    }

    private static async Task<ScenarioObservation> StaleAnswerPreservedAsync(string provider, CancellationToken ct)
    {
        if (provider != CapabilityMatrix.Claude)
        {
            AssertNoDrainPreserveConcept(provider);
            return ObserveAbsence([]);
        }

        await using var replay = new ProviderFrameReplay.ClaudeReplay();
        var frames = ProviderFrameReplay.ReadClaudeFrames("assistant-stream.ndjson");

        // Buffer a completed prior turn's events, then drain them the way the next turn's start
        // does. The stale answer must be PRESERVED, not discarded.
        foreach (var frame in frames)
            await replay.FeedAsync(frame, ct);
        replay.Executor.DrainStaleTurnEventsForTests();

        var preserved = replay.Executor.PreservedDrainedAnswerTextForTests;
        Assert.False(string.IsNullOrEmpty(preserved),
            "Claude must preserve a stale assistant answer across a drain (S14).");

        var progress = new List<AgentProgress>
        {
            new() { EventType = "recovered_answer", Summary = preserved!, IsSignificant = true },
            ScriptedExecutor.Final("Synthetic answer B."),
        };
        return Observe(
            MatrixCells.Frames(frames.Select(ClaudeDiscriminator)), progress, await ProjectAsync(progress, ct));
    }

    private static async Task<ScenarioObservation> SteerDuringTurnAsync(string provider, CancellationToken ct)
    {
        var (frames, status) = await ObserveInjectionDuringTurnAsync(provider, afterFinalAnswer: false, ct);
        var events = await ProjectSteerAsync(status, ct);
        return new ScenarioObservation(frames, MatrixCells.Empty, MatrixCells.ClientEvents(events), events);
    }

    private static async Task<ScenarioObservation> SteerAfterFinalAnswerAsync(string provider, CancellationToken ct)
    {
        var (frames, status) = await ObserveInjectionDuringTurnAsync(provider, afterFinalAnswer: true, ct);
        var events = await ProjectSteerAsync(status, ct);
        return new ScenarioObservation(frames, MatrixCells.Empty, MatrixCells.ClientEvents(events), events);
    }

    /// <summary>
    /// L1 half of S3/S4: drive each provider's real injection primitive and record what it reports.
    /// The frames cell records the frame that CAUSED the outcome, or <c>—</c> when none exists.
    /// </summary>
    private static async Task<(string Frames, MidTurnInjectionStatus Status)> ObserveInjectionDuringTurnAsync(
        string provider, bool afterFinalAnswer, CancellationToken ct)
    {
        switch (provider)
        {
            case CapabilityMatrix.Claude:
            {
                await using var replay = new ProviderFrameReplay.ClaudeReplay();
                var turn = replay.StartTurnAsync(ct);
                await replay.WaitForPromptWriteAsync(ct);

                if (afterFinalAnswer)
                {
                    // An assistant TEXT frame is what commits the turn to its final answer. The
                    // refusal below is therefore caused by a real, observable provider frame.
                    await replay.FeedAsync(
                        new ClaudeStreamEvent
                        {
                            Type = "assistant",
                            Message = new ClaudeMessage
                            {
                                Content = [new ClaudeContentBlock { Type = "text", Text = "Synthetic answer A." }],
                            },
                        }, ct);
                    await WaitUntilAsync(() => replay.Executor.TurnCommittedToFinalAnswerForTests, ct);
                }

                var result = await replay.Executor.TryInjectMessageAsync("steer", ct: ct);

                await replay.FeedAsync(
                    new ClaudeStreamEvent { Type = "result", Result = "Synthetic answer A.", NumTurns = 1 }, ct);
                await turn;

                // MUST NOT #7: the stdin frame carries no `priority` field. Both non-default
                // values are traps — "now" aborts the turn and "later" starts a separate one.
                return (afterFinalAnswer
                    ? MatrixCells.Frames(["assistant/text"])
                    : MatrixCells.Empty, result.Status);
            }

            case CapabilityMatrix.Codex:
            {
                using var standIn = new StandInProcess();
                await using var executor = ProviderFrameReplay.CreateCodexExecutor();
                executor.SetProcessForTests(standIn.Process);
                executor.SetStdinForTests(standIn.StandardInput);
                executor.SetThreadStateForTests("t_thread_1", "t_1");

                if (afterFinalAnswer)
                {
                    executor.BuildItemStartedProgressForTests(new JsonObject
                    {
                        ["item"] = new JsonObject { ["type"] = "agentMessage", ["phase"] = "final_answer" },
                    });
                    Assert.True(executor.TurnHasFinalAnswerPhaseForTests);
                    var refused = await executor.TryInjectMessageAsync("steer", ct: ct);
                    return (MatrixCells.Frames(["agentMessage"]), refused.Status);
                }

                executor.BuildItemStartedProgressForTests(new JsonObject
                {
                    ["item"] = new JsonObject { ["type"] = "agentMessage", ["phase"] = "commentary" },
                });
                Assert.False(executor.TurnHasFinalAnswerPhaseForTests);

                var resolve = executor.WaitAndCompleteNextPendingSteerForTests(
                    new JsonObject { ["turnId"] = "t_1" }, ct);
                var inject = executor.TryInjectMessageAsync("steer", ct: ct);
                await Task.WhenAll(resolve, inject);
                return (MatrixCells.Frames(["turn/steer"]), (await inject).Status);
            }

            case CapabilityMatrix.Gemini:
            {
                await using var executor = ProviderFrameReplay.CreateGeminiExecutor();

                // Absence, asserted structurally AND behaviourally. GeminiExecutor declares no
                // TryInjectMessageAsync at all — the call below binds to the INTERFACE DEFAULT,
                // which is why the refusal is Unsupported rather than NoActiveTurn: there is
                // nothing to steer, ever. Adding an override turns this red.
                Assert.Null(typeof(GeminiExecutor).GetMethod(
                    nameof(IAgentExecutor.TryInjectMessageAsync),
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly));

                var result = await ((IAgentExecutor)executor).TryInjectMessageAsync("steer", ct: ct);
                Assert.Equal(MidTurnInjectionStatus.Unsupported, result.Status);
                return (MatrixCells.Empty, result.Status);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.");
        }
    }

    /// <summary>
    /// L2 half of S3/S4: a second submission arrives while a turn is genuinely running, and the
    /// runtime's dispatch — not the harness — decides inject-vs-queue from the status L1 measured.
    /// </summary>
    private static async Task<IReadOnlyList<ConversationEvent>> ProjectSteerAsync(
        MidTurnInjectionStatus status, CancellationToken ct)
    {
        var injection = status switch
        {
            MidTurnInjectionStatus.Injected => MidTurnInjectionResult.Injected,
            MidTurnInjectionStatus.Unsupported => MidTurnInjectionResult.Unsupported,
            MidTurnInjectionStatus.NoActiveTurn => MidTurnInjectionResult.NoActiveTurn("final answer"),
            _ => MidTurnInjectionResult.Failed("failed"),
        };

        var executor = new ScriptedExecutor([ScriptedExecutor.Final("Synthetic answer A.")])
        {
            BlockFirstTurnUntilReleased = true,
            InjectionResult = injection,
        };
        executor.Enqueue([ScriptedExecutor.Final("Synthetic answer B.")]);

        var harness = ConversationHarness.Build(executor);
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task-1", "task-1", isSessionTask: true, identity: identity);
        await executor.FirstTurnStarted;
        await harness.Manager.StartTask(
            key, "task-2", "task-2", isSessionTask: true, identity: identity with { SubmissionId = "s_2" });
        executor.Release();

        // An injected message is answered by the SAME turn, so one terminal. A queued one starts
        // its own turn, so two.
        var expected = status == MidTurnInjectionStatus.Injected ? 1 : 2;
        return await harness.PumpUntilTerminalAsync(ct, expected);
    }

    /// <summary>
    /// S5/S6 — queued coalescing. Consecutive compatible submissions refused by the injection gate
    /// merge into ONE continuation turn; the 11th part exceeds <c>MaxParts</c> and forms a second.
    /// Scripted progress, no frame replay — every cell is <c>inferred</c>.
    /// </summary>
    private static async Task<ScenarioObservation> QueuedCoalescingAsync(CancellationToken ct, int queuedParts)
    {
        var executor = new ScriptedExecutor([ScriptedExecutor.Final("Synthetic answer A.")])
        {
            BlockFirstTurnUntilReleased = true,
            InjectionResult = MidTurnInjectionResult.NoActiveTurn("final answer"),
        };
        for (var i = 0; i < 4; i++)
            executor.Enqueue([ScriptedExecutor.Final($"Synthetic answer {(char)('B' + i)}.")]);

        var harness = ConversationHarness.Build(executor);
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task-1", "task-1", isSessionTask: true, identity: identity);
        await executor.FirstTurnStarted;
        for (var i = 2; i <= queuedParts + 1; i++)
        {
            await harness.Manager.StartTask(
                key, $"task-{i}", $"task-{i}", isSessionTask: true,
                identity: identity with { SubmissionId = $"s_{i}" });
        }
        executor.Release();

        // 2..10 parts coalesce into one continuation turn (2 terminals in total); an 11th part
        // exceeds MaxParts and forms a second continuation (3 terminals).
        var expectedTerminals = queuedParts > QueuedMessage.MaxParts ? 3 : 2;
        var events = await harness.PumpUntilTerminalAsync(ct, expectedTerminals);
        return new ScenarioObservation(
            MatrixCells.Empty, MatrixCells.Empty, MatrixCells.ClientEvents(events), events);
    }

    /// <summary>
    /// S10 — the provider process exits mid-turn with an injected message still pending. The
    /// runtime redelivers it under a new turn id and an incremented attempt. Scripted progress.
    /// </summary>
    private static async Task<ScenarioObservation> ProcessExitWithPendingInjectionAsync(CancellationToken ct)
    {
        var executor = new ScriptedExecutor([ScriptedExecutor.ProcessExit("Provider process exited.")])
        {
            BlockFirstTurnUntilReleased = true,
            InjectionResult = MidTurnInjectionResult.Injected,
        };
        executor.Enqueue([ScriptedExecutor.Final("Synthetic answer B.")]);

        var harness = ConversationHarness.Build(executor);
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task-1", "task-1", isSessionTask: true, identity: identity);
        await executor.FirstTurnStarted;
        await harness.Manager.StartTask(
            key, "task-2", "task-2", isSessionTask: true, identity: identity with { SubmissionId = "s_2" });
        executor.Release();

        var events = await harness.PumpUntilTerminalAsync(ct);
        return new ScenarioObservation(
            MatrixCells.Empty, MatrixCells.Empty, MatrixCells.ClientEvents(events), events);
    }

    /// <summary>
    /// S16 — an explicit restart between turns. The restart is a LOCAL process action: no provider
    /// emits a frame acknowledging it, so every cell is <c>inferred</c>.
    /// </summary>
    private static async Task<ScenarioObservation> RestartBetweenTurnsAsync(string provider, CancellationToken ct)
    {
        // L1: the executor accepts the request and reports no warm process afterwards, so the next
        // turn starts cold. Running a SECOND turn through the real executor is deliberately not
        // done here — Claude's restart path kills the process and the next ExecuteAsync would
        // start a real provider CLI, which MUST NOT #1 forbids.
        switch (provider)
        {
            case CapabilityMatrix.Claude:
            {
                await using var replay = new ProviderFrameReplay.ClaudeReplay();
                replay.Executor.RequestRestart();
                Assert.False(replay.Executor.IsProcessWarm);
                break;
            }
            case CapabilityMatrix.Codex:
            {
                await using var executor = ProviderFrameReplay.CreateCodexExecutor();
                executor.RequestRestart();
                Assert.False(executor.IsProcessWarm);
                break;
            }
            case CapabilityMatrix.Gemini:
            {
                await using var executor = ProviderFrameReplay.CreateGeminiExecutor();
                executor.RequestRestart();
                Assert.False(executor.IsProcessWarm);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.");
        }

        // L2: two independent turns either side of the restart, each terminating on its own.
        var executorL2 = new ScriptedExecutor([ScriptedExecutor.Final("Synthetic answer A.")]);
        executorL2.Enqueue([ScriptedExecutor.Final("Synthetic answer B.")]);

        var harness = ConversationHarness.Build(executorL2);
        var (key, identity) = harness.OpenClientConversation();

        await harness.Manager.StartTask(key, "task-1", "task-1", isSessionTask: true, identity: identity);

        // Wait for the first turn to be genuinely OVER before requesting the restart. Without
        // this the second submission lands while the first task is still registered, takes the
        // mid-turn dispatch path, and S16 stops being "between turns" at all — which made the
        // scenario flaky rather than wrong.
        await PumpUntilIdleAsync(harness, key, ct);
        executorL2.RequestRestart();
        await harness.Manager.StartTask(
            key, "task-2", "task-2", isSessionTask: true,
            identity: identity with { SubmissionId = "s_2" });
        var events = await harness.PumpUntilTerminalAsync(ct, 2);

        Assert.Equal(1, executorL2.RestartRequests);
        return new ScenarioObservation(
            MatrixCells.Empty, MatrixCells.Empty, MatrixCells.ClientEvents(events), events);
    }

    // ── Cross-cutting capability rows ────────────────────────────────────────

    /// <summary>
    /// G1 — tool COMPLETION is invisible to a client on every provider. The provider does emit a
    /// completion frame; the runtime constructs it with <c>IsSignificant = false</c> and
    /// <c>TaskManager</c> publishes <c>turn.progress</c> only when
    /// <c>IsSignificant &amp;&amp; ToolName is not null</c>. A client sees a tool START and never a
    /// tool FINISH, so any UI that renders a spinner per tool has no event that clears it.
    /// </summary>
    private static async Task<ScenarioObservation> ToolCompletionAsync(string provider, CancellationToken ct)
    {
        var (frames, progress) = await ReplayAsync(provider, "tool-only-turn", ct);

        var completions = progress.Where(IsToolCompletion).ToList();
        Assert.NotEmpty(completions);
        Assert.All(completions, p => Assert.False(
            p.IsSignificant, "A tool completion must stay insignificant, or this gap row is stale."));

        var events = await ProjectAsync(progress, ct);
        var toolProgress = events
            .Where(e => e.Kind == ConversationEventKind.TurnProgress
                        && e.PayloadAs<TurnProgressPayload>()?.Activity == ProgressActivity.Tool)
            .ToList();

        // Exactly one tool progress event for a start+completion pair: the start.
        Assert.Single(toolProgress);

        return Observe(
            MatrixCells.Frames(completions.Select(_ => "tool completion")), completions, toolProgress);
    }

    private static bool IsToolCompletion(AgentProgress p) =>
        p.EventType is "tool_result"
        // Claude delivers the tool result as a plain `user` frame; it has no dedicated event type.
        || (p.EventType == "user" && !p.IsSignificant);

    /// <summary>
    /// G2 — there is no incremental assistant-text event in v1. <c>ConversationEventKind</c> has no
    /// delta kind, so a client cannot begin rendering (or speaking) before the whole answer exists.
    /// This is a structural input to the voice stop/go decision, not a tuning problem.
    /// </summary>
    private static async Task<ScenarioObservation> IncrementalAssistantTextAsync(
        string provider, CancellationToken ct)
    {
        Assert.DoesNotContain(
            ConversationEventKind.Outbound,
            kind => kind.Contains("delta", StringComparison.OrdinalIgnoreCase));

        var (frames, progress) = await ReplayAsync(provider, "assistant-stream", ct);
        var chunks = progress.Where(p => p.EventType == "assistant").ToList();
        Assert.True(chunks.Count > 1, "The assistant-stream fixture must carry several text chunks.");

        var events = await ProjectAsync(progress, ct);
        var terminals = events.Where(e => e.IsTerminal).ToList();

        // Several assistant chunks in, exactly one client-visible answer out.
        Assert.Single(terminals);
        Assert.Equal(ConversationEventKind.TurnFinal, terminals[0].Kind);

        return Observe(frames, chunks, terminals);
    }

    /// <summary>
    /// G3 — Codex's <c>ToolName</c> is not a tool name. For <c>commandExecution</c> it is the
    /// executed shell command, and <c>ProtocolSanitizer.BoundToolName</c> only BOUNDS it to 64
    /// UTF-16 units — it does not classify it. The protocol doc states that only the tool name
    /// leaves the runtime and arguments never do; on this path that invariant is satisfied in
    /// letter and violated in substance.
    ///
    /// <para>This row PINS the leak so a later fix flips a red test rather than silently changing
    /// behaviour. Fixing it here is forbidden (MUST NOT #6).</para>
    /// </summary>
    private static async Task<ScenarioObservation> CommandExecutionToolNameAsync(CancellationToken ct)
    {
        var (frames, progress) = await ReplayAsync(CapabilityMatrix.Codex, "command-execution-turn", ct);

        var toolUse = Assert.Single(progress, p => p.EventType == "tool_use");
        var command = toolUse.ToolName!;
        Assert.True(command.Length > ProtocolLimits.MaxToolNameChars,
            "The fixture command must exceed the 64-unit bound, or the truncation assertion proves nothing.");

        var events = await ProjectAsync(progress, ct);
        var toolProgress = Assert.Single(
            events, e => e.Kind == ConversationEventKind.TurnProgress
                         && e.PayloadAs<TurnProgressPayload>()?.Activity == ProgressActivity.Tool);

        var clientVisible = toolProgress.PayloadAs<TurnProgressPayload>()!.ToolName;
        Assert.Equal(command[..ProtocolLimits.MaxToolNameChars], clientVisible);

        return Observe(frames, [toolUse], [toolProgress]);
    }

    // ── Absence assertions ───────────────────────────────────────────────────

    /// <summary>
    /// An <c>unsupported</c> cell is ASSERTED, not skipped. These go red if a provider gains the
    /// capability, which is what forces the matrix to be updated rather than quietly outgrown.
    /// </summary>
    private static void AssertNoNestedTurnConcept(string provider)
    {
        var executorType = provider == CapabilityMatrix.Codex
            ? typeof(CodexExecutor)
            : typeof(GeminiExecutor);

        Assert.DoesNotContain(
            executorType.GetMembers(System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Instance
                                    | System.Reflection.BindingFlags.Static),
            member => member.Name.Contains("Parent", StringComparison.OrdinalIgnoreCase)
                      || member.Name.Contains("Nested", StringComparison.OrdinalIgnoreCase)
                      || member.Name.Contains("Subagent", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoDrainPreserveConcept(string provider)
    {
        var executorType = provider == CapabilityMatrix.Codex
            ? typeof(CodexExecutor)
            : typeof(GeminiExecutor);

        Assert.DoesNotContain(
            executorType.GetMembers(System.Reflection.BindingFlags.Public
                                    | System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Instance
                                    | System.Reflection.BindingFlags.Static),
            member => member.Name.Contains("Preserved", StringComparison.OrdinalIgnoreCase)
                      || member.Name.Contains("RecoveredAnswer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Pump until the conversation has no running task, so the next submission is a genuinely new
    /// turn rather than a mid-turn steer. Pumping as it goes keeps delivery order equal to
    /// emission order across the boundary.
    /// </summary>
    private static async Task PumpUntilIdleAsync(
        ConversationHarness harness, long key, CancellationToken ct)
    {
        while (harness.Manager.HasRunningTasks(key))
        {
            ct.ThrowIfCancellationRequested();
            await harness.Pump.DrainOnceAsync(ct);
            await Task.Yield();
        }

        await harness.Pump.DrainOnceAsync(ct);
    }

    /// <summary>
    /// Poll a predicate without a clock. Used only where the observed state is set by another task
    /// on the same in-process path, so the loop is bounded by the caller's token.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
