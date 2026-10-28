using Fleet.Temporal.Engine;

namespace Fleet.Temporal.Tests.Engine;

public sealed class AcceptanceDefinitionTests
{
    [Theory]
    [InlineData("UwePrAcceptanceWorkflow")]
    [InlineData("UweAcceptanceCleanupWorkflow")]
    public void ChildDefinition_LoadsWithLiteralBoundedLoops(string name)
    {
        var root = SeedHarness.Parse(name);
        Assert.All(WorkflowDefinitionValidator.Walk(root).OfType<LoopStep>(), loop =>
            Assert.InRange(loop.MaxIterations, 1, 4));
    }

    [Fact]
    public void AcceptanceChild_BackstopPrecedesSingleAttemptOwnerDelegatesAndLiteralWait()
    {
        var root = Assert.IsType<SequenceStep>(SeedHarness.Parse("UwePrAcceptanceWorkflow"));
        var backstop = Assert.Single(root.Steps.OfType<FireAndForgetStep>());
        Assert.Equal("UweAcceptanceCleanupWorkflow", backstop.WorkflowType);
        var steps = WorkflowDefinitionValidator.Walk(root).ToList();
        var owners = steps.OfType<DelegateStep>().Where(s => s.Target == "{{input.Owner}}").ToList();
        Assert.Equal(3, owners.Count);
        Assert.All(owners, owner =>
        {
            Assert.IsType<DelegateStep>(owner);
            Assert.False(owner.RetryOnIncomplete);
            Assert.Equal(0, owner.MaxIncompleteRetries);
            Assert.Equal(60, owner.TimeoutMinutes);
            Assert.True(owner.IgnoreFailure);
            Assert.Contains("lease", owner.Instruction);
            Assert.Contains("cleanup receipt", owner.Instruction);
        });
        Assert.True(steps.IndexOf(backstop) < steps.IndexOf(owners[0]));
        var wait = steps.OfType<BranchStep>().Single(s => s.Name == "acceptance_wait");
        Assert.Equal("{{input.WaitSeconds}}", wait.On);
        Assert.Equal(["0", "60", "300", "900", "1800", "3660", "7260", "14460"], wait.Cases.Keys);
        Assert.All(wait.Cases.Values.OfType<SleepStep>(), s => Assert.NotNull(s.Seconds));
    }

