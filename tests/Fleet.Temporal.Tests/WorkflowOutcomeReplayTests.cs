using System.Reflection;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Tests.Engine;
using Fleet.Temporal.Workflows.Fleet;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.WorkflowOutcomes;

/// <summary>
/// #476 AC22: the feature changes no workflow code, so every recorded pre-feature history — UWE
/// and compiled — must replay against the current workflows, and the same scenarios run live
/// today must emit the same command sequence. The observer lives in the orchestrator and the
/// bridge changes only its listener and MCP tools: no workflow command, signal, query or update
/// is added, and the approver-only gate list is unchanged.
/// </summary>
[Trait("WorkflowOutcomeSuite", "replay")]
public sealed class WorkflowOutcomeReplayTests
{
    public static TheoryData<string> Fixtures => new()
    {
        "uwe-gate-wait",
        "uwe-delegate-5arg",
        "uwe-delegate-statusvar",
        "compiled-notify-cto",
        "compiled-consensus-review",
    };

    private static WorkflowHistory Fixture(string name) => name switch
    {
        "uwe-gate-wait" => Recorded(GateWaitHistoryScenario.WorkflowId, GateWaitHistoryScenario.FixturePath),
        "uwe-delegate-5arg" => Recorded(DelegateHistoryScenario.WorkflowId, DelegateHistoryScenario.FixturePath),
        "uwe-delegate-statusvar" => Recorded(DelegateStatusVarHistoryScenario.WorkflowId, DelegateStatusVarHistoryScenario.FixturePath),
        "compiled-notify-cto" => CompiledHistoryScenarios.Load(CompiledHistoryScenarios.NotifyCtoWorkflowId, CompiledHistoryScenarios.NotifyCtoFixture),
        "compiled-consensus-review" => CompiledHistoryScenarios.Load(CompiledHistoryScenarios.ConsensusWorkflowId, CompiledHistoryScenarios.ConsensusFixture),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static WorkflowHistory Recorded(string workflowId, string path) =>
        WorkflowHistory.FromJson(workflowId, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, path)));

    private static Task<WorkflowHistory> RunLive(string name) => name switch
    {
        "uwe-gate-wait" => GateWaitHistoryScenario.RunAsync(GateWaitHistoryScenario.Definition()),
        "uwe-delegate-5arg" => DelegateHistoryScenario.RunAsync(DelegateHistoryScenario.Definition(), DelegateHistoryScenario.Input()),
        "uwe-delegate-statusvar" => DelegateStatusVarHistoryScenario.RunAsync(),
        "compiled-notify-cto" => CompiledHistoryScenarios.RunNotifyCtoAsync(),
        "compiled-consensus-review" => CompiledHistoryScenarios.RunConsensusAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Every workflow the bridge registers on its namespace workers (Program.cs).</summary>
    private static WorkflowReplayerOptions CurrentWorkflows() => new WorkflowReplayerOptions()
        .AddWorkflow<UniversalWorkflow>()
        .AddWorkflow<ConsensusReviewWorkflow>()
        .AddWorkflow<AuthTokenRefreshWorkflow>()
        .AddWorkflow<NotifyCtoWorkflow>();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task RecordedPreFeatureHistory_ReplaysAgainstTheCurrentWorkflows(string fixture)
    {
        var history = Fixture(fixture);
        Assert.NotEmpty(history.Events);

        var results = new List<WorkflowReplayResult>();
        await foreach (var result in new WorkflowReplayer(CurrentWorkflows())
                           .ReplayWorkflowsAsync(AsAsync(history), throwOnReplayFailure: false))
            results.Add(result);

        var replayed = Assert.Single(results);
        Assert.Null(replayed.ReplayFailure);
    }

    /// <summary>
    /// Replay does not compare command attributes (activity inputs, timer lengths, upsert values),
    /// so each scenario runs live on the current code and its command sequence — activity type and
    /// input bytes, timers, markers, upserts, completion — must equal the recorded one entry by entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task SameScenarioRunToday_EmitsTheRecordedCommandSequence(string fixture)
    {
        var recorded = GateWaitHistoryScenario.Commands(Fixture(fixture));
        var live = GateWaitHistoryScenario.Commands(await RunLive(fixture));

        Assert.NotEmpty(recorded);
        Assert.Equal(recorded, live);
    }

    [Fact]
    public void CompiledFixtures_AreScrubbedPreFeatureRecordings()
    {
        foreach (var name in new[] { CompiledHistoryScenarios.NotifyCtoFixture, CompiledHistoryScenarios.ConsensusFixture })
        {
            var json = File.ReadAllText(Path.Combine(CompiledHistoryScenarios.FixtureDirectory, name));
            Assert.Contains("\"identity\": \"0@example-host\"", json);
            Assert.DoesNotContain("WorkflowOutcome", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("recovery", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The workflow surface a caller can drive — workflow types, their signals, queries and
    /// updates — is exactly what it was before #476, and no workflow type references a #476 type.
    /// </summary>
    [Fact]
    public void WorkflowSurface_IsUnchanged_AndCarriesNoOutcomeTypes()
    {
        var assembly = typeof(UniversalWorkflow).Assembly;
        var workflows = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<WorkflowAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [typeof(AuthTokenRefreshWorkflow), typeof(ConsensusReviewWorkflow), typeof(NotifyCtoWorkflow), typeof(UniversalWorkflow)],
            workflows.OrderBy(t => t.Name, StringComparer.Ordinal));

        var surface = workflows.ToDictionary(
            t => t.Name,
            t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(m => new[]
                {
                    m.GetCustomAttribute<WorkflowSignalAttribute>() is { } s ? "signal:" + (s.Name ?? m.Name) : null,
                    m.GetCustomAttribute<WorkflowQueryAttribute>() is { } q ? "query:" + (q.Name ?? m.Name) : null,
                    m.GetCustomAttribute<WorkflowUpdateAttribute>() is { } u ? "update:" + (u.Name ?? m.Name) : null,
                    m.GetCustomAttribute<WorkflowSignalAttribute>()?.Dynamic == true ? "dynamic-signal" : null,
                })
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToArray());

        Assert.Equal(PreFeatureSurface, surface.ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value)));

        foreach (var workflow in workflows)
        {
            var referenced = workflow.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.FieldType)
                .Concat(workflow.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));
            Assert.DoesNotContain(referenced, t => t.FullName?.Contains("WorkflowOutcome", StringComparison.Ordinal) == true ||
                                                   t.FullName?.Contains("WorkflowRecovery", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public void ApproverOnlyGateList_IsUnchanged()
    {
        Assert.Equal(
            ["acceptance-decision", "advisory-review", "design-approval", "doc-review", "merge-approval"],
            CeoGateSignals.All.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Computed with the same reflection from the #476 base commit's workflow code: only the UWE
    /// dynamic signal handler exists. Any new signal, query or update fails here.
    /// </summary>
    private static readonly Dictionary<string, string> PreFeatureSurface = new()
    {
        ["AuthTokenRefreshWorkflow"] = "",
        ["ConsensusReviewWorkflow"] = "",
        ["NotifyCtoWorkflow"] = "",
        ["UniversalWorkflow"] = "dynamic-signal,signal:HandleSignalAsync",
    };

    private static async IAsyncEnumerable<WorkflowHistory> AsAsync(WorkflowHistory history)
    {
        await Task.CompletedTask;
        yield return history;
    }
}
