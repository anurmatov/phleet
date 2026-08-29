using System.Text.Json;
using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;

namespace Fleet.Temporal.Tests.Workflows;

/// <summary>
/// Coverage for the consensus review envelope: parsing, compact rendering, blocker survival,
/// and the fail-closed rules.
///
/// These call the production methods directly (Fleet.Temporal exposes internals to this
/// assembly) rather than a mirror implementation — a test that reimplements the parser proves
/// only that two copies of the same idea agree with each other.
///
/// The recurring device below is a SENTINEL: a unique string placed in the raw review body,
/// outside every marker payload. Asserting the sentinel is absent from the synthesis prompt and
/// from ConsolidatedReasoning is what actually proves the verbose text stopped flowing
/// downstream; asserting the compact text is present would not.
/// </summary>
public class ConsensusReviewWorkflowTests
{
    private const string Sentinel = "ZZQ-RAW-ONLY-SENTINEL-7413";

    private static string RawReview(
        string body, string? summary = "concise decision text", string? evidence = "none",
        string[]? blockers = null, string verdict = ReviewVerdict.Approved)
    {
        var lines = new List<string> { body };
        if (summary is not null) lines.Add($"SUMMARY: {summary}");
        if (evidence is not null) lines.Add($"EVIDENCE: {evidence}");
        foreach (var b in blockers ?? []) lines.Add($"BLOCKER: {b}");
        lines.Add($"VERDICT: {verdict}");
        return string.Join("\n", lines);
    }

    private static ConsensusReviewInput Input(string subject = "a change under review") =>
        new(subject, "review it", ["reviewer-one", "reviewer-two"], null, "synthesizer");

    // ── Parsing: the happy path ───────────────────────────────────────────────

    [Fact]
    public void ParseReview_ExtractsAllFourMarkers_AndKeepsRawTextIntact()
    {
        var raw = RawReview(
            $"Long detailed body. {Sentinel}",
            summary: "Two real problems in the retry path.",
            evidence: "https://example.invalid/c/1",
            blockers: ["Guard the null case in the parser", "Add a test for the empty list"],
            verdict: ReviewVerdict.ChangesRequested);

        var review = ConsensusReviewWorkflow.ParseReview("reviewer-one", raw);

        Assert.Equal(ReviewVerdict.ChangesRequested, review.Verdict);
        Assert.Equal("Two real problems in the retry path.", review.Summary);
        Assert.Equal("https://example.invalid/c/1", review.EvidenceUrl);
        Assert.Equal(2, review.Blockers.Count);
        Assert.Equal("Guard the null case in the parser", review.Blockers[0]);

        // Durable field is byte-for-byte the original, markers included. Nothing is stripped.
        Assert.Equal(raw, review.ReviewText);
        Assert.Contains(Sentinel, review.ReviewText);
    }

    [Fact]
    public void ParseReview_MarkerMatchingIsCaseInsensitive()
    {
        var raw = "body\nsummary: lowercase works\nevidence: none\nverdict: approved";

        var review = ConsensusReviewWorkflow.ParseReview("r", raw);

        Assert.Equal(ReviewVerdict.Approved, review.Verdict);
        Assert.Equal("lowercase works", review.Summary);
    }

    [Fact]
    public void ParseReview_SummaryWrapsUntilTheNextMarker_ButBlockerDoesNot()
    {
        var raw = string.Join("\n",
            "body",
            "SUMMARY: first line",
            "second line of the same summary",
            "BLOCKER: only this one line is the blocker",
            "this continuation line is NOT part of the blocker",
            "VERDICT: changes_requested");

        var review = ConsensusReviewWorkflow.ParseReview("r", raw);

        Assert.Contains("second line of the same summary", review.Summary);
        var blocker = Assert.Single(review.Blockers);
        Assert.Equal("only this one line is the blocker", blocker);
        Assert.DoesNotContain("continuation line", blocker);
    }

