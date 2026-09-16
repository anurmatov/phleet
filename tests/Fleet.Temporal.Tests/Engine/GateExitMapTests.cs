using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Temporal.Engine;
using Temporalio.Workflows;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// #280 §6 / T28 / T29 — the definition half of the fix, checked against the shipped seed.
///
/// <para>
/// The engine can deliver a wakeup; whether one is ever SENT is a property of the gate-owning
/// definitions, and of which of their exits count as terminal. Getting that map wrong is silent in
/// both directions: a missing signal leaves the waiter parked until its safety-net timeout, and a
/// signal from a non-terminal exit wakes a driver whose blocker is still in progress.
/// </para>
///
/// <para>
/// These read <c>seed.example.json</c> — the file a fresh install is built from — so seed drift
/// from the engine cannot pass unnoticed.
/// </para>
/// </summary>
public sealed class GateExitMapTests
{
    private const string WakeSignal = "blocker-resolved";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    // ── T28: every gate owner signals its terminal decision ──────────────────

    [Theory]
    [InlineData("UweDesignWorkflow")]
    [InlineData("UwePrImplementationWorkflow")]
    [InlineData("UweDocMaintenanceWorkflow")]
    public void EveryGateOwner_EndsWithASingleWakeupStep(string workflowName)
    {
        var root = Definition(workflowName);
        var signals = Descendants(root).Where(n => Type(n) == "signal_workflow").ToList();

        // ONE, at the definition-terminal exit. One per branch case would fan out wakeups from
        // paths that are not terminal at all.
        var signal = Assert.Single(signals);

        Assert.Equal(WakeSignal, signal["signalName"]!.GetValue<string>());
        Assert.Equal("{{input.WaiterWorkflowId | default: ''}}", signal["workflowId"]!.GetValue<string>());

        // The payload carries the decision AND the sending workflow's own id, which is what the
        // waiter correlates against. Without blockerRef every wakeup would score as a mismatch.
        Assert.Equal("{{vars.terminal_decision}}", signal["payload"]!["decision"]!.GetValue<string>());
        Assert.Equal("{{workflow.id}}", signal["payload"]!["blockerRef"]!.GetValue<string>());

        // A gate owner must never fail because the peer it was being polite to has closed.
        Assert.True(signal["ignoreFailure"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("UweDesignWorkflow")]
    [InlineData("UwePrImplementationWorkflow")]
    [InlineData("UweDocMaintenanceWorkflow")]
    public void TheWakeupStep_IsTheLastStepOfTheRootSequence(string workflowName)
    {
        var root = Definition(workflowName);
        var steps = root["steps"]!.AsArray();

        Assert.Equal("signal_workflow", Type(steps[^1]!));
    }

    /// <summary>
    /// The decisions each definition can send. Read from the seed rather than asserted in prose,
    /// because the whole point of the map is that it matches the definition that ships.
    ///
    /// <c>cancelled</c> is for an exit that ended the run WITHOUT a human decision on the merits —
    /// a gate that timed out, a loop that ran out of rounds. A driver treats it as terminal, which
    /// is why it must not be attached to an exit a human could still act on.
    /// </summary>
    [Theory]
    [InlineData("UweDesignWorkflow",           "approved,cancelled,rejected")]
    [InlineData("UwePrImplementationWorkflow", "approved,cancelled,rejected")]
    [InlineData("UweDocMaintenanceWorkflow",   "approved,cancelled,rejected")]
    public void EachGateOwner_EmitsTheExpectedDecisionSet(string workflowName, string expected)
    {
        var root = Definition(workflowName);

        var decisions = Descendants(root)
            .Where(n => Type(n) == "set_variable" && n["vars"]?["terminal_decision"] is not null)
            .Select(n => n["vars"]!["terminal_decision"]!.GetValue<string>())
            .Distinct()
            .OrderBy(d => d, StringComparer.Ordinal);

        Assert.Equal(expected, string.Join(",", decisions));
    }

    /// <summary>
    /// The chain owns no gate. It threads the waiter id to both children — the leaf gate owners
    /// signal directly — and must send nothing itself: when its design child completes, the
    /// implementation run it then starts is still in flight, so a wakeup there would resume a
    /// driver whose blocker has not cleared.
    /// </summary>
    [Fact]
    public void TheChainWorkflow_PropagatesTheWaiterIdAndSignalsNothing()
    {
        var root = Definition("UweDesignToPrWorkflow");

        Assert.DoesNotContain(Descendants(root), n => Type(n) == "signal_workflow");

        var forwarding = Descendants(root)
            .Where(n => Type(n) is "child_workflow" or "fire_and_forget")
            .ToList();

        Assert.Equal(2, forwarding.Count);
        Assert.All(forwarding, n => Assert.Equal(
            "{{input.WaiterWorkflowId | default: ''}}",
            n["args"]!["WaiterWorkflowId"]!.GetValue<string>()));
    }

    /// <summary>
    /// Non-terminal exits send nothing. <c>changes_requested</c> is a real human decision on a live
    /// gate, but the gated work is still in progress — waking a driver there produces a tick that
    /// immediately reports blocked again, and re-parks.
    ///
    /// Asserted structurally: the only wakeup in each definition is the terminal one, so no branch
    /// case can contain a second.
    /// </summary>
    [Theory]
    [InlineData("UweDesignWorkflow")]
    [InlineData("UwePrImplementationWorkflow")]
    [InlineData("UweDocMaintenanceWorkflow")]
    public void NoWakeupIsSentFromInsideABranchCase(string workflowName)
    {
        var root = Definition(workflowName);

        foreach (var node in Descendants(root).Where(n => Type(n) == "branch"))
        {
            Assert.DoesNotContain(
                Descendants(node).Where(n => n != node),
                n => Type(n) == "signal_workflow");
        }
    }

    /// <summary>
    /// The whole seed still parses through the production deserializer, and passes the new
    /// load-time validation. A seed that no longer loads is a fresh install that cannot start.
    /// </summary>
    [Fact]
    public void EverySeededDefinition_LoadsAndValidates()
    {
        foreach (var (name, definition) in AllDefinitions())
        {
            var root = JsonSerializer.Deserialize<StepDefinition>(TypeFirst(definition)!.ToJsonString(), WebOptions);
            Assert.NotNull(root);
            WorkflowDefinitionValidator.Validate(root!, name);
        }
    }

    // ── T29: no compiled workflow owns a human gate ──────────────────────────

    /// <summary>
    /// T29. Everything above only reaches gates that live in UWE definitions. A compiled workflow
    /// with its own signal handler would need its own wakeup and its own buffer, and none of this
    /// would apply to it.
    ///
    /// Today none exists. This fails the moment one is added, which forces the limitation to be
    /// revisited rather than silently outgrown.
    /// </summary>
    [Fact]
    public void NoCompiledFleetWorkflow_DeclaresASignalHandler()
    {
        var offenders = typeof(UniversalWorkflow).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "Fleet.Temporal.Workflows.Fleet")
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<WorkflowSignalAttribute>() is not null)
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── seed access ──────────────────────────────────────────────────────────

    private static JsonObject Definition(string workflowName)
    {
        var match = AllDefinitions().FirstOrDefault(d => d.Name == workflowName);
        Assert.NotNull(match.Definition);
        return (JsonObject)TypeFirst(match.Definition)!;
    }

    private static IEnumerable<(string Name, JsonNode Definition)> AllDefinitions()
    {
        var seed = JsonNode.Parse(File.ReadAllText(SeedPath()))!;
        foreach (var entry in seed["workflowDefinitions"]!.AsArray())
        {
            yield return (entry!["name"]!.GetValue<string>(), entry["definition"]!);
        }
    }

    private static string SeedPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "seed.example.json");
    }

    private static string? Type(JsonNode node) => node["type"]?.GetValue<string>();

    private static IEnumerable<JsonObject> Descendants(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                yield return obj;
                foreach (var (_, value) in obj)
                    foreach (var child in Descendants(value))
                        yield return child;
                break;

            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var child in Descendants(item))
                        yield return child;
                break;
        }
    }

    /// <summary>
    /// Mirrors the load activity's normalization: System.Text.Json polymorphic deserialization
    /// needs the discriminator first, and definition authors write properties in any order.
    /// </summary>
    private static JsonNode? TypeFirst(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                if (obj.TryGetPropertyValue("type", out var t))
                    result["type"] = t?.DeepClone();
                foreach (var (key, value) in obj)
                {
                    if (key == "type") continue;
                    result[key] = TypeFirst(value);
                }
                return result;
            }
            case JsonArray arr:
                return new JsonArray(arr.Select(TypeFirst).ToArray());
            default:
                return node?.DeepClone();
        }
    }
}
