using System.Text.Json;
using Fleet.Telegram.Services;
using Fleet.Telegram.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Tests.Tools;

public class SendToCeoToolTests
{
    private static SendToCeoTool CreateTool(
        FakeBotClient agentBot,
        FakeBotClient? fallbackBot = null,
        long ceoChatId = 123456789)
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

        var ceoConfig = new CeoConfigService();
        if (ceoChatId != 0)
            ceoConfig.Apply(ceoChatId.ToString());

        return new SendToCeoTool(factory, accessor, ceoConfig, NullLogger<SendToCeoTool>.Instance);
    }

    // ── CEO chat ID not configured ─────────────────────────────────────────────

    [Fact]
    public async Task NoCeoChatId_ReturnsError()
    {
        var tool = CreateTool(new FakeBotClient(), ceoChatId: 0);
        var json = await tool.SendAsync("hello");
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.False(doc.GetProperty("ok").GetBoolean());
        Assert.Contains("CEO chat ID", doc.GetProperty("error").GetString());
    }

    // ── flag on: auto-detect formats markdown to HTML ─────────────────────────

    [Fact]
    public async Task AutoDetect_FormatsMarkdownToHtml()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("**bold** and `code`");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.NotNull(captured);
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        Assert.Contains("<b>", captured.Text);
        Assert.Contains("<code>", captured.Text);
    }

    // ── flag on: angle brackets are HTML-escaped ─────────────────────────────

    [Fact]
    public async Task AutoDetect_AngleBracketsEscaped()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        await tool.SendAsync("result is a < b");

        Assert.NotNull(captured);
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        Assert.Contains("&lt;", captured.Text);
        Assert.DoesNotContain(" < ", captured.Text);
    }

    // ── flag off (backward compat): plain prose unchanged ─────────────────────

    [Fact]
    public async Task PlainProse_TextBodyUnchanged()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("hello world");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.False(doc.TryGetProperty("format_fallback", out _));
        Assert.NotNull(captured);
        // Plain prose with no special chars is byte-identical through the formatter
        Assert.Equal("hello world", captured!.Text);
        Assert.Equal(ParseMode.Html, captured.ParseMode);
    }

    // ── explicit PLAIN: markers preserved, HTML-special chars escaped ──────────

    [Fact]
    public async Task ParseModePLAIN_MarkdownMarkersPreservedAsLiteral()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        await tool.SendAsync("**bold** text", parse_mode: "PLAIN");

        Assert.NotNull(captured);
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        Assert.DoesNotContain("<b>", captured.Text);
        Assert.Contains("**", captured.Text);
    }

    [Fact]
    public async Task ParseModePLAIN_AngleBracketsEscaped()
    {
        SendMessageRequest? captured = null;
        var bot = new FakeBotClient(req =>
        {
            captured = req as SendMessageRequest;
            return new Message { Id = 1, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        await tool.SendAsync("result <angle>", parse_mode: "PLAIN");

        Assert.NotNull(captured);
        Assert.Equal(ParseMode.Html, captured!.ParseMode);
        Assert.Contains("&lt;angle&gt;", captured.Text);
        Assert.DoesNotContain("<angle>", captured.Text);
    }

    // ── formatting rejected by API → resend unformatted ──────────────────────

    [Fact]
    public async Task FormattingRejectedByApi_ResentsAsPlainText()
    {
        var bot = new FakeBotClient(req =>
        {
            if (req is SendMessageRequest smr && smr.ParseMode == ParseMode.Html)
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            return new Message { Id = 99, Chat = new Chat { Id = 1 } };
        });

        var tool = CreateTool(bot);
        var json = await tool.SendAsync("**verdict**: success");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("format_fallback").GetBoolean());
        Assert.False(doc.TryGetProperty("error", out _));
    }

    // ── notifier fallback still fires on 403 ─────────────────────────────────

    [Fact]
    public async Task FallbackBot_UsedOn403_FlagPresent()
    {
        var agent = new FakeBotClient(_ =>
            throw new ApiRequestException("Forbidden: bot was blocked by the user", 403));
        var fallback = new FakeBotClient(_ =>
            new Message { Id = 55, Chat = new Chat { Id = 1 } });

        var tool = CreateTool(agent, fallback);
        var json = await tool.SendAsync("escalation message");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("fallback").GetBoolean());
    }

    // ── both format_fallback and fallback can fire together ───────────────────

    [Fact]
    public async Task FormatFallback_ThenFallbackBot_BothFlagsSet()
    {
        bool firstCall = true;
        var agent = new FakeBotClient(req =>
        {
            if (firstCall)
            {
                firstCall = false;
                throw new ApiRequestException("Bad Request: can't parse entities in the message");
            }
            throw new ApiRequestException("Forbidden: bot was blocked by the user", 403);
        });
        var fallbackBot = new FakeBotClient(_ =>
            new Message { Id = 77, Chat = new Chat { Id = 1 } });

        var tool = CreateTool(agent, fallbackBot);
        var json = await tool.SendAsync("**test**");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("format_fallback").GetBoolean());
        Assert.True(doc.GetProperty("fallback").GetBoolean());
    }

    // ── success returns message_id ────────────────────────────────────────────

    [Fact]
    public async Task SuccessfulSend_ReturnsMessageId()
    {
        var bot = new FakeBotClient(_ => new Message { Id = 42, Chat = new Chat { Id = 1 } });
        var tool = CreateTool(bot);
        var json = await tool.SendAsync("hello");
        var doc = JsonDocument.Parse(json).RootElement;

        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.Equal(42, doc.GetProperty("message_id").GetInt32());
    }
}
