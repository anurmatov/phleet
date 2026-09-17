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

        // Ordered WITHIN each group, not membership. A terminal ahead of its own turn.started is
        // unrecoverable on the client and invisible to a set-based assertion.
        //
        // Deliberately NOT an example of that: a turn.final ahead of the submission.accepted it
        // answers. That looks like the same defect and is in fact normal — the disposition is
        // reported after the turn is dispatched — which is exactly why the cell separates the two
        // groups rather than pinning one sequence across them.
        Assert.Equal(row.ClientEvents, observed.ClientEvents);

        // ...and the delivery order the adapter actually saw must satisfy the three properties the
        // bus really guarantees: seq unique, each event delivered once, and a turn's terminal
        // sequenced after its own turn.started. Nothing beyond that is asserted, because nothing
        // beyond that is promised.
        MatrixCells.AssertDeliveryOrderIsLawful(observed.RawEvents);

        // Every row carries an evidence cell, and this issue builds L1+L2 only, so it can only be
        // fixture-only. A `verified` cell here would be the exact conflation the honesty rule
        // exists to prevent.
        Assert.Equal(CapabilityMatrix.FixtureOnly, row.Evidence);

        AssertTypingHeartbeatIsWellPlaced(observed);
    }

    /// <summary>
    /// The heartbeat is excluded from the ordered cell because its COUNT is time-driven. Its
    /// PRESENCE is not: a running turn always emits at least one. Asserted here rather than in the
    /// cell so no row becomes a timing assertion.
    ///
    /// <para><b>What is deliberately NOT asserted, and why.</b> An earlier revision also required
    /// every heartbeat to be sequenced BEFORE the final terminal. That is not true.
    /// <c>TaskManager</c> cancels the typing loop in <c>ProcessTask</c>'s <c>finally</c>, which
    /// runs after every terminal publish, so a heartbeat landing in that window legitimately takes
    /// a later <c>seq</c> than the terminal. It is unsound by construction — found by reading
    /// <c>TaskManager</c> rather than by watching it fail — and it is the same defect class as the
    /// two ordering claims already removed from this suite: an assertion about two independent
    /// publishers that the runtime never promised.</para>
    /// </summary>
    private static void AssertTypingHeartbeatIsWellPlaced(ScenarioObservation observed)
    {
        var ordered = observed.RawEvents.OrderBy(e => e.Seq).ToList();
        var firstStart = ordered.FirstOrDefault(e => e.Kind == ConversationEventKind.TurnStarted);

        if (firstStart is null)
        {
            Assert.Equal(0, observed.TypingHeartbeats);
            return;
        }

        Assert.True(observed.TypingHeartbeats > 0);

        // The one ordering fact here that IS causal: the typing loop is started inside ProcessTask,
        // which only runs after registration has published turn.started, so no heartbeat can
        // precede it.
        foreach (var heartbeat in ordered.Where(MatrixCells.IsTypingHeartbeat))
        {
            Assert.True(
                heartbeat.Seq > firstStart.Seq,
                $"A typing heartbeat took seq {heartbeat.Seq}, ahead of turn.started at {firstStart.Seq}.");
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
