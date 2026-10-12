using System.Text.Json;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Tests.Engine;

namespace Fleet.Temporal.Tests;

public class CarteBlancheDefinitionTests
{
    private static StepDefinition Root() => SeedHarness.Parse("UweCarteBlancheWorkflow");

    [Fact]
    public void Seed_AllWaitsFloorsAndFailureResets_AreBoundedAndWakeOnly()
    {
        var root = Root(); var steps = WorkflowDefinitionValidator.Walk(root).ToArray();
        var sleeps = steps.OfType<SleepStep>().ToArray(); Assert.Equal(9, sleeps.Length);
        Assert.Equal([60L, 60L, 60L, 60L, 600L, 600L, 1800L, 1800L, 1800L], sleeps.Select(s => s.Seconds!.Value).Order());
        var waits = steps.OfType<WaitForSignalStep>().ToArray(); Assert.Equal(3, waits.Length);
        Assert.All(waits, w =>
        {
            Assert.Equal("blocker-resolved", w.SignalName); Assert.True(w.AutoCompleteOnTimeout); Assert.Equal(0, w.MaxReminders);
            Assert.Null(w.BindTo); Assert.Null(w.VisitVar); Assert.Null(w.DelegatedGuard); Assert.Null(w.NotifyStep); Assert.Null(w.Phase);
        });
        Assert.Equal(336, Assert.Single(steps.OfType<LoopStep>()).MaxIterations);
        Assert.DoesNotContain(steps.OfType<BranchStep>(), b => b.On.Contains("vars.wake."));
        var read = Assert.Single(steps.OfType<SetVariableStep>(), s => s.Name == "read_tick_status");
        Assert.DoesNotContain("default", read.Vars["tick_status"]?.ToString());
        var resets = steps.OfType<SetVariableStep>().Where(s => s.Name == "reset_failed_tick").ToArray(); Assert.Equal(2, resets.Length);
        Assert.All(resets, s =>
        {
            Assert.Equal("", s.Vars["tick_status"]); Assert.Equal("none", s.Vars["epic_fingerprint"]);
            Assert.Equal("none", s.Vars["epic_next"]); Assert.Equal("", s.Vars["tick_ask"]); Assert.False(s.Vars.ContainsKey("epic_seen"));
        });
        var write = Assert.Single(steps.OfType<DelegateStep>(), s => s.Name == "record_findings");
        Assert.True(write.IgnoreFailure); Assert.Equal("findings_write_status", write.StatusVar); Assert.Equal("findings_write_result", write.OutputVar);
    }

    [Fact]
    public void Templates_StrictTickStatusAndStreakFloors_DoNotDefaultToWorking()
    {
        var vars = new Dictionary<string, object?>(); var engine = new TemplateEngine(new() { ["vars"] = vars });
        var all = WorkflowDefinitionValidator.Walk(Root()).ToArray();
        var read = all.OfType<SetVariableStep>().Single(s => s.Name == "read_tick_status");
        foreach (var invalid in new[] { "", "EPIC_STATUS: done", "EPIC_STATUS: working extra" })
        { vars["tick_result"] = invalid; Assert.Equal("", engine.ResolveString(read.Vars["tick_status"]?.ToString())); }
        vars["tick_result"] = "EPIC_STATUS: blocked\n"; Assert.Equal("blocked", engine.ResolveString(read.Vars["tick_status"]?.ToString()));
        var floor = all.OfType<SetVariableStep>().Single(s => s.Name == "read_floor_class");
        foreach (var pair in new[] { ("", "none"), ("xx", "none"), ("xxx", "xxx"), ("xxxxx", "xxx"), ("xxxxxx", "xxxxxx") })
        { vars["wake_streak"] = pair.Item1; Assert.Equal(pair.Item2, engine.ResolveString(floor.Vars["floor_class"]?.ToString())); }
    }

    [Fact]
    public void OverflowFindings_AllExplicitBlockersSurviveVerbatimInTheNextHeader()
    {
        var steps = WorkflowDefinitionValidator.Walk(Root()).ToArray();
        var overflow = steps.OfType<SetVariableStep>().Single(s => s.Name == "overflow_findings");
        var blockers = new[] { "blocker-one " + new string('a', 2100), "blocker-two " + new string('b', 2100), "blocker-three" };
        var vars = new Dictionary<string, object?> { ["epic_consensus"] = JsonSerializer.SerializeToElement(new
            { FinalVerdict = "needs_human_review", ConsolidatedReasoning = "blocker overflow", PerAgentVerdicts = new[] { new { AgentName = "reviewer", Blockers = blockers } } }) };
        var engine = new TemplateEngine(new() { ["vars"] = vars });
        vars["completion_findings"] = engine.ResolveString(overflow.Vars["completion_findings"]?.ToString());
        var tick = steps.OfType<DelegateStep>().Single(s => s.Name == "epic_tick");
        var header = engine.ResolveString(tick.Instruction);
        foreach (var blocker in blockers) Assert.Contains(blocker, header);
        Assert.Contains("blocker overflow", header);
        Assert.Contains("partial", tick.Instruction); Assert.Contains("unknown, not absent", tick.Instruction);
        Assert.Contains("previous status is not `proposed`", tick.Instruction);
    }
}
