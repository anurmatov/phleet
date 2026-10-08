using Fleet.Agent.Abstractions;
using static Fleet.Agent.Tests.MessageCopyToolTests;
namespace Fleet.Agent.Tests;

public sealed class MessageCopyDeadlineTests
{
    [Theory]
    [InlineData("getChat", 5, "destination_unreachable")]
    [InlineData("sendMessage", 5, "prompt_failed")]
    [InlineData("copyMessage", 10, "ambiguous")]
    [InlineData("editMessageText", 2, "copied")]
    [InlineData("answerCallbackQuery", 2, "copied")]
    public async Task Call_Hangs_EndsAtChildDeadline(string method, int seconds, string outcome)
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bot.Before = async (name, token) => { if (name == method) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var call = rig.Copy();
        if (method is "copyMessage" or "editMessageText" or "answerCallbackQuery") { await rig.Bot.Prompted.Task; await rig.Tap(); }
        await started.Task; rig.Clock.Advance(TimeSpan.FromSeconds(seconds));
        Assert.Equal(outcome, Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(seconds, (rig.Clock.GetUtcNow() - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds);
        Assert.Equal(method == "copyMessage" ? 1 : 0, rig.Bot.Calls.Count(c => c == "copyMessage" && method == "copyMessage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_CancelDuringRequest_IndependentAnswerAndEditStillRun(bool request)
    {
        using var rig = new Rig(); using var ct = new CancellationTokenSource();
        var copying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bot.Before = async (name, token) => { if (name == "copyMessage") { copying.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var call = rig.Copy(ct.Token); await rig.Bot.Prompted.Task; await rig.Tap(); await copying.Task;
        if (request) ct.Cancel(); else rig.Owner.Cts.Cancel();
        Assert.Equal("ambiguous", Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(1, rig.Bot.Calls.Count(c => c == "copyMessage"));
        Assert.Contains("answerCallbackQuery", rig.Bot.Calls); Assert.Contains("editMessageText", rig.Bot.Calls);
        Assert.False(rig.Bot.CleanupCancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_AfterCancelledWait_IsAwaitedAndEndsInTwoSeconds(bool request)
    {
        using var rig = new Rig(); using var ct = new CancellationTokenSource();
        var editing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bot.Before = async (name, token) => { if (name == "editMessageText") { editing.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        var call = rig.Copy(ct.Token); await rig.Bot.Prompted.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(15));
        if (request) ct.Cancel(); else rig.Owner.Cts.Cancel();
        await editing.Task; Assert.False(call.IsCompleted); Assert.False(rig.Bot.CleanupCancelled);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("confirmation_timeout", Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Copy_LateTapAndSlowCopyAndCleanup_EndBefore50Seconds()
    {
        using var rig = new Rig();
        var copying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Bot.Before = async (name, token) =>
        {
            if (name == "copyMessage")
            {
                copying.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { rig.Clock.Advance(TimeSpan.FromSeconds(0.2)); } // cancellation unwind time
            }
            if (name == "editMessageText") { editing.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        };
        var call = rig.Copy(); await rig.Bot.Prompted.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(37.9)); await rig.Tap(); await copying.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(10)); await editing.Task;
        // Advance near the original deadline before allowing cleanup to end: it is capped
        // by the remaining time, not an extra two seconds on top of elapsed work.
        rig.Clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal("ambiguous", Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.True((rig.Clock.GetUtcNow() - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds <= 50);
        await rig.Tap(); Assert.Equal(1, rig.Bot.Calls.Count(c => c == "copyMessage"));
    }

    [Fact]
    public async Task Copy_SlowResolveAndPrompt_ShortenWaitTo28Seconds()
    {
        using var rig = new Rig();
        rig.Bot.Before = (name, token) => { if (name is "getChat" or "sendMessage") rig.Clock.Advance(TimeSpan.FromSeconds(4.9)); return Task.CompletedTask; };
        var call = rig.Copy(); await rig.Bot.Prompted.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(28.1)); Assert.False(call.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal("confirmation_timeout", Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
    }
}
