using Fleet.Agent.Tests.Harness;
using Fleet.Protocol;

namespace Fleet.Agent.Tests;

/// <summary>
/// The per-provider half of the spike (D4): replay a synthetic fixture through each executor's real
/// parse path, project the result through the real runtime, and assert what a client observed
/// against the COMMITTED matrix row.
///
/// <para>The row drives the assertion, which is what binds the document to the code. A row that
/// says something the harness does not observe fails here; a scenario with no row fails on lookup;
/// a row with no scenario fails in <see cref="ProviderCapabilityMatrixTests"/>.</para>
///
/// <para>Runs with no credentials, no network and no provider CLI. Claude and Codex spawn a
/// <c>/bin/cat</c> stand-in so their real turn loops can run; that is a stand-in, not a provider.</para>
/// </summary>
public class RealAdapterMappingTests
{
    /// <summary>Per-test budget. Every wait in the harness is signal- or event-driven, never timed.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    public static TheoryData<string, string> Rows()
    {
        var data = new TheoryData<string, string>();
        foreach (var (provider, scenario) in ScenarioRunner.AllRows())
            data.Add(provider, scenario);
        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task ObservationMatchesTheCommittedMatrixRow(string provider, string scenario)
    {
        using var cts = new CancellationTokenSource(Budget);
        var row = CapabilityMatrix.Parse().Row(provider, scenario);

        var observed = await ScenarioRunner.RunAsync(provider, scenario, cts.Token);

        Assert.Equal(row.ProviderFrames, observed.Frames);
        Assert.Equal(row.AgentProgress, observed.Progress);

        // Ordered, not membership. A terminal delivered before its own turn.started, or a
        // turn.final before the submission.accepted it answers, is unrecoverable on the client and
        // invisible to a set-based assertion.
        Assert.Equal(row.ClientEvents, observed.ClientEvents);

        // ...and the delivery order the adapter actually saw must be lawful under the runtime's
        // own guarantee, which is weaker than "equals emission order" and stronger than "any".
        MatrixCells.AssertDeliveryOrderIsLawful(observed.RawEvents);

        // Every row carries an evidence cell, and this issue builds L1+L2 only, so it can only be
        // fixture-only. A `verified` cell here would be the exact conflation the honesty rule
        // exists to prevent.
        Assert.Equal(CapabilityMatrix.FixtureOnly, row.Evidence);

        AssertTypingHeartbeatIsWellPlaced(observed);
    }

    /// <summary>
    /// The heartbeat is excluded from the ordered cell because its COUNT is time-driven. Its
    /// PRESENCE is not: a running turn always emits at least one, and it never appears outside the
    /// turn window. Asserted here rather than in the cell so no row becomes a timing assertion.
    /// </summary>
    private static void AssertTypingHeartbeatIsWellPlaced(ScenarioObservation observed)
    {
        var ranATurn = observed.RawEvents.Any(e => e.Kind == ConversationEventKind.TurnStarted);
        if (!ranATurn)
        {
            Assert.Equal(0, observed.TypingHeartbeats);
            return;
        }

        Assert.True(observed.TypingHeartbeats > 0);

        var kinds = observed.RawEvents.OrderBy(e => e.Seq).Select(e => e.Kind).ToList();
        var firstStart = kinds.IndexOf(ConversationEventKind.TurnStarted);
        var lastTerminal = observed.RawEvents.OrderBy(e => e.Seq).ToList().FindLastIndex(e => e.IsTerminal);

        foreach (var (evt, index) in observed.RawEvents.OrderBy(e => e.Seq).Select((e, i) => (e, i)))
        {
            if (!MatrixCells.IsTypingHeartbeat(evt)) continue;
            Assert.True(index > firstStart, "A typing heartbeat preceded turn.started.");
            Assert.True(index < lastTerminal, "A typing heartbeat followed the final terminal event.");
        }
    }

    /// <summary>
    /// AC5 — Claude's mid-turn injection is <c>inferred</c> by construction. The stdin write has no
    /// acknowledgement frame, so nothing the provider sends confirms the steer landed.
    /// </summary>
    [Fact]
    public void ClaudeInjectionIsRecordedAsInferredWithItsReason()
    {
        var row = CapabilityMatrix.Parse().Row(CapabilityMatrix.Claude, "S3");

        Assert.Equal(Verdict.Inferred, row.Verdict);
        Assert.Contains("no provider acknowledgement frame exists", row.Note, StringComparison.Ordinal);
        Assert.Equal(MatrixCells.Empty, row.ProviderFrames);
    }

    /// <summary>
    /// AC6(a) — every Gemini start/terminal row carries the D3 reason. Gemini's turn-start marker
    /// and terminal are built inline in <c>ExecuteAsync</c> with no seam beneath it, so they cannot
    /// be reached from the public suite at all.
    /// </summary>
    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("S9")]
    [InlineData("S16")]
    public void GeminiStartAndTerminalRowsAreInferredWithTheD3Reason(string scenario)
    {
        var row = CapabilityMatrix.Parse().Row(CapabilityMatrix.Gemini, scenario);

        Assert.Equal(Verdict.Inferred, row.Verdict);
        Assert.Contains("no seam below ExecuteAsync", row.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC6(b) — the nine L2-only rows. S5, S6 and S10 run through a scripted executor for ALL
    /// THREE providers, so no provider frame is replayed for any of them. Calling one
    /// <c>supported</c> would assert a frame the test never saw.
    /// </summary>
    [Fact]
    public void EveryL2OnlyRowIsInferredForEveryProvider()
    {
        var rows = CapabilityMatrix.Parse();
        var checkedRows = 0;

        foreach (var provider in CapabilityMatrix.Providers)
        {
            foreach (var scenario in new[] { "S5", "S6", "S10" })
            {
                var row = rows.Row(provider, scenario);
                Assert.Equal(Verdict.Inferred, row.Verdict);
                Assert.Contains("scripted progress, no frame replay", row.Note, StringComparison.Ordinal);
                checkedRows++;
            }
        }

        Assert.Equal(9, checkedRows);
    }

    /// <summary>
    /// AC6(c) — the structural backstop. A row whose <c>provider frames</c> cell is empty is
    /// <c>inferred</c> or <c>unsupported</c> by construction; it can never be <c>supported</c>.
    /// This is asserted separately from the clauses above so a regression in one cannot hide behind
    /// another passing.
    /// </summary>
    [Fact]
    public void NoRowIsSupportedWithoutProviderFrames()
    {
        foreach (var row in CapabilityMatrix.Parse().Where(r => r.ProviderFrames == MatrixCells.Empty))
            Assert.NotEqual(Verdict.Supported, row.Verdict);
    }

    /// <summary>
    /// AC6(d) — no Gemini row claims <c>supported</c> for a start or terminal event while its
    /// evidence is <c>fixture-only</c>. Gemini rows can only reach <c>verified</c> through an L3
    /// capture, and there is no public path to one.
    /// </summary>
    [Fact]
    public void NoGeminiStartOrTerminalRowIsSupportedOnFixtureOnlyEvidence()
    {
        var rows = CapabilityMatrix.Parse();
        foreach (var scenario in new[] { "S1", "S2", "S9", "S16" })
        {
            var row = rows.Row(CapabilityMatrix.Gemini, scenario);
            Assert.True(
                row.Verdict != Verdict.Supported || row.Evidence != CapabilityMatrix.FixtureOnly,
                $"Gemini/{scenario} claims supported on fixture-only evidence.");
        }
    }

    /// <summary>
    /// AC7 — tool completion is <c>unsupported</c> on all three providers. The runner asserts the
    /// mechanism (a completion frame exists, is insignificant, and yields exactly one client-visible
    /// tool event — the start); this pins the recorded verdict.
    /// </summary>
    [Theory]
    [InlineData(CapabilityMatrix.Claude)]
    [InlineData(CapabilityMatrix.Codex)]
    [InlineData(CapabilityMatrix.Gemini)]
    public void ToolCompletionIsUnsupportedOnEveryProvider(string provider)
    {
        var row = CapabilityMatrix.Parse().Row(provider, "G1");

        Assert.Equal(Verdict.Unsupported, row.Verdict);
        Assert.Contains("IsSignificant = false", row.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC8 — the Codex <c>commandExecution</c> → <c>ToolName</c> leak, pinned byte-for-byte. The
    /// runner asserts the client-visible name equals the first 64 UTF-16 units of the fixture's
    /// command; this pins the verdict so a later fix flips a red test.
    /// </summary>
    [Fact]
    public void CodexCommandExecutionToolNameIsRecordedAsLeaky()
    {
        var row = CapabilityMatrix.Parse().Row(CapabilityMatrix.Codex, ScenarioRunner.CodexLeakScenario);

        Assert.Equal(Verdict.Leaky, row.Verdict);
        Assert.Contains("shell command", row.Note, StringComparison.Ordinal);
    }
}
