using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// The observer end to end over a fake Temporal (raw RPC seam) and SQLite: discovery windows,
/// cursors, exact-RunId history, classification, storage, chains, gaps, health and timing (#476).
/// </summary>
public sealed class WorkflowOutcomeReconcilerTests
{
    private static readonly DateTime Old = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string Ns = "namespace-a";

    private static WorkflowOutcomeOptions Options(Action<WorkflowOutcomeOptions>? configure = null, params string[] namespaces)
    {
        var o = ObserverHarness.DefaultOptions(namespaces.Length == 0 ? [Ns] : namespaces);
        configure?.Invoke(o);
        return o;
    }

    private static FakeExecution Run(ObserverHarness h, string wf, Func<HistoryBuilder, HistoryBuilder> close, string run = "run-1",
        string ns = Ns, string type = "SomeWorkflow", DateTime? start = null, object? input = null,
        Action<WorkflowExecutionStartedEventAttributes>? started = null) =>
        h.Temporal.Add(ns, run, close(new HistoryBuilder(type, wf, input, start ?? Old, started)));

    private static bool Eligible(WorkflowOutcomeRun row) => WorkflowOutcomeClassifier.IsEnqueueEligible(row, ObserverHarness.Coordinator);

    /// <summary>A type the fixture classifier policy covers at its loaded seed hash.</summary>
    private const string Covered = "UweDesignWorkflow";

    private static async Task<WorkflowOutcomeRun> WaitClassifiedAsync(ObserverHarness h, string wf, string run, int maxCycles = 30, string ns = Ns)
    {
        for (var i = 0; i < maxCycles; i++)
        {
            var row = (await h.RunsAsync(ns)).SingleOrDefault(r => r.WorkflowId == wf && r.RunId == run);
            if (row?.TerminalEventId is not null) return row;
            await h.CyclesAsync(1);
        }
        throw new Xunit.Sdk.XunitException($"{wf}/{run} was never classified");
    }

    // ─── pagination ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    public async Task Reconcile_MoreThan250RunsWithEmptyPageAndToken_EveryRunRetainedAndClassified()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        h.Temporal.EmptyPagesBeforeData = 1;
        var recent = h.Clock.UtcNow.AddHours(-1);
        for (var i = 0; i < 300; i++)
        {
            var n = i;
            Run(h, "wf-" + n, b => n % 2 == 0 ? b.Failed() : b.CompletedWith(new { ok = n }), run: "run-" + n,
                start: n < 150 ? recent.AddSeconds(-n * 10) : Old.AddMinutes(-n));
        }

        await h.CyclesAsync(4);

        var rows = await h.RunsAsync();
        Assert.Equal(300, rows.Count);
        Assert.Equal(300, rows.Select(r => r.IdentityHash).Distinct().Count());
        Assert.All(rows, r => Assert.NotNull(r.TerminalEventId));
        Assert.Equal(150, rows.Count(Eligible));
        Assert.Contains("0:1", h.Temporal.ListTokens);
        var full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.True(full!.Complete);
        Assert.Equal(300, full.SeenCount);
        Assert.Null(full.PageToken);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    public async Task Reconcile_HistoryOver300EventsAcrossSlices_ClassifiedOnlyWhenEveryPageRead()
    {
        await using var h = await ObserverHarness.CreateAsync(Options(o => { o.HistoryPageSize = 100; o.MaxHistoryPagesPerSlice = 2; }));
        Run(h, "wf-long", b => b.Filler(350).Failed());

        await h.CyclesAsync(1);
        var partial = await h.RunAsync(Ns, "wf-long", "run-1");
        Assert.Null(partial.TerminalEventId);
        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, partial.Classification);
        Assert.Null(partial.ReasonCode);
        Assert.Equal(2, h.Temporal.HistoryCalls);

