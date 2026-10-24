using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>The definition snapshot the fixture LoadWorkflowDefinition activity returns.</summary>
public sealed record FixtureDefinition(string Name, string Namespace, string TaskQueue, JsonElement Root, int Version);

/// <summary>Same shape the engine's DelegateToAgent returns (Fleet.Temporal AgentTaskResult).</summary>
public sealed record FixtureAgentResult(string Text, string Status);

public sealed class FixtureActivities
{
    public static FixtureDefinition Definition(string type) =>
        new(type, "fleet", "outcomes", JsonDocument.Parse("{\"type\":\"sequence\",\"steps\":[]}").RootElement.Clone(), 3);

    [Activity("LoadWorkflowDefinition")]
    public FixtureDefinition LoadWorkflowDefinition(string type) => Definition(type);

    [Activity("DelegateToAgent")]
    public FixtureAgentResult DelegateToAgent(string target, string instruction, string taskId, bool retryOnIncomplete, int maxIncompleteRetries) =>
        instruction == "FAIL" ? new FixtureAgentResult("could not finish", "failed") : new FixtureAgentResult("done by " + target, "completed");
}

/// <summary>One dynamic workflow standing in for UWE and compiled types; the input's Mode picks the exit.</summary>
[Workflow(Dynamic = true)]
public sealed class FixtureWorkflow
{
    private static readonly ActivityOptions Quick = new() { StartToCloseTimeout = TimeSpan.FromSeconds(20) };

    [WorkflowRun]
    public async Task<object?> RunAsync(IRawValue[] args)
    {
        var input = Workflow.PayloadConverter.ToValue<JsonElement>(args[0]);
        var mode = input.GetProperty("Mode").GetString();
        var id = Workflow.Info.WorkflowId;
        switch (mode)
        {
            case "fail":
                throw new ApplicationFailureException("fixture infrastructure failure", errorType: "InfraError", nonRetryable: true);
            case "fail-later":
                await Workflow.DelayAsync(TimeSpan.FromSeconds(3));
                throw new ApplicationFailureException("abandoned child failed", errorType: "InfraError", nonRetryable: true);
            case "fail-first-attempt":
                if (Workflow.Info.Attempt == 1) throw new ApplicationFailureException("transient", errorType: "Transient");
                return new Dictionary<string, object> { ["ok"] = true };
            case "hang":
                await Workflow.DelayAsync(TimeSpan.FromHours(1));
                return null;
            case "result":
                return new Dictionary<string, object> { ["ok"] = true };
            case "null":
                return null;
            case "can":
                throw Workflow.CreateContinueAsNewException(Workflow.Info.WorkflowType,
                    [new Dictionary<string, object> { ["Mode"] = "result" }]);
            case "uwe-review-cap":
                await Workflow.ExecuteActivityAsync<JsonElement>("LoadWorkflowDefinition", [Workflow.Info.WorkflowType], Quick);
                await Workflow.ExecuteActivityAsync<FixtureAgentResult>("DelegateToAgent", ["agent1", "implement", id + "/phase1_implement", false, 0], Quick);
                await Workflow.ExecuteActivityAsync<FixtureAgentResult>("DelegateToAgent", ["coordinator", "notify", id + "/notify_consensus_exhausted", false, 0], Quick);
                return null;
            case "compiled-notify-failed":
                // No LoadWorkflowDefinition: a compiled workflow taking its notify_failed exit.
                await Workflow.ExecuteActivityAsync<FixtureAgentResult>("DelegateToAgent", ["agent1", "notify", id + "/notify_failed", false, 0], Quick);
                return new Dictionary<string, object> { ["ok"] = false };
            case "task-failed":
                await Workflow.ExecuteActivityAsync<JsonElement>("LoadWorkflowDefinition", [Workflow.Info.WorkflowType], Quick);
                await Workflow.ExecuteActivityAsync<FixtureAgentResult>("DelegateToAgent", ["agent1", "FAIL", id + "/delegate", true, 3], Quick);
                var notice = await Workflow.ExecuteActivityAsync<FixtureAgentResult>("DelegateToAgent", ["coordinator", "notify", id + "/notify-result", false, 0], Quick);
                return notice.Text;
            case "parent":
                var child = await Workflow.StartChildWorkflowAsync("ChildWorkflow", [new Dictionary<string, object> { ["Mode"] = "fail" }],
                    new ChildWorkflowOptions { Id = id + "-child" });
                await Workflow.StartChildWorkflowAsync("ChildWorkflow", [new Dictionary<string, object> { ["Mode"] = "fail-later" }],
                    new ChildWorkflowOptions { Id = id + "-abandoned", ParentClosePolicy = ParentClosePolicy.Abandon });
                try { await child.GetResultAsync<object?>(); }
                catch (ChildWorkflowFailureException) { /* consumed by the parent: the child's failure must still be covered */ }
                return new Dictionary<string, object> { ["handled"] = true };
            default:
                throw new ApplicationFailureException("unknown mode", nonRetryable: true);
        }
    }
}