    [Fact]
    public void ParseReview_LastSummaryAndEvidenceWin()
    {
        var raw = string.Join("\n",
            "SUMMARY: first", "EVIDENCE: https://example.invalid/a",
            "SUMMARY: second", "EVIDENCE: https://example.invalid/b",
            "VERDICT: approved");

        var review = ConsensusReviewWorkflow.ParseReview("r", raw);

        Assert.Equal("second", review.Summary);
        Assert.Equal("https://example.invalid/b", review.EvidenceUrl);
    }

    // ── Fail-closed rules ─────────────────────────────────────────────────────

    [Fact]
    public void ParseReview_MissingVerdict_ForcesNeedsHumanReview_NeverApproved()
    {
        var review = ConsensusReviewWorkflow.ParseReview("r", $"body {Sentinel}\nSUMMARY: fine");

        Assert.Equal(ReviewVerdict.NeedsHumanReview, review.Verdict);
        Assert.Equal("[unparseable verdict from r; see ReviewText]", review.Summary);
    }

    [Fact]
    public void ParseReview_UnknownVerdictValue_ForcesNeedsHumanReview()
    {
        // "VERDICT: yes" is recognised as a marker line but is not a valid value.
        var review = ConsensusReviewWorkflow.ParseReview("r", "body\nVERDICT: yes");

        Assert.Equal(ReviewVerdict.NeedsHumanReview, review.Verdict);
        Assert.Contains("unparseable verdict", review.Summary);
    }

    [Fact]
    public void ParseReview_TwoDifferentValidVerdicts_AreContradictory()
    {
        var raw = "body\nVERDICT: approved\nVERDICT: changes_requested";

        Assert.Equal(ReviewVerdict.NeedsHumanReview,
            ConsensusReviewWorkflow.ParseReview("r", raw).Verdict);
    }

    [Fact]
    public void ParseReview_RepeatingTheSameVerdictIsHarmless()
    {
        var raw = "body\nSUMMARY: s\nVERDICT: approved\nVERDICT: approved";

        Assert.Equal(ReviewVerdict.Approved,
            ConsensusReviewWorkflow.ParseReview("r", raw).Verdict);
    }

    [Theory]
    [InlineData(null)]        // no BLOCKER: line at all
    [InlineData("none")]      // the literal word none
    [InlineData("")]          // blank payload
    public void ParseReview_ChangesRequestedWithoutANamedBlocker_Escalates(string? blocker)
    {
        // A requested change nobody named cannot be acted on. Letting it through unlabeled is
        // exactly the failure this contract exists to prevent.
        var lines = new List<string> { "body", "SUMMARY: something is wrong" };
        if (blocker is not null) lines.Add($"BLOCKER: {blocker}");
        lines.Add("VERDICT: changes_requested");

        var review = ConsensusReviewWorkflow.ParseReview("r", string.Join("\n", lines));

        Assert.Equal(ReviewVerdict.NeedsHumanReview, review.Verdict);
        Assert.Empty(review.Blockers);
    }

    [Fact]
    public void ParseReview_ApprovedWithABlocker_IsContradictory_ButKeepsTheBlocker()
    {
        var raw = RawReview("body", blockers: ["fix the leak"], verdict: ReviewVerdict.Approved);

        var review = ConsensusReviewWorkflow.ParseReview("r", raw);

        Assert.Equal(ReviewVerdict.NeedsHumanReview, review.Verdict);
        // The named change is still preserved and still reaches the output.
        Assert.Equal("fix the leak", Assert.Single(review.Blockers));
    }

    // ── Summary fallback and truncation ───────────────────────────────────────

    [Fact]
    public void BuildSummary_MissingSummary_FallsBackToAPrefixedSlice()
    {
        var body = new string('x', 1000);

        var summary = ConsensusReviewWorkflow.BuildSummary(null, body);

        Assert.StartsWith(ConsensusReviewWorkflow.AutoSummaryPrefix, summary);
        Assert.Equal(
            ConsensusReviewWorkflow.AutoSummaryPrefix.Length + ConsensusReviewWorkflow.AutoSummaryLength,
            summary.Length);
    }

