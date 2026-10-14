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

    // ── The `## pi` section (#386 B13) ───────────────────────────────────────
    //
    // Pi has no harness scenarios, so its section is not in the scenario grammar above and the
    // scenario parser never reads it. It carries only production-path canary results, one row per
    // gate: B1–B12 (#386) and the attachment canary V1–V9 (#455). Neither canary has run, so every
    // row must still say so: a number here before the canary would be a claim, not evidence
    // (#386 MUST NOT 4, #455 §10 V).

    private const string PiHeading = "## pi";
    private const string PiPending = "not measured (B canary pending)";
    private const string PiAttachmentPending = "not measured (V canary pending)";
    private const string PiNoEvidence = "—";
    private static readonly string[] PiHeader = ["gate", "measures", "result", "evidence"];
    private static readonly string[] PiGates =
    [
        "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B11", "B12",
        "V1", "V2", "V3", "V4", "V5", "V6", "V7", "V8", "V9",
    ];

    /// <summary>The pending literal for a gate: the V rows belong to the #455 attachment canary.</summary>
    private static string PendingFor(string gate) => gate.StartsWith('V') ? PiAttachmentPending : PiPending;

    /// <summary>The evidence a measured pi result must cite: the dated canary run and the Pi pin.</summary>
    private static readonly System.Text.RegularExpressions.Regex PiCanaryEvidence =
        new(@"^canary@\d{4}-\d{2}-\d{2}/pi-\d+\.\d+\.\d+$");

    private sealed record PiRow(string Gate, string Measures, string Result, string Evidence);

    private static string PiDocument(params string[] rows) =>
        "# probe\n\n" + PiHeading + "\n\nprose\n\n| gate | measures | result | evidence |\n|---|---|---|---|\n"
        + string.Join("\n", rows) + "\n\n## next\n";

    private static string PendingRow(string gate) => $"| {gate} | something | {PendingFor(gate)} | {PiNoEvidence} |";

    private static string[] AllPendingRows() => PiGates.Select(PendingRow).ToArray();

    /// <summary>Rows of the one <c>## pi</c> section, in document order; a malformed section throws.</summary>
    private static IReadOnlyList<PiRow> ParsePiSection(string markdown)
    {
        var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r').Trim()).ToList();
        var headings = lines.Select((line, index) => (line, index)).Where(l => l.line == PiHeading).ToList();
        if (headings.Count != 1)
            throw new FormatException($"expected exactly one '{PiHeading}' heading, found {headings.Count}");

        var rows = new List<PiRow>();
        var sawHeader = false;
        foreach (var line in lines.Skip(headings[0].index + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)))
        {
            if (!line.StartsWith('|'))
                continue;

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != PiHeader.Length)
                throw new FormatException($"pi row has {cells.Length} cells, expected {PiHeader.Length}: {line}");
            if (!sawHeader)
            {
                if (!cells.SequenceEqual(PiHeader, StringComparer.Ordinal))
                    throw new FormatException($"the pi table header must be '{string.Join(" | ", PiHeader)}': {line}");
                sawHeader = true;
                continue;
            }
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
                continue;

            rows.Add(new PiRow(cells[0], cells[1], cells[2], cells[3]));
        }

        if (rows.Count == 0)
            throw new FormatException("the pi section has no rows");
        return rows;
    }

    /// <summary>
    /// The section's standing rules, whatever the canary state: exactly B1–B12 in order, and a
    /// result is either the pending literal with no evidence or a measurement citing a dated
    /// canary run. Returns the first fault, or null.
    /// </summary>
    private static string? PiSectionFault(IReadOnlyList<PiRow> rows)
    {
        if (!rows.Select(r => r.Gate).SequenceEqual(PiGates, StringComparer.Ordinal))
            return $"the pi rows must be exactly {string.Join(", ", PiGates)} in order, found {string.Join(", ", rows.Select(r => r.Gate))}";

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Measures))
                return $"{row.Gate} does not say what it measures";

            if (row.Result == PendingFor(row.Gate))
            {
                if (row.Evidence != PiNoEvidence)
                    return $"{row.Gate} is pending but cites evidence '{row.Evidence}'";
            }
            else if (!PiCanaryEvidence.IsMatch(row.Evidence))
            {
                return $"{row.Gate} states a result without canary evidence: '{row.Evidence}'";
            }
        }

        return null;
    }

    private static IReadOnlyList<PiRow> CommittedPiRows() =>
        ParsePiSection(File.ReadAllText(RepoPaths.Resolve(CapabilityMatrix.DocumentPath)));

    [Fact]
    public void PiSection_IsNotReadAsScenarioRows()
    {
        Assert.DoesNotContain("pi", CapabilityMatrix.Providers);
        Assert.DoesNotContain(CapabilityMatrix.Parse(), r => r.Provider == "pi");
    }

    [Fact]
    public void PiSection_HasOneRowPerCanaryGateAndOnlyCanaryEvidence()
    {
        Assert.Null(PiSectionFault(CommittedPiRows()));
    }

    /// <summary>
    /// The B canary has not run. Every pi result stays pending until it does; this test changes in
    /// the same commit as the canary evidence, never before.
    /// </summary>
    [Fact]
    public void PiSection_ClaimsNoMeasurementUntilTheCanaryRuns()
    {
        foreach (var row in CommittedPiRows())
        {
            Assert.Equal(PendingFor(row.Gate), row.Result);
            Assert.Equal(PiNoEvidence, row.Evidence);
            Assert.DoesNotMatch(@"\d", row.Result);
        }
    }

    [Fact]
    public void PiSectionRules_AcceptAPendingSection()
    {
        Assert.Null(PiSectionFault(ParsePiSection(PiDocument(AllPendingRows()))));
    }

    /// <summary>The shape a measured row takes once the canary has run.</summary>
    [Fact]
    public void PiSectionRules_AcceptAMeasurementCitingACanaryRun()
    {
        var rows = AllPendingRows();
        rows[6] = "| B7 | something | lowest cached/prompt 0.93 | canary@2026-11-02/pi-1.0.4 |";

        Assert.Null(PiSectionFault(ParsePiSection(PiDocument(rows))));
    }

    [Theory]
    [InlineData("a number without evidence", 6, "| B7 | something | cached/prompt 0.99 | — |")]
    [InlineData("spike evidence", 6, "| B7 | something | cached/prompt 0.99 | spike@2026-10-13 |")]
    [InlineData("pending with evidence", 6, "| B7 | something | not measured (B canary pending) | canary@2026-11-02/pi-1.0.4 |")]
    [InlineData("a reopen-gate row", 0, "| G1 | summary-call cache ratio | not measured (B canary pending) | — |")]
    [InlineData("a B13 row", 11, "| B13 | the matrix itself | not measured (B canary pending) | — |")]
    [InlineData("no measures", 2, "| B3 |  | not measured (B canary pending) | — |")]
    [InlineData("a V row with the B pending text", 12, "| V1 | something | not measured (B canary pending) | — |")]
    [InlineData("a V10 row", 20, "| V10 | something | not measured (V canary pending) | — |")]
    public void PiSectionRules_RejectABrokenRow(string _, int index, string replacement)
    {
        var rows = AllPendingRows();
        rows[index] = replacement;

        Assert.NotNull(PiSectionFault(ParsePiSection(PiDocument(rows))));
    }

    [Fact]
    public void PiSectionRules_RejectAMissingGate()
    {
        Assert.NotNull(PiSectionFault(ParsePiSection(PiDocument(AllPendingRows()[..^1]))));
    }

    [Fact]
    public void PiSectionParser_RejectsAMissingOrRepeatedSectionAndAWrongTable()
    {
        Assert.Throws<FormatException>(() => ParsePiSection("# probe\n\n## claude\n"));
        Assert.Throws<FormatException>(() => ParsePiSection(PiDocument(AllPendingRows()) + PiDocument(AllPendingRows())));
        Assert.Throws<FormatException>(() => ParsePiSection(
            PiDocument(AllPendingRows()).Replace("| gate | measures | result | evidence |", "| gate | result | evidence | note |")));
        Assert.Throws<FormatException>(() => ParsePiSection(PiDocument("| B1 | something | pending |")));
    }
}
