using System.Text.Json;
using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Workflows;

/// <summary>
/// #436 consensus changes: reviewer independence (<c>ExcludedAgents</c>), the artifact named to
/// every reviewer (<c>ReviewRef</c>), and the two attestations derived from the round
/// (<c>AttestedRef</c>, <c>ScrubAttested</c>).
///
/// The workflow-level tests drive <see cref="ConsensusReviewWorkflow.RunAsync"/> in a real
/// time-skipping worker with the delegate activity stubbed under its production name, so what is
/// asserted is what the orchestration actually scheduled and returned. The helper-level tests
/// cover the matching rules that no compact-path round can reach (for example a split round whose
/// final verdict is still approved, which only the legacy branch can produce).
/// </summary>
[Collection("consensus-workflow")]
public class ConsensusAttestationTests
{
    private const string TaskQueue = "consensus-attestation-tests";
    private const string ActivityName = "DelegateToAgent";
    private const string BudgetActivityName = "LoadAgentBudgetSeconds";
    private const string Ref = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherRef = "89abcdef0123456789abcdef0123456789abcdef";

    private sealed class Calls
    {
        private readonly List<(string Agent, string Instruction)> _delegations = [];
        private readonly Lock _gate = new();
        private int _budgetLookups;

        public void RecordDelegation(string agent, string instruction)
        {
            lock (_gate) _delegations.Add((agent, instruction));
        }

        public void RecordBudgetLookup() => Interlocked.Increment(ref _budgetLookups);

        public IReadOnlyList<(string Agent, string Instruction)> Delegations
        {
            get { lock (_gate) return [.. _delegations]; }
        }

        public int BudgetLookups => Volatile.Read(ref _budgetLookups);

        public string InstructionFor(string agent) => Delegations.Single(d => d.Agent == agent).Instruction;
    }