/// <summary>
/// The observer against a real Temporal dev server with three namespaces: real histories, real
/// payload encodings, real visibility queries and page tokens, the production RPC seam and the
/// production TemporalClientRegistry — no fakes between Temporal and the store.
/// </summary>
public sealed class WorkflowOutcomeRealTemporalTests
{
    private const string Queue = "outcomes";
    private static readonly string[] Namespaces = ["namespace-a", "namespace-b", "namespace-c"];

    private static string ClassifierPolicy()
    {
        static string Hash(string type) => WorkflowOutcomeHashes.Sha256Hex(JsonSerializer.SerializeToUtf8Bytes(FixtureActivities.Definition(type)));
        var commit = OutcomeFixtures.SourceCommit;
        return "{\"schemaVersion\":1,\"rules\":[" +
            "{\"id\":\"real-review-cap\",\"workflowType\":\"UwePrImplementationWorkflow\",\"sourceCommit\":\"" + commit + "\",\"definitionHash\":\"" + Hash("UwePrImplementationWorkflow") +
            "\",\"terminalEventTypes\":[\"WorkflowExecutionCompleted\"],\"evidence\":{\"kind\":\"failure_branch\",\"exitBranch\":\"notify_consensus_exhausted\"},\"classification\":\"confirmed_failure\",\"reasonCode\":\"business_review_cap\"}," +
            "{\"id\":\"real-task-failed\",\"workflowType\":\"TaskDelegationWorkflow\",\"sourceCommit\":\"" + commit + "\",\"definitionHash\":\"" + Hash("TaskDelegationWorkflow") +
            "\",\"terminalEventTypes\":[\"WorkflowExecutionCompleted\"],\"evidence\":{\"kind\":\"delegate_result\",\"exitBranch\":\"delegate\",\"path\":\"/Status\",\"equals\":\"failed\"},\"classification\":\"confirmed_failure\",\"reasonCode\":\"business_task_failed\"}]}";
    }

