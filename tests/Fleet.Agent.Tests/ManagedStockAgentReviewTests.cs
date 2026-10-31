using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Fleet.Agent.Tests;

/// <summary>
/// #492 review: managed journal attachment sends keep the journal-specific zero-retry contract. They used
/// the managed reply client (SDK RetryCount 3), so a synthetic 429 was retried inside the send and an
/// upload retry could re-send disposed content.
/// </summary>
public sealed class ManagedStockAgentJournalSendTests
{
    private const string P = "openshell:resolve:env:v7_TELEGRAM_BOT_TOKEN";
    private static readonly string Token = StockTelegramHttpHandler.SdkToken(424242, P)!;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static HttpResponseMessage TooManyRequests() => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(
            "{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests: retry after 1\",\"parameters\":{\"retry_after\":1}}",
            Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Managed_send_client_surfaces_a_429_after_one_request_through_the_placeholder_handler()
    {
        var inner = new ManagedStockAgentTelegramTests.Stub { Respond = _ => TooManyRequests() };
        var bot = AgentTransport.ManagedJournalSendBot(Token, inner);

        var error = await Assert.ThrowsAsync<ApiRequestException>(() => bot.SendMessage(1, "x"));

        Assert.Equal(429, error.ErrorCode);
        var only = Assert.Single(inner.Requests);
        Assert.Equal("https://api.telegram.org/bot" + P + "/sendMessage", only.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Managed_send_client_never_resends_an_upload_after_a_429()
    {
        var inner = new ManagedStockAgentTelegramTests.Stub { Respond = _ => TooManyRequests() };
        var bot = AgentTransport.ManagedJournalSendBot(Token, inner);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("synthetic upload"));

        var error = await Assert.ThrowsAsync<ApiRequestException>(() =>
            bot.SendDocument(1, global::Telegram.Bot.Types.InputFile.FromStream(stream, "a.txt")));

        // A retry would re-send multipart content the SDK already disposed: exactly one request leaves.
        Assert.Equal(429, error.ErrorCode);
        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Managed_SendEnabled_wires_a_separate_zero_retry_client_and_keeps_reply_retries(bool sendEnabled)
    {
        var transport = Transport(sendEnabled);
        var normal = (TelegramBotClient)typeof(AgentTransport).GetField("_bot", Flags)!.GetValue(transport)!;
        var sending = (TelegramBotClient?)typeof(AgentTransport).GetField("_journalSendBot", Flags)!.GetValue(transport);
        var options = typeof(TelegramBotClient).GetField("_options", Flags)!;
        Assert.Equal(3, ((TelegramBotClientOptions)options.GetValue(normal)!).RetryCount);
        if (!sendEnabled)
        {
            Assert.Null(sending);
            return;
        }
        Assert.NotNull(sending);
        Assert.NotSame(normal, sending);
        Assert.Equal(normal.Token, sending!.Token);
        Assert.Equal(0, ((TelegramBotClientOptions)options.GetValue(sending)!).RetryCount);
        // The send client goes through the stock placeholder handler, never straight to the network.
        var http = (HttpClient)typeof(TelegramBotClient).GetField("_httpClient", Flags)!.GetValue(sending)!;
        var handler = typeof(HttpMessageInvoker).GetField("_handler", Flags)!.GetValue(http);
        Assert.IsType<StockTelegramHttpHandler>(handler);
    }

    private static AgentTransport Transport(bool sendEnabled)
    {
        var agentOpts = Options.Create(new AgentOptions { Name = "fleet-agent1", Role = "generic-role", WorkDir = "/tmp/fleet-test", ShortName = "agent1" });
        var telegramOpts = Options.Create(new TelegramOptions { BotToken = Token });
        var executor = Substitute.For<IAgentExecutor>();
        var allowlist = new AllowlistHolder(telegramOpts);
        var relay = new GroupRelayService(agentOpts, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var taskMgr = new TaskManager(agentOpts, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        var cmdDisp = new CommandDispatcher(taskMgr, executor, agentOpts, NullLogger<CommandDispatcher>.Instance);
        var httpFact = Substitute.For<IHttpClientFactory>();
        var voice = new VoiceTranscriptionService(httpFact, Options.Create(new WhisperOptions()), NullLogger<VoiceTranscriptionService>.Instance);
        var tts = new TtsService(httpFact, Options.Create(new TtsOptions()), NullLogger<TtsService>.Instance);
        var groupBhvr = new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay, taskMgr, cmdDisp, new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agentOpts, telegramOpts, allowlist, taskMgr, groupBhvr, relay, cmdDisp, NullLogger<MessageRouter>.Instance);
        // Only presence matters to the constructor: the stock startup gate registers the identity.
        var stock = (StockBrokerIdentity)RuntimeHelpers.GetUninitializedObject(typeof(StockBrokerIdentity));
        var journal = (JournalCapture)RuntimeHelpers.GetUninitializedObject(typeof(JournalCapture));
        typeof(JournalCapture).GetField("_sendEnabled", Flags)!.SetValue(journal, sendEnabled);
        return new AgentTransport(agentOpts, telegramOpts, allowlist, relay, taskMgr, groupBhvr, router, cmdDisp, voice, tts,
            Substitute.For<IFleetConnectionState>(), NullLogger<AgentTransport>.Instance, new MessageSinkHolder(),
            journal: journal, stockEnrollment: stock);
    }
}