    private static TemporalWorker BuildWorker(WorkflowEnvironment env, Calls calls, Func<string, string> responseFor) =>
        new(
            env.Client,
            new TemporalWorkerOptions(TaskQueue)
                .AddActivity(ActivityDefinition.Create(
                    ActivityName,
                    typeof(AgentTaskResult),
                    [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int)],
                    3,
                    args =>
                    {
                        var agent = (string)args[0]!;
                        calls.RecordDelegation(agent, (string)args[1]!);
                        return new AgentTaskResult(responseFor(agent), "completed");
                    }))
                .AddActivity(ActivityDefinition.Create(
                    BudgetActivityName, typeof(int), [], 0, _ =>
                    {
                        calls.RecordBudgetLookup();
                        return 5400;
                    }))
                .AddWorkflow<ConsensusReviewWorkflow>());

    private static async Task<(ConsensusReviewOutput Output, Calls Calls)> RunAsync(
        ConsensusReviewInput input, Func<string, string> responseFor)
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var calls = new Calls();
        using var worker = BuildWorker(env, calls, responseFor);

        ConsensusReviewOutput output = null!;
        await worker.ExecuteAsync(async () =>
        {
            output = await env.Client.ExecuteWorkflowAsync(
                (ConsensusReviewWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions($"consensus-attest-{Guid.NewGuid():N}", TaskQueue));
        });

        return (output, calls);
    }

    private static ConsensusReviewInput Input(
        string[]? reviewers = null, string? reviewRef = Ref, string[]? excluded = null,
        Dictionary<string, string>? perspectives = null) =>
        new(
            "a change under review", "review it",
            reviewers ?? ["reviewer-one", "reviewer-two"],
            perspectives, "synthesizer",
            ExcludedAgents: excluded,
            ReviewRef: reviewRef);

    private static string Response(string verdict, string[]? extraLines = null, string[]? blockers = null)
    {
        var lines = new List<string> { "Detailed review body." };
        lines.AddRange(extraLines ?? []);
        lines.Add("SUMMARY: decision text");
        lines.Add("EVIDENCE: none");
        foreach (var b in blockers ?? []) lines.Add($"BLOCKER: {b}");
        lines.Add($"VERDICT: {verdict}");
        return string.Join("\n", lines);
    }

    private static string ReviewedRefLine(string reviewRef) => $"REVIEWED_REF: {reviewRef}";

    // ── AC-C1: an excluded reviewer fails the round before anyone reviews ─────────

    [Fact]
    public async Task ExcludedReviewer_FailsNonRetryablyWithReviewerNotIndependent_AndDelegatesToNoOne()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var calls = new Calls();
        using var worker = BuildWorker(env, calls, _ => Response(ReviewVerdict.Approved));
        var workflowId = $"consensus-excluded-{Guid.NewGuid():N}";

        var input = Input(
            reviewers: ["reviewer-one", "author-agent"],
            excluded: ["  AUTHOR-AGENT ", "", "cto-agent"]);

        WorkflowFailedException failure = null!;
        await worker.ExecuteAsync(async () =>
        {
            failure = await Assert.ThrowsAsync<WorkflowFailedException>(() =>
                env.Client.ExecuteWorkflowAsync(
                    (ConsensusReviewWorkflow wf) => wf.RunAsync(input),
                    new WorkflowOptions(workflowId, TaskQueue)));
        });

        var appFailure = Assert.IsType<ApplicationFailureException>(failure.InnerException);
        Assert.Equal("ReviewerNotIndependent", appFailure.ErrorType);
        Assert.True(appFailure.NonRetryable);
        Assert.Contains("author-agent", appFailure.Message);
        Assert.DoesNotContain("reviewer-one", appFailure.Message);

        // Zero reviewer delegations, and not even the budget lookup ran.
        Assert.Empty(calls.Delegations);
        Assert.Equal(0, calls.BudgetLookups);

        // The refusal precedes every command: no patch marker and no activity in the history.
        var history = await env.Client.GetWorkflowHandle(workflowId).FetchHistoryAsync();
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.MarkerRecorded);
        Assert.DoesNotContain(history.Events, e => e.EventType == EventType.ActivityTaskScheduled);
    }

    [Fact]
    public async Task ExcludedAgents_AsACommaSeparatedString_IsHonouredThroughTheConverter()
    {
        // The UWE shape: definitions pass ExcludedAgents as "<author>,{{config.CtoAgent}}".
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var calls = new Calls();
        using var worker = BuildWorker(env, calls, _ => Response(ReviewVerdict.Approved));

        var raw = JsonSerializer.SerializeToElement(new
        {
            Subject = "a change under review",
            ReviewPrompt = "review it",
            ReviewerAgents = "reviewer-one,cto-agent",
            Synthesizer = "synthesizer",
            ExcludedAgents = "author-agent, Cto-Agent",
            ReviewRef = Ref,
        });

        WorkflowFailedException failure = null!;
        await worker.ExecuteAsync(async () =>
        {
            failure = await Assert.ThrowsAsync<WorkflowFailedException>(() =>
                env.Client.ExecuteWorkflowAsync<ConsensusReviewOutput>(
                    "ConsensusReviewWorkflow",
                    [raw],
                    new WorkflowOptions($"consensus-excluded-csv-{Guid.NewGuid():N}", TaskQueue)));
        });

        var appFailure = Assert.IsType<ApplicationFailureException>(failure.InnerException);
        Assert.Equal("ReviewerNotIndependent", appFailure.ErrorType);
        Assert.True(appFailure.NonRetryable);
        Assert.Contains("cto-agent", appFailure.Message);
        Assert.Empty(calls.Delegations);
    }

    [Fact]
    public async Task ExcludedAgents_ThatReviewNothing_LeaveTheRoundUnchanged()
    {
        var (output, calls) = await RunAsync(
            Input(excluded: ["author-agent", "cto-agent"]),
            _ => Response(ReviewVerdict.Approved, [ReviewedRefLine(Ref)]));

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Equal(2, calls.Delegations.Count);
        Assert.Equal(Ref, output.AttestedRef);
    }

    [Theory]
    [InlineData(new[] { "reviewer-one" }, new[] { "reviewer-two" }, new string[0])]
    [InlineData(new[] { "reviewer-one", "author" }, new[] { " Author " }, new[] { "author" })]
    [InlineData(new[] { "reviewer-one", "author" }, new[] { "", "  " }, new string[0])]
    [InlineData(new[] { "  ", "author" }, new[] { "  ", "AUTHOR", "author" }, new[] { "author" })]
    public void FindNonIndependentReviewers_TrimsComparesCaseInsensitivelyAndIgnoresBlanks(
        string[] reviewers, string[] excluded, string[] expected)
    {
        Assert.Equal(expected, ConsensusReviewWorkflow.FindNonIndependentReviewers(reviewers, excluded));
    }

    [Fact]
    public void FindNonIndependentReviewers_NoExclusions_FindsNothing()
    {
        Assert.Empty(ConsensusReviewWorkflow.FindNonIndependentReviewers(["reviewer-one"], null));
        Assert.Empty(ConsensusReviewWorkflow.FindNonIndependentReviewers(["reviewer-one"], []));
    }

    // ── ReviewRef in front of every reviewer, and nothing when blank ──────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankReviewRef_LeavesEveryReviewerInstructionByteIdentical(string? reviewRef)
    {
        var (_, calls) = await RunAsync(
            Input(
                reviewRef: reviewRef,
                perspectives: new Dictionary<string, string> { ["reviewer-two"] = "Your perspective: security." }),
            _ => Response(ReviewVerdict.Approved));

        var envelope = ConsensusReviewWorkflow.BuildReviewEnvelopeInstruction();
        Assert.Equal("review it" + envelope, calls.InstructionFor("reviewer-one"));
        Assert.Equal("review it\n\nYour perspective: security." + envelope, calls.InstructionFor("reviewer-two"));
    }

    [Fact]
    public async Task NonBlankReviewRef_IsNamedInFrontOfEveryReviewerInstruction()
    {
        var (_, calls) = await RunAsync(
            Input(perspectives: new Dictionary<string, string> { ["reviewer-two"] = "Your perspective: security." }),
            _ => Response(ReviewVerdict.Approved));

        var envelope = ConsensusReviewWorkflow.BuildReviewEnvelopeInstruction();
        var refLine = $"Artifact under review (ReviewRef): {Ref}\n\n";
        Assert.Equal(refLine + "review it" + envelope, calls.InstructionFor("reviewer-one"));
        Assert.Equal(refLine + "review it\n\nYour perspective: security." + envelope, calls.InstructionFor("reviewer-two"));
    }

    // ── AC-C2: AttestedRef only for a unanimous round with every exact line ───────

    [Fact]
    public async Task UnanimousApproval_WithEveryExactReviewedRefLine_AttestsTheRef()
    {
        var (output, calls) = await RunAsync(
            Input(),
            agent => Response(ReviewVerdict.Approved,
                agent == "reviewer-one" ? [ReviewedRefLine(Ref)] : ["   " + ReviewedRefLine(Ref) + "  \r"]));

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Equal(Ref, output.AttestedRef);
        Assert.DoesNotContain(calls.Delegations, d => d.Agent == "synthesizer");
    }

    [Fact]
    public async Task UnanimousApproval_OneReviewerMissingTheLine_AttestsNothing()
    {
        var (output, _) = await RunAsync(
            Input(),
            agent => Response(ReviewVerdict.Approved, agent == "reviewer-one" ? [ReviewedRefLine(Ref)] : []));

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Equal("", output.AttestedRef);
    }

    [Fact]
    public async Task UnanimousApproval_OneReviewerNamesADifferentRef_AttestsNothing()
    {
        var (output, _) = await RunAsync(
            Input(),
            agent => Response(ReviewVerdict.Approved,
                [ReviewedRefLine(agent == "reviewer-one" ? Ref : OtherRef)]));

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Equal("", output.AttestedRef);
    }

    [Fact]
    public async Task SplitRound_ApprovedByTheSynthesizer_AttestsNothing()
    {
        var (output, calls) = await RunAsync(
            Input(),
            agent => agent switch
            {
                "synthesizer" => "The single concern is minor.\nVERDICT: approved",
                "reviewer-one" => Response(ReviewVerdict.ChangesRequested, [ReviewedRefLine(Ref)],
                    blockers: ["rename the flag"]),
                _ => Response(ReviewVerdict.Approved, [ReviewedRefLine(Ref)]),
            });

        Assert.Contains(calls.Delegations, d => d.Agent == "synthesizer");
        Assert.Equal("", output.AttestedRef);
    }

    [Fact]
    public async Task NoReviewRef_AttestsNothing_EvenWhenReviewersWriteTheLine()
    {
        var (output, _) = await RunAsync(
            Input(reviewRef: null),
            _ => Response(ReviewVerdict.Approved, [ReviewedRefLine(Ref)]));

        Assert.Equal(ReviewVerdict.Approved, output.FinalVerdict);
        Assert.Equal("", output.AttestedRef);
    }

    private static AgentReview Review(string agent, string verdict, params string[] lines) =>
        new(agent, string.Join("\n", new[] { "body" }.Concat(lines).Append($"VERDICT: {verdict}")), verdict);

    private static ConsensusReviewOutput Output(string finalVerdict, params AgentReview[] reviews) =>
        new(finalVerdict, "reasoning", reviews);

    [Fact]
    public void ComputeAttestedRef_ASplitRoundWithAnApprovedFinalVerdict_StillAttestsNothing()
    {
        // The shape only the legacy branch can return: the synthesizer's approval stands over a
        // dissenting reviewer. Unanimity is required independently of the final verdict.
        var output = Output(ReviewVerdict.Approved,
            Review("reviewer-one", ReviewVerdict.ChangesRequested, ReviewedRefLine(Ref)),
            Review("reviewer-two", ReviewVerdict.Approved, ReviewedRefLine(Ref)));

        Assert.Equal("", ConsensusReviewWorkflow.ComputeAttestedRef(output, Ref));
    }

    [Theory]
    [InlineData(ReviewVerdict.ChangesRequested)]
    [InlineData(ReviewVerdict.NeedsHumanReview)]
    public void ComputeAttestedRef_ANonApprovedFinalVerdict_AttestsNothing(string finalVerdict)
    {
        var output = Output(finalVerdict,
            Review("reviewer-one", ReviewVerdict.Approved, ReviewedRefLine(Ref)));

        Assert.Equal("", ConsensusReviewWorkflow.ComputeAttestedRef(output, Ref));
    }

    [Theory]
    [InlineData("reviewed_ref: " + Ref)]
    [InlineData("REVIEWED_REF:" + Ref)]
    [InlineData("REVIEWED_REF:  " + Ref)]
    [InlineData("REVIEWED_REF: " + Ref + " (checked)")]
    [InlineData("I checked REVIEWED_REF: " + Ref)]
    [InlineData("REVIEWED_REF: " + "0123456789ABCDEF0123456789ABCDEF01234567")]
    public void ComputeAttestedRef_AnythingButTheExactTrimmedLine_AttestsNothing(string line)
    {
        var output = Output(ReviewVerdict.Approved,
            Review("reviewer-one", ReviewVerdict.Approved, line));

        Assert.Equal("", ConsensusReviewWorkflow.ComputeAttestedRef(output, Ref));
    }

    [Fact]
    public void ComputeAttestedRef_NoReviews_AttestsNothing()
    {
        Assert.Equal("", ConsensusReviewWorkflow.ComputeAttestedRef(Output(ReviewVerdict.Approved), Ref));
    }

    // ── AC-C3: ScrubAttested ──────────────────────────────────────────────────────

    [Fact]
    public async Task ScrubAttested_OnePass_IsTrue()
    {
        var (output, _) = await RunAsync(
            Input(),
            agent => Response(ReviewVerdict.Approved,
                agent == "reviewer-one" ? [ReviewedRefLine(Ref), "PUBLIC_SCRUB: pass"] : [ReviewedRefLine(Ref)]));

        Assert.True(output.ScrubAttested);
        Assert.Equal(Ref, output.AttestedRef);
    }

    [Fact]
    public async Task ScrubAttested_OnePassAndOneFail_IsFalse()
    {
        var (output, _) = await RunAsync(
            Input(),
            agent => Response(ReviewVerdict.Approved,
                [ReviewedRefLine(Ref), agent == "reviewer-one" ? "PUBLIC_SCRUB: pass" : "PUBLIC_SCRUB: fail"]));

        Assert.False(output.ScrubAttested);
    }

    [Fact]
    public async Task ScrubAttested_None_IsFalse()
    {
        var (output, _) = await RunAsync(
            Input(),
            _ => Response(ReviewVerdict.Approved, [ReviewedRefLine(Ref)]));

        Assert.False(output.ScrubAttested);
        Assert.Equal(Ref, output.AttestedRef);
    }

    [Theory]
    [InlineData("PUBLIC_SCRUB: FAIL")]
    [InlineData("  public_scrub:fail  ")]
    [InlineData("Public_Scrub:   Fail")]
    public void ComputeScrubAttested_AFailInAnyCasing_FromAnyVerdict_WinsOverAPass(string failLine)
    {
        Assert.False(ConsensusReviewWorkflow.ComputeScrubAttested(
        [
            Review("reviewer-one", ReviewVerdict.Approved, "PUBLIC_SCRUB: pass"),
            Review("reviewer-two", ReviewVerdict.ChangesRequested, failLine),
        ]));
    }

    [Fact]
    public void ComputeScrubAttested_APassOnlyFromANonApprovingReviewer_IsFalse()
    {
        Assert.False(ConsensusReviewWorkflow.ComputeScrubAttested(
        [
            Review("reviewer-one", ReviewVerdict.ChangesRequested, "PUBLIC_SCRUB: pass"),
            Review("reviewer-two", ReviewVerdict.Approved),
        ]));
    }

    [Theory]
    [InlineData("PUBLIC_SCRUB: PASS")]
    [InlineData("PUBLIC_SCRUB:pass")]
    [InlineData("PUBLIC_SCRUB: pass, mostly")]
    public void ComputeScrubAttested_APassThatIsNotTheExactLine_IsFalse(string passLine)
    {
        Assert.False(ConsensusReviewWorkflow.ComputeScrubAttested(
            [Review("reviewer-one", ReviewVerdict.Approved, passLine)]));
    }

    [Fact]
    public void ComputeScrubAttested_AnIndentedExactPass_IsTrue()
    {
        Assert.True(ConsensusReviewWorkflow.ComputeScrubAttested(
            [Review("reviewer-one", ReviewVerdict.Approved, "   PUBLIC_SCRUB: pass\r")]));
    }

    // ── Compatibility: pre-#436 payloads ──────────────────────────────────────────

    [Fact]
    public void PrePayloads_WithoutTheNewFields_DeserializeToTheDefaults()
    {
        var output = JsonSerializer.Deserialize<ConsensusReviewOutput>(
            """{"FinalVerdict":"approved","ConsolidatedReasoning":"r","PerAgentVerdicts":[]}""")!;
        Assert.Equal("", output.AttestedRef);
        Assert.False(output.ScrubAttested);

        var input = JsonSerializer.Deserialize<ConsensusReviewInput>(
            """{"Subject":"s","ReviewPrompt":"p","ReviewerAgents":"a,b","Synthesizer":"z"}""")!;
        Assert.Null(input.ExcludedAgents);
        Assert.Null(input.ReviewRef);
        Assert.Equal(new[] { "a", "b" }, input.ReviewerAgents);
    }

    [Fact]
    public void ExcludedAgents_AcceptsAJsonArrayOrACommaSeparatedString()
    {
        var fromArray = JsonSerializer.Deserialize<ConsensusReviewInput>(
            """{"Subject":"s","ReviewPrompt":"p","ExcludedAgents":["author","cto"]}""")!;
        var fromCsv = JsonSerializer.Deserialize<ConsensusReviewInput>(
            """{"Subject":"s","ReviewPrompt":"p","ExcludedAgents":" author , cto "}""")!;

        Assert.Equal(new[] { "author", "cto" }, fromArray.ExcludedAgents);
        Assert.Equal(new[] { "author", "cto" }, fromCsv.ExcludedAgents);
    }
}
