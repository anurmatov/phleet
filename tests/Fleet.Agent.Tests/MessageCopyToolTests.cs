using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.MessageCopy;
using Fleet.Agent.Tests.Harness;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public sealed class MessageCopyToolTests
{
    [Fact]
    public async Task Copy_Confirmed_CopiesOnceAndAwaitsCleanup()
    {
        using var rig = new Rig();
        var call = rig.Copy();
        await rig.Bot.Prompted.Task;
        await rig.Tap();
        Assert.Equal("{\"ok\":true,\"outcome\":\"copied\",\"destination_chat_id\":2002,\"destination_message_id\":88,\"source_message_id\":77,\"journal_recorded\":false}", await call);
        Assert.Equal(28, rig.Bot.NonceData.Length);
        Assert.Equal(new[] { "getChat", "sendMessage", "copyMessage", "answerCallbackQuery", "editMessageText" }, rig.Bot.Calls);
        await rig.Tap();
        Assert.Equal(1, rig.Bot.Calls.Count(c => c == "copyMessage"));
        Assert.Equal(1, rig.Counter.Count("copied"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("relay")]
    [InlineData("bridge")]
    [InlineData("checkin")]
    [InlineData("batch")]
    [InlineData("group")]
    [InlineData("mismatch")]
    [InlineData("requester")]
    [InlineData("destination")]
    [InlineData("same")]
    [InlineData("message")]
    [InlineData("turnid")]
    public async Task Copy_InvalidRequest_ZeroBotCalls(string denial)
    {
        using var rig = new Rig();
        switch (denial)
        {
            case "none": rig.Current = null; break;
            case "relay": rig.Replace(TaskSource.Relay); break;
            case "bridge": rig.Replace(TaskSource.Bridge); break;
            case "checkin": rig.Replace(TaskSource.CheckIn); break;
            case "batch": rig.Replace(TaskSource.DebouncedGroupBatch); break;
            case "group": rig.Chat = -3003; break;
            case "mismatch": rig.Chat = 2002; break;
            case "requester": rig.Revoke(1001); break;
            case "destination": rig.Revoke(2002); break;
            case "turnid": rig.Owner.Identity = rig.Owner.Identity! with { TurnId = null }; break;
        }
        var result = await rig.Coordinator.CopyAsync(denial == "message" ? 0 : 77, denial == "same" ? 1001 : 2002, default);
        Assert.False(JsonDocument.Parse(result).RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(rig.Bot.Calls);
    }

    [Theory]
    [InlineData("requester", "not_a_human_request")]
    [InlineData("destination", "destination_not_allowed")]
    [InlineData("closed", "not_a_human_request")]
    [InlineData("continued", "not_a_human_request")]
    [InlineData("replaced", "not_a_human_request")]
    public async Task Tap_LiveStateChanged_DeniesWithoutCopy(string change, string outcome)
    {
        using var rig = new Rig(); var call = rig.Copy(); await rig.Bot.Prompted.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(15));
        switch (change)
        {
            case "requester": rig.Revoke(1001); break;
            case "destination": rig.Revoke(2002); break;
            case "closed": rig.Owner.Closed = true; break;
            case "continued": rig.Owner.Identity = rig.Owner.Identity! with { TurnId = "turn2" }; break;
            case "replaced": rig.Replace(TaskSource.UserMessage); break;
        }
        Assert.False(call.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(5)); await rig.Tap();
        Assert.Equal(outcome, Outcome(await call));
        Assert.DoesNotContain("copyMessage", rig.Bot.Calls); Assert.Contains("editMessageText", rig.Bot.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_CancelBeforeTap_IndependentCleanupRunsImmediately(bool request)
    {
        using var rig = new Rig(); using var ct = new CancellationTokenSource();
        var call = rig.Copy(ct.Token); await rig.Bot.Prompted.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(15));
        if (request) ct.Cancel(); else rig.Owner.Cts.Cancel();
        Assert.Equal("confirmation_timeout", Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Contains("editMessageText", rig.Bot.Calls); Assert.DoesNotContain("copyMessage", rig.Bot.Calls);
        Assert.False(rig.Bot.CleanupCancelled);
    }

    [Fact]
    public async Task Copy_ClosedWithoutTap_ExpiresAt38Seconds()
    {
        using var rig = new Rig(); var call = rig.Copy(); await rig.Bot.Prompted.Task;
        rig.Clock.Advance(TimeSpan.FromSeconds(15)); rig.Owner.Closed = true;
        rig.Clock.Advance(TimeSpan.FromSeconds(22)); Assert.False(call.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("confirmation_timeout", Outcome(await call.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.DoesNotContain("copyMessage", rig.Bot.Calls);
    }

    [Fact]
    public async Task Tap_InvalidUserNonceOrPrompt_DoesNotConsumeConfirmation()
    {
        using var rig = new Rig(); var call = rig.Copy(); await rig.Bot.Prompted.Task;
        await rig.Tap(user: 2002); await rig.Tap(data: "cp1:wrong:y"); await rig.Tap(prompt: 999);
        Assert.False(call.IsCompleted); Assert.DoesNotContain("copyMessage", rig.Bot.Calls);
        await rig.Tap(); Assert.Equal("copied", Outcome(await call));
    }

    [Fact]
    public async Task Tap_CancelAndLateTap_CopyNothing()
    {
        using var rig = new Rig(); var call = rig.Copy(); await rig.Bot.Prompted.Task;
        await rig.Tap(data: rig.Bot.NonceData[..^1] + "n"); Assert.Equal("cancelled", Outcome(await call));
        await rig.Tap(); Assert.DoesNotContain("copyMessage", rig.Bot.Calls);
    }

    [Fact]
    public async Task Copy_ConcurrentCall_IsBusy()
    {
        using var rig = new Rig(); var call = rig.Copy(); await rig.Bot.Prompted.Task;
        Assert.Equal("busy", Outcome(await rig.Copy()));
        await rig.Tap(); await call;
    }

    [Fact]
    public async Task Copy_LogsOnlyOutcomeKindAndElapsed_NoLabelOrNonce()
    {
        var logs = new CaptureLog();
        using var rig = new Rig(logs);
        var call = rig.Copy(); await rig.Bot.Prompted.Task; await rig.Tap(); await call;
        var line = Assert.Single(logs.Lines);
        Assert.Contains("outcome=copied", line);
        Assert.Contains("destination_kind=private", line);
        Assert.DoesNotContain("Synthetic recipient", line);
        Assert.DoesNotContain(rig.Bot.NonceData[4..^2], line);
        Assert.DoesNotContain("1001", line);
        Assert.DoesNotContain("2002", line);
    }
    private sealed class CaptureLog : ILogger<MessageCopyCoordinator>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    internal static string Outcome(string result) => JsonDocument.Parse(result).RootElement.GetProperty("outcome").GetString()!;
    internal sealed class Rig : IDisposable
    {
        public ManualTimeProvider Clock { get; } = new();
        public FakeBot Bot { get; } = new();
        public AllowlistHolder Allowlist { get; } = new(Options.Create(new TelegramOptions { AllowedUserIds = [1001, 2002], AllowedGroupIds = [-3003] }));
        public RunningTask Owner { get; private set; } = Task(TaskSource.UserMessage);
        public RunningTask? Current { get; set; }
        public long Chat { get; set; } = 1001;
        public MessageCopyCounter Counter { get; } = new();
        public MessageCopyCoordinator Coordinator { get; }
        public MessageCopyTools Tools { get; }
        public Rig(ILogger<MessageCopyCoordinator>? logger = null)
        {
            Current = Owner;
            Coordinator = new(() => (Chat, Current), Allowlist, () => Bot, Counter, logger ?? NullLogger<MessageCopyCoordinator>.Instance, Clock);
            Tools = new(Coordinator);
        }
        private static RunningTask Task(TaskSource source) => new()
        {
            Id = 1, Description = "synthetic", StartedAt = DateTimeOffset.UnixEpoch, Cts = new(), IsSessionTask = true, UserId = 1001, Source = source,
            Identity = new() { PrincipalId = "p", Role = PrincipalRole.Owner, ChannelId = ChannelIds.Telegram, ConversationId = "c", SubmissionId = "s", TurnId = "turn1", Attempt = 1 }
        };
        public void Replace(TaskSource source) { Owner = Task(source); Current = Owner; }
        public void Revoke(long user) => Allowlist.Apply(new([], [user], [], []));
        public Task<string> Copy(CancellationToken ct = default) => Coordinator.CopyAsync(77, 2002, ct);
        public Task Tap(long user = 1001, string? data = null, int prompt = 66) => Coordinator.HandleCallbackAsync(new("callback", user, 1001, prompt, data ?? Bot.NonceData));
        public void Dispose() => Owner.Cts.Dispose();
    }

    internal sealed class FakeBot : ITelegramCopyClient
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource Prompted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string NonceData { get; private set; } = "";
        public bool CleanupCancelled { get; private set; }
        public Func<string, CancellationToken, Task>? Before { get; set; }
        public int CopyId { get; set; } = 88;
        private async Task Call(string method, CancellationToken ct) { Calls.Add(method); if (Before is not null) await Before(method, ct); ct.ThrowIfCancellationRequested(); }
        public async Task<string> GetChatAsync(long chat, CancellationToken ct) { await Call("getChat", ct); return "Synthetic recipient"; }
        public async Task<int> SendPromptAsync(long chat, int message, string label, string nonce, CancellationToken ct) { await Call("sendMessage", ct); NonceData = $"cp1:{nonce}:y"; Prompted.TrySetResult(); return 66; }
        public async Task<int> CopyMessageAsync(long chat, long source, int message, CancellationToken ct) { await Call("copyMessage", ct); return CopyId; }
        public async Task AnswerCallbackAsync(string id, string text, CancellationToken ct) { CleanupCancelled |= ct.IsCancellationRequested; await Call("answerCallbackQuery", ct); }
        public async Task EditPromptAsync(long chat, int message, string text, CancellationToken ct) { CleanupCancelled |= ct.IsCancellationRequested; await Call("editMessageText", ct); }
    }
}