        await h.CyclesAsync(1);
        var done = await h.RunAsync(Ns, "wf-long", "run-1");
        Assert.Equal(352, done.TerminalEventId);
        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, done.Classification);
        Assert.Equal(4, h.Temporal.HistoryCalls);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    public async Task Reconcile_RepeatedListToken_WindowIncompleteAndNoPassCompletion()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 250; i++) Run(h, "wf-" + i, b => b.CompletedWith(new { ok = true }), run: "r" + i);
        h.Temporal.RepeatNextListToken = true;

        await h.CyclesAsync(1);

        var full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.False(full!.Complete);
        Assert.Null(full.PassCompletedAt);
        Assert.StartsWith("RepeatedToken", full.LastError);
        Assert.Equal(100, (await h.RunsAsync()).Count);
        Assert.Contains(await h.HealthAsync(), x => x.Component == WorkflowOutcomeHealthComponents.Temporal && x.State == "open");

        await h.CyclesAsync(4);
        full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.True(full!.Complete);
        Assert.Equal(250, (await h.RunsAsync()).Count);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    public async Task Reconcile_InvalidToken_RestartsSameWindowWithDedup()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 250; i++) Run(h, "wf-" + i, b => b.Failed(), run: "r" + i);
        h.Temporal.RejectNextListToken = true;

        await h.CyclesAsync(3);

        var rows = await h.RunsAsync();
        Assert.Equal(250, rows.Count);
        Assert.Equal(250, rows.Select(r => r.IdentityHash).Distinct().Count());
        var full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.True(full!.Complete);
        // Two recent partitions, then the full window: page 1, rejected page 2, restart page 1, 2, 3.
        Assert.Equal(["", "", "", "100:0", "", "100:0", "200:0"], h.Temporal.ListTokens.Take(7));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_Page2Failure_CursorStaysOnCommittedPageAndNoSuccess()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 250; i++) Run(h, "wf-" + i, b => b.Failed(), run: "r" + i);
        h.Temporal.FailListCall[Ns] = 4; // recent, recent, full page 1, full page 2

        await h.CyclesAsync(1);

        var full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.Equal(Convert.ToBase64String("100:0"u8.ToArray()), full!.PageToken);
        Assert.False(full.Complete);
        Assert.Null(full.PassCompletedAt);
        Assert.StartsWith("Unavailable", full.LastError);
        Assert.Equal(100, (await h.RunsAsync()).Count);
        var incident = Assert.Single(await h.HealthAsync(), x => x.OutageKey == "namespace_unavailable:" + Ns);
        Assert.Equal("open", incident.State);

        await h.CyclesAsync(4);

        Assert.Equal(250, (await h.RunsAsync()).Count);
        Assert.Contains("100:0", h.Temporal.ListTokens.Skip(4).Take(1));
        Assert.True((await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns)))!.Complete);
        Assert.Equal("resolved", (await h.HealthAsync()).Single(x => x.OutageKey == "namespace_unavailable:" + Ns).State);
    }

    // ─── ordering ───────────────────────────────────────────────────────────

    private static async Task<(WorkflowOutcomeRunFacts Facts, WorkflowOutcomeVerdict Verdict, WorkflowOutcomeOwnershipResult Owner)> Classified(
        ObserverHarness h, HistoryBuilder b, string run = "run-1")
    {
        var policies = h.Services.GetRequiredService<WorkflowOutcomePolicyLoader>().Current;
        var facts = b.Facts(Ns, run);
        await Task.CompletedTask;
        return (facts, WorkflowOutcomeClassifier.Classify(facts, policies.Classifiers), WorkflowOutcomeOwnership.Resolve(facts, policies.Ownership, null));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    public async Task Store_DuplicateTerminalObservation_OneRecordOneObservationNoVersionBump()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var b = new HistoryBuilder("SomeWorkflow", "wf-d", start: Old).Failed();
        h.Temporal.Add(Ns, "run-1", b);
        await h.CyclesAsync(1);
        var (facts, verdict, owner) = await Classified(h, b);

        var again = await h.Store.StoreOutcomeAsync(h.Reconciler.Lease!, facts, verdict, owner, 604800, default);

        Assert.Equal(WorkflowOutcomeStoreOutcome.Unchanged, again.Outcome);
        var row = Assert.Single(await h.RunsAsync());
        Assert.Equal(1, row.Version);
        Assert.Single(await h.ObservationsAsync(row.Id));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    public async Task Store_MaterialChange_IncrementsVersionOnceThenDuplicatesDoNot()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var b = new HistoryBuilder("SomeWorkflow", "wf-m", start: Old).Terminated();
        var (facts, verdict, owner) = await Classified(h, b);
        await h.CyclesAsync(1); // lease
        var lease = h.Reconciler.Lease!;

        var first = await h.Store.StoreOutcomeAsync(lease, facts, verdict, owner, 604800, default);
        var promoted = verdict with { Classification = WorkflowOutcomeClassifications.ConfirmedFailure, ReasonCode = WorkflowOutcomeReasonCodes.FailureCausedTerminate };
        var second = await h.Store.StoreOutcomeAsync(lease, facts, promoted, owner, 604800, default);
        var third = await h.Store.StoreOutcomeAsync(lease, facts, promoted, owner, 604800, default);

        Assert.Equal((1, false), (first.Version, first.VersionBumped));
        Assert.Equal((2, true), (second.Version, second.VersionBumped));
        Assert.Equal((2, false, WorkflowOutcomeStoreOutcome.Unchanged), (third.Version, third.VersionBumped, third.Outcome));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    public async Task Store_ContradictingTerminal_QuarantinedNeverMerged()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var b = new HistoryBuilder("SomeWorkflow", "wf-x", start: Old).Failed();
        h.Temporal.Add(Ns, "run-1", b);
        await h.CyclesAsync(1);
        var (facts, verdict, owner) = await Classified(h, new HistoryBuilder("SomeWorkflow", "wf-x", start: Old).Filler(2).CompletedWith(new { ok = 1 }));

        var result = await h.Store.StoreOutcomeAsync(h.Reconciler.Lease!, facts, verdict, owner, 604800, default);

        Assert.Equal(WorkflowOutcomeStoreOutcome.Quarantined, result.Outcome);
        var row = Assert.Single(await h.RunsAsync());
        Assert.True(row.Quarantined);
        Assert.Equal(2, row.TerminalEventId);
        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, row.Classification);
        Assert.False(Eligible(row));
        Assert.Contains(await h.ObservationsAsync(row.Id), o => o.Contradicting && o.EventId == 4);
        Assert.Contains(await h.HealthAsync(), x => x.Component == WorkflowOutcomeHealthComponents.Correlation);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    public async Task Reconcile_StaleRunningVisibilityAfterClosure_NoStateRegression()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var exec = Run(h, "wf-s", b => b.Failed(), start: h.Clock.UtcNow.AddHours(-1));
        await h.CyclesAsync(1);
        exec.Status = "Running"; // a lagging visibility replica

        await h.CyclesAsync(14);

        var row = await h.RunAsync(Ns, "wf-s", "run-1");
        Assert.Equal("Failed", row.Status);
        Assert.Equal(1, row.Version);
        Assert.True(Eligible(row));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task Reconcile_RetrySuccessorClassifiedBeforePredecessor_PredecessorRetrySucceeded()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var first = Run(h, "wf-r", b => b.LoadSeed(Covered).Failed(retry: RetryState.InProgress, newRunId: "run-2"), run: "run-1", type: Covered);
        first.VisibleAt = DateTime.MaxValue;
        Run(h, "wf-r", b => b.LoadSeed(Covered).CompletedWith(new { ok = true }), run: "run-2", start: Old.AddMinutes(1), type: Covered,
            started: a => { a.ContinuedExecutionRunId = "run-1"; a.Initiator = ContinueAsNewInitiator.Retry; a.Attempt = 2; });

        await h.CyclesAsync(1);
        Assert.Equal(WorkflowOutcomeClassifications.ExcludedNonfailure, (await h.RunAsync(Ns, "wf-r", "run-2")).Classification);

        first.VisibleAt = DateTime.MinValue;
        h.Clock.Advance(TimeSpan.FromSeconds(301));
        await h.CyclesAsync(1);

        var predecessor = await h.RunAsync(Ns, "wf-r", "run-1");
        Assert.Equal(WorkflowOutcomeClassifications.ExcludedNonfailure, predecessor.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.RetrySucceeded, predecessor.ReasonCode);
        Assert.False(Eligible(predecessor));
    }

    // ─── chains ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task Reconcile_RetryInProgressThenSuccess_NoInboxAndVersionedRetrySucceeded()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-r", b => b.LoadSeed(Covered).Failed(retry: RetryState.InProgress, newRunId: "run-2"), run: "run-1", type: Covered);

        await h.CyclesAsync(1);
        var pending = await h.RunAsync(Ns, "wf-r", "run-1");
        Assert.Equal(WorkflowOutcomeEvidenceStates.SuccessorPending, pending.EvidenceState);
        Assert.False(Eligible(pending));
        var tracked = await h.RunAsync(Ns, "wf-r", "run-2");
        Assert.Equal("Running", tracked.Status);

        Run(h, "wf-r", b => b.LoadSeed(Covered).CompletedWith(new { ok = true }), run: "run-2", start: Old.AddMinutes(1), type: Covered,
            started: a => { a.ContinuedExecutionRunId = "run-1"; a.Initiator = ContinueAsNewInitiator.Retry; });
        await WaitClassifiedAsync(h, "wf-r", "run-2");

        var resolved = await h.RunAsync(Ns, "wf-r", "run-1");
        Assert.Equal(WorkflowOutcomeReasonCodes.RetrySucceeded, resolved.ReasonCode);
        Assert.Equal(2, resolved.Version);
        Assert.False(Eligible(resolved));
        Assert.Contains(await h.ObservationsAsync(resolved.Id), o => o.EventType == WorkflowOutcomeStore.SuccessorOutcomeEvent);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task Reconcile_RetryThenCompletedUncoveredType_PredecessorJoinsTheGapNeitherSuccessNorFailure()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-u", b => b.Failed(retry: RetryState.InProgress, newRunId: "run-2"), run: "run-1");
        await h.CyclesAsync(1);
        Run(h, "wf-u", b => b.CompletedWith(new { ok = true }), run: "run-2", start: Old.AddMinutes(1),
            started: a => { a.ContinuedExecutionRunId = "run-1"; a.Initiator = ContinueAsNewInitiator.Retry; });
        await WaitClassifiedAsync(h, "wf-u", "run-2");

        foreach (var row in new[] { await h.RunAsync(Ns, "wf-u", "run-1"), await h.RunAsync(Ns, "wf-u", "run-2") })
        {
            Assert.Equal((WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierMissing), (row.Classification, row.ReasonCode));
            Assert.False(Eligible(row));
        }
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_ClassifierMissingRow_StoresPolicyHashAndIsNotReReadEveryCycle()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-gap", b => b.CompletedWith(new { ok = true }));
        await h.CyclesAsync(1);
        var row = await h.RunAsync(Ns, "wf-gap", "run-1");
        Assert.Equal(OutcomeFixtures.Classifiers().Hash, row.ClassifierHash);

        var store = h.Services.GetRequiredService<WorkflowOutcomeStore>();
        Assert.Empty(await store.ListAwaitingHistoryAsync(Ns, row.ClassifierHash!, 100, default));
        // A changed policy re-reads it exactly once more.
        Assert.Single(await store.ListAwaitingHistoryAsync(Ns, new string('0', 64), 100, default));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_OneRunHistoryUnavailable_OtherRunsInTheCycleStillClassified()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-poison", b => b.Failed(), start: Old);
        Run(h, "wf-fine", b => b.Failed(), start: Old.AddSeconds(1));
        h.Temporal.HistoryFault = (request, _) => request.Execution.WorkflowId == "wf-poison"
            // What the production RPC seam raises for an RPC timeout or an unavailable server.
            ? new WorkflowOutcomeReadException(WorkflowOutcomeReadFailure.Unavailable, "timeout")
            : null;

        await h.CyclesAsync(4);

        Assert.True(Eligible(await h.RunAsync(Ns, "wf-fine", "run-1")));
        Assert.Null((await h.RunAsync(Ns, "wf-poison", "run-1")).TerminalEventId);
        Assert.Contains(await h.HealthAsync(), x => x.State == "open" && x.Component == WorkflowOutcomeHealthComponents.History);

        h.Temporal.HistoryFault = null;
        await h.CyclesAsync(15); // past the 5..60 s retry backoff
        Assert.True(Eligible(await h.RunAsync(Ns, "wf-poison", "run-1")));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task Reconcile_RetryChainEndsInFinalFailure_BothRetainedUnderOneIncident()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-f", b => b.Failed(retry: RetryState.InProgress, newRunId: "run-2"), run: "run-1");
        Run(h, "wf-f", b => b.Failed(retry: RetryState.MaximumAttemptsReached), run: "run-2", start: Old.AddMinutes(1),
            started: a => { a.ContinuedExecutionRunId = "run-1"; a.Initiator = ContinueAsNewInitiator.Retry; });

        await h.CyclesAsync(2);

        var first = await h.RunAsync(Ns, "wf-f", "run-1");
        var last = await h.RunAsync(Ns, "wf-f", "run-2");
        Assert.True(Eligible(first));
        Assert.True(Eligible(last));
        Assert.NotNull(first.FailureIncidentId);
        Assert.Equal(first.FailureIncidentId, last.FailureIncidentId);
        Assert.Equal(1, first.Version);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task Reconcile_ContinueAsNewAndCronSuccess_NoInboxSuccessorsTracked()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-can", b => b.ContinuedAsNew("run-2"), run: "run-1");
        Run(h, "wf-cron", b => b.Completed(HistoryBuilder.Json(new { report = 1 }), newRunId: "cron-2"), run: "cron-1",
            started: a => a.CronSchedule = "0 * * * *");

        await h.CyclesAsync(1);

        var rows = await h.RunsAsync();
        Assert.DoesNotContain(rows, Eligible);
        Assert.Equal(WorkflowOutcomeReasonCodes.ContinuedAsNew, rows.Single(r => r.RunId == "run-1").ReasonCode);
        // The cron type has no classifier rules: its Completed run is a visible coverage gap, never a success claim.
        Assert.Equal(WorkflowOutcomeReasonCodes.ClassifierMissing, rows.Single(r => r.RunId == "cron-1").ReasonCode);
        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, rows.Single(r => r.RunId == "cron-1").Classification);
        // Successors are Described before any logical-failure claim; a missing one is not a failure.
        Assert.Contains(rows, r => r.RunId == "run-2" && r.PreviousRunId == "run-1");
        Assert.Contains(rows, r => r.RunId == "cron-2");
        await h.CyclesAsync(7);
        var missing = (await h.RunsAsync()).Single(r => r.RunId == "run-2");
        Assert.Equal(WorkflowOutcomeEvidenceStates.Missing, missing.EvidenceState);
        Assert.False(Eligible(missing));
    }

    // ─── catchup ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task Reconcile_RestartAfterPartialPass_ResumesFromDurableCheckpointAfterLeaseExpiry()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 250; i++) Run(h, "wf-" + i, b => b.Failed(), run: "r" + i);
        h.Temporal.FailListCall[Ns] = 4;
        await h.CyclesAsync(1);
        Assert.Equal(100, (await h.RunsAsync()).Count);

        await h.RestartAsync();
        await h.CyclesAsync(1);
        Assert.Null(h.Reconciler.Lease); // the dead process's lease has not expired yet: idle, no writes

        h.Clock.Advance(WorkflowOutcomeStore.LeaseTtl);
        var calls = h.Temporal.ListTokens.Count;
        await h.CyclesAsync(3);

        Assert.NotNull(h.Reconciler.Lease);
        Assert.Equal(250, (await h.RunsAsync()).Count);
        Assert.Contains("100:0", h.Temporal.ListTokens.Skip(calls));
        Assert.All(await h.RunsAsync(), r => Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, r.Classification));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_PartialNamespaceConnect_OthersProceedAndFailedNamespaceCatchesUp()
    {
        await using var h = await ObserverHarness.CreateAsync(Options(null, "namespace-a", "namespace-b", "namespace-c"));
        foreach (var ns in new[] { "namespace-a", "namespace-b", "namespace-c" })
            Run(h, "wf-" + ns, b => b.Failed(), ns: ns);
        h.Temporal.Unavailable.Add("namespace-b");

        await h.CyclesAsync(3);

        var rows = await h.RunsAsync();
        Assert.Contains(rows, r => r.Namespace == "namespace-a" && Eligible(r));
        Assert.Contains(rows, r => r.Namespace == "namespace-c" && Eligible(r));
        Assert.DoesNotContain(rows, r => r.Namespace == "namespace-b");
        Assert.Contains(await h.HealthAsync(), x => x.OutageKey == "namespace_unavailable:namespace-b" && x.State == "open");

        h.Temporal.Unavailable.Clear();
        await h.CyclesAsync(5);

        Assert.Contains(await h.RunsAsync(), r => r.Namespace == "namespace-b" && Eligible(r));
        Assert.Equal("resolved", (await h.HealthAsync()).Single(x => x.OutageKey == "namespace_unavailable:namespace-b").State);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task Reconcile_RunsStartedLongBeforeRetention_InspectedByUnboundedSweep()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var ancient = h.Clock.UtcNow.AddDays(-90);
        Run(h, "wf-old-running", b => b.OpenDelegate("work", "agent1"), start: ancient);
        Run(h, "wf-old-failed", b => b.Failed(), start: ancient.AddDays(1));

        await h.CyclesAsync(1);

        Assert.Equal("Running", (await h.RunAsync(Ns, "wf-old-running", "run-1")).Status);
        Assert.True(Eligible(await h.RunAsync(Ns, "wf-old-failed", "run-1")));
        Assert.Contains(h.Temporal.Queries, q => q.StartsWith("StartTime < ", StringComparison.Ordinal) && !q.Contains(">="));
    }

    // ─── retention ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "retention")]
    public async Task Reconcile_ListedRunWithMissingHistory_DurableGapWithKnownIdNoFailureClaim()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-gone", b => b.Failed()).HistoryDeleted = true;

        await h.CyclesAsync(1);

        var row = await h.RunAsync(Ns, "wf-gone", "run-1");
        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, row.Classification);
        Assert.Equal(WorkflowOutcomeReasonCodes.HistoryIncomplete, row.ReasonCode);
        Assert.Equal(WorkflowOutcomeEvidenceStates.Missing, row.EvidenceState);
        Assert.Null(row.MetadataExpiresAt);
        Assert.False(Eligible(row));
        var gap = Assert.Single(await h.HealthAsync(), x => x.OutageKey == "history_missing:" + Ns);
        Assert.Equal(1, gap.KnownRunCount);
        Assert.Contains("outcome:" + row.Id.ToString("D"), gap.CauseReviewRefsJson);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "retention")]
    public async Task Reconcile_ArchivedHistory_NotFetchedEvidenceMissing()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-arch", b => b.Failed()).Archived = true;

        await h.CyclesAsync(1);

        Assert.Equal(WorkflowOutcomeEvidenceStates.Missing, (await h.RunAsync(Ns, "wf-arch", "run-1")).EvidenceState);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "retention")]
    public async Task Reconcile_KnownRunVanishesBeforeClosureSeen_GapNotFailure()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var exec = Run(h, "wf-v", b => b.OpenDelegate("work", "agent1"));
        await h.CyclesAsync(1);
        exec.HistoryDeleted = true;

        await h.CyclesAsync(7);

        var row = await h.RunAsync(Ns, "wf-v", "run-1");
        Assert.Equal(WorkflowOutcomeEvidenceStates.Missing, row.EvidenceState);
        Assert.False(Eligible(row));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "retention")]
    public async Task Reconcile_OutageLongerThanRetention_UnknownCountIntervalGap()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        h.Temporal.RetentionSeconds[Ns] = 86400;
        var firstPass = h.Clock.UtcNow;
        await h.CyclesAsync(1);

        h.Clock.Advance(TimeSpan.FromDays(3));
        await h.CyclesAsync(1);

        var gap = Assert.Single(await h.HealthAsync(), x => x.OutageKey.StartsWith("retention_overrun:" + Ns, StringComparison.Ordinal));
        Assert.True(gap.UnknownRunCount);
        Assert.Equal(firstPass, gap.GapStart!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(firstPass.AddDays(2).AddSeconds(5), gap.GapEnd!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "retention")]
    public async Task Cleanup_NonfailureMetadataExpiresAfterRetentionPlusScan_FailuresNeverExpire()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        h.Temporal.RetentionSeconds[Ns] = 86400;
        var recent = h.Clock.UtcNow.AddHours(-1);
        var ok = Run(h, "wf-ok", b => b.LoadSeed(Covered).CompletedWith(new { ok = 1 }), start: recent, type: Covered);
        var uncovered = Run(h, "wf-gap", b => b.CompletedWith(new { ok = 1 }), start: recent);
        var bad = Run(h, "wf-bad", b => b.Failed(), start: recent);
        await h.CyclesAsync(1);

        var okRow = await h.RunAsync(Ns, "wf-ok", "run-1");
        Assert.Equal(okRow.CloseTime!.Value.AddSeconds(86400 + 300), okRow.MetadataExpiresAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Null(okRow.PayloadCiphertext); // no raw success payload
        Assert.Null((await h.RunAsync(Ns, "wf-bad", "run-1")).MetadataExpiresAt);

        h.Temporal.Remove(ok);
        h.Temporal.Remove(bad);
        h.Temporal.Remove(uncovered);
        h.Clock.Advance(TimeSpan.FromSeconds(86400 + 400));
        await h.CyclesAsync(1);

        var rows = await h.RunsAsync();
        Assert.DoesNotContain(rows, r => r.WorkflowId == "wf-ok");
        Assert.Contains(rows, r => r.WorkflowId == "wf-bad" && Eligible(r));
        // A coverage gap is never auto-deleted.
        Assert.Contains(rows, r => r.WorkflowId == "wf-gap" && r.Classification == WorkflowOutcomeClassifications.ClassificationPending);
        Assert.Empty(await h.ObservationsAsync(okRow.Id));
    }

    // ─── timing ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "timing")]
    public async Task Reconcile_KnownRunClosure_DetectedWithin60sAndStoredWithin30s()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var b = new HistoryBuilder("SomeWorkflow", "wf-t", start: h.Clock.UtcNow.AddMinutes(-5)).OpenDelegate("work", "agent1");
        var exec = h.Temporal.Add(Ns, "run-1", b);
        await h.CyclesAsync(1);
        Assert.Equal("Running", (await h.RunAsync(Ns, "wf-t", "run-1")).Status);

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        var closedAt = h.Clock.UtcNow;
        h.Temporal.Close(exec, b.At(closedAt).TimedOut());
        var row = await WaitClassifiedAsync(h, "wf-t", "run-1", 20);

        Assert.True(Eligible(row));
        Assert.Equal(closedAt, row.CloseTime!.Value, TimeSpan.FromMilliseconds(1));
        Assert.InRange(row.DetectedAt - closedAt, TimeSpan.Zero, TimeSpan.FromSeconds(60));
        Assert.InRange(row.StoredAt - row.DetectedAt, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "timing")]
    public async Task Reconcile_NewRunVisibleLate_DiscoveredWithin300sOfVisibility()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var exec = Run(h, "wf-late", b => b.Failed(), start: h.Clock.UtcNow.AddMinutes(-2));
        var visibleAt = h.Clock.UtcNow.AddSeconds(25);
        exec.VisibleAt = visibleAt;

        var row = await WaitClassifiedAsync(h, "wf-late", "run-1", 80);

        Assert.True(row.DetectedAt >= visibleAt);
        Assert.InRange(row.DetectedAt - visibleAt, TimeSpan.Zero, TimeSpan.FromSeconds(300));
        Assert.InRange(row.StoredAt - row.DetectedAt, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    // ─── failure matrix ─────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_TemporalOutage_ReconnectBackoff5To60Seconds()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        h.Temporal.Unavailable.Add(Ns);
        var start = h.Clock.UtcNow;

        await h.CyclesAsync(200, TimeSpan.FromSeconds(1));

        var attempts = h.Temporal.Attempts.Where(a => a.Namespace == Ns).Select(a => (int)(a.At - start).TotalSeconds).Distinct().ToArray();
        Assert.Equal([0, 5, 15, 35, 75, 135, 195], attempts);
        Assert.Empty(await h.RunsAsync());
        Assert.Null(await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns)));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_DatabaseFailsMidPass_NoCursorAdvanceRowsSurviveAndCatchUp()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 250; i++) Run(h, "wf-" + i, b => b.Failed(), run: "r" + i);
        var armed = true;
        h.Temporal.OnList = r => { if (armed && !r.NextPageToken.IsEmpty) { armed = false; h.Outage.Down = true; } };

        await h.Reconciler.RunCycleAsync(default); // must not throw
        h.Outage.Down = false;

        var full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.Equal(Convert.ToBase64String("100:0"u8.ToArray()), full!.PageToken);
        Assert.Null(full.PassCompletedAt);
        Assert.Equal(100, (await h.RunsAsync()).Count);

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.CyclesAsync(3);

        Assert.Equal(250, (await h.RunsAsync()).Count);
        Assert.True((await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns)))!.Complete);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_DatabaseDownAtCycleStart_NoWorkNoClaims()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-1", b => b.Failed());
        h.Outage.Down = true;

        await h.Reconciler.RunCycleAsync(default);

        Assert.Null(h.Reconciler.Lease);
        Assert.Equal(0, h.Temporal.ListCalls);
        h.Outage.Down = false;
        Assert.Empty(await h.RunsAsync());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Reconcile_KeyringFailure_FailsClosedRecordKeptThenRecovers()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-k", b => b.Failed("secret-bearing failure text"));
        h.Protector.Fail = true;

        await h.CyclesAsync(1);

        var row = await h.RunAsync(Ns, "wf-k", "run-1");
        Assert.Equal(WorkflowOutcomeClassifications.ConfirmedFailure, row.Classification);
        Assert.Equal(WorkflowOutcomeEvidenceStates.Pending, row.EvidenceState);
        Assert.Null(row.PayloadCiphertext);
        Assert.DoesNotContain("secret-bearing", row.CauseRefsJson + row.ProjectEvidenceJson + row.SourceTaskRefsJson);
        Assert.Contains(await h.HealthAsync(), x => x.Component == WorkflowOutcomeHealthComponents.Keyring && x.State == "open");

        h.Protector.Fail = false;
        await h.CyclesAsync(1);

        row = await h.RunAsync(Ns, "wf-k", "run-1");
        Assert.Equal(WorkflowOutcomeEvidenceStates.Complete, row.EvidenceState);
        Assert.Equal(WorkflowOutcomeStore.PayloadPurpose, row.PayloadKeyPurpose);
        Assert.Contains("secret-bearing", System.Text.Encoding.UTF8.GetString(h.Protector.Unprotect(row.PayloadKeyPurpose!, row.PayloadCiphertext!)));
        Assert.Equal(1, row.Version);
        Assert.Equal("resolved", (await h.HealthAsync()).Single(x => x.Component == WorkflowOutcomeHealthComponents.Keyring).State);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_LeaseHeldByLiveOwner_IdleUntilExpiryThenFencedTakeover()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-1", b => b.Failed());
        await h.DbAsync(async db =>
        {
            db.WorkflowOutcomeCheckpoints.Add(new WorkflowOutcomeCheckpoint
            {
                Namespace = "", WindowKind = "lease", WindowKey = "lease", QueryHash = "", LeaseOwner = "other-instance",
                LeaseEpoch = 7, LeaseExpiresAt = h.Clock.UtcNow.AddSeconds(60), UpdatedAt = h.Clock.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        await h.CyclesAsync(1);
        Assert.Null(h.Reconciler.Lease);
        Assert.Empty(await h.RunsAsync());
        Assert.Equal(0, h.Temporal.ListCalls);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.CyclesAsync(1);

        Assert.Equal(8, h.Reconciler.Lease!.Epoch);
        Assert.Single(await h.RunsAsync());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_LeaseLostMidPass_StopsNewWorkWithoutAdvancingCursor()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 250; i++) Run(h, "wf-" + i, b => b.Failed(), run: "r" + i);
        var armed = true;
        h.Temporal.OnList = r =>
        {
            if (!armed || r.NextPageToken.IsEmpty) return;
            armed = false;
            h.DbAsync(async db =>
            {
                var lease = await db.WorkflowOutcomeCheckpoints.SingleAsync(c => c.WindowKey == "lease");
                lease.LeaseOwner = "other-instance";
                lease.LeaseEpoch++;
                return await db.SaveChangesAsync();
            }).GetAwaiter().GetResult();
        };

        await h.Reconciler.RunCycleAsync(default);

        Assert.Null(h.Reconciler.Lease);
        Assert.Equal(100, (await h.RunsAsync()).Count);
        var full = await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns));
        Assert.Equal(Convert.ToBase64String("100:0"u8.ToArray()), full!.PageToken);
        Assert.False(full.Complete);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Initialize_InvalidPolicyFiles_StartupFailure()
    {
        var dir = Directory.CreateTempSubdirectory("wo-policy-");
        var badClassifiers = Path.Combine(dir.FullName, "classifiers.json");
        await File.WriteAllTextAsync(badClassifiers, "{\"schemaVersion\":1,\"rules\":[],\"catchAll\":true}");
        var inactiveOwners = Path.Combine(dir.FullName, "ownership.json");
        await File.WriteAllTextAsync(inactiveOwners,
            "{\"schemaVersion\":1,\"defaultCoordinator\":\"coordinator\",\"repoProjects\":[],\"namespaceProjects\":[{\"namespace\":\"namespace-a\",\"project\":\"project-old\"}],\"rules\":[]}");

        foreach (var configure in new Action<WorkflowOutcomeOptions>[]
        {
            o => o.ClassifiersFile = badClassifiers,
            o => o.OwnersFile = inactiveOwners,
            o => o.OwnersFile = Path.Combine(dir.FullName, "missing.json"),
            o => o.ClassifiersFile = dir.FullName, // a missing file bind mount becomes a directory
        })
        {
            await using var h = await ObserverHarness.CreateAsync(Options(configure), initialize: false);
            await Assert.ThrowsAsync<WorkflowOutcomePolicyException>(() => h.Reconciler.InitializeAsync(default));
            Assert.NotEmpty(await WorkflowOutcomeReconciler.ValidateStartupAsync(h.Services, default));
        }
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Initialize_InvalidOptions_FeatureUnavailableWithoutThrowing()
    {
        await using var h = await ObserverHarness.CreateAsync(Options(o => o.Namespaces = " , "), initialize: false);

        Assert.False(await h.Reconciler.InitializeAsync(default));
        Assert.Contains("WorkflowOutcomes:Namespaces is required", await WorkflowOutcomeReconciler.ValidateStartupAsync(h.Services, default));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Execute_Disabled_ReturnsWithoutResolvingStoreDbOrKeyring()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<WorkflowOutcomeOptions>>(Microsoft.Extensions.Options.Options.Create(new WorkflowOutcomeOptions()));
        services.AddWorkflowOutcomeObserver();
        await using var sp = services.BuildServiceProvider();
        var hosted = Assert.Single(sp.GetServices<IHostedService>());

        await hosted.StartAsync(default);
        var reconciler = Assert.IsType<WorkflowOutcomeReconciler>(hosted);
        await reconciler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await hosted.StopAsync(default);

        Assert.True(reconciler.ExecuteTask.IsCompletedSuccessfully);
        Assert.Empty(await WorkflowOutcomeReconciler.ValidateStartupAsync(sp, default));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Reconcile_HistoryReads_AtMostMaxConcurrentReadsInFlight()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 40; i++) Run(h, "wf-" + i, b => b.Failed(), run: "r" + i);
        h.Temporal.HistoryDelay = TimeSpan.FromMilliseconds(20);

        await h.CyclesAsync(1);

        Assert.Equal(40, (await h.RunsAsync()).Count(Eligible));
        Assert.InRange(h.Temporal.MaxInFlight, 2, 4);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task Reconcile_MoreThanOneBatchOfRunningRuns_EveryKnownRunDescribed()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        for (var i = 0; i < 1100; i++) Run(h, "wf-" + i, b => b, run: "r" + i);

        await h.CyclesAsync(1);

        Assert.Equal(1100, (await h.RunsAsync()).Count(r => r.Status == "Running"));
        Assert.Equal(1100, h.Temporal.DescribeCalls);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "timing")]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task Reconcile_FullSweepLongerThan300s_CapacityDegradedThenResolved()
    {
        await using var h = await ObserverHarness.CreateAsync(Options(o => o.ListPageSize = 1));
        for (var i = 0; i < 60; i++) Run(h, "wf-" + i, b => b.CompletedWith(new { ok = i }), run: "r" + i);

        await h.CyclesAsync(1, TimeSpan.FromSeconds(301)); // 50 pages per cycle: the pass spans cycles
        await h.CyclesAsync(1);

        var capacity = Assert.Single(await h.HealthAsync(), x => x.Component == WorkflowOutcomeHealthComponents.Capacity);
        Assert.Equal("open", capacity.State);
        Assert.True((await h.CheckpointAsync(WorkflowOutcomeStore.FullKey(Ns)))!.Complete);

        foreach (var run in h.Temporal.Runs.Skip(5).ToList()) h.Temporal.Remove(run);
        h.Clock.Advance(TimeSpan.FromSeconds(300));
        await h.CyclesAsync(1);
        Assert.Equal("resolved", (await h.HealthAsync()).Single(x => x.Component == WorkflowOutcomeHealthComponents.Capacity).State);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Reconcile_Logs_IdsAndStatesOnlyNeverPayloads()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-p", b => b.Delegate("delegate", "agent1", "PRIVATE-RESULT-TEXT", "failed").Failed("PRIVATE-FAILURE-TEXT"),
            input: new { Repo = "owner/repo", TaskDescription = "PRIVATE-INPUT-TEXT" });
        Run(h, "wf-q", b => b.CompletedWith(new { secret = "PRIVATE-SUCCESS-PAYLOAD" }));

        await h.CyclesAsync(2);

        Assert.Contains(h.Logs.Lines, l => l.Message.StartsWith("workflow_outcome_stored", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs.Lines, l => l.Message.Contains("PRIVATE-", StringComparison.Ordinal));
        var row = await h.RunAsync(Ns, "wf-q", "run-1");
        Assert.DoesNotContain("PRIVATE", row.CauseRefsJson + row.ProjectEvidenceJson + row.SourceTaskRefsJson);
        Assert.Null(row.PayloadCiphertext);
    }

    // ─── recursion / kill / ownership end to end ────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recursion")]
    public async Task Reconcile_200SuccessfulVoidSystemRunsAnd200InfraFailures_OnlyFailuresEligible()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        string[] types = ["NotifyCtoWorkflow", "MemoryStoreRequestWorkflow", "AuthTokenRefreshWorkflow"];
        for (var i = 0; i < 200; i++)
        {
            var n = i;
            Run(h, "ok-" + n, b => (n % 3) switch { 0 => b.Completed(null), 1 => b.Completed(HistoryBuilder.Null()), _ => b.CompletedWith(new { ok = true }) },
                type: types[n % 3], start: Old.AddSeconds(n));
            Run(h, "bad-" + n, b => n % 2 == 0 ? b.Failed("provider down") : b.TimedOut(), type: types[n % 3], start: Old.AddSeconds(n));
        }

        await h.CyclesAsync(4);

        var rows = await h.RunsAsync();
        Assert.Equal(400, rows.Count(r => r.TerminalEventId is not null));
        Assert.Equal(200, rows.Count(Eligible));
        Assert.All(rows.Where(r => r.WorkflowId.StartsWith("ok-")), r =>
        {
            // No rules for these system types: a visible coverage gap, no inbox and no stored payload.
            Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, r.Classification);
            Assert.False(Eligible(r));
            Assert.Null(r.PayloadCiphertext);
        });
        Assert.All(rows.Where(r => r.WorkflowId.StartsWith("bad-")), r => Assert.True(Eligible(r)));
        // The observer has read RPCs only: nothing it does can start a notification workflow.
        Assert.DoesNotContain(typeof(IWorkflowOutcomeRpc).GetMethods(), m => m.Name.Contains("Start") || m.Name.Contains("Signal") || m.Name.Contains("Cancel"));
        var health = await h.HealthAsync();
        Assert.Contains(health, x => x.State == "open" && x.Component == WorkflowOutcomeHealthComponents.Coverage && x.OutageKey.StartsWith("classifier_missing:"));
        Assert.DoesNotContain(health, x => x.State == "open" && x.Component != WorkflowOutcomeHealthComponents.Capacity && x.Component != WorkflowOutcomeHealthComponents.Coverage);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public async Task Store_OperatorAttestation_AuthoritativeAndNeverOverwritten()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var b = new HistoryBuilder("SomeWorkflow", "wf-kill", start: Old).OpenDelegate("work", "agent1").Terminated();
        h.Temporal.Add(Ns, "run-1", b);
        await h.CyclesAsync(1);
        var row = await h.RunAsync(Ns, "wf-kill", "run-1");
        Assert.Equal(WorkflowOutcomeClassifications.CauseUnresolved, row.Classification);
        Assert.False(Eligible(row));

        // The operator classification API (another component) promotes the run and records the attestation.
        await h.DbAsync(async db =>
        {
            var r = await db.WorkflowOutcomeRuns.SingleAsync(x => x.Id == row.Id);
            r.Classification = WorkflowOutcomeClassifications.ConfirmedFailure;
            r.ReasonCode = WorkflowOutcomeReasonCodes.FailureCausedTerminate;
            r.Version = 2;
            db.WorkflowOutcomeObservations.Add(new WorkflowOutcomeObservation
            {
                RunRowId = r.Id, EventId = r.TerminalEventId!.Value, EventType = WorkflowOutcomeStore.OperatorClassificationEvent,
                EventHash = new string('b', 64), Status = r.Status, Classification = r.Classification, ReasonCode = r.ReasonCode, ObservedAt = h.Clock.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
        var (facts, verdict, owner) = await Classified(h, b);

        var result = await h.Store.StoreOutcomeAsync(h.Reconciler.Lease!, facts, verdict, owner, 604800, default);

        Assert.Equal(WorkflowOutcomeStoreOutcome.OperatorAuthoritative, result.Outcome);
        row = await h.RunAsync(Ns, "wf-kill", "run-1");
        Assert.Equal((WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.FailureCausedTerminate, 2),
            (row.Classification, row.ReasonCode, row.Version));
        Assert.True(Eligible(row));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public async Task Reconcile_WorkerOutageTimeout_DetectedWithoutFinalActivityOwnedByTask()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        var b = new HistoryBuilder("UwePrImplementationWorkflow", "wf-wo", new { Repo = "owner/repo" }, start: Old)
            .LoadSeed("UwePrImplementationWorkflow").OpenDelegate("phase1_implement", "agent1");
        var exec = h.Temporal.Add(Ns, "run-1", b);
        await h.CyclesAsync(1);

        h.Temporal.Close(exec, b.At(h.Clock.UtcNow).TimedOut());
        var row = await WaitClassifiedAsync(h, "wf-wo", "run-1");

        Assert.Equal(WorkflowOutcomeReasonCodes.WorkflowTimedOut, row.ReasonCode);
        Assert.Equal("agent1", row.TaskOwner);
        Assert.Equal("project-a", row.ProjectName);
        Assert.Equal(OutcomeFixtures.LoadedHash("UwePrImplementationWorkflow"), row.DefinitionHash);
        Assert.True(Eligible(row));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public async Task Reconcile_FailedChildWithoutRepo_InheritsVerifiedRunningParentProject()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "parent", b => b.OpenDelegate("work", "agent1"), run: "parent-run", input: new { Repo = "owner/repo" });
        Run(h, "child", b => b.Failed(), run: "child-run", input: new { Project = "project-b" },
            started: a => a.ParentWorkflowExecution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = "parent", RunId = "parent-run" });
        Run(h, "abandoned", b => b.Failed(), run: "abandoned-run",
            started: a => a.ParentWorkflowExecution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = "gone", RunId = "gone-run" });

        await h.CyclesAsync(1);

        var child = await h.RunAsync(Ns, "child", "child-run");
        Assert.Equal("project-a", child.ProjectName);
        Assert.Contains("\"source\":\"parent\"", child.ProjectEvidenceJson);
        Assert.Equal(("parent", "parent-run"), (child.ParentWorkflowId, child.ParentRunId));
        Assert.True(Eligible(child));
        var orphan = await h.RunAsync(Ns, "abandoned", "abandoned-run");
        Assert.Null(orphan.ProjectName);
        Assert.Equal(ObserverHarness.Coordinator, orphan.Coordinator);
        Assert.True(Eligible(orphan));
        Assert.Equal("project-a", (await h.RunAsync(Ns, "parent", "parent-run")).ProjectName);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    [Trait("WorkflowOutcomeSuite", "coordinator")]
    public async Task Reconcile_ExactCoordinatorRule_FailureOwnedByRuleCoordinator()
    {
        await using var h = await ObserverHarness.CreateAsync(Options(null, "namespace-b"));
        Run(h, "wf-b", b => b.Failed(), ns: "namespace-b", input: new { Repo = "owner/other" });

        await h.CyclesAsync(1);

        var row = await h.RunAsync("namespace-b", "wf-b", "run-1");
        Assert.Equal(("project-b", "coordinator-b"), (row.ProjectName, row.Coordinator));
        Assert.False(Eligible(row)); // a different coordinator's failure never enters this coordinator's inbox
        Assert.True(WorkflowOutcomeClassifier.IsEnqueueEligible(row, "coordinator-b"));
    }

    // ─── identity ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    public async Task Reconcile_EmptyRunIdOrHashCollision_QuarantinedNeverMerged()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-empty", b => b.Failed(), run: "");
        Run(h, "wf-c", b => b.Failed(), run: "run-c");
        var collided = new WorkflowOutcomeRun
        {
            Id = Guid.NewGuid(), Namespace = Ns, WorkflowId = "wf-other", RunId = "run-other",
            IdentityHash = WorkflowOutcomeHashes.IdentityHash(Ns, "wf-c", "run-c"), WorkflowType = "Other", Status = "Completed",
            Classification = WorkflowOutcomeClassifications.ExcludedNonfailure, EvidenceState = WorkflowOutcomeEvidenceStates.Complete,
            TerminalEventId = 2, TerminalEventType = WorkflowOutcomeTerminalTypes.Completed, ReasonCode = WorkflowOutcomeReasonCodes.NormalCompletion,
        };
        await h.DbAsync(async db => { db.WorkflowOutcomeRuns.Add(collided); return await db.SaveChangesAsync(); });

        await h.CyclesAsync(2);

        var rows = await h.RunsAsync();
        var empty = rows.Single(r => r.WorkflowId == "wf-empty");
        Assert.True(empty.Quarantined);
        Assert.Equal(WorkflowOutcomeReasonCodes.CorrelationUnverified, empty.ReasonCode);
        Assert.Null(empty.TerminalEventId);
        Assert.False(Eligible(empty));
        var kept = rows.Single(r => r.Id == collided.Id);
        Assert.Equal(("wf-other", WorkflowOutcomeClassifications.ExcludedNonfailure), (kept.WorkflowId, kept.Classification));
        Assert.DoesNotContain(rows, r => r.WorkflowId == "wf-c");
        Assert.Contains(await h.HealthAsync(), x => x.OutageKey == "identity_collision:" + Ns);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public async Task Reconcile_BusinessFailureAndPinnedGap_StoredWithClassifierAndCoverageIncident()
    {
        await using var h = await ObserverHarness.CreateAsync(Options());
        Run(h, "wf-cap", b => b.LoadSeed("UwePrImplementationWorkflow").Delegate("notify_consensus_exhausted", "coordinator", "posted").CompletedWith(null),
            type: "UwePrImplementationWorkflow", input: new { Repo = "owner/repo", IssueNumber = 12 });
        Run(h, "wf-gap", b => b.LoadDefinition("{\"Name\":\"UwePrImplementationWorkflow\",\"Version\":9}"u8.ToArray()).CompletedWith(new { ok = 1 }),
            type: "UwePrImplementationWorkflow");

        await h.CyclesAsync(1);

        var cap = await h.RunAsync(Ns, "wf-cap", "run-1");
        Assert.Equal((WorkflowOutcomeReasonCodes.BusinessReviewCap, "uwe-pr-consensus-exhausted"), (cap.ReasonCode, cap.ClassifierId));
        Assert.Equal(12, cap.IssueNumber);
        Assert.True(Eligible(cap));
        Assert.NotNull(cap.PayloadCiphertext);
        var gap = await h.RunAsync(Ns, "wf-gap", "run-1");
        Assert.Equal(WorkflowOutcomeClassifications.ClassificationPending, gap.Classification);
        Assert.NotNull(gap.ClassificationPendingSince);
        Assert.Contains(await h.HealthAsync(), x => x.OutageKey == "classifier_missing:" + Ns && x.State == "open");

        h.Clock.Advance(TimeSpan.FromSeconds(301));
        await h.CyclesAsync(1);
        var review = Assert.Single(await h.HealthAsync(), x => x.OutageKey == "cause_unresolved:" + Ns);
        Assert.Equal(1, review.KnownRunCount);
    }
}
