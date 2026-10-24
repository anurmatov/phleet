using Fleet.Temporal.Models;
using Fleet.Temporal.Workflows.Fleet;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.WorkflowOutcomes;

/// <summary>
/// Two compiled (non-UWE) workflow runs whose histories are committed under
/// <c>WorkflowOutcomes/Fixtures/</c> for the #476 replay suite (AC22).
///
/// <para>
/// The fixtures were recorded with these scenarios on a time-skipping test server from workflow,
/// engine and activity code identical to the #476 base commit (the feature changes none of it).
/// Only replay-irrelevant text was edited afterwards: worker identities became a placeholder host
/// and the failure stack trace was emptied. Never regenerate them from later code: they would then
/// stop being pre-feature histories.
/// </para>
///
/// <para>
/// <c>NotifyCtoWorkflow</c> records its failure branch (the delegation fails and is ignored);
/// <c>ConsensusReviewWorkflow</c> records a two-reviewer fan-out, the budget lookup and the
/// synthesizer. Ids are fixed so the activity inputs (which embed them) are stable.
/// </para>
/// </summary>
internal static class CompiledHistoryScenarios
{
    public const string NotifyCtoWorkflowId = "example-notify-cto-history";
    public const string NotifyCtoTaskQueue = "example-notify-cto-history";
    public const string NotifyCtoFixture = "notify-cto-failed-delegation-history.json";

    public const string ConsensusWorkflowId = "example-consensus-history";
    public const string ConsensusTaskQueue = "example-consensus-history";
    public const string ConsensusFixture = "consensus-review-history.json";

    private const string DelegateActivity = "DelegateToAgent";
    private const string BudgetActivity = "LoadAgentBudgetSeconds";

    /// <summary>The committed fixture directory, found from the test output up to the repository root.</summary>
    public static string FixtureDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
                dir = dir.Parent;
            if (dir is null)
                throw new InvalidOperationException("repository root (seed.example.json) not found above " + AppContext.BaseDirectory);
            return Path.Combine(dir.FullName, "tests", "Fleet.Temporal.Tests", "WorkflowOutcomes", "Fixtures");
        }
    }

    public static WorkflowHistory Load(string workflowId, string fixture) =>
        WorkflowHistory.FromJson(workflowId, File.ReadAllText(Path.Combine(FixtureDirectory, fixture)));

    public static async Task<WorkflowHistory> RunNotifyCtoAsync()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(NotifyCtoTaskQueue)
                .AddActivity(ActivityDefinition.Create(
                    DelegateActivity,
                    typeof(AgentTaskResult),
                    [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int)],
                    3,
                    _ => throw new ApplicationFailureException("agent unavailable", errorType: "AgentUnavailable", nonRetryable: true)))
                .AddWorkflow<NotifyCtoWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            await env.Client.ExecuteWorkflowAsync(
                (NotifyCtoWorkflow wf) => wf.RunAsync(new NotifyCtoWorkflowInput("coordinator", "a workflow needs attention")),
                new WorkflowOptions(NotifyCtoWorkflowId, NotifyCtoTaskQueue));
        });

        return await env.Client.GetWorkflowHandle(NotifyCtoWorkflowId).FetchHistoryAsync();
    }

    public static async Task<WorkflowHistory> RunConsensusAsync()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(ConsensusTaskQueue)
                .AddActivity(ActivityDefinition.Create(
                    DelegateActivity,
                    typeof(AgentTaskResult),
                    [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(int), typeof(int)],
                    3,
                    args => new AgentTaskResult(ResponseFor((string)args[0]!), "completed")))
                .AddActivity(ActivityDefinition.Create(BudgetActivity, typeof(int), [], 0, _ => 5400))
                .AddWorkflow<ConsensusReviewWorkflow>());

        await worker.ExecuteAsync(async () =>
        {
            var input = new ConsensusReviewInput(
                "a change under review", "review it", ["agent1", "agent2"], null, "agent3");
            await env.Client.ExecuteWorkflowAsync(
                (ConsensusReviewWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions(ConsensusWorkflowId, ConsensusTaskQueue));
        });

        return await env.Client.GetWorkflowHandle(ConsensusWorkflowId).FetchHistoryAsync();
    }

    /// <summary>
    /// agent1 approves, agent2 requests one change, so the synthesizer runs and its own verdict closes the review.
    /// </summary>
    private static string ResponseFor(string agent) => agent switch
    {
        "agent1" => "Looks right.\nSUMMARY: no objection\nEVIDENCE: none\nVERDICT: approved",
        "agent2" => "One problem.\nSUMMARY: one real problem\nEVIDENCE: none\nBLOCKER: rename the flag\nVERDICT: changes_requested",
        _ => "The flag naming is the blocking issue.\nVERDICT: changes_requested",
    };
}
