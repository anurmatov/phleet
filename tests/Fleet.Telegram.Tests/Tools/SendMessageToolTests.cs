using System.Text.Json;
using Fleet.Telegram.Services;
using Fleet.Telegram.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tests.Tools;

/// <summary>
/// Integration-style tests for SendMessageTool — covers fallback flags,
/// parse-mode behavior, and backward compat (reply threading, 403 path).
///
/// SendMessage is a Telegram.Bot extension method (not on ITelegramBotClient itself),
/// so we use FakeBotClient instead of NSubstitute.
/// </summary>
public class SendMessageToolTests
{
    private static SendMessageTool CreateTool(
        FakeBotClient agentBot,
        FakeBotClient? fallbackBot = null)
    {
        const string agentToken = "tok:agent";
        var factory = new BotClientFactory(
            NullLogger<BotClientFactory>.Instance,
            token => token == agentToken ? agentBot : agentBot);

        factory.ApplyAgentTokens(new Dictionary<string, string>
            { ["testagent"] = agentToken });

        if (fallbackBot is not null)
        {
            var field = typeof(BotClientFactory)
                .GetField("_notifierClient",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            field.SetValue(factory, fallbackBot);
        }

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?agent=testagent");
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        return new SendMessageTool(factory, accessor, NullLogger<SendMessageTool>.Instance);
    }

    // ── Empty message ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyMessageReturnsError()
    {
        var tool = CreateTool(new FakeBotClient());
        var json = await tool.SendAsync("123", "   ");
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("empty", doc.GetProperty("error").GetString());
    }

    // ── Test 8: Plain-text fallback, single chunk ─────────────────────────────

    [Fact]
    public async Task SingleChunkFormattingRejection_FallsBackToPlainText()
    {
        var bot = new FakeBotClient(req =>
        {
            if (req is SendMessageRequest smr && smr.ParseMode == ParseMode.Html)
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            return new Message { Id = 99, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("123", "**verdict**: success");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("format_fallback").GetBoolean());
        Assert.False(doc.TryGetProperty("error", out _));
    }

    // ── Test 9: Multi-chunk partial failure ───────────────────────────────────

    [Fact]
    public async Task MultiChunkPartialFailure_RemainingChunksSentAsPlain()
    {
        int callCount = 0;
        var bot = new FakeBotClient(req =>
        {
            callCount++;
            if (req is SendMessageRequest smr && smr.ParseMode == ParseMode.Html && callCount == 2)
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            return new Message { Id = callCount * 10, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var longText = "**important**: " + new string('a', 4000) + "\n**more**: " + new string('b', 4000);
        var json = await tool.SendAsync("123", longText);
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("format_fallback").GetBoolean());
    }

    // ── Test 10: parse_mode = PLAIN disables formatting ──────────────────────

    [Fact]
    public async Task ParseModePLAIN_SendsWithHtmlModeAndPreservesMarkers()
    {
        // PLAIN escapes HTML-special chars and sends with ParseMode.Html so the
        // escaping renders correctly. Markdown markers must survive as literal chars.
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        await tool.SendAsync("123", "**bold** text", parse_mode: "PLAIN");

        Assert.NotNull(captured);
        // PLAIN now uses ParseMode.Html to honor HTML-entity escaping
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        // No HTML conversion: ** must survive as literal asterisks
        Assert.DoesNotContain("<b>", captured.Text);
        Assert.Contains("**", captured.Text);
    }

    [Fact]
    public async Task ParseModePLAIN_AngleBracketsAreEscaped()
    {
        // PLAIN sends with ParseMode.Html. Angle brackets must be HTML-escaped so
        // Telegram renders them as the < > characters, not as unknown tags.
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        await tool.SendAsync("123", "text with <angle>", parse_mode: "PLAIN");

        Assert.NotNull(captured);
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        // Raw < must be escaped so Telegram's HTML parser sees &lt;angle&gt;
        Assert.Contains("&lt;angle&gt;", captured.Text);
        Assert.DoesNotContain("<angle>", captured.Text);
    }

    // ── Finding 1 regression: plain prose with angle brackets uses Html mode ──

    [Fact]
    public async Task PlainProseWithAngleBrackets_SentWithHtmlModeAndEscaped()
    {
        // Before fix: usingHtml was false for prose with no formatting tokens,
        // so text got escaped but sent with pm=null → &lt; rendered literally.
        // After fix: always use ParseMode.Html so escaping is correct.
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        await tool.SendAsync("123", "Result is List<string> and a < b");

        Assert.NotNull(captured);
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        Assert.Contains("&lt;string&gt;", captured.Text);
        Assert.DoesNotContain("List<string>", captured.Text); // raw < must not appear
    }

    // ── Finding 2 regression: reply thread preserved when format fallback fires ─

    [Fact]
    public async Task FormatFallback_PreservesReplyThread()
    {
        // Before fix: replyConsumed was set unconditionally before checking result.ok,
        // so the plain-text retry got pr=null and the reply thread was silently dropped.
        int callCount = 0;
        SendMessageRequest? firstPlainRequest = null;
        var bot = new FakeBotClient(req =>
        {
            callCount++;
            var smr = (SendMessageRequest)req;
            if (smr.ParseMode == ParseMode.Html)
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            firstPlainRequest ??= smr;
            return new Message { Id = callCount * 10, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("123", "**verdict**: success", reply_to_message_id: 42);
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("format_fallback").GetBoolean());
        // Reply must be carried to the first plain-text chunk, not dropped
        Assert.NotNull(firstPlainRequest?.ReplyParameters);
        Assert.Equal(42, firstPlainRequest!.ReplyParameters!.MessageId);
    }

    // ── Test 11a: Reply threading preserved for formatted message ─────────────

    [Fact]
    public async Task ReplyThreading_PreservedForFormattedMessage()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 42, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("123", "**hello**", reply_to_message_id: 77);
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.NotNull(captured?.ReplyParameters);
        Assert.Equal(77, captured!.ReplyParameters!.MessageId);
    }

    // ── Test 11b: 403 fallback-bot path preserved ─────────────────────────────

    [Fact]
    public async Task FallbackBot_UsedOn403_FlagPresent()
    {
        var agent = new FakeBotClient(_ =>
            throw new ApiRequestException("Forbidden: bot was blocked by the user", 403));
        var fallback = new FakeBotClient(_ =>
            new Message { Id = 55, Chat = new Chat { Id = 1 } });

        var tool = CreateTool(agent, fallback);
        var json = await tool.SendAsync("123", "plain message");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("fallback").GetBoolean());
    }

    // ── Test 11c: format_fallback distinct from fallback/reply_fallback ────────

    [Fact]
    public void FlagNamesDontCollide()
    {
        const string f1 = "fallback";
        const string f2 = "reply_fallback";
        const string f3 = "format_fallback";
        Assert.NotEqual(f1, f2);
        Assert.NotEqual(f1, f3);
        Assert.NotEqual(f2, f3);
    }

    // ── Backward compat: plain-text caller unchanged ──────────────────────────

    [Fact]
    public async Task PlainTextCaller_BehaviorUnchanged()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("123", "hello world");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.False(doc.TryGetProperty("format_fallback", out _));
        Assert.False(doc.TryGetProperty("fallback", out _));
        Assert.False(doc.TryGetProperty("reply_fallback", out _));
        Assert.NotNull(captured);
        Assert.Equal("hello world", captured!.Text);
        // Formatter always uses Html parse_mode; "hello world" has no special chars so it's safe
        Assert.Equal(ParseMode.Html, captured.ParseMode);
    }
}

/// <summary>
/// Minimal ITelegramBotClient implementation for testing. Routes all requests through
/// a configurable handler. Needed because SendMessage is an extension method and
/// NSubstitute cannot intercept extension method calls.
/// </summary>
internal sealed class FakeBotClient : ITelegramBotClient
{
    private readonly Func<object, object>? _handler;

    public FakeBotClient(Func<object, object>? handler = null) => _handler = handler;

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
        if (_handler is null) throw new InvalidOperationException("No handler configured");
        var result = _handler(request);
        if (result is Exception ex) throw ex;
        return Task.FromResult((TResponse)result);
    }

    public Task<TResponse> SendRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<TResponse> MakeRequest<TResponse>(
        IRequest<TResponse> request, CancellationToken cancellationToken = default)
        => MakeRequestAsync(request, cancellationToken);

    public Task<bool> TestApi(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
