using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TGFile = Telegram.Bot.Types.TGFile;

namespace Fleet.Agent.Tests;

/// <summary>
/// AC12 and Constraint 3: a reserved-band (non-Telegram) conversation must never reach the bot
/// client. One test per IMessageSink method, because the guard is per method and a missing one
/// would leak a private client conversation into Telegram.
/// </summary>
public class ReservedKeyGuardTests
{
    private const long ReservedKey = 1L << 56;
    private const long TelegramKey = 99L;

    private static (AgentTransport transport, RecordingBot bot) BuildTransport()
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1",
            Role = "generic-role",
            WorkDir = "/tmp/fleet-test",
            FormattingMode = Fleet.Shared.FormattingMode.PlainText,
            ShortName = "agent1",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts = Options.Create(new RabbitMqOptions());
        var whisperOpts = Options.Create(new WhisperOptions());
        var ttsOpts = Options.Create(new TtsOptions());

        var executor = Substitute.For<IAgentExecutor>();
        var sessions = new SessionManager();
        var connState = Substitute.For<IFleetConnectionState>();
        var httpFact = Substitute.For<IHttpClientFactory>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var taskMgr = new TaskManager(agentOpts, executor, sessions, NullLogger<TaskManager>.Instance);
        var prompts = new PromptAssembler(executor);
        var cmdDisp = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var voice = new VoiceTranscriptionService(httpFact, whisperOpts, NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFact, ttsOpts, NullLogger<TtsService>.Instance);
        var groupBhvr = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, cmdDisp, prompts, NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, groupBhvr, relay, cmdDisp, NullLogger<MessageRouter>.Instance);

        var transport = new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, taskMgr,
            groupBhvr, router, cmdDisp, voice, tts, connState,
            NullLogger<AgentTransport>.Instance, null);

        var bot = new RecordingBot();
        transport.BotForTesting = bot;
        return (transport, bot);
    }

    [Fact]
    public async Task SendTextAsync_DoesNotTouchTheBotClientForAReservedKey()
    {
        var (transport, bot) = BuildTransport();

        await transport.SendTextAsync(ReservedKey, "a private client conversation");

        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task SendHtmlTextAsync_DoesNotTouchTheBotClientForAReservedKey()
    {
        var (transport, bot) = BuildTransport();

        await transport.SendHtmlTextAsync(ReservedKey, "<b>tool</b>");

        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task SendPhotoAsync_DoesNotTouchTheBotClientForAReservedKey()
    {
        var (transport, bot) = BuildTransport();

        // A non-existent path would otherwise take the "photo not found" branch and still send a
        // text message, so this also proves the guard runs BEFORE that fallback.
        await transport.SendPhotoAsync(ReservedKey, "/tmp/definitely-not-here.png", "caption");

        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task SendTypingAsync_DoesNotTouchTheBotClientForAReservedKey()
    {
        var (transport, bot) = BuildTransport();

        await transport.SendTypingAsync(ReservedKey);

        Assert.Empty(bot.ChatActions);
    }

    /// <summary>
    /// The negative control. Without this, all four tests above would pass just as well against a
    /// transport whose bot client was simply never wired up.
    /// </summary>
    [Fact]
    public async Task ATelegramKey_StillReachesTheBotClient()
    {
        var (transport, bot) = BuildTransport();

        await transport.SendTextAsync(TelegramKey, "an ordinary Telegram message");

        Assert.Single(bot.Requests);
        Assert.Equal("an ordinary Telegram message", bot.Requests[0].Text);
        Assert.Equal(ParseMode.None, bot.Requests[0].ParseMode);
    }

    [Fact]
    public async Task ATelegramKey_StillReachesTheBotClientForTyping()
    {
        var (transport, bot) = BuildTransport();

        await transport.SendTypingAsync(TelegramKey);

        Assert.Single(bot.ChatActions);
    }
}

/// <summary>
/// A recording bot client local to this suite.
///
/// Deliberately NOT the fake in the existing send-text suite: those files must pass unmodified to
/// prove the change is additive, and this one needs to record chat actions and photo sends too.
/// </summary>
internal sealed class RecordingBot : ITelegramBotClient
{
    public readonly List<SendMessageRequest> Requests = [];
    public readonly List<SendChatActionRequest> ChatActions = [];
    public readonly List<SendPhotoRequest> Photos = [];

    public bool LocalBotServer => false;
    public long BotId => 1;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

#pragma warning disable CS0067
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
#pragma warning restore CS0067

    public Task<TResponse> MakeRequestAsync<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        switch (request)
        {
            case SendMessageRequest smr:
                Requests.Add(smr);
                return Task.FromResult((TResponse)(object)new Message
                {
                    Id = Requests.Count,
                    Chat = new Chat { Id = smr.ChatId.Identifier ?? 0 },
                });
            case SendChatActionRequest scar:
                ChatActions.Add(scar);
                return Task.FromResult((TResponse)(object)true);
            case SendPhotoRequest spr:
                Photos.Add(spr);
                return Task.FromResult((TResponse)(object)new Message
                {
                    Id = Photos.Count,
                    Chat = new Chat { Id = spr.ChatId.Identifier ?? 0 },
                });
            default:
                throw new InvalidOperationException($"Unexpected request type: {request.GetType().Name}");
        }
    }

    public Task<TResponse> SendRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<TResponse> MakeRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> TestApiAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task DownloadFileAsync(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
