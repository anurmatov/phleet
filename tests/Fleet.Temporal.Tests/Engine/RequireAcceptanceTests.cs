using System.Text.Json;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Engine;

public sealed class RequireAcceptanceTests
{
    internal const string Workflow = "UwePrImplementationWorkflow";
    internal static readonly string Head = new('a', 40);
    internal static Dictionary<string, object?> Input() => new()
    {
        ["Repo"] = "example-org/example-repo",
        ["IssueNumber"] = 7,
        ["TargetAgent"] = "impl-agent",
        ["ConsensusAgents"] = "reviewer-a",
    };
    public static IEnumerable<object?[]> OffValues() => new object?[][]
    { ["absent"], [null], [""], ["false"], ["False"], [false] };
    public static IEnumerable<object?[]> InvalidValues() => new object?[][]
    { ["TRUE"], ["yes"], ["1"], [" true"], ["true "], [1], [new Dictionary<string, object?> { ["value"] = true }] };

    internal static string Respond(Call call) => call.Step switch
    {
        "phase1_implement" => $"PR_URL: https://github.com/example-org/example-repo/pull/7\nHEAD_SHA: {Head}",
        "pin" or "pin_after_approval" => $"HEAD_OID: {Head}\nCI: success\nCHANGE_CLASS: code",
        "phase2_5_unresolved_check" => "UNRESOLVED: none",
        "verify_merge_status" => "PR_STATE_JSON: {\"state\":\"MERGED\",\"mergedAt\":\"2026-01-01T00:00:00Z\",\"mergeCommit\":{\"oid\":\"" + Head + "\"}}",
        "prepare" => "PREP: NO_DOC",
        _ when call.Step.StartsWith("review-", StringComparison.Ordinal) => $"SUMMARY: checked\nVERDICT: approved\nREVIEWED_REF: {Head}\nPUBLIC_SCRUB: pass",
        _ => "ok",
    };

