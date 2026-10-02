using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public sealed partial class MidTurnIsolationTests
{
    [Theory]
    [InlineData("claude", MidTurnInjectionStatus.Injected)]
    [InlineData("claude", MidTurnInjectionStatus.NoActiveTurn)]
    [InlineData("codex", MidTurnInjectionStatus.NoActiveTurn)]
    [InlineData("gemini", MidTurnInjectionStatus.Unsupported)]
    public async Task X5_SameGroupUsesInjectionOrInboxBeforeDifferentGroupPriority(string provider, MidTurnInjectionStatus status)
    {
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(status);
        var manager = MidTurnIsolationTests.Manager(provider, executor);
        try
        {
            await manager.StartTask(-101, "human", "human", true); await executor.WaitStarted(1);
            await manager.StartTask(-202, "other group", "other group", true, priority: TaskPriority.PrimaryHuman, telegramMessageId: 1);
            var outcome = await manager.StartTask(-101, "same group", "same group", true, priority: TaskPriority.PrimaryHuman, telegramMessageId: 2);
            Assert.Equal(status == MidTurnInjectionStatus.Injected ? TaskDispatchOutcome.Injected : TaskDispatchOutcome.Queued, outcome);
            Assert.Equal(TaskPriority.PrimaryHuman, Assert.Single(manager.GetQueueSnapshot()).Priority);
            Assert.Equal(-202, manager.GetQueueSnapshot()[0].ChatId);
            executor.Release(); await executor.WaitStarted(2);
            Assert.Equal(status == MidTurnInjectionStatus.Injected ? "other group" : "same group", executor.Tasks[1]);
            if (status != MidTurnInjectionStatus.Injected)
            { executor.Release(); await executor.WaitStarted(3); Assert.Equal("other group", executor.Tasks[2]); }
            executor.Release(); await Until(() => !manager.HasRunningTasks(-101) && !manager.HasRunningTasks(-202));
        }
        finally { await manager.CancelAllAsync(); }
    }

}
