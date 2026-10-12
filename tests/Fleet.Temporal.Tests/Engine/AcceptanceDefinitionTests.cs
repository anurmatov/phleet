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
}
