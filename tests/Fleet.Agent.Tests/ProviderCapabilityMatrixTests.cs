using Fleet.Agent.Tests.Harness;
using Fleet.Protocol;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC3, AC4 — the committed matrix cannot drift from the harness, in either direction, and the
/// parser's four failure modes are each proven by a deliberately-broken input built in this test's
/// own arrange block.
///
/// <para>Proving a gate can go RED is half the job; a gate that has never gone green is not a gate
/// either, so the happy path is asserted against the real committed document.</para>
/// </summary>
public class ProviderCapabilityMatrixTests
{
    private const string GoodRow =
        "| S1 | `system/init` | `result`(sig,final) | turn.started → turn.final(completed) | supported | fixture-only |  |";

    /// <summary>
    /// A row whose client-events cell carries BOTH groups, so the two-group grammar is exercised
    /// rather than assumed. Every real row in the committed matrix has this shape.
    /// </summary>
    private const string TwoGroupRow =
        "| S1 | `system/init` | `result`(sig,final) | submission.accepted(ran) → submission.accepted(queued)"
        + " ‖ turn.started → turn.final(completed) | supported | fixture-only |  |";

    private static string Document(params string[] rows) =>
        """
        # probe

        ## claude

        | scenario | provider frames | agent progress | client events | verdict | evidence | note |
        |---|---|---|---|---|---|---|
        """ + "\n" + string.Join("\n", rows) + "\n";

    // ── The committed document ───────────────────────────────────────────────

    [Fact]
    public void EveryAssertedRowHasADocumentRow()
    {
        var rows = CapabilityMatrix.Parse();

        foreach (var (provider, scenario) in ScenarioRunner.AllRows())
        {
            Assert.True(
                rows.Any(r => r.Provider == provider && r.Scenario == scenario),
                $"The harness asserts {provider}/{scenario} but the matrix has no row for it.");
        }
    }

    [Fact]
    public void EveryDocumentRowHasAnAssertedRow()
    {
        var asserted = ScenarioRunner.AllRows().ToHashSet();

        foreach (var row in CapabilityMatrix.Parse())
        {
            Assert.True(
                asserted.Contains((row.Provider, row.Scenario)),
                $"The matrix carries {row.Key} but no harness assertion produces it. A row nothing "
                + "measures is a claim, not evidence.");
        }
    }

