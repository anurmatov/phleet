using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public sealed partial class MidTurnIsolationTests
{
    [Theory]
    [InlineData(TaskSource.UserMessage)]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    public async Task ToolSendReceipt_KeepsTheExecutingTurnsOrigin_WhenAHumanArrives(TaskSource source)
    {
        using var rig = new ReceiptRig();
        await using var executor = new ControlledExecutor(MidTurnInjectionStatus.Injected, rig.Ledger);
        var manager = Manager("claude", executor, ledger: rig.Ledger);
        try
        {
            rig.At(90);
            await manager.StartTask(ReceiptRig.HumanDm, "original turn", "original", true, source);
            await executor.WaitStarted(1);
            rig.At(95);
            var delivered = await manager.StartTask(ReceiptRig.HumanDm, "human reply", "reply", true, priority: TaskPriority.PrimaryHuman, telegramMessageId: 7);
            Assert.Equal(source == TaskSource.UserMessage ? TaskDispatchOutcome.Injected : TaskDispatchOutcome.Queued, delivered);
            var interval = Assert.Single(rig.Ledger.SnapshotForTests());
            Assert.Equal(source switch
            {
                TaskSource.UserMessage => TurnOrigin.Human,
                TaskSource.Relay => TurnOrigin.Relay,
                _ => TurnOrigin.Bridge,
            }, interval.Origin);
            rig.At(100.1);
            await rig.DeliverAsync(ReceiptRig.Receipt(100, messageId: 501));
            rig.At(102.25);
            Assert.Equal(1, await rig.DecideAsync());
            Assert.Single(rig.Acked);
            // #439: a relay (workflow) turn's send is captured as relay; a bridge turn's stays excluded.
            Assert.Equal(source == TaskSource.Bridge ? 0 : 1, rig.Rows);
            Assert.Equal(source == TaskSource.Bridge ? 0 : 1, rig.Counters.Get("tool_send_captured"));
            Assert.Equal(source == TaskSource.Relay ? 1 : 0, rig.Counters.Get("tool_send_captured_relay"));
            Assert.Equal(source == TaskSource.Bridge ? 1 : 0, rig.Counters.Get("tool_send_excluded_origin"));
            executor.Release();
            if (source != TaskSource.UserMessage)
            {
                await executor.WaitStarted(2);
                Assert.Equal("human reply", executor.Tasks[1]);
                Assert.Equal(TurnOrigin.Human, rig.Ledger.SnapshotForTests().Last().Origin);
                executor.Release();
            }
            await Until(() => !manager.HasRunningTasks(ReceiptRig.HumanDm));
        }
        finally { await manager.CancelAllAsync(); }
    }
}
