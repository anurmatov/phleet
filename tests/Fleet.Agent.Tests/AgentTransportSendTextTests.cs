using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
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

namespace Fleet.Agent.Tests;

/// <summary>
/// Integration-style tests for AgentTransport.SendTextAsync.
/// Verifies flag-ON/OFF routing and parse-entities fallback behavior.
/// Uses a fake ITelegramBotClient injected via the BotForTesting seam.
/// </summary>
public class AgentTransportSendTextTests
{
    // ── Helper: build a minimal AgentTransport with a captured fake bot ────────

    private static (AgentTransport transport, FakeAgentBot bot) BuildTransport(
        bool useFormatter, bool prefixMessages, string shortName = "agent1")
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1",
            Role = "generic-role",
            WorkDir = "/tmp/fleet-test",
            UseFormatter = useFormatter,
            PrefixMessages = prefixMessages,
            ShortName = shortName,
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts   = Options.Create(new RabbitMqOptions());
        var whisperOpts  = Options.Create(new WhisperOptions());
        var ttsOpts      = Options.Create(new TtsOptions());

        var executor   = Substitute.For<IAgentExecutor>();
        var sessions   = new SessionManager();
        var connState  = Substitute.For<IFleetConnectionState>();
        var httpFact   = Substitute.For<IHttpClientFactory>();
        var allowlist  = new AllowlistHolder(telegramOpts);
        var relay      = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var taskMgr    = new TaskManager(agentOpts, executor, sessions, NullLogger<TaskManager>.Instance);
        var prompts    = new PromptAssembler(executor);
        var cmdDisp    = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var voice      = new VoiceTranscriptionService(httpFact, whisperOpts, NullLogger<VoiceTranscriptionService>.Instance);
        var tts        = new TtsService(httpFact, ttsOpts, NullLogger<TtsService>.Instance);
        var groupBhvr  = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, cmdDisp, prompts, NullLogger<GroupBehavior>.Instance);
        var router     = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, groupBhvr, relay, cmdDisp, NullLogger<MessageRouter>.Instance);

        var transport = new AgentTransport(
            agentOpts, telegramOpts, allowlist, relay, taskMgr,
            groupBhvr, router, cmdDisp, voice, tts, connState,
            NullLogger<AgentTransport>.Instance);

        var fakeBot = new FakeAgentBot();
        transport.BotForTesting = fakeBot;
        return (transport, fakeBot);
    }

    // ── F2: flag-OFF plain branch (UseFormatter=false, PrefixMessages=false) ──

    [Fact]
    public async Task FlagOff_PlainBranch_SendsWithoutParseMode()
    {
        // UseFormatter=false, PrefixMessages=false: legacy SplitMessage(text, 4000), no ParseMode.
        // Byte-identity guarantee: same text, no HTML escaping applied by the formatter.
        var (transport, bot) = BuildTransport(useFormatter: false, prefixMessages: false);

        await transport.SendTextAsync(99, "hello **world**");

        Assert.Single(bot.Requests);
        var req = bot.Requests[0];
        // Legacy plain path sends with ParseMode.None (no parse_mode set)
        Assert.Equal(ParseMode.None, req.ParseMode);
        // Text must be byte-identical to input (no formatter transformation)
        Assert.Equal("hello **world**", req.Text);
    }

    [Fact]
    public async Task FlagOff_PlainBranch_LongTextSplitsAt4000()
    {
        // Legacy plain path splits at 4000, not 3990 or 4096 minus formatter reserve.
        var (transport, bot) = BuildTransport(useFormatter: false, prefixMessages: false);

        var text = new string('a', 4001); // just over the 4000-char boundary
        await transport.SendTextAsync(99, text);

        Assert.Equal(2, bot.Requests.Count);
        Assert.True(bot.Requests[0].Text.Length <= 4000);
        Assert.Equal(ParseMode.None, bot.Requests[0].ParseMode);
        Assert.Equal(ParseMode.None, bot.Requests[1].ParseMode);
    }

    // ── F2: flag-OFF prefix branch (UseFormatter=false, PrefixMessages=true) ───

    [Fact]
    public async Task FlagOff_PrefixBranch_SendsHtmlWithBoldPrefix()
    {
        // UseFormatter=false, PrefixMessages=true: legacy SplitMessage(text, 3990),
        // text is HtmlEncoded, wrapped with <b>Name:</b>\n prefix, sent with ParseMode.Html.
        var (transport, bot) = BuildTransport(useFormatter: false, prefixMessages: true, shortName: "agent1");

        await transport.SendTextAsync(99, "result: a < b");

        Assert.Single(bot.Requests);
        var req = bot.Requests[0];
        // Legacy prefix path always uses ParseMode.Html
        Assert.Equal(ParseMode.Html, req.ParseMode);
        // Bold prefix present
        Assert.StartsWith("<b>Agent1:</b>\n", req.Text);
        // Text is HTML-encoded (< → &lt;) but NOT reformatted by TelegramFormatter
        Assert.Contains("&lt;", req.Text);
        Assert.DoesNotContain("<b>result</b>", req.Text); // no bold from formatter
    }

    // ── F4: wiring — flag-ON routes through TelegramFormatter ─────────────────

    [Fact]
    public async Task FlagOn_FormatterPath_SendsWithHtmlParseMode()
    {
        // UseFormatter=true: FormatAndSplit is called; output always uses ParseMode.Html.
        var (transport, bot) = BuildTransport(useFormatter: true, prefixMessages: false);

        await transport.SendTextAsync(99, "**bold** and `code`");

        Assert.Single(bot.Requests);
        var req = bot.Requests[0];
        Assert.Equal(ParseMode.Html, req.ParseMode);
        // Formatter must have converted ** to <b> and ` to <code>
        Assert.Contains("<b>bold</b>", req.Text);
        Assert.Contains("<code>code</code>", req.Text);
    }

    [Fact]
    public async Task FlagOn_WithPrefix_ChunkFitsWithinBudget()
    {
        // UseFormatter=true, PrefixMessages=true: prefix budget is deducted per chunk.
        // prefix = "<b>Agent1:</b>\n" = 15 chars, so each chunk ≤ 4096 - 15 = 4081.
        // With prefix prepended, the total sent is prefix (15) + chunk (≤4081) = ≤4096.
        var (transport, bot) = BuildTransport(useFormatter: true, prefixMessages: true, shortName: "agent1");

        // 5000 plain chars → will be split. With prefix budget, each chunk ≤ 4083.
        var text = new string('x', 5000);
        await transport.SendTextAsync(99, text);

        Assert.True(bot.Requests.Count >= 2);
        foreach (var req in bot.Requests)
        {
            Assert.Equal(ParseMode.Html, req.ParseMode);
            Assert.True(req.Text.Length <= 4096, $"Chunk of {req.Text.Length} exceeds 4096");
            Assert.StartsWith("<b>Agent1:</b>\n", req.Text);
        }
    }

    // ── Parse-entities fallback (F1): dropped → degraded ─────────────────────

    [Fact]
    public async Task FlagOn_ParseEntitiesRejected_FallsBackToPlainDelivery()
    {
        // If the API rejects ParseMode.Html, SendMessageWithReplyFallbackAsync must
        // fall back to plain text rather than propagating the exception and dropping the message.
        var (transport, bot) = BuildTransport(useFormatter: true, prefixMessages: false);

        bot.ThrowOnHtml = true; // Reject any Html parse_mode send
        await transport.SendTextAsync(99, "**hello**");

        // The message must have been delivered (fallback succeeded, not thrown)
        Assert.True(bot.Requests.Count >= 1);
        // The fallback send must use ParseMode.None (no parse_mode set)
        var lastReq = bot.Requests[^1];
        Assert.Equal(ParseMode.None, lastReq.ParseMode);
    }
}

/// <summary>
/// Minimal ITelegramBotClient for AgentTransport tests. Captures SendMessage requests.
/// </summary>
internal sealed class FakeAgentBot : ITelegramBotClient
{
    public readonly List<SendMessageRequest> Requests = [];
    public bool ThrowOnHtml { get; set; }

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
        if (request is SendMessageRequest smr)
        {
            if (ThrowOnHtml && smr.ParseMode == ParseMode.Html)
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            Requests.Add(smr);
            var msg = new Message { Id = Requests.Count, Chat = new Chat { Id = smr.ChatId.Identifier ?? 0 } };
            return Task.FromResult((TResponse)(object)msg);
        }
        throw new InvalidOperationException($"Unexpected request type: {request.GetType().Name}");
    }

    public Task<TResponse> SendRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<TResponse> MakeRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task DownloadFile(global::Telegram.Bot.Types.TGFile file, Stream destination, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
