using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Google.Protobuf;
using Temporalio.Api.Common.V1;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// The pure classifier, ownership resolver and strict policy loading (#476 §1). Every case builds a
/// raw Temporal history and runs it through the real fact extractor before classification.
/// </summary>
public sealed class WorkflowOutcomeClassifierTests
{
    private static readonly WorkflowOutcomeClassifierPolicy Fixture = OutcomeFixtures.Classifiers();
    private static readonly WorkflowOutcomeOwnershipPolicy Owners = OutcomeFixtures.Ownership();

    private static WorkflowOutcomeVerdict Classify(HistoryBuilder h, string ns = "namespace-a") =>
        WorkflowOutcomeClassifier.Classify(h.Facts(ns), Fixture);

    private static void AssertNotFailure(WorkflowOutcomeVerdict v) =>
        Assert.NotEqual(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);

    // ─── terminal matrix ────────────────────────────────────────────────────

    [Theory]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [InlineData("UwePrImplementationWorkflow")]
    [InlineData("CompiledReviewWorkflow")]
    [InlineData("NotifyCtoWorkflow")]
    public void Classify_FailedTerminal_ConfirmedFailureRegardlessOfNullResult(string type)
    {
        var v = Classify(new HistoryBuilder(type, "wf-1").Failed());

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.WorkflowFailed, v.ReasonCode);
        Assert.Equal(WorkflowOutcomeEvidenceStates.Complete, v.EvidenceState);
        Assert.Contains("event:2", v.CauseRefs);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_TimedOutTerminal_ConfirmedTimedOut()
    {
        var v = Classify(new HistoryBuilder("UweDesignWorkflow", "wf-1").LoadSeed("UweDesignWorkflow").TimedOut());

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.WorkflowTimedOut, v.ReasonCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_CompletedWithResult_ExcludedNormalCompletion()
    {
        var v = Classify(new HistoryBuilder("UweDesignWorkflow", "wf-1").LoadSeed("UweDesignWorkflow").CompletedWith(new { IssueNumber = 7 }));

        Assert.Equal(WorkflowOutcomeClassifications.ExcludedNonfailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.NormalCompletion, v.ReasonCode);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [InlineData(true)]
    [InlineData(false)]
    public void Classify_CompletedTypeWithoutRules_ClassificationPendingCoverageGap(bool withResult)
    {
        var h = new HistoryBuilder("SomeWorkflow", "wf-1");
        var v = Classify(withResult ? h.CompletedWith(new { IssueNumber = 7 }) : h.Completed(null));

        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.ClassifierMissing, v.ReasonCode);
        Assert.Equal("classifier_missing", v.GapReason);
        Assert.Equal(Fixture.Hash, v.ClassifierHash);
    }

    public static TheoryData<string> NullishResults => new() { "none", "binary_null", "json_null", "empty_string", "blank_string", "empty_object", "empty_array" };

    [Theory]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [MemberData(nameof(NullishResults))]
    public void Classify_CompletedNullOrEmptyResult_InsufficientEvidenceNotFailure(string kind)
    {
        Payload? result = kind switch
        {
            "none" => null,
            "binary_null" => HistoryBuilder.Null(),
            "json_null" => HistoryBuilder.RawJson("null"),
            "empty_string" => HistoryBuilder.Json(""),
            "blank_string" => HistoryBuilder.Json("   "),
            "empty_object" => HistoryBuilder.RawJson("{}"),
            _ => HistoryBuilder.RawJson("[]"),
        };
        var v = Classify(new HistoryBuilder("UweDesignWorkflow", "wf-1").LoadSeed("UweDesignWorkflow").Completed(result));

        Assert.Equal(WorkflowOutcomeClassifications.InsufficientEvidence, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.NullResultOnly, v.ReasonCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_CompletedEncryptedResult_InsufficientQuarantinedNeverInterpreted()
    {
        var h = new HistoryBuilder("UweDesignWorkflow", "wf-1").LoadSeed("UweDesignWorkflow")
            .Completed(HistoryBuilder.RawBytes("ciphertext"u8.ToArray(), "binary/encrypted"));
        var facts = h.Facts();
        var v = WorkflowOutcomeClassifier.Classify(facts, Fixture);

        Assert.Equal(WorkflowOutcomeClassifications.InsufficientEvidence, v.Classification);
        Assert.Equal(WorkflowOutcomeEvidenceStates.Quarantined, v.EvidenceState);
        Assert.Contains(facts.UnsupportedPayloadRefs, r => r == WorkflowOutcomeClassifier.EventRef(facts.Terminal!.EventId) + ":result");
        Assert.Null(facts.Terminal!.Result!.Json);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_ContinuedAsNew_ExcludedNonfailure()
    {
        var v = Classify(new HistoryBuilder("UweCarteBlancheWorkflow", "wf-1").ContinuedAsNew("run-2"));

        Assert.Equal(WorkflowOutcomeClassifications.ExcludedNonfailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.ContinuedAsNew, v.ReasonCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_CanceledWithoutRule_CauseUnresolved()
    {
        var v = Classify(new HistoryBuilder("SomeWorkflow", "wf-1").CancelRequested().Canceled());

        Assert.Equal(WorkflowOutcomeClassifications.CauseUnresolved, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.AbortCauseUnknown, v.ReasonCode);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [Trait("WorkflowOutcomeSuite", "kill")]
    [InlineData("failure: agent crashed, terminating", "coordinator")]
    [InlineData("intentional cleanup", "operator@dashboard")]
    [InlineData("Terminated via Fleet Dashboard", "")]
    public void Classify_TerminatedWithAnyReasonOrIdentity_CauseUnresolvedNeverInferred(string reason, string identity)
    {
        var h = new HistoryBuilder("UwePrImplementationWorkflow", "wf-1").LoadSeed("UwePrImplementationWorkflow")
            .OpenDelegate("phase1_implement", "agent1").Terminated(reason, identity);
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.CauseUnresolved, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.AbortCauseUnknown, v.ReasonCode);
        Assert.Equal("agent1", v.TaskOwner);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_HistoryWithEventIdGap_ClassificationPendingNotFailure()
    {
        var h = new HistoryBuilder("SomeWorkflow", "wf-1").Filler(3).Failed();
        var events = h.Events.Where(e => e.EventId != 3).ToList();
        var facts = WorkflowOutcomeFactsBuilder.FromEvents("namespace-a", "wf-1", "run-1", events);
        var v = WorkflowOutcomeClassifier.Classify(facts, Fixture);

        Assert.False(facts.HistoryComplete);
        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.HistoryIncomplete, v.ReasonCode);
        Assert.NotNull(v.GapReason);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_PartialPages_NeverClassifiedAsFailure()
    {
        var builder = new WorkflowOutcomeFactsBuilder("namespace-a", "wf-1", "run-1");
        foreach (var e in new HistoryBuilder("SomeWorkflow", "wf-1").Failed().Events) builder.Add(e);
        var v = WorkflowOutcomeClassifier.Classify(builder.Build(pagesComplete: false), Fixture);

        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, v.Classification);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Classify_SameFactsTwice_IdenticalVerdict()
    {
        var h = new HistoryBuilder("TaskDelegationWorkflow", "wf-1").LoadSeed("TaskDelegationWorkflow")
            .Delegate("delegate", "agent1", "could not finish", "failed").Delegate("notify-result", "coordinator", "notice").CompletedWith("notice");

        var a = Classify(h);
        var b = Classify(h);

        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Extract_TerminalFacts_HashTypeAndAttributesRecorded()
    {
        var h = new HistoryBuilder("SomeWorkflow", "wf-1").Failed("disk full", RetryState.NonRetryableFailure);
        var t = h.Facts().Terminal!;

        Assert.Equal(2, t.EventId);
        Assert.Equal(WorkflowOutcomeTerminalTypes.Failed, t.EventType);
        Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(h.Events[1].ToByteArray()), t.EventSha256);
        Assert.Equal("InfraError", t.FailureType);
        Assert.Equal("disk full", t.FailureMessage);
        Assert.Equal("NonRetryableFailure", t.RetryState);
        Assert.Contains("RETRY_STATE_NON_RETRYABLE_FAILURE", t.AttributesJson);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void Extract_LoadedDefinition_HashIsOverExactRecordedBytes()
    {
        var facts = new HistoryBuilder("UweDesignWorkflow", "wf-1").LoadSeed("UweDesignWorkflow").CompletedWith(null).Facts();

        Assert.Equal(OutcomeFixtures.LoadedHash("UweDesignWorkflow"), facts.DefinitionHash);
        Assert.Equal("UweDesignWorkflow", facts.DefinitionName);
        Assert.Equal(1, facts.DefinitionVersion);
    }

    // ─── business (Completed with loaded-hash proof) ────────────────────────

    public static TheoryData<string, string, string> BusinessExits => new()
    {
        { "UwePrImplementationWorkflow", "notify_consensus_exhausted", WorkflowOutcomeReasonCodes.BusinessReviewCap },
        { "UwePrImplementationWorkflow", "notify_outer_loop_exhausted", WorkflowOutcomeReasonCodes.BusinessReviewCap },
        { "UwePrImplementationWorkflow", "notify_post_feedback_human_review_timeout", WorkflowOutcomeReasonCodes.BusinessDeadline },
        { "UweDesignWorkflow", "notify_approved_body_changed", WorkflowOutcomeReasonCodes.BusinessArtifactMissing },
    };

    [Theory]
    [Trait("WorkflowOutcomeSuite", "business")]
    [MemberData(nameof(BusinessExits))]
    public void Classify_CompletedThroughPinnedFailureBranch_ConfirmedBusinessFailure(string type, string branch, string reason)
    {
        var h = new HistoryBuilder(type, "wf-7").LoadSeed(type)
            .Delegate("phase1_implement", "agent1", "IMPLEMENTATION: complete")
            .Delegate(branch, "coordinator", "notified").CompletedWith(null);
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(reason, v.ReasonCode);
        Assert.NotNull(v.ClassifierId);
        // The notifier's target is never the task owner.
        Assert.NotEqual("coordinator", v.TaskOwner);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_DesignErrorSentinelDelegateResult_BusinessReviewCap()
    {
        var h = new HistoryBuilder("UweDesignWorkflow", "wf-8").LoadSeed("UweDesignWorkflow")
            .Delegate("create_or_refine", "agent1", "ISSUE_NUMBER: 12")
            .Delegate("notify_consensus_exhausted", "coordinator", "posted")
            .Delegate("emit_exhausted_error", "coordinator", "ERROR:consensus_exhausted").CompletedWith(null);
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.BusinessReviewCap, v.ReasonCode);
        Assert.Equal("uwe-design-consensus-exhausted", v.ClassifierId);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_TaskDelegationTypedFailedResult_BusinessTaskFailedOwnedByTarget()
    {
        var h = new HistoryBuilder("TaskDelegationWorkflow", "wf-9", new { TargetAgent = "agent1" }).LoadSeed("TaskDelegationWorkflow")
            .Delegate("delegate", "agent1", "could not finish", "failed")
            .Delegate("notify-result", "coordinator", "TaskDelegationWorkflow completed. Result: could not finish")
            .CompletedWith("TaskDelegationWorkflow completed. Result: could not finish");
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.BusinessTaskFailed, v.ReasonCode);
        Assert.Equal("agent1", v.TaskOwner);
        Assert.Contains("task:wf-9/delegate", v.SourceTaskRefs);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "business")]
    [InlineData(true)]
    [InlineData(false)]
    public void Classify_TaskDelegationDelegateActivityFailedOrTimedOut_BusinessTaskFailed(bool timedOut)
    {
        var h = new HistoryBuilder("TaskDelegationWorkflow", "wf-10").LoadSeed("TaskDelegationWorkflow")
            .DelegateFailed("delegate", "agent1", timedOut)
            .Delegate("notify-result", "coordinator", "Task failed or timed out — check Temporal UI for details")
            .CompletedWith("Task failed or timed out — check Temporal UI for details");
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.BusinessTaskFailed, v.ReasonCode);
        Assert.Equal("agent1", v.TaskOwner);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "business")]
    [InlineData("completed")]
    [InlineData("incomplete")]
    [InlineData("idle")]
    public void Classify_TaskDelegationNonFailedStatusWithFailureWordsInNotifier_NotFailure(string status)
    {
        var h = new HistoryBuilder("TaskDelegationWorkflow", "wf-11").LoadSeed("TaskDelegationWorkflow")
            .Delegate("delegate", "agent1", "done", status)
            .Delegate("notify-result", "coordinator", "Result: FAILED needs_human_review rejected")
            .CompletedWith("Result: FAILED needs_human_review rejected");
        var v = Classify(h);

        AssertNotFailure(v);
        Assert.Equal(WorkflowOutcomeClassifications.ExcludedNonfailure, v.Classification);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_DesignHumanRejection_NullResultInsufficientNotFailure()
    {
        var h = new HistoryBuilder("UweDesignWorkflow", "wf-12").LoadSeed("UweDesignWorkflow")
            .Delegate("create_or_refine", "agent1", "ISSUE_NUMBER: 3").Signal("design-approval")
            .Delegate("notify_rejected", "coordinator", "posted").CompletedWith(null);
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.InsufficientEvidence, v.Classification);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_DesignNeedsHumanReviewSentinel_NotFailure()
    {
        var h = new HistoryBuilder("UweDesignWorkflow", "wf-13").LoadSeed("UweDesignWorkflow")
            .Delegate("notify_ceo_needs_human", "coordinator", "posted")
            .Delegate("emit_ceo_needs_human_error", "coordinator", "ERROR:ceo_needs_human_review").CompletedWith(null);
        var v = Classify(h);

        AssertNotFailure(v);
        Assert.Equal(WorkflowOutcomeClassifications.InsufficientEvidence, v.Classification);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_DeliberateHumanStopBranch_IntentionalNonfailure()
    {
        var h = new HistoryBuilder("UwePrImplementationWorkflow", "wf-14").LoadSeed("UwePrImplementationWorkflow")
            .Signal("acceptance-stop").Delegate("notify_stopped", "coordinator", "posted").CompletedWith(null);
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.IntentionalNonfailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.DeliberateDisposition, v.ReasonCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_PinnedTypeWithDifferentLoadedHash_ClassificationPendingGapNotSuccess()
    {
        var other = Encoding.UTF8.GetBytes("{\"Name\":\"UwePrImplementationWorkflow\",\"Version\":2,\"Root\":{\"type\":\"sequence\",\"steps\":[]}}");
        var h = new HistoryBuilder("UwePrImplementationWorkflow", "wf-15").LoadDefinition(other)
            .Delegate("notify_consensus_exhausted", "coordinator", "posted").CompletedWith(null);
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.ClassifierMissing, v.ReasonCode);
        Assert.Equal("classifier_missing", v.GapReason);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_PinnedTypeWithoutLoadedDefinition_ClassificationPending()
    {
        var v = Classify(new HistoryBuilder("UweDesignWorkflow", "wf-16").CompletedWith(new { IssueNumber = 4 }));

        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, v.Classification);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_FailureBranchOfAnotherWorkflowId_DoesNotMatch()
    {
        var h = new HistoryBuilder("UwePrImplementationWorkflow", "wf-17").LoadSeed("UwePrImplementationWorkflow");
        h.ScheduleDelegate("notify_consensus_exhausted", "coordinator", workflowId: "wf-other");
        var v = Classify(h.CompletedWith(new { ok = true }));

        Assert.Equal(WorkflowOutcomeClassifications.ExcludedNonfailure, v.Classification);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void Classify_UndecodableDelegateTaskId_PendingNotGuessed()
    {
        var h = new HistoryBuilder("UwePrImplementationWorkflow", "wf-18").LoadSeed("UwePrImplementationWorkflow");
        h.ScheduleDelegate("x", "coordinator", taskIdPayload: HistoryBuilder.RawBytes([1, 2, 3], "binary/encrypted"));
        var v = Classify(h.CompletedWith(new { ok = true }));

        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, v.Classification);
        Assert.Equal(WorkflowOutcomeEvidenceStates.Quarantined, v.EvidenceState);
    }

    // ─── kill ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public void Classify_WorkerOutageTimedOutWithoutFinalActivity_ConfirmedOwnedByOpenTask()
    {
        var h = new HistoryBuilder("UwePrImplementationWorkflow", "wf-20").LoadSeed("UwePrImplementationWorkflow")
            .OpenDelegate("phase1_implement", "agent1").TimedOut();
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.WorkflowTimedOut, v.ReasonCode);
        Assert.Equal("agent1", v.TaskOwner);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public void Classify_CanceledAfterPinnedFailureBranch_FailureCausedCancel()
    {
        var h = new HistoryBuilder("UweDesignWorkflow", "wf-21").LoadSeed("UweDesignWorkflow")
            .Delegate("notify_consensus_exhausted", "coordinator", "posted").CancelRequested().Canceled();
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.FailureCausedCancel, v.ReasonCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public void Classify_CanceledWithoutProvenBranch_CauseUnresolvedEvenWithCancelCause()
    {
        var h = new HistoryBuilder("UweDesignWorkflow", "wf-22").LoadSeed("UweDesignWorkflow").CancelRequested().Canceled();

        Assert.Equal(WorkflowOutcomeClassifications.CauseUnresolved, Classify(h).Classification);
    }

    // ─── chains ─────────────────────────────────────────────────────────────

    [Theory]
    [Trait("WorkflowOutcomeSuite", "chains")]
    [InlineData(true)]
    [InlineData(false)]
    public void Classify_FailedOrTimedOutWithRetryInProgress_SuccessorPendingNotFinal(bool failed)
    {
        var h = new HistoryBuilder("SomeWorkflow", "wf-30");
        if (failed) h.Failed(retry: RetryState.InProgress, newRunId: "run-2");
        else h.TimedOut(RetryState.InProgress, "run-2");
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, v.Classification);
        Assert.Equal(WorkflowOutcomeEvidenceStates.SuccessorPending, v.EvidenceState);
        Assert.False(WorkflowOutcomeClassifier.IsEnqueueEligible(Row(v), "coordinator"));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public void Classify_FailedCronRunWithNextRun_FinalFailure()
    {
        var h = new HistoryBuilder("ScheduledReportWorkflow", "wf-31", started: a => a.CronSchedule = "0 * * * *")
            .Failed(retry: RetryState.RetryPolicyNotSet, newRunId: "run-2");
        var v = Classify(h);

        Assert.Equal(WorkflowOutcomeEvidenceStates.Complete, v.EvidenceState);
        Assert.True(WorkflowOutcomeClassifier.IsEnqueueEligible(Row(v), "coordinator"));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public void Extract_ChainLinks_FromStartedAndTerminalEvents()
    {
        var facts = new HistoryBuilder("SomeWorkflow", "wf-32", started: a =>
        {
            a.ContinuedExecutionRunId = "run-1";
            a.Initiator = ContinueAsNewInitiator.Retry;
            a.FirstExecutionRunId = "run-0";
            a.Attempt = 2;
            a.ParentWorkflowExecution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = "parent", RunId = "parent-run" };
        }).Completed(null, newRunId: "run-3").Facts(runId: "run-2");

        Assert.Equal("run-1", facts.ContinuedExecutionRunId);
        Assert.Equal("Retry", facts.Initiator);
        Assert.Equal("run-0", facts.FirstExecutionRunId);
        Assert.Equal("parent", facts.ParentWorkflowId);
        Assert.Equal("parent-run", facts.ParentRunId);
        Assert.Equal("run-3", facts.Terminal!.NewExecutionRunId);
    }

    public static TheoryData<string, string, string, string?, string> PredecessorCases => new()
    {
        { WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.NormalCompletion, WorkflowOutcomeEvidenceStates.Complete, WorkflowOutcomeReasonCodes.RetrySucceeded, WorkflowOutcomeEvidenceStates.Complete },
        { WorkflowOutcomeClassifications.InsufficientEvidence, WorkflowOutcomeReasonCodes.NullResultOnly, WorkflowOutcomeEvidenceStates.Complete, WorkflowOutcomeReasonCodes.RetrySucceeded, WorkflowOutcomeEvidenceStates.Complete },
        { WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowFailed, WorkflowOutcomeEvidenceStates.Complete, WorkflowOutcomeReasonCodes.WorkflowFailed, WorkflowOutcomeEvidenceStates.Complete },
        { WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowFailed, WorkflowOutcomeEvidenceStates.SuccessorPending, WorkflowOutcomeReasonCodes.WorkflowFailed, WorkflowOutcomeEvidenceStates.SuccessorPending },
        { WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeReasonCodes.AbortCauseUnknown, WorkflowOutcomeEvidenceStates.Complete, WorkflowOutcomeReasonCodes.WorkflowFailed, WorkflowOutcomeEvidenceStates.Complete },
        { WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.ContinuedAsNew, WorkflowOutcomeEvidenceStates.Complete, null, "" },
    };

    [Theory]
    [Trait("WorkflowOutcomeSuite", "chains")]
    [MemberData(nameof(PredecessorCases))]
    public void ResolvePredecessor_SuccessorOutcome_RetrySucceededOrFinal(string successorClass, string successorReason, string successorEvidence,
        string? expectedReason, string expectedEvidence)
    {
        var successor = new WorkflowOutcomeVerdict(successorClass, successorReason, successorEvidence, null, [], [], null, null);
        var change = WorkflowOutcomeClassifier.ResolvePredecessor(WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowFailed, successor);

        if (expectedReason is null) { Assert.Null(change); return; }
        Assert.NotNull(change);
        Assert.Equal(expectedReason, change.ReasonCode);
        Assert.Equal(expectedEvidence, change.EvidenceState);
        Assert.Equal(successorClass == WorkflowOutcomeClassifications.ConfirmedFailure, change.ShareIncident);
    }

    // ─── recursion ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recursion")]
    public void Classify_200SuccessfulOrVoidSystemWorkflows_NoneEnqueueEligible()
    {
        string[] types = ["NotifyCtoWorkflow", "MemoryStoreRequestWorkflow", "AuthTokenRefreshWorkflow"];
        var verdicts = Enumerable.Range(0, 200).Select(i =>
        {
            var h = new HistoryBuilder(types[i % 3], "sys-" + i).Delegate("notify", "coordinator", "ok");
            h = (i % 4) switch
            {
                0 => h.Completed(null),
                1 => h.Completed(HistoryBuilder.Null()),
                2 => h.CompletedWith(""),
                _ => h.CompletedWith(new { stored = true }),
            };
            return Classify(h);
        }).ToList();

        Assert.All(verdicts, v => Assert.False(WorkflowOutcomeClassifier.IsEnqueueEligible(Row(v), "coordinator")));
        Assert.All(verdicts, AssertNotFailure);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recursion")]
    public void Classify_200InfraFailuresOfSystemWorkflows_AllConfirmed()
    {
        string[] types = ["NotifyCtoWorkflow", "MemoryStoreRequestWorkflow", "AuthTokenRefreshWorkflow", "UnknownWorkflow"];
        var verdicts = Enumerable.Range(0, 200).Select(i =>
        {
            var h = new HistoryBuilder(types[i % 4], "sys-" + i);
            return Classify(i % 2 == 0 ? h.Failed("provider down") : h.TimedOut());
        }).ToList();

        Assert.All(verdicts, v => Assert.True(WorkflowOutcomeClassifier.IsEnqueueEligible(Row(v), "coordinator")));
    }

    // ─── ownership (AC18) ───────────────────────────────────────────────────

    private static WorkflowOutcomeOwnershipResult Own(object? input, string ns = "namespace-a", WorkflowOutcomeParentOwnership? parent = null,
        string? parentId = null, string? parentRun = null)
    {
        var h = new HistoryBuilder("SomeWorkflow", "wf-40", input, started: a =>
        {
            if (parentId is not null)
                a.ParentWorkflowExecution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = parentId, RunId = parentRun ?? "" };
        }).Failed();
        return WorkflowOutcomeOwnership.Resolve(h.Facts(ns), Owners, parent);
    }

    private static WorkflowOutcomeParentOwnership Parent(string? project, string? coordinator = "coordinator", string ns = "namespace-a") =>
        new(ns, "parent", "parent-run", project, coordinator, true);

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_ExactRepoMapping_BeatsVerifiedParent()
    {
        var o = Own(new { Repo = "owner/repo" }, parent: Parent("project-b"), parentId: "parent", parentRun: "parent-run");

        Assert.Equal("project-a", o.ProjectName);
        Assert.Equal("input_repo", o.ProjectSource);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    [InlineData("not a repo", "repo_invalid")]
    [InlineData("owner/repo/extra", "repo_invalid")]
    [InlineData("", "repo_invalid")]
    [InlineData("owner/unmapped", "repo_unmapped")]
    public void Ownership_PresentInvalidOrUnmappedRepo_UnknownWithNoFallback(string repo, string reason)
    {
        // namespace-c has an explicit namespace mapping and the parent is verified: neither is used.
        var o = Own(new { Repo = repo }, ns: "namespace-c", parent: Parent("project-c", ns: "namespace-c"), parentId: "parent", parentRun: "parent-run");

        Assert.Null(o.ProjectName);
        Assert.Equal(reason, o.UnknownReason);
        Assert.Equal("coordinator", o.Coordinator);
        Assert.Equal("default", o.CoordinatorSource);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_NonStringRepo_InvalidNoFallback()
    {
        var o = Own(new { Repo = 42 }, ns: "namespace-c");

        Assert.Null(o.ProjectName);
        Assert.Equal("repo_invalid", o.UnknownReason);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_ConflictingRepoSpellings_Unknown()
    {
        var o = Own(JsonNode.Parse("{\"Repo\":\"owner/repo\",\"repo\":\"owner/other\"}"), ns: "namespace-a");

        Assert.Null(o.ProjectName);
        Assert.Equal("repo_conflicting", o.UnknownReason);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_RepoTrimmedAndLowercased_Mapped()
    {
        Assert.Equal("project-a", Own(new { Repo = "  Owner/Repo " }).ProjectName);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_RepoAbsent_VerifiedParentProject()
    {
        var o = Own(new { TaskDescription = "x" }, ns: "namespace-c", parent: Parent("project-a", ns: "namespace-c"), parentId: "parent", parentRun: "parent-run");

        Assert.Equal("project-a", o.ProjectName);
        Assert.Equal("parent", o.ProjectSource);
        Assert.Contains("run:namespace-c/parent/parent-run", o.EvidenceRefs);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_RepoAbsentParentUnverified_ExplicitNamespaceMap()
    {
        var unverified = new WorkflowOutcomeParentOwnership("namespace-c", "parent", "parent-run", "project-a", "coordinator", false);
        var o = Own(new { }, ns: "namespace-c", parent: unverified, parentId: "parent", parentRun: "parent-run");

        Assert.Equal("project-c", o.ProjectName);
        Assert.Equal("namespace_map", o.ProjectSource);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_ParentRecordForAnotherRun_NotUsed()
    {
        var o = Own(new { }, ns: "namespace-a", parent: Parent("project-b"), parentId: "parent", parentRun: "different-run");

        Assert.Null(o.ProjectName);
        Assert.Equal("no_mapping", o.UnknownReason);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_SpoofedProjectTypeOrAgentInInput_NeverMaps()
    {
        var o = Own(new { Project = "project-a", TargetAgent = "project-a", Repository = "owner/repo", WorkflowType = "project-a" }, ns: "namespace-a");

        Assert.Null(o.ProjectName);
        Assert.Equal("no_mapping", o.UnknownReason);
        Assert.Equal("coordinator", o.Coordinator);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_NoInputNoMapping_UnknownDefaultCoordinator()
    {
        var o = Own(null, ns: "namespace-b");

        Assert.Null(o.ProjectName);
        Assert.Equal("coordinator", o.Coordinator);
        Assert.Equal(Owners.Hash, o.PolicyHash);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "coordinator")]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Coordinator_ExactNamespaceProjectRule_Wins()
    {
        var o = Own(new { Repo = "owner/other" }, ns: "namespace-b", parent: Parent("project-b", "coordinator-x", "namespace-b"), parentId: "parent", parentRun: "parent-run");

        Assert.Equal("project-b", o.ProjectName);
        Assert.Equal("coordinator-b", o.Coordinator);
        Assert.Equal("rule", o.CoordinatorSource);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "coordinator")]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Coordinator_VerifiedSameProjectParent_InheritedElseDefault()
    {
        var same = Own(new { Repo = "owner/repo" }, parent: Parent("project-a", "coordinator-x"), parentId: "parent", parentRun: "parent-run");
        var other = Own(new { Repo = "owner/repo" }, parent: Parent("project-c", "coordinator-x"), parentId: "parent", parentRun: "parent-run");

        Assert.Equal(("coordinator-x", "parent"), (same.Coordinator, same.CoordinatorSource));
        Assert.Equal(("coordinator", "default"), (other.Coordinator, other.CoordinatorSource));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Ownership_FailedChildOfUnknownTypeWithoutOwner_StillEligibleUnderDefault()
    {
        var h = new HistoryBuilder("UnknownChildWorkflow", "child-1", started: a =>
            a.ParentWorkflowExecution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = "gone-parent", RunId = "gone-run" }).Failed();
        var v = WorkflowOutcomeClassifier.Classify(h.Facts("namespace-b"), Fixture);
        var o = WorkflowOutcomeOwnership.Resolve(h.Facts("namespace-b"), Owners, null);
        var row = Row(v);
        row.Coordinator = o.Coordinator;

        Assert.Null(v.TaskOwner);
        Assert.Null(o.ProjectName);
        Assert.True(WorkflowOutcomeClassifier.IsEnqueueEligible(row, "coordinator"));
    }

    public static TheoryData<string, string> BadOwnership => new()
    {
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[{\"namespace\":\"namespace-a\",\"repo\":\"owner/repo\",\"project\":\"project-a\"},{\"namespace\":\"namespace-a\",\"repo\":\" OWNER/Repo\",\"project\":\"project-b\"}],\"namespaceProjects\":[],\"rules\":[]}", "duplicate namespace+repo" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[{\"namespace\":\"namespace-a\",\"project\":\"project-a\"},{\"namespace\":\"namespace-a\",\"project\":\"project-b\"}],\"rules\":[]}", "duplicate namespace" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[],\"rules\":[{\"namespace\":\"namespace-a\",\"project\":\"project-a\",\"coordinator\":\"c1\"},{\"namespace\":\"namespace-a\",\"project\":\"project-a\",\"coordinator\":\"c2\"}]}", "duplicate namespace+project" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[{\"namespace\":\"namespace-a\",\"project\":\"project-old\"}],\"rules\":[]}", "inactive project" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[{\"namespace\":\"namespace-a\",\"project\":\"Project-A\"}],\"rules\":[]}", "unknown project" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[],\"rules\":[],\"extra\":1}", "unknown key" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"defaultCoordinator\":\"other\",\"repoProjects\":[],\"namespaceProjects\":[],\"rules\":[]}", "duplicate key" },
        { "{\"schemaVersion\":2,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[],\"rules\":[]}", "schemaVersion" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"\",\"repoProjects\":[],\"namespaceProjects\":[],\"rules\":[]}", "defaultCoordinator" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[{\"namespace\":\"namespace-a\",\"repo\":\"*\",\"project\":\"project-a\"}],\"namespaceProjects\":[],\"rules\":[]}", "owner/name" },
        { "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[]}", "missing key" },
    };

    [Theory]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    [MemberData(nameof(BadOwnership))]
    public void OwnershipPolicy_InvalidFile_StartupFailure(string json, string fault)
    {
        var ex = Assert.Throws<WorkflowOutcomePolicyException>(() => WorkflowOutcomeOwnershipPolicy.Parse(Encoding.UTF8.GetBytes(json), OutcomeFixtures.Projects));
        Assert.Contains(fault, ex.Message);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void OwnershipPolicy_Fixture_LoadsWithHashOfExactBytes()
    {
        Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(File.ReadAllBytes(OutcomeFixtures.OwnershipPath)), Owners.Hash);
        Assert.Equal("coordinator", Owners.DefaultCoordinator);
        Assert.Equal(2, Owners.RepoProjects.Count);
    }

    // ─── classifier policy loading ──────────────────────────────────────────

    private static string Rule(string evidence, string terminal = "\"WorkflowExecutionCompleted\"", string classification = "confirmed_failure",
        string reason = "business_task_failed", string? hash = PinnedHash, string type = "TaskDelegationWorkflow", string id = "r1",
        string extra = "") =>
        "{\"id\":\"" + id + "\",\"workflowType\":\"" + type + "\",\"sourceCommit\":\"" + OutcomeFixtures.SourceCommit + "\"" +
        (hash is null ? "" : ",\"definitionHash\":" + hash) + ",\"terminalEventTypes\":[" + terminal + "],\"evidence\":" + evidence +
        ",\"classification\":\"" + classification + "\",\"reasonCode\":\"" + reason + "\"" + extra + "}";

    private const string PinnedHash = "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"";
    private const string Branch = "{\"kind\":\"failure_branch\",\"exitBranch\":\"delegate\"}";

    public static TheoryData<string, string> BadClassifiers => new()
    {
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch) + "," + Rule(Branch, id: "r1", type: "Other") + "]}", "duplicate id" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch) + "," + Rule(Branch, id: "r2") + "]}", "duplicates the evidence" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, type: "*") + "]}", "exact type" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, hash: null) + "]}", "requires definitionHash" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, terminal: "\"WorkflowExecutionFailed\"") + "]}", "terminalEventTypes" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, terminal: "\"Completed\"") + "]}", "terminalEventTypes" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, terminal: "\"WorkflowExecutionCanceled\"") + "]}", "not allowed" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"terminal_event\",\"path\":\"/attributes/reason\",\"equals\":\"x\"}", terminal: "\"WorkflowExecutionTerminated\"", reason: "failure_caused_terminate") + "]}", "cannot classify WorkflowExecutionTerminated" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"delegate_result\",\"exitBranch\":\"delegate\",\"path\":\"Status\",\"equals\":\"failed\"}") + "]}", "RFC 6901" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"delegate_result\",\"exitBranch\":\"delegate\",\"path\":\"/Status\",\"equals\":{\"any\":1}}") + "]}", "equals" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"delegate_result\",\"exitBranch\":\"delegate\",\"path\":\"/Status\"}") + "]}", "requires path, equals" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"regex\",\"exitBranch\":\"delegate\"}") + "]}", "evidence.kind" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"failure_branch\",\"exitBranch\":\"de*\"}") + "]}", "exact step name" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule("{\"kind\":\"failure_branch\",\"exitBranch\":\"delegate\",\"expression\":\"x\"}") + "]}", "unknown key" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, extra: ",\"priority\":1") + "]}", "unknown key" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, classification: "excluded_nonfailure", reason: "normal_completion") + "]}", "not allowed" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, reason: "workflow_failed") + "]}", "not allowed" },
        { "{\"schemaVersion\":1,\"rules\":[" + Rule(Branch, hash: "\"ABC\"") + "]}", "definitionHash" },
        { "{\"schemaVersion\":1,\"schemaVersion\":1,\"rules\":[]}", "duplicate key" },
        { "{\"schemaVersion\":1,\"rules\":[]} trailing", "not valid JSON" },
        { "{\"schemaVersion\":\"1\",\"rules\":[]}", "schemaVersion" },
    };

    [Theory]
    [Trait("WorkflowOutcomeSuite", "business")]
    [MemberData(nameof(BadClassifiers))]
    public void ClassifierPolicy_InvalidFile_StartupFailure(string json, string fault)
    {
        var ex = Assert.Throws<WorkflowOutcomePolicyException>(() => WorkflowOutcomeClassifierPolicy.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains(fault, ex.Message);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void ClassifierPolicy_Fixture_EveryRulePinsTheSeedSnapshotAndARealStep()
    {
        Assert.NotEmpty(Fixture.Rules);
        foreach (var rule in Fixture.Rules)
        {
            Assert.Equal(OutcomeFixtures.SourceCommit, rule.SourceCommit);
            Assert.Equal(OutcomeFixtures.LoadedHash(rule.WorkflowType), rule.DefinitionHash);
            // The branch a rule names must be a delegate step of the pinned definition snapshot.
            using var doc = JsonDocument.Parse(OutcomeFixtures.Loaded(rule.WorkflowType));
            Assert.Contains(rule.ExitBranch, DelegateStepNames(doc.RootElement.GetProperty("Root")));
        }
        Assert.Equal(WorkflowOutcomeReasonCodes.Business.Order(), Fixture.Rules.Select(r => r.ReasonCode)
            .Where(WorkflowOutcomeReasonCodes.IsBusiness).Distinct().Order());
    }

    private static IEnumerable<string> DelegateStepNames(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out var t) && t.GetString() == "delegate" && node.TryGetProperty("name", out var n) && n.GetString() is { } name)
                yield return name;
            foreach (var p in node.EnumerateObject())
                foreach (var inner in DelegateStepNames(p.Value)) yield return inner;
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                foreach (var inner in DelegateStepNames(item)) yield return inner;
        }
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "business")]
    [InlineData("", true)]
    [InlineData("/a~1b/0", true)]
    [InlineData("/a~0b", true)]
    [InlineData("/a~2b", false)]
    [InlineData("a", false)]
    public void JsonPointer_Rfc6901Validation(string pointer, bool valid) =>
        Assert.Equal(valid, WorkflowOutcomeJsonPointer.IsValid(pointer));

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void JsonPointer_ResolvesEscapedKeysAndArrayIndexes()
    {
        using var doc = JsonDocument.Parse("{\"a/b\":[{\"c~d\":5}],\"x\":[1]}");
        Assert.True(WorkflowOutcomeJsonPointer.TryResolve(doc.RootElement, "/a~1b/0/c~0d", out var value));
        Assert.Equal(5, value.GetInt32());
        Assert.False(WorkflowOutcomeJsonPointer.TryResolve(doc.RootElement, "/x/01", out _));
        Assert.False(WorkflowOutcomeJsonPointer.TryResolve(doc.RootElement, "/X", out _));
    }

    private static WorkflowOutcomeRun Row(WorkflowOutcomeVerdict v) => new()
    {
        Namespace = "namespace-a", WorkflowId = "wf", RunId = "run", IdentityHash = "h", WorkflowType = "t", Status = "Failed",
        Classification = v.Classification, ReasonCode = v.ReasonCode, EvidenceState = v.EvidenceState, Coordinator = "coordinator",
    };
}