    /// <summary>AC4 — verdict, evidence and a non-empty note whenever the verdict is not supported.</summary>
    [Fact]
    public void EveryRowCarriesAVerdictEvidenceAndAReasonWhenItIsNotSupported()
    {
        foreach (var row in CapabilityMatrix.Parse())
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Evidence), $"{row.Key} has no evidence cell.");

            if (row.Evidence != CapabilityMatrix.FixtureOnly)
                Assert.Matches(CapabilityMatrix.VerifiedEvidencePattern, row.Evidence);

            if (row.Verdict != Verdict.Supported)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(row.Note),
                    $"{row.Key} is {row.Verdict} and must say why.");
            }
        }
    }

    /// <summary>
    /// This issue builds L1 and L2 only, so every row it produces is necessarily
    /// <c>fixture-only</c>. A <c>verified</c> cell here would be the exact conflation the honesty
    /// rule exists to prevent (MUST NOT #2).
    /// </summary>
    [Fact]
    public void NoRowClaimsVerifiedEvidenceYet()
    {
        foreach (var row in CapabilityMatrix.Parse())
            Assert.Equal(CapabilityMatrix.FixtureOnly, row.Evidence);
    }

    /// <summary>Runtime-only scenarios have no home in a per-provider table (D4, AC10).</summary>
    [Fact]
    public void NoRuntimeOnlyScenarioAppearsInAPerProviderTable()
    {
        var rows = CapabilityMatrix.Parse();
        foreach (var scenario in RuntimeScenarioTests.RuntimeOnlyScenarios)
            Assert.DoesNotContain(rows, r => r.Scenario == scenario);
    }

    // ── The four parser failure modes, each proven in-arrange ────────────────

    /// <summary>Mode 1 — a row with no corresponding harness assertion.</summary>
    [Fact]
    public void ParserRejects_RowWithNoHarnessAssertion()
    {
        var parsed = CapabilityMatrix.Parse(Document(GoodRow.Replace("| S1 |", "| S99 |")));
        var asserted = ScenarioRunner.AllRows().ToHashSet();

        var orphan = Assert.Single(parsed);
        Assert.DoesNotContain((orphan.Provider, orphan.Scenario), asserted);
    }

    /// <summary>Mode 2 — a harness assertion with no row.</summary>
    [Fact]
    public void ParserRejects_HarnessAssertionWithNoRow()
    {
        var parsed = CapabilityMatrix.Parse(Document(GoodRow));

        // S2 is asserted by the harness; this document omits it.
        Assert.Throws<InvalidOperationException>(() => parsed.Row(CapabilityMatrix.Claude, "S2"));
    }

    /// <summary>
    /// Mode 3 — a client-events cell listing kinds in an order the adapter did not observe. The
    /// cell is compared as a STRING, so a reordering is a different value, not a formatting nit.
    /// </summary>
    [Fact]
    public void ParserRejects_ClientEventsInTheWrongOrder()
    {
        var reordered = GoodRow.Replace(
            "turn.started → turn.final(completed)",
            "turn.final(completed) → turn.started");

        var correct = CapabilityMatrix.Parse(Document(GoodRow)).Row(CapabilityMatrix.Claude, "S1");
        var broken = CapabilityMatrix.Parse(Document(reordered)).Row(CapabilityMatrix.Claude, "S1");

        Assert.NotEqual(correct.ClientEvents, broken.ClientEvents);
    }

    /// <summary>
    /// Mode 3, on the two-group cell. Reordering WITHIN a group is a different value, because the
    /// order inside a group is a real claim. This is the case the single-group row above could not
    /// reach.
    /// </summary>
    [Fact]
    public void ParserRejects_ReorderingWithinAGroupOfATwoGroupCell()
    {
        var reordered = TwoGroupRow.Replace(
            "turn.started → turn.final(completed)",
            "turn.final(completed) → turn.started");

        var correct = CapabilityMatrix.Parse(Document(TwoGroupRow)).Row(CapabilityMatrix.Claude, "S1");
        var broken = CapabilityMatrix.Parse(Document(reordered)).Row(CapabilityMatrix.Claude, "S1");

        Assert.Contains(MatrixCells.GroupSeparator, correct.ClientEvents);
        Assert.NotEqual(correct.ClientEvents, broken.ClientEvents);
    }

    /// <summary>
    /// The other half of the two-group grammar, and the one that actually matters: ordering ACROSS
    /// the separator is not a claim, so the renderer must normalise it away. A disposition emitted
    /// AFTER the terminal it dispatched — which is what the runtime really does — still renders in
    /// the first group, so the cell is stable while the race is not.
    ///
    /// <para>Without this, the grammar's central promise is only documented, never demonstrated.</para>
    /// </summary>
    [Fact]
    public void ClientEventsCell_NormalisesTheDispositionRaceAcrossTheSeparator()
    {
        var identity = new ConversationIdentity
        {
            PrincipalId = "p", Role = PrincipalRole.Owner, ChannelId = "example-adapter",
            ConversationId = "c_1", SubmissionId = "s_1", Attempt = 1, TurnId = "t_1",
        };

        ConversationEvent At(long seq, string kind, object payload) =>
            ConversationEvent.Create(kind, identity, $"e{seq}", seq, DateTimeOffset.UnixEpoch, payload);

        // seq order: turn.started, turn.final, THEN the disposition — the observed production
        // ordering, because ReportDisposition runs after Task.Run has handed off the turn.
        var raced = new[]
        {
            At(1, ConversationEventKind.TurnStarted, new TurnStartedPayload()),
            At(2, ConversationEventKind.TurnFinal, new TurnFinalPayload
            {
                Text = "", Completion = TurnCompletion.Completed, IsPartial = false,
                Truncated = false, MergedSubmissionIds = [],
            }),
            At(3, ConversationEventKind.SubmissionAccepted,
               new SubmissionAcceptedPayload { Disposition = SubmissionDisposition.Ran }),
        };

        Assert.Equal(
            "submission.accepted(ran) ‖ turn.started → turn.final(completed)",
            MatrixCells.ClientEvents(raced));
    }

    /// <summary>Mode 4 — a malformed <c>verified</c> evidence cell.</summary>
    [Theory]
    [InlineData("verified")]
    [InlineData("verified@2026-9-30/claude-code-2.1.259")]
    [InlineData("verified@2026-09-30")]
    [InlineData("verified@2026-09-30/Claude-Code-2.1.259")]
    [InlineData("verified 2026-09-30 claude-code-2.1.259")]
    public void ParserRejects_MalformedVerifiedEvidence(string evidence)
    {
        var row = CapabilityMatrix
            .Parse(Document(GoodRow.Replace("| fixture-only |", $"| {evidence} |")))
            .Row(CapabilityMatrix.Claude, "S1");

        Assert.DoesNotMatch(CapabilityMatrix.VerifiedEvidencePattern, row.Evidence);
    }

    /// <summary>The positive control: a well-formed verified cell is accepted.</summary>
    [Fact]
    public void ParserAccepts_WellFormedVerifiedEvidence()
    {
        var row = CapabilityMatrix
            .Parse(Document(GoodRow.Replace("| fixture-only |", "| verified@2026-09-30/claude-code-2.1.259 |")))
            .Row(CapabilityMatrix.Claude, "S1");

        Assert.Matches(CapabilityMatrix.VerifiedEvidencePattern, row.Evidence);
    }

    // ── Grammar guards ───────────────────────────────────────────────────────

    [Fact]
    public void ParserRejects_UnknownVerdict()
    {
        Assert.Throws<FormatException>(
            () => CapabilityMatrix.Parse(Document(GoodRow.Replace("| supported |", "| probably |"))));
    }

    [Fact]
    public void ParserRejects_WrongCellCount()
    {
        Assert.Throws<FormatException>(
            () => CapabilityMatrix.Parse(Document(GoodRow.TrimEnd('|').TrimEnd() + " | extra |")));
    }

    [Fact]
    public void ParserRejects_DuplicateRow()
    {
        Assert.Throws<FormatException>(() => CapabilityMatrix.Parse(Document(GoodRow, GoodRow)));
    }
}