    [Theory]
    [MemberData(nameof(OffValues))]
    public async Task Pr_OffValue_MergesWithoutAcceptanceTokensOrChildren(object? value)
    {
        var input = Input(); if (!Equals(value, "absent")) input["RequireAcceptance"] = value;
        var run = await SeedHarness.RunAsync(Workflow, input, Respond, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            Assert.DoesNotContain(h.Calls, c => c.Step == "phase4_merge");
            await h.SignalHumanApprovalAsync(handle);
        });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Empty(run.ChildStarts("UweAcceptanceCleanupWorkflow"));
        Assert.DoesNotContain(run.Upserts("Phase"), p => p is "acceptance-blocked" or "acceptance-continue");
        Assert.Contains($"v1;head={Head};result=not_required;iso=production;block=none;fix=0;batch=0", run.Upserts("Acceptance"));
        Assert.Contains(run.Calls, c => c.Instruction.Contains("basis require_acceptance_false", StringComparison.Ordinal));
        Assert.DoesNotContain("doc_only", Assert.Single(run.Calls, c => c.Instruction.StartsWith("Notify the primary human of verified exact-head readiness", StringComparison.Ordinal)).Instruction);
        Assert.Contains($"--match-head-commit {Head}", Assert.Single(run.Calls, c => c.Step == "phase4_merge").Instruction);
        Assert.Single(run.Calls, c => c.Step == "pin_after_approval");
    }

    [Fact]
    public async Task Pr_OffWithMalformedAcceptanceEvidence_IgnoresEvidence()
    {
        var input = Input(); input["PriorFixRounds"] = "invalid"; input["PriorBatchRounds"] = "invalid";
        var run = await SeedHarness.RunAsync(Workflow, input, c => Respond(c) + (c.Step is "pin" or "pin_after_approval"
            ? "\nPLAN: missing\nPRIOR_ROUNDS: invalid\nPRIOR_ATTEMPTS_CLEAN: no\nHARNESS_RETRY_SPENT: yes\nACCEPTANCE_ISO: isolated" : ""), async (handle, h) =>
        { await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle); });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Contains("result=not_required;iso=production;block=none;fix=0;batch=0", run.Upserts("Acceptance").Last());
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task Pr_InvalidValue_FailsBeforeAnyEffect(object value) => await Invalid(Workflow, value);

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task Wrapper_InvalidValue_FailsBeforeAnyEffect(object value) => await Invalid("UweDesignToPrWorkflow", value);

    private static Task Invalid(string workflow, object value)
    { var input = Input(); input["RequireAcceptance"] = value; return FailsBeforeEffects(workflow, input, "RequireAcceptance must be true, false or absent"); }

    internal static async Task FailsBeforeEffects(string workflow, Dictionary<string, object?> input, string message)
    {
        var error = await Assert.ThrowsAsync<WorkflowFailedException>(() => SeedHarness.RunAsync(workflow, input, Respond, async (handle, h) =>
        {
            await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync<JsonElement?>());
            Assert.Empty(h.Calls);
            var history = await handle.FetchHistoryAsync();
            // Workflow.Patched emits TemporalChangeVersion before the seed executes.
            // Assert no application search attributes, without hiding that SDK metadata.
            Assert.DoesNotContain(history.Events, e => e.EventType == EventType.UpsertWorkflowSearchAttributes
                && e.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields.Keys.Any(k => k != "TemporalChangeVersion"));
            Assert.DoesNotContain(history.Events, e => e.EventType is EventType.StartChildWorkflowExecutionInitiated
                or EventType.SignalExternalWorkflowExecutionInitiated);
        }));
        var failure = Assert.IsType<ApplicationFailureException>(error.InnerException);
        Assert.Equal("ExplicitFail", failure.ErrorType); Assert.Contains(message, failure.Message);
    }

    [Theory]
    [InlineData("<grant-id>")]
    [InlineData("None")]
    [InlineData(" none")]
    public async Task Wrapper_GrantWithoutRequiredMode_FailsBeforeDesign(string grant)
    {
        foreach (var value in new[] { "absent", "false", "" })
        {
            var input = Input(); input["AutonomyGrantId"] = grant; if (value != "absent") input["RequireAcceptance"] = value;
            await FailsBeforeEffects("UweDesignToPrWorkflow", input, "AutonomyGrantId requires RequireAcceptance=true");
        }
    }

    [Theory]
    [InlineData("absent", "false", "none")]
    [InlineData("false", "false", "")]
    [InlineData("False", "false", "none")]
    [InlineData("true", "true", "<grant-id>")]
    [InlineData("True", "true", "None")]
    public async Task Wrapper_ValidValue_ForwardsNormalizedLiteral(string value, string expected, string grant)
    {
        var input = Input(); input["AutonomyGrantId"] = grant;
        input["DesignAgent"] = "design-agent"; input["ImplementAgent"] = "impl-agent";
        input["ExistingIssueNumber"] = 7;
        if (value != "absent") input["RequireAcceptance"] = value;
        var run = await SeedHarness.RunAsync("UweDesignToPrWorkflow", input, DesignRespond, async (handle, h) =>
        { var design = await h.WaitForChildAsync(handle, "UweDesignWorkflow"); await h.WaitForGateVisitAsync(design, "design-approval:1"); await design.SignalAsync("design-approval", [JsonSerializer.SerializeToElement(new { Decision = "approved" })]); });
        var child = Assert.Single(run.ChildStarts(Workflow));
        Assert.Equal(expected, child.GetProperty("RequireAcceptance").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrapper_JsonBoolean_ForwardsNormalizedLiteral(bool value)
    {
        var input = Input(); input["RequireAcceptance"] = value;
        input["DesignAgent"] = "design-agent"; input["ImplementAgent"] = "impl-agent"; input["ExistingIssueNumber"] = 7;
        var run = await SeedHarness.RunAsync("UweDesignToPrWorkflow", input, DesignRespond, async (handle, h) =>
        { var design = await h.WaitForChildAsync(handle, "UweDesignWorkflow"); await h.WaitForGateVisitAsync(design, "design-approval:1"); await design.SignalAsync("design-approval", [JsonSerializer.SerializeToElement(new { Decision = "approved" })]); });
        Assert.Equal(value ? "true" : "false", Assert.Single(run.ChildStarts(Workflow)).GetProperty("RequireAcceptance").GetString());
    }

    internal static readonly string Body = new('b', 64);
    internal static string DesignRespond(Call c) => c.Step switch
    {
        "create_or_refine" => $"ISSUE_NUMBER: 7\nBODY_SHA256: {Body}",
        "verify_approved_body" => $"BODY_SHA256_NOW: {Body}",
        "emit_approved_result" => "{\"IssueNumber\":7}",
        _ when c.Step.StartsWith("review-", StringComparison.Ordinal) => $"SUMMARY: checked\nVERDICT: approved\nREVIEWED_REF: {Body}\nPUBLIC_SCRUB: pass",
        _ => "ok",
    };

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(true)]
    public async Task Pr_RequiredMissingPlan_Blocks(object value)
    {
        var input = Input(); input["RequireAcceptance"] = value;
        var run = await SeedHarness.RunAsync(Workflow, input, c => c.Step == "pin" ? SeedHarness.DocOnlyPin(Head).Replace("CHANGE_CLASS: doc_only", "CHANGE_CLASS: code").Replace("PLAN: ok", "PLAN: missing") : Respond(c), async (handle, h) =>
        { await WaitPhase(handle, h, "acceptance-blocked"); await Stop(handle, h); });
        Assert.Contains(run.Upserts("Acceptance"), a => a.Contains("block=plan_missing;", StringComparison.Ordinal));
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task Pr_DocOnly_BasisIsDocOnly(string value)
    {
        var input = Input(); input["RequireAcceptance"] = value;
        var run = await SeedHarness.RunAsync(Workflow, input, c => c.Step is "pin" or "pin_after_approval" ? SeedHarness.DocOnlyPin(Head) : Respond(c), async (handle, h) =>
        { await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle); });
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Contains(run.Calls, c => c.Instruction.Contains("basis doc_only", StringComparison.Ordinal));
    }

    // Six since the merge hold: PIN, the CI wait, the cleanup wait, a delegated approval's re-pin,
    // and the re-pin a human approval is checked with, once in the hold and once in the stall
    // after it. All carry the mode and the off contract. Five share one text and report their CI
    // read; the delegated re-pin keeps the text it shipped with, because nothing on the grant
    // path changes.
    [Fact]
    public void Pin_AllSixInstructions_IncludeModeAndOffContract()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).ToList();
        var pins = steps.OfType<DelegateStep>().Where(s => s.Name is "pin" or "pin_after_approval").ToArray();
        Assert.Equal(6, pins.Length);
        Assert.Equal(3, pins.Count(p => p.Name == "pin_after_approval"));
        Assert.All(pins, p => { Assert.Contains("REQUIRE_ACCEPTANCE: {{vars.acceptance_mode}}", p.Instruction); Assert.Contains("In off mode", p.Instruction); });

        var delegated = Assert.IsType<DelegateStep>(Assert.Single(steps.OfType<SequenceStep>(), s => s.Name == "merge_repin").Steps[0]);
        Assert.Equal("pin_after_approval", delegated.Name);
        Assert.Contains("Return HEAD_OID, CI and CHANGE_CLASS only.", delegated.Instruction);
        Assert.Contains("PR, HEAD_OID, IS_DRAFT, CI, CI_FAILING, CHANGE_CLASS,", delegated.Instruction);
        Assert.DoesNotContain("CI_READ", delegated.Instruction);
        Assert.DoesNotContain("CI_DETAIL", delegated.Instruction);

        var reading = pins.Where(p => !ReferenceEquals(p, delegated)).ToArray();
        Assert.Equal(5, reading.Length);
        Assert.Equal(2, reading.Count(p => p.Name == "pin_after_approval"));
        Assert.Single(reading.Select(p => p.Instruction).Distinct());
        Assert.All(reading, p =>
        {
            Assert.Contains("Return HEAD_OID, CI_READ, CI, CI_DETAIL and CHANGE_CLASS only.", p.Instruction);
            Assert.Contains("PR, HEAD_OID, IS_DRAFT, CI_READ, CI, CI_FAILING, CI_DETAIL, CHANGE_CLASS,", p.Instruction);
            Assert.Contains("error when any GitHub read failed; CI is then pending", p.Instruction);
        });
        // The only difference between the two texts is those tokens.
        Assert.Equal(delegated.Instruction, reading[0].Instruction!
            .Replace("Return HEAD_OID, CI_READ, CI, CI_DETAIL and CHANGE_CLASS only. CI_READ is ok when every check-run and status page was read and error when any GitHub read failed; CI is then pending.", "Return HEAD_OID, CI and CHANGE_CLASS only.", StringComparison.Ordinal)
            .Replace("IS_DRAFT, CI_READ, CI, CI_FAILING, CI_DETAIL, CHANGE_CLASS,", "IS_DRAFT, CI, CI_FAILING, CHANGE_CLASS,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pr_OffCiFailure_ReturnsToImplement()
    {
        var pins = 0; var implementations = 0;
        var run = await SeedHarness.RunAsync(Workflow, Input(), c =>
        {
            if (c.Step == "phase1_implement" || c.Step.StartsWith("revise_", StringComparison.Ordinal)) { implementations++; return Respond(new Call("phase1_implement", c.Target, c.Instruction)); }
            if (c.Step == "pin" && ++pins == 1) return Respond(c).Replace("CI: success", "CI: failure"); return Respond(c);
        }, async (handle, h) =>
          { await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle); });
        Assert.Equal(2, implementations); Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
    }

    // Off mode keeps the existing bound (twelve 300-second waits). Past it the run stalls on its own
    // instead of opening the acceptance-blocked gate: no gate notice, no decision, no acceptance work.
    [Fact]
    public async Task Pr_OffPendingCi_StallsAtExistingTimeout()
    {
        var run = await SeedHarness.RunAsync(Workflow, Input(), c => c.Step == "ci_probe" ? SeedHarness.CiProbe(Head, "pending") : Respond(c).Replace("CI: success", "CI: pending"), async (handle, h) =>
        { await h.AdvanceAsync(TimeSpan.FromHours(2)); await WaitPhase(handle, h, "technical-stalled"); });
        Assert.Contains(run.Upserts("Acceptance"), a => a.Contains("block=ci_timeout;", StringComparison.Ordinal));
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.DoesNotContain(run.Upserts("Phase"), p => p is "acceptance-blocked" or "acceptance-continue");
        Assert.Empty(run.Upserts("GateRef"));
        Assert.DoesNotContain(run.Calls, c => c.Step == "notify_acceptance_gate");
        Assert.Equal(12, run.History.Events.Count(e => e.EventType == EventType.TimerStarted && e.TimerStartedEventAttributes.StartToFireTimeout.Seconds == 300));
        Assert.Contains("reason ci_timeout", Assert.Single(run.Calls, c => c.Step == "notify_stalled").Instruction);
        // The stall's reads are the CI-only probe in off mode too.
        Assert.Equal(24, run.Calls.Count(c => c.Step == "ci_probe"));
        Assert.Equal(WorkflowExecutionStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Pr_RequiredPassedChild_NoticeBasisIsPassed()
    {
        var input = Input(); input["RequireAcceptance"] = true;
        var run = await SeedHarness.RunAsync(Workflow, input, c =>
        {
            if (c.Step is "pin" or "pin_after_approval") return SeedHarness.DocOnlyPin(Head).Replace("CHANGE_CLASS: doc_only", "CHANGE_CLASS: code");
            if (c.Step == "pre_stage")
            {
                var attempt = System.Text.RegularExpressions.Regex.Match(c.Instruction, @"ATTEMPT: ([^,]+)").Groups[1].Value;
                return $"ATTEMPT: {attempt}\nHEAD: {Head}\nSTAGE_RESULT: done\nREASON: synthetic\nRECEIPT: https://example.com/stage";
            }
            if (c.Step is "cleanup" or "backstop_cleanup") return "CLEANUP: verified\nRECEIPT: https://example.com/cleanup";
            return Respond(c);
        }, async (handle, h) =>
        { await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle); });
        Assert.Single(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Contains(run.Calls, c => c.Instruction.Contains("basis passed", StringComparison.Ordinal));
        Assert.Contains(run.Upserts("Acceptance"), a => a.Contains("result=passed;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pr_OffStaleSignals_BlockWithoutAcceptanceChildren()
    {
        var run = await SeedHarness.RunAsync(Workflow, Input(), Respond, async (handle, h) =>
        {
            for (var i = 1; i <= 6; i++)
            {
                await h.WaitForGateVisitAsync(handle, $"merge-approval:{i}");
                await handle.SignalAsync("merge-approval", [JsonSerializer.SerializeToElement(new { Decision = "approved", Ref = "old" })]);
            }
            await WaitPhase(handle, h, "acceptance-blocked"); await Stop(handle, h);
        });
        Assert.Contains(run.Upserts("Acceptance"), a => a.Contains("block=stale_signals;", StringComparison.Ordinal));
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.DoesNotContain(run.Calls, c => c.Step == "phase4_merge");
    }

    [Fact]
    public async Task Pr_OffUnknownHead_BlocksWithoutPlanReason()
    {
        var run = await SeedHarness.RunAsync(Workflow, Input(), c => c.Step is "pin" or "notify_invalidation" ? "HEAD_OID: unknown" : Respond(c), async (handle, h) =>
        { await WaitPhase(handle, h, "acceptance-blocked"); await Stop(handle, h); });
        Assert.Contains(run.Upserts("Acceptance"), a => a.Contains("block=owner_unavailable;", StringComparison.Ordinal));
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
    }

    [Fact]
    public async Task Pr_OffUnattestedReview_ReReviewsBeforePin()
    {
        var reviews = 0;
        var run = await SeedHarness.RunAsync(Workflow, Input(), c => c.Step.StartsWith("review-", StringComparison.Ordinal) && ++reviews == 1
            ? "SUMMARY: checked\nVERDICT: approved\nREVIEWED_REF: " + new string('c', 40) : Respond(c), async (handle, h) =>
        { await h.WaitForGateVisitAsync(handle, "merge-approval:1"); await h.SignalHumanApprovalAsync(handle); });
        Assert.Equal(2, run.ConsensusInputs.Count); Assert.Single(run.Calls, c => c.Step == "pin");
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
    }

    [Fact]
    public void Pr_OffRouting_RemainsBehindReviewRefEquality()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).ToArray();
        Assert.Contains(steps.OfType<SetVariableStep>(), s => s.Name == "pin_review_ref_pair" && s.Vars["pair"] == "{{vars.review_ref}}={{vars.H}}");
        var check = Assert.Single(steps.OfType<BranchStep>(), s => s.Name == "pin_review_ref_equal");
        Assert.Contains(WorkflowDefinitionValidator.Walk(check.Cases[""]).OfType<SetVariableStep>(), s => s.Vars.GetValueOrDefault("state") == "REVIEW");
        Assert.Contains(WorkflowDefinitionValidator.Walk(check.Default!).OfType<BranchStep>(), s => s.Name == "pin_acceptance_mode");
    }

    internal static async Task WaitPhase(WorkflowHandle handle, SeedHarness h, string phase)
    {
        var end = DateTime.UtcNow.AddSeconds(30); while (h.LastUpsert(await handle.FetchHistoryAsync(), "Phase") != phase)
        { if (DateTime.UtcNow > end) throw new TimeoutException(phase); await Task.Delay(20); }
    }
    internal static async Task Stop(WorkflowHandle handle, SeedHarness h) => await handle.SignalAsync("acceptance-decision",
        [JsonSerializer.SerializeToElement(new { Decision = "stop", Ref = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef") })]);
}