    [Fact]
    public void BuildSummary_OversizedSummary_IsTruncatedWithSuffix_AndDoesNotEscalate()
    {
        var payload = new string('y', 600);

        var summary = ConsensusReviewWorkflow.BuildSummary(payload, "raw");

        Assert.Equal(ConsensusReviewWorkflow.SummaryMaxLength, summary.Length);
        Assert.EndsWith(ConsensusReviewWorkflow.SummaryTruncationSuffix, summary);

        // Oversized-but-present is a formatting violation, not a broken review.
        var review = ConsensusReviewWorkflow.ParseReview(
            "r", $"SUMMARY: {payload}\nVERDICT: approved");
        Assert.Equal(ReviewVerdict.Approved, review.Verdict);
    }

    [Fact]
    public void BuildSummary_SurrogatePairStraddlingTheCut_IsNotSplit()
    {
        // Place an astral character so the 500-cap cut lands inside it.
        var budget = ConsensusReviewWorkflow.SummaryMaxLength
                     - ConsensusReviewWorkflow.SummaryTruncationSuffix.Length;
        var payload = new string('a', budget - 1) + "\U0001F600" + new string('b', 200);

        var summary = ConsensusReviewWorkflow.BuildSummary(payload, "raw");

        var body = summary[..^ConsensusReviewWorkflow.SummaryTruncationSuffix.Length];
        Assert.False(char.IsHighSurrogate(body[^1]), "truncation split a surrogate pair");
        Assert.True(summary.Length <= ConsensusReviewWorkflow.SummaryMaxLength);
    }

    // ── Evidence ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]
    [InlineData("NONE")]
    public void NormalizeEvidence_AbsentBlankOrNone_IsNull(string? payload) =>
        Assert.Null(ConsensusReviewWorkflow.NormalizeEvidence(payload));

    [Fact]
    public void NormalizeEvidence_NormalUrl_IsKeptVerbatim() =>
        Assert.Equal("https://example.invalid/c/9",
            ConsensusReviewWorkflow.NormalizeEvidence("  https://example.invalid/c/9  "));

    [Fact]
    public void NormalizeEvidence_OversizedPayload_IsDroppedNotTruncated()
    {
        // Half a URL is worse than no URL — it looks usable and isn't.
        var oversized = "https://example.invalid/" + new string('u', 1001);

        Assert.Null(ConsensusReviewWorkflow.NormalizeEvidence(oversized));
    }

    [Fact]
    public void ParseReview_OversizedEvidence_StaysOnlyInReviewText()
    {
        var oversized = "https://example.invalid/" + new string('u', 1001);
        var raw = $"body\nSUMMARY: s\nEVIDENCE: {oversized}\nVERDICT: approved";

        var review = ConsensusReviewWorkflow.ParseReview("r", raw);

        Assert.Null(review.EvidenceUrl);
        Assert.Contains(oversized, review.ReviewText);
        Assert.DoesNotContain(oversized, ConsensusReviewWorkflow.RenderCompactReview(review));
    }

    // ── Compact rendering ─────────────────────────────────────────────────────

    [Fact]
    public void RenderCompactReview_MatchesTheExactFormat()
    {
        var withEvidence = new AgentReview("r1", "raw", ReviewVerdict.Approved)
        { Summary = "looks fine", EvidenceUrl = "https://example.invalid/c/1" };
        var withoutEvidence = new AgentReview("r2", "raw", ReviewVerdict.ChangesRequested)
        { Summary = "needs work" };

        Assert.Equal("r1 [approved]: looks fine\nevidence: https://example.invalid/c/1",
            ConsensusReviewWorkflow.RenderCompactReview(withEvidence));
        Assert.Equal("r2 [changes_requested]: needs work",
            ConsensusReviewWorkflow.RenderCompactReview(withoutEvidence));

        // Reviewer order preserved, blocks joined by a blank line.
        Assert.Equal(
            "r1 [approved]: looks fine\nevidence: https://example.invalid/c/1\n\nr2 [changes_requested]: needs work",
            ConsensusReviewWorkflow.RenderCompactReviews([withEvidence, withoutEvidence]));
    }

    // ── The synthesis prompt never carries raw text or blockers ───────────────

