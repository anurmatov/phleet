using System.Net;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

public sealed class TurnBindingPublisherTests
{
    [Theory]
    [InlineData(TaskSource.UserMessage, true)]
    [InlineData(TaskSource.NewCommand, true)]
    [InlineData(TaskSource.DebouncedGroupBatch, true)]
    [InlineData(TaskSource.CheckIn, true)]
    [InlineData(TaskSource.Relay, false)]
    [InlineData(TaskSource.Bridge, false)]
    public void BeginTurn_UsesOnlyObservedHumanChat(TaskSource source, bool bound)
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://journal.test") };
        using var publisher = Build(http);
        publisher.ObserveChat(101, 7001, "private");
        var seq = publisher.BeginTurn(101, source);
        Assert.Equal(bound ? "bound" : "unbound", publisher.Current.State);
        Assert.Equal(bound ? 101L : (long?)null, publisher.Current.ChatId);
        publisher.EndTurn(seq);
        Assert.Equal("unbound", publisher.Current.State);
        Assert.True(publisher.Current.Seq > seq);
        publisher.BeginTurn(202, source);
        Assert.Equal("unbound", publisher.Current.State);
        publisher.ObserveChat(ConversationRegistry.ReservedBandStart, 7001, "private");
        publisher.BeginTurn(ConversationRegistry.ReservedBandStart, source);
        Assert.Equal("unbound", publisher.Current.State);
    }

    [Fact]
    public void LateEnd_CannotClearANewerTurn()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://journal.test") };
        using var publisher = Build(http);
        publisher.ObserveChat(101, 7001, "private");
        publisher.ObserveChat(-202, 7001, "supergroup");
        var first = publisher.BeginTurn(101, TaskSource.UserMessage);
        var second = publisher.BeginTurn(-202, TaskSource.UserMessage);
        publisher.EndTurn(first);
        Assert.Equal(second, publisher.Current.Seq);
        Assert.Equal(-202, publisher.Current.ChatId);
    }

    [Fact]
    public async Task Loop_UsesLatestState_AndNeverBlocksTurnUpdates()
    {
        var handler = new BlockingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        using var publisher = Build(http);
        await publisher.StartAsync(default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        publisher.ObserveChat(101, 7001, "private");
        publisher.BeginTurn(101, TaskSource.UserMessage);
        Assert.Equal("bound", publisher.Current.State);
        handler.Release.TrySetResult();
        await handler.Second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"chatId\":101", handler.SecondBody);
        await publisher.StopAsync(default);
    }

    [Theory]
    [InlineData(global::Telegram.Bot.Types.Enums.ChatType.Private, "private")]
    [InlineData(global::Telegram.Bot.Types.Enums.ChatType.Group, "group")]
    [InlineData(global::Telegram.Bot.Types.Enums.ChatType.Supergroup, "supergroup")]
    public async Task InboundTransport_CopiesPlatformKindAndBotId(global::Telegram.Bot.Types.Enums.ChatType type, string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "binding-test-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient { BaseAddress = new Uri("http://journal.test") };
        using var publisher = Build(http);
        try
        {
            var chatId = type == global::Telegram.Bot.Types.Enums.ChatType.Private ? JournalCaptureTests.User : JournalCaptureTests.Group;
            var rig = JournalCaptureTests.Rig.Build(root, botId: 5001, publisher: publisher);
            await rig.Transport.OnMessage(new global::Telegram.Bot.Types.Message
            {
                Id = 7, Text = "hello", Date = DateTime.UtcNow,
                Chat = new global::Telegram.Bot.Types.Chat { Id = chatId, Type = type },
                From = new global::Telegram.Bot.Types.User { Id = JournalCaptureTests.User, FirstName = "User" },
            }, global::Telegram.Bot.Types.Enums.UpdateType.Message);
            publisher.BeginTurn(chatId, TaskSource.UserMessage);
            Assert.Equal("bound", publisher.Current.State);
            Assert.Equal(kind, publisher.Current.ChatKind);
            Assert.Equal(5001, publisher.Current.BotId);
            Assert.Equal(chatId, publisher.Current.ChatId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static TurnBindingPublisher Build(HttpClient http) => new(
        new JournalHttpClient(http, "synthetic-token"), NullLogger<TurnBindingPublisher>.Instance);

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SecondBody;
        private int _calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            else
            {
                SecondBody = await request.Content!.ReadAsStringAsync(ct);
                Second.TrySetResult();
            }
            return new(HttpStatusCode.NoContent);
        }
    }
}