    [Fact]
    public void CleanupBackstop_UsesDurationPlusMarginAndTwoRunningRechecks()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse("UweAcceptanceCleanupWorkflow")).ToList();
        var delay = steps.OfType<BranchStep>().Single(s => s.Name == "backstop_delay");
        Assert.Equal("{{input.MaxDurationSeconds}}", delay.On);
        Assert.Equal([5400L, 9000L, 16200L, 23400L], delay.Cases.Values.Cast<SleepStep>().Select(s => s.Seconds!.Value));
        var checks = steps.OfType<LoopStep>().Single();
        Assert.Equal(2, checks.MaxIterations);
        Assert.Equal(1800, Assert.Single(checks.Steps.OfType<SleepStep>()).Seconds);
        var owner = Assert.Single(steps.OfType<DelegateStep>().Where(s => s.Target == "{{input.Owner}}"));
        Assert.False(owner.RetryOnIncomplete);
        Assert.Contains("ALREADY_CLEAN", owner.Instruction);
    }
    [Fact]
    public void Parent_AllBoundGatesHaveAnImmediateRequiredThreeAttributeWrite()
    {
        var root = SeedHarness.Parse("UwePrImplementationWorkflow");
        foreach (var container in WorkflowDefinitionValidator.Walk(root))
        {
            var children = container switch { SequenceStep q => q.Steps, LoopStep q => q.Steps, _ => [] };
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i] is not WaitForSignalStep { BindField: "Ref" } wait) continue;
                Assert.True(i > 0);
                var write = Assert.IsType<SetAttributeStep>(children[i - 1]);
                Assert.True(write.Required); Assert.False(write.IgnoreFailure);
                Assert.Equal(wait.Phase, write.Attributes["Phase"]);
                Assert.Equal("{{vars.gate_ref}}", write.Attributes["GateRef"]);
                Assert.True(write.Attributes.ContainsKey("Acceptance"));
                Assert.Equal("{{vars.gate_ref}}", wait.BindTo);
            }
        }
        var waits = WorkflowDefinitionValidator.Walk(root).OfType<WaitForSignalStep>().Where(w => w.BindField == "Ref").ToList();
        Assert.Equal(3, waits.Count);
        var merge = Assert.Single(waits, w => w.SignalName == "merge-approval");
        Assert.Equal(600, merge.TimeoutMinutes); Assert.Equal(180, merge.ReminderIntervalMinutes); Assert.Equal(2, merge.MaxReminders);
        Assert.All(waits.Where(w => w.SignalName == "acceptance-decision"), w => Assert.Equal(720, w.TimeoutMinutes));
    }

    [Fact]
    public void Parent_CapsStoppedWritersAndMergeContractsAreExplicit()
    {
        var steps = WorkflowDefinitionValidator.Walk(SeedHarness.Parse("UwePrImplementationWorkflow")).ToList();
        var loops = steps.OfType<LoopStep>().ToDictionary(x => x.Name!);
        Assert.Equal(60, loops["outer_loop"].MaxIterations);
        Assert.Equal(50, loops["consensus_loop"].MaxIterations);
        Assert.Equal(12, loops["ci_wait_loop"].MaxIterations);
        Assert.Equal(16, loops["cleanup_wait_loop"].MaxIterations);
        foreach (var name in new[] { "check_early_exit_in_consensus", "exit_if_consensus_failed", "exit_before_ceo_gate", "outer_exit_check", "outer_exhaustion_notification", "proceed_to_merge_check", "map_terminal_decision", "fail_on_technical_outcome" })
            Assert.Contains("STOPPED", steps.OfType<BranchStep>().Single(s => s.Name == name).On);
        foreach (var write in steps.OfType<SetAttributeStep>().Where(w => w.Attributes.ContainsKey("Acceptance")))
        { Assert.True(write.Required); Assert.False(write.IgnoreFailure); }
        var delegates = steps.OfType<DelegateStep>().ToList();
        Assert.All(delegates.Where(d => d.Target == "{{input.TargetAgent}}"), d =>
        { Assert.Equal(60, d.TimeoutMinutes); Assert.Contains("HEAD_RANGE:", d.Instruction); Assert.Contains("DO NOT BLOCK ON CI", d.Instruction); });
        Assert.All(delegates.Where(d => d.Name is "phase4_merge" or "phase4_merge_pinned"), d => Assert.Contains("--match-head-commit {{vars.review_ref}}", d.Instruction));
        var pin = delegates.First(d => d.Name == "pin");
        Assert.Contains("<capability map, see docs/pr-acceptance.md>", pin.Instruction);
        Assert.Contains("run_id=<row.runId>", pin.Instruction);
        Assert.Contains("ReviewRef=", pin.Instruction);
        Assert.Contains("FINAL AUDIT", Assert.Single(steps.OfType<ChildWorkflowStep>(), c => c.Name == "consensus_review").Args!["ReviewPrompt"]!.ToString());
        Assert.Equal(new[] { 60, 120, 240, 360 }, steps.OfType<ChildWorkflowStep>().Where(c => c.WorkflowType == "UwePrAcceptanceWorkflow").Select(c => c.ExecutionTimeoutMinutes!.Value));
    }

    [Theory]
    [InlineData("passed", "none")]
    [InlineData("failed", "none")]
    [InlineData("blocked", "owner_timeout")]
    [InlineData("head_changed", "none")]
    public void Parent_W2SelectsAValidWholeStringWithoutBranchingOnTheChild(string result, string reason)
    {
        var root = SeedHarness.Parse("UwePrImplementationWorkflow");
        var nodes = WorkflowDefinitionValidator.Walk(root).ToList();
        var values = new Dictionary<string, object?>
        {
            ["vars"] = new Dictionary<string, object?>
            {
                ["H"] = new string('a', 40),
                ["acceptance_iso"] = "isolated",
                ["fix_round"] = "5",
                ["batch_round"] = "5",
                ["child_reason"] = reason,
                ["child_state"] = result
            }
        };
        var engine = new TemplateEngine(values);
        var candidates = nodes.OfType<SetVariableStep>().First(s => s.Name == "w2_candidates");
        ((Dictionary<string, object?>)values["vars"]!)["w2_values"] = engine.ResolveString(candidates.Vars["w2_values"]);
        var write = nodes.OfType<SetAttributeStep>().First(s => s.Name == "w2_after_child_60");
        var actual = engine.ResolveString(write.Attributes["Acceptance"]);
        if (result == "head_changed") Assert.Equal("", actual);
        else { Assert.True(Fleet.Shared.AcceptanceAttribute.IsValid(actual)); Assert.Contains($"result={result};", actual); }
    }

}