    [Fact]
    public void BuildSynthesisInstruction_ContainsCompactFieldsOnly()
    {
        var reviews = new[]
        {
            ConsensusReviewWorkflow.ParseReview("reviewer-one",
                RawReview($"twenty pages of detail {Sentinel}", summary: "one real problem",
                    blockers: ["rename the flag"], verdict: ReviewVerdict.ChangesRequested)),
            ConsensusReviewWorkflow.ParseReview("reviewer-two",
                RawReview($"more detail {Sentinel}-2", summary: "no objection")),
        };

        var instruction = ConsensusReviewWorkflow.BuildSynthesisInstruction(Input(), reviews);

        // The raw body never reaches the synthesizer.
        Assert.DoesNotContain(Sentinel, instruction);
        // Neither does the blocker list — blocker transport is owned by workflow code.
        Assert.DoesNotContain("rename the flag", instruction);
        // The compact fields do.
        Assert.Contains("one real problem", instruction);
        Assert.Contains("no objection", instruction);
        Assert.Contains("a change under review", instruction);
    }

    [Fact]
    public void BuildSynthesisInstruction_AsksForTheCompactShape()
    {
        var reviews = new[] { ConsensusReviewWorkflow.ParseReview("r", RawReview("b")) };

        var instruction = ConsensusReviewWorkflow.BuildSynthesisInstruction(Input(), reviews);

        Assert.Contains("no validation-checklist replay", instruction);
        Assert.Contains($"VERDICT: {ReviewVerdict.Approved}", instruction);
    }

    [Fact]
    public void SynthesisInstruction_StaysCompactEvenForAHugeReview()
    {
        // 20 KB of raw response: durable field keeps all of it, the prompt sees none of it.
        var huge = new string('q', 20_000);
        var review = ConsensusReviewWorkflow.ParseReview(
            "r", RawReview($"{huge} {Sentinel}", summary: "short and decision-relevant"));

        Assert.True(review.ReviewText.Length >= 20_000);

        var instruction = ConsensusReviewWorkflow.BuildSynthesisInstruction(Input(), [review]);

        Assert.DoesNotContain(Sentinel, instruction);
        Assert.DoesNotContain(huge, instruction);
        Assert.True(instruction.Length < 2_000,
            $"synthesis instruction should stay compact, was {instruction.Length}");
    }

    // ── Blocker survival ──────────────────────────────────────────────────────

    [Fact]
    public void ComposeOutput_AllBlockersSurvive_EvenWhenTheSynthesizerOmitsOne()
    {
        var reviews = new[]
        {
            Reviewer("reviewer-one", "alpha blocker"),
            Reviewer("reviewer-two", "beta blocker"),
            Reviewer("reviewer-three", "gamma blocker"),
        };

        // The synthesizer mentions only one of the three — the exact failure mode this design
        // exists to close.
        const string synthesized = "The main issue is the alpha blocker.\nVERDICT: changes_requested";

        var output = ConsensusReviewWorkflow.ComposeOutput(
            reviews, ReviewVerdict.ChangesRequested, synthesized);

        Assert.Contains("- reviewer-one: alpha blocker", output.ConsolidatedReasoning);
        Assert.Contains("- reviewer-two: beta blocker", output.ConsolidatedReasoning);
        Assert.Contains("- reviewer-three: gamma blocker", output.ConsolidatedReasoning);
    }

