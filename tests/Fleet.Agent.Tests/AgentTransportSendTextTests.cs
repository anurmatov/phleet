using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Fleet.Shared;
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

namespace Fleet.Agent.Tests;

/// <summary>
/// Integration-style tests for AgentTransport.SendTextAsync.
/// Verifies FormattingMode routing and parse-entities fallback behavior.
/// Uses a fake ITelegramBotClient injected via the BotForTesting seam.
/// </summary>
public class AgentTransportSendTextTests
{
    // ── Helper: build a minimal AgentTransport with a captured fake bot ────────

    private static (AgentTransport transport, FakeAgentBot bot) BuildTransport(
        FormattingMode formattingMode = FormattingMode.PlainText,
        bool prefixMessages = false,
        string shortName = "agent1",
        RichFallbackCounter? counter = null,
        ILogger<AgentTransport>? logger = null)
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "fleet-agent1",
            Role = "generic-role",
            WorkDir = "/tmp/fleet-test",
            FormattingMode = formattingMode,
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
            logger ?? NullLogger<AgentTransport>.Instance,
            counter);

        var fakeBot = new FakeAgentBot();
        transport.BotForTesting = fakeBot;
        return (transport, fakeBot);
    }

    // ── PlainText: byte-identical output, no formatting ───────────────────────

    [Fact]
    public async Task PlainText_SendsWithoutParseMode()
    {
        // FormattingMode.PlainText: legacy split at 4000, no ParseMode.
        // Byte-identity guarantee: same text, no HTML escaping applied by the formatter.
        var (transport, bot) = BuildTransport(formattingMode: FormattingMode.PlainText, prefixMessages: false);

        await transport.SendTextAsync(99, "hello **world**");

        Assert.Single(bot.Requests);
        var req = bot.Requests[0];
        // Plain path sends with ParseMode.None (no parse_mode set)
        Assert.Equal(ParseMode.None, req.ParseMode);
        // Text must be byte-identical to input (no formatter transformation)
        Assert.Equal("hello **world**", req.Text);
    }

    [Fact]
    public async Task PlainText_LongTextSplitsAt4000()
    {
        // PlainText splits at 4000, not 3990 or 4096 minus formatter reserve.
        var (transport, bot) = BuildTransport(formattingMode: FormattingMode.PlainText, prefixMessages: false);

        var text = new string('a', 4001); // just over the 4000-char boundary
        await transport.SendTextAsync(99, text);

        Assert.Equal(2, bot.Requests.Count);
        Assert.True(bot.Requests[0].Text.Length <= 4000);
        Assert.Equal(ParseMode.None, bot.Requests[0].ParseMode);
        Assert.Equal(ParseMode.None, bot.Requests[1].ParseMode);
    }

    // ── PlainText + prefix branch ────────────────────────────────────────────

    [Fact]
    public async Task PlainText_WithPrefix_SendsHtmlWithBoldPrefix()
    {
        // FormattingMode.PlainText, PrefixMessages=true: legacy split at 3990,
        // text is HtmlEncoded, wrapped with <b>Name:</b>\n prefix, sent with ParseMode.Html.
        var (transport, bot) = BuildTransport(formattingMode: FormattingMode.PlainText, prefixMessages: true, shortName: "agent1");

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

    // ── LegacyHtml: wiring — routes through TelegramFormatter ─────────────────

    [Fact]
    public async Task LegacyHtml_FormatterPath_SendsWithHtmlParseMode()
    {
        // FormattingMode.LegacyHtml: FormatAndSplit is called; output always uses ParseMode.Html.
        var (transport, bot) = BuildTransport(formattingMode: FormattingMode.LegacyHtml, prefixMessages: false);

        await transport.SendTextAsync(99, "**bold** and `code`");

        Assert.Single(bot.Requests);
        var req = bot.Requests[0];
        Assert.Equal(ParseMode.Html, req.ParseMode);
        // Formatter must have converted ** to <b> and ` to <code>
        Assert.Contains("<b>bold</b>", req.Text);
        Assert.Contains("<code>code</code>", req.Text);
    }

    [Fact]
    public async Task LegacyHtml_WithPrefix_ChunkFitsWithinBudget()
    {
        // FormattingMode.LegacyHtml, PrefixMessages=true: prefix budget is deducted per chunk.
        // prefix = "<b>Agent1:</b>\n" = 15 chars, so each chunk ≤ 4096 - 15 = 4081.
        var (transport, bot) = BuildTransport(formattingMode: FormattingMode.LegacyHtml, prefixMessages: true, shortName: "agent1");

        // 5000 plain chars → will be split. With prefix budget, each chunk ≤ 4081.
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

    // ── Parse-entities fallback (LegacyHtml): dropped → degraded ──────────────

    [Fact]
    public async Task LegacyHtml_ParseEntitiesRejected_FallsBackToPlainDelivery()
    {
        // If the API rejects ParseMode.Html, fallback to plain text — message must not be dropped.
        var (transport, bot) = BuildTransport(formattingMode: FormattingMode.LegacyHtml, prefixMessages: false);

        bot.ThrowOnHtml = true; // Reject any Html parse_mode send
        await transport.SendTextAsync(99, "**hello**");

        // The message must have been delivered (fallback succeeded, not thrown)
        Assert.True(bot.Requests.Count >= 1);
        // The fallback send must use ParseMode.None (no parse_mode set)
        var lastReq = bot.Requests[^1];
        Assert.Equal(ParseMode.None, lastReq.ParseMode);
    }

    // ── Rich: sendRichMessage fails → LegacyHtml fallback, counter+warning ────

    [Fact]
    public async Task Rich_SendRichFails_FallsBackToLegacyHtml_IncrementsCounter()
    {
        // FakeAgentBot throws InvalidOperationException for any non-SendMessageRequest,
        // which simulates sendRichMessage failing (e.g. in a private DM where it is not supported).
        // The fallback chain must catch this, fall back to LegacyHtml, and increment the counter.
        var counter = new RichFallbackCounter();
        var (transport, bot) = BuildTransport(
            formattingMode: FormattingMode.Rich, prefixMessages: false, counter: counter);

        await transport.SendTextAsync(99, "**bold** text");

        // Counter must be incremented for rich_to_html fallback
        Assert.Equal(1, counter.GetCount("fleet-agent1", "rich_to_html"));
        // Message must still be delivered via LegacyHtml (SendMessageRequest with Html parse mode)
        Assert.True(bot.Requests.Count >= 1);
        Assert.Equal(ParseMode.Html, bot.Requests[0].ParseMode);
    }

    [Fact]
    public async Task Rich_SendRichFails_LogsWarning()
    {
        // The Warning log must be emitted when sendRichMessage fails.
        var capturingLogger = new CapturingLogger<AgentTransport>();
        var counter = new RichFallbackCounter();
        var (transport, bot) = BuildTransport(
            formattingMode: FormattingMode.Rich, prefixMessages: false,
            counter: counter, logger: capturingLogger);

        await transport.SendTextAsync(99, "hello");

        // A Warning with failureType=rich_to_html must have been logged
        var warnings = capturingLogger.Entries
            .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("rich_to_html"))
            .ToList();
        Assert.True(warnings.Count >= 1, "Expected at least one Warning log mentioning rich_to_html");
    }

    [Fact]
    public async Task Rich_SendRichFails_HtmlAlsoFails_FallsBackToPlain_IncrementsCounter()
    {
        // When both sendRichMessage and LegacyHtml fail with a non-parse-entities error,
        // the counter for html_to_plain is incremented and the message is delivered as plain text.
        // Note: ThrowOnHtml (parse-entities) is caught INSIDE SendMessageWithReplyFallbackAsync
        // and doesn't propagate. We use ThrowGenericOnHtml which throws an unexpected exception
        // that does propagate, triggering the html_to_plain fallback in SendRichWithFallbackAsync.
        var counter = new RichFallbackCounter();
        var (transport, bot) = BuildTransport(
            formattingMode: FormattingMode.Rich, prefixMessages: false, counter: counter);

        bot.ThrowGenericOnHtml = true; // Non-parse-entities error — propagates past SendMessageWithReplyFallbackAsync

        await transport.SendTextAsync(99, "**hello**");

        // Both fallback counters should be incremented
        Assert.Equal(1, counter.GetCount("fleet-agent1", "rich_to_html"));
        Assert.Equal(1, counter.GetCount("fleet-agent1", "html_to_plain"));
        // Message must still be delivered (plain text last resort)
        Assert.True(bot.Requests.Count >= 1);
        Assert.Equal(ParseMode.None, bot.Requests[^1].ParseMode);
    }
}

/// <summary>
/// Minimal ITelegramBotClient for AgentTransport tests. Captures SendMessage requests.
/// Throws InvalidOperationException for any request type it doesn't recognise — this
/// naturally simulates sendRichMessage failures since its request type is not SendMessageRequest.
/// </summary>
internal sealed class FakeAgentBot : ITelegramBotClient
{
    public readonly List<SendMessageRequest> Requests = [];
    /// <summary>Throw a parse-entities ApiRequestException for Html sends (caught internally by SendMessageWithReplyFallbackAsync).</summary>
    public bool ThrowOnHtml { get; set; }
    /// <summary>Throw a generic non-parse-entities exception for Html sends (propagates up to SendRichWithFallbackAsync's LegacyHtml catch).</summary>
    public bool ThrowGenericOnHtml { get; set; }

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
            if (ThrowGenericOnHtml && smr.ParseMode == ParseMode.Html)
                throw new Exception("Simulated network error (not a parse-entities error)");
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

/// <summary>
/// Simple ILogger that captures log entries for test assertions.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public record LogEntry(LogLevel Level, string Message);
    public readonly List<LogEntry> Entries = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