    /// <summary>
    /// A compiled rule against real histories: the run whose worker build id is the rule's commit is
    /// classified by it; the same workflow on a worker with the SDK default build id stays pending.
    /// </summary>
    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public async Task RealTemporal_CompiledRule_AppliesOnlyToRunsWhoseWorkersRecordedItsCommit()
    {
        // No skip: if the dev server cannot be downloaded/started the test fails loudly.
        await using var env = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions
        {
            DevServerOptions = new DevServerOptions { LogLevel = "error" },
        });
        var policy = WorkflowOutcomeClassifierPolicy.Parse(System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"rules\":[{\"id\":\"real-compiled\",\"workflowType\":\"SomeCompiledWorkflow\",\"sourceCommit\":\"" + OutcomeFixtures.SourceCommit +
            "\",\"terminalEventTypes\":[\"WorkflowExecutionCompleted\"],\"evidence\":{\"kind\":\"failure_branch\",\"exitBranch\":\"notify_failed\"}," +
            "\"classification\":\"confirmed_failure\",\"reasonCode\":\"business_task_failed\"}]}"));

        async Task<(WorkflowOutcomeRunFacts Facts, WorkflowOutcomeVerdict Verdict)> RunOnAsync(string queue, string? buildId)
        {
            var options = new TemporalWorkerOptions(queue).AddWorkflow<FixtureWorkflow>().AddAllActivities(new FixtureActivities());
#pragma warning disable CS0618 // the bridge's own stamping (WorkerSourceCommit) sets exactly this
            if (buildId is not null) options.BuildId = buildId;
#pragma warning restore CS0618
            using var worker = new TemporalWorker(env.Client, options);
            return await worker.ExecuteAsync(async () =>
            {
                var handle = await env.Client.StartWorkflowAsync("SomeCompiledWorkflow",
                    [new Dictionary<string, object> { ["Mode"] = "compiled-notify-failed" }], new WorkflowOptions(queue, queue));
                await handle.GetResultAsync();
                var events = new List<Temporalio.Api.History.V1.HistoryEvent>();
                await foreach (var e in handle.FetchHistoryEventsAsync()) events.Add(e);
                var facts = WorkflowOutcomeFactsBuilder.FromEvents(env.Client.Options.Namespace, queue, handle.ResultRunId ?? "", events);
                return (facts, WorkflowOutcomeClassifier.Classify(facts, policy));
            });
        }

        var verified = await RunOnAsync("compiled-at-commit", OutcomeFixtures.SourceCommit);
        var unverified = await RunOnAsync("compiled-default-build", null);

        Assert.True(verified.Facts.HistoryComplete);
        Assert.False(verified.Facts.DefinitionScheduled);
        Assert.Equal(OutcomeFixtures.SourceCommit, verified.Facts.WorkerBuildId);
        Assert.Equal((WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.BusinessTaskFailed, "real-compiled"),
            (verified.Verdict.Classification, verified.Verdict.ReasonCode, verified.Verdict.ClassifierId));

        Assert.True(unverified.Facts.HistoryComplete);
        Assert.NotEqual(OutcomeFixtures.SourceCommit, unverified.Facts.WorkerBuildId);
        Assert.Equal((WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierMissing, WorkflowOutcomeClassifier.SourceUnverifiedGap),
            (unverified.Verdict.Classification, unverified.Verdict.ReasonCode, unverified.Verdict.GapReason));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [Trait("WorkflowOutcomeSuite", "kill")]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task RealTemporal_ThreeNamespaces_ExactRowsFailuresEligibleSuccessesNot()
    {
        // No skip: if the dev server cannot be downloaded/started the test fails loudly.
        await using var env = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions
        {
            Namespace = Namespaces[0],
            DevServerOptions = new DevServerOptions { ExtraArgs = ["--namespace", Namespaces[1], "--namespace", Namespaces[2]], LogLevel = "error" },
        });
        var target = env.Client.Connection.Options.TargetHost!;
        var clients = Namespaces.ToDictionary(ns => ns, ns => (ITemporalClient)new TemporalClient(env.Client.Connection, new TemporalClientOptions { Namespace = ns }));

        using var stop = new CancellationTokenSource();
        var workers = clients.Values.Select(c => new TemporalWorker(c, new TemporalWorkerOptions(Queue)
            .AddWorkflow<FixtureWorkflow>().AddAllActivities(new FixtureActivities()))).ToList();
        var running = workers.Select(w => w.ExecuteAsync(stop.Token)).ToList();
        try
        {
            static Dictionary<string, object> In(string mode, string? repo = null) =>
                repo is null ? new() { ["Mode"] = mode } : new() { ["Mode"] = mode, ["Repo"] = repo };
            async Task<WorkflowHandle> Start(string ns, string type, string id, Dictionary<string, object> input, Action<WorkflowOptions>? configure = null)
            {
                var options = new WorkflowOptions(id, Queue);
                configure?.Invoke(options);
                return await clients[ns].StartWorkflowAsync(type, [input], options);
            }

            // namespace-a: Failed, run-timeout TimedOut, Completed with/without result, UWE business failure.
            await Start("namespace-a", "SomeCompiledWorkflow", "a-failed", In("fail"));
            await Start("namespace-a", "SomeCompiledWorkflow", "a-timeout", In("hang"), o => o.RunTimeout = TimeSpan.FromSeconds(3));
            await Start("namespace-a", "NotifyCtoWorkflow", "a-result", In("result"));
            await Start("namespace-a", "MemoryStoreRequestWorkflow", "a-null", In("null"));
            await Start("namespace-a", "UwePrImplementationWorkflow", "a-review-cap", In("uwe-review-cap", "owner/repo"));
            // namespace-b: Canceled, Terminated (free-text reason), continue-as-new chain, typed failed task.
            var canceled = await Start("namespace-b", "SomeCompiledWorkflow", "b-canceled", In("hang"));
            var terminated = await Start("namespace-b", "SomeCompiledWorkflow", "b-terminated", In("hang"));
            await Start("namespace-b", "UweCarteBlancheWorkflow", "b-can", In("can"));
            await Start("namespace-b", "TaskDelegationWorkflow", "b-task-failed", In("task-failed", "owner/other"));
            // namespace-c: consumed failed child + abandoned child, retry then success, scheduled (cron) runs.
            await Start("namespace-c", "ParentWorkflow", "c-parent", In("parent"));
            await Start("namespace-c", "RetryWorkflow", "c-retry", In("fail-first-attempt"),
                o => o.RetryPolicy = new RetryPolicy { MaximumAttempts = 2, InitialInterval = TimeSpan.FromSeconds(1) });
            var cron = await Start("namespace-c", "ScheduledReportWorkflow", "c-cron", In("result"), o => o.CronSchedule = "@every 2s");

            await Task.Delay(TimeSpan.FromSeconds(2));
            await canceled.CancelAsync();
            await terminated.TerminateAsync("failure: agent crashed, terminating");
            // Let the abandoned child, the run timeout, the retry and two cron runs close.
            await Task.Delay(TimeSpan.FromSeconds(7));
            await cron.TerminateAsync("cleanup");

            // The observer: production registry + RPC seam, SQLite store, manual clock started at real time.
            var registry = new TemporalClientRegistry(Options.Create(new TemporalOptions { Address = target, Namespaces = [.. Namespaces] }),
                NullLogger<TemporalClientRegistry>.Instance);
            var dir = Directory.CreateTempSubdirectory("wo-real-");
            var classifiers = Path.Combine(dir.FullName, "classifiers.json");
            await File.WriteAllTextAsync(classifiers, ClassifierPolicy());
            var options = ObserverHarness.DefaultOptions(Namespaces);
            options.ClassifiersFile = classifiers;
            await using var h = await ObserverHarness.CreateAsync(options, DateTimeOffset.UtcNow,
                harness => harness.RpcOverride = new TemporalWorkflowOutcomeRpc(registry, Options.Create(options)));

            string[] expectedIds = ["a-failed", "a-timeout", "a-result", "a-null", "a-review-cap", "b-canceled", "b-terminated", "b-can",
                "b-task-failed", "c-parent", "c-parent-child", "c-parent-abandoned", "c-retry", "c-cron"];
            List<WorkflowOutcomeRun> rows = [];
            for (var i = 0; i < 40; i++)
            {
                await h.Reconciler.RunCycleAsync(default);
                h.Clock.Advance(TimeSpan.FromSeconds(61));
                rows = await h.RunsAsync();
                var closedIds = rows.Where(r => r.TerminalEventId is not null).Select(r => r.WorkflowId).ToHashSet();
                if (expectedIds.All(closedIds.Contains) && rows.Count(r => r.WorkflowId is "b-can" or "c-retry" && r.TerminalEventId is not null) >= 4
                    && rows.Where(r => r.WorkflowId != "c-cron").All(r => r.TerminalEventId is not null))
                    break;
                await Task.Delay(500);
            }

            WorkflowOutcomeRun One(string id, string? status = null) => Assert.Single(rows, r => r.WorkflowId == id && (status is null || r.Status == status));
            bool Eligible(WorkflowOutcomeRun r) => WorkflowOutcomeClassifier.IsEnqueueEligible(r, ObserverHarness.Coordinator);
            void Is(WorkflowOutcomeRun r, string classification, string reason, bool eligible)
            {
                Assert.Equal((r.WorkflowId, classification, reason), (r.WorkflowId, r.Classification, r.ReasonCode));
                Assert.Equal((r.WorkflowId, eligible), (r.WorkflowId, Eligible(r)));
            }

            Is(One("a-failed"), WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowFailed, true);
            Is(One("a-timeout"), WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowTimedOut, true);
            // These compiled test types have no classifier rules: a Completed run is a coverage gap, not a success claim.
            Is(One("a-result"), WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierUncoveredType, false);
            Is(One("a-null"), WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierUncoveredType, false);
            var cap = One("a-review-cap");
            Is(cap, WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.BusinessReviewCap, true);
            Assert.Equal(("project-a", "real-review-cap", 3), (cap.ProjectName, cap.ClassifierId, cap.DefinitionVersion));
            Is(One("b-canceled"), WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeReasonCodes.AbortCauseUnknown, false);
            Is(One("b-terminated"), WorkflowOutcomeClassifications.CauseUnresolved, WorkflowOutcomeReasonCodes.AbortCauseUnknown, false);
            Is(One("b-can", "ContinuedAsNew"), WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.ContinuedAsNew, false);
            Is(One("b-can", "Completed"), WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierUncoveredType, false);
            Assert.Equal(One("b-can", "ContinuedAsNew").NextRunId, One("b-can", "Completed").RunId);
            var task = One("b-task-failed");
            // Owned by the exact namespace+project rule's coordinator, so it enters only that coordinator's inbox.
            Is(task, WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.BusinessTaskFailed, false);
            Assert.Equal(("agent1", "project-b", "coordinator-b"), (task.TaskOwner, task.ProjectName, task.Coordinator));
            Assert.True(WorkflowOutcomeClassifier.IsEnqueueEligible(task, "coordinator-b"));
            Is(One("c-parent"), WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierUncoveredType, false);
            var child = One("c-parent-child");
            Is(child, WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowFailed, true);
            Assert.Equal(("c-parent", "project-c"), (child.ParentWorkflowId, child.ProjectName));
            var abandoned = One("c-parent-abandoned");
            Is(abandoned, WorkflowOutcomeClassifications.ConfirmedFailure, WorkflowOutcomeReasonCodes.WorkflowFailed, true);
            Assert.Equal("c-parent", abandoned.ParentWorkflowId);
            var firstAttempt = One("c-retry", "Failed");
            // The retry closed Completed (no failure evidence) under an uncovered type: retry succeeded, gap per type.
            Is(firstAttempt, WorkflowOutcomeClassifications.ExcludedNonfailure, WorkflowOutcomeReasonCodes.RetrySucceeded, false);
            Assert.Equal(2, firstAttempt.Version);
            Is(One("c-retry", "Completed"), WorkflowOutcomeClassifications.ClassificationPending, WorkflowOutcomeReasonCodes.ClassifierUncoveredType, false);
            var cronRuns = rows.Where(r => r.WorkflowId == "c-cron").ToList();
            Assert.Contains(cronRuns, r => r.Status == "Completed" && r.Classification == WorkflowOutcomeClassifications.ClassificationPending);
            Assert.DoesNotContain(cronRuns, Eligible);

            // Exactly the proven failures are enqueue-eligible; every success and unresolved abort is inbox-ineligible.
            Assert.Equal(["a-failed", "a-review-cap", "a-timeout", "c-parent-abandoned", "c-parent-child"],
                rows.Where(Eligible).Select(r => r.WorkflowId).Order(StringComparer.Ordinal));

            // The stored terminal hash is the SHA-256 of the exact recorded closing event.
            var history = await clients["namespace-a"].GetWorkflowHandle("a-failed").FetchHistoryAsync();
            var closing = history.Events.Last();
            Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(Google.Protobuf.MessageExtensions.ToByteArray(closing)), One("a-failed").TerminalEventHash);
            Assert.Equal(closing.EventId, One("a-failed").TerminalEventId);
        }
        finally
        {
            stop.Cancel();
            foreach (var r in running) { try { await r; } catch (OperationCanceledException) { } }
        }
    }
}