    [Fact]
    public void ComposeOutput_NeverApprovesWhileABlockerIsNamed()
    {
        // One dissenter with a blocker, everyone else approving, and a candidate verdict that
        // resolved to approved. This is the case that would slip through if blocker assembly
        // were keyed on FinalVerdict instead of on the Blockers data.
        var reviews = new[]
        {
            Reviewer("reviewer-one", "the migration is missing a down step"),
            new AgentReview("reviewer-two", "raw", ReviewVerdict.Approved) { Summary = "fine" },
            new AgentReview("reviewer-three", "raw", ReviewVerdict.Approved) { Summary = "fine" },
        };

        var output = ConsensusReviewWorkflow.ComposeOutput(
            reviews, ReviewVerdict.Approved, "Everything looks good.\nVERDICT: approved");

        Assert.NotEqual(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Equal(ReviewVerdict.ChangesRequested, output.FinalVerdict);
        Assert.Contains("- reviewer-one: the migration is missing a down step",
            output.ConsolidatedReasoning);
    }

    [Fact]
    public void ComposeOutput_DropsTheProseEntirelyRatherThanIncludingHalfOfIt()
    {
        var reviews = new[] { Reviewer("r", new string('b', 1900)) };
        var longProse = new string('p', 500);   // cannot fit in the remaining budget

        var output = ConsensusReviewWorkflow.ComposeOutput(
            reviews, ReviewVerdict.ChangesRequested, longProse);

        Assert.Contains(new string('b', 1900), output.ConsolidatedReasoning);
        Assert.DoesNotContain("p", output.ConsolidatedReasoning);
        Assert.True(output.ConsolidatedReasoning.Length
            <= ConsensusReviewWorkflow.ConsolidatedReasoningMaxLength);
    }

    [Fact]
    public void ComposeOutput_BlockerOverflow_EscalatesAndNeverEmitsAPartialList()
    {
        // Enough distinct blockers that the rendered block alone busts the cap.
        var reviews = Enumerable.Range(0, 3)
            .Select(i => Reviewer($"reviewer-{i}", new string((char)('a' + i), 900)))
            .ToArray();

        var output = ConsensusReviewWorkflow.ComposeOutput(
            reviews, ReviewVerdict.ChangesRequested, "some prose");

        Assert.Equal(ReviewVerdict.NeedsHumanReview, output.FinalVerdict);
        Assert.Equal(ConsensusReviewWorkflow.BuildOverflowMessage(3, 3), output.ConsolidatedReasoning);

        // No fragment of any blocker leaked into the message.
        Assert.DoesNotContain(new string('a', 50), output.ConsolidatedReasoning);
    }

    [Fact]
    public void ComposeOutput_NoBlockers_TruncatesProseAtTheCap()
    {
        var reviews = new[] { new AgentReview("r", "raw", ReviewVerdict.Approved) { Summary = "ok" } };

        var output = ConsensusReviewWorkflow.ComposeOutput(
            reviews, ReviewVerdict.Approved, new string('z', 5000));

        Assert.Equal(ConsensusReviewWorkflow.ConsolidatedReasoningMaxLength,
            output.ConsolidatedReasoning.Length);
        Assert.EndsWith(ConsensusReviewWorkflow.ProseTruncationSuffix, output.ConsolidatedReasoning);
    }

    // ── Raw text never reaches the output, on any path ────────────────────────

    [Theory]
    [InlineData(ReviewVerdict.Approved)]
    [InlineData(ReviewVerdict.ChangesRequested)]
    [InlineData(ReviewVerdict.NeedsHumanReview)]
    public void ConsolidatedReasoning_NeverContainsTheRawSentinel(string candidateVerdict)
    {
        var reviews = new[]
        {
            ConsensusReviewWorkflow.ParseReview("reviewer-one",
                RawReview($"detail {Sentinel}-A", summary: "summary A", blockers: ["do X"],
                    verdict: ReviewVerdict.ChangesRequested)),
            ConsensusReviewWorkflow.ParseReview("reviewer-two",
                RawReview($"detail {Sentinel}-B", summary: "summary B")),
        };

        var output = ConsensusReviewWorkflow.ComposeOutput(
            reviews, candidateVerdict, ConsensusReviewWorkflow.RenderCompactReviews(reviews));

        Assert.DoesNotContain(Sentinel, output.ConsolidatedReasoning);

        // ...while every complete original response is still byte-for-byte durable.
        Assert.Contains($"{Sentinel}-A", output.PerAgentVerdicts[0].ReviewText);
        Assert.Contains($"{Sentinel}-B", output.PerAgentVerdicts[1].ReviewText);
    }

    // ── Generic (non-GitHub) run ──────────────────────────────────────────────

    [Fact]
    public void GenericRun_WithNoEvidenceLine_CompletesWithNullEvidenceAndFullDetail()
    {
        var raw = $"An opinion about an idea. {Sentinel}\nSUMMARY: seems sound\nVERDICT: approved";

        var review = ConsensusReviewWorkflow.ParseReview("reviewer-one", raw);
        var output = ConsensusReviewWorkflow.ComposeOutput(
            [review], ReviewVerdict.Approved, ConsensusReviewWorkflow.RenderCompactReviews([review]));

        Assert.Null(review.EvidenceUrl);
        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Contains(Sentinel, output.PerAgentVerdicts[0].ReviewText);
        Assert.DoesNotContain("evidence:", output.ConsolidatedReasoning);
    }

    [Fact]
    public void FailedEvidenceMirror_LeavesVerdictAndBlockersUntouched()
    {
        var raw = RawReview("detail", summary: "could not post the mirror comment",
            evidence: "none", blockers: ["fix the ordering"],
            verdict: ReviewVerdict.ChangesRequested);

        var review = ConsensusReviewWorkflow.ParseReview("r", raw);

        Assert.Null(review.EvidenceUrl);
        Assert.Equal(ReviewVerdict.ChangesRequested, review.Verdict);
        Assert.Equal("fix the ordering", Assert.Single(review.Blockers));
        Assert.Contains("detail", review.ReviewText);
    }

    // ── The envelope is appended once, centrally ──────────────────────────────

    [Fact]
    public void ReviewEnvelopeInstruction_ContainsEachMarkerExactlyOnceAsALine()
    {
        var envelope = ConsensusReviewWorkflow.BuildReviewEnvelopeInstruction();

        foreach (var marker in new[] { "SUMMARY:", "EVIDENCE:", "BLOCKER:", "VERDICT:" })
        {
            var atLineStart = envelope.Split('\n').Count(l => l.TrimStart().StartsWith(marker, StringComparison.Ordinal));
            Assert.True(atLineStart == 1,
                $"{marker} should head exactly one line of the envelope, found {atLineStart}");
        }
    }

    // ── Backward compatibility ────────────────────────────────────────────────

    [Fact]
    public void LegacyPayload_WithoutTheNewProperties_StillDeserializes()
    {
        // An output recorded before this change: nested AgentReview objects carry only the
        // three original members.
        const string legacy = """
        {
          "FinalVerdict": "approved",
          "ConsolidatedReasoning": "old style reasoning",
          "PerAgentVerdicts": [
            { "AgentName": "reviewer-one", "ReviewText": "full old review", "Verdict": "approved" }
          ]
        }
        """;

        var output = JsonSerializer.Deserialize<ConsensusReviewOutput>(legacy)!;

        Assert.Equal("approved", output.FinalVerdict);
        var review = Assert.Single(output.PerAgentVerdicts);
        Assert.Equal("full old review", review.ReviewText);
        Assert.Equal("", review.Summary);
        Assert.Null(review.EvidenceUrl);
        Assert.Empty(review.Blockers);
    }

    [Fact]
    public void NewPayload_RoundTrips_WithTheAddedProperties()
    {
        var original = new ConsensusReviewOutput(
            ReviewVerdict.ChangesRequested, "compact",
            [new AgentReview("r", "raw", ReviewVerdict.ChangesRequested)
             { Summary = "s", EvidenceUrl = "https://example.invalid/c/1", Blockers = ["b1"] }]);

        var round = JsonSerializer.Deserialize<ConsensusReviewOutput>(
            JsonSerializer.Serialize(original))!;

        var review = Assert.Single(round.PerAgentVerdicts);
        Assert.Equal("s", review.Summary);
        Assert.Equal("https://example.invalid/c/1", review.EvidenceUrl);
        Assert.Equal("b1", Assert.Single(review.Blockers));
        Assert.Equal("raw", review.ReviewText);
    }

    private static AgentReview Reviewer(string name, string blocker) =>
        new(name, $"raw for {name}", ReviewVerdict.ChangesRequested)
        { Summary = $"{name} summary", Blockers = [blocker] };
}
