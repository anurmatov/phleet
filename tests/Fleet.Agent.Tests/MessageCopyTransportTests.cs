using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Services;
using Fleet.Agent.Services.MessageCopy;
using Fleet.Journal.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
namespace Fleet.Agent.Tests;

public sealed class MessageCopyTransportTests
{
    internal const string SyntheticBotToken = "5005:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal static ServiceProvider Provider(bool copy = true, bool bot = true, string? root = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "agent1", ["Agent:Provider"] = "codex", ["Agent:Model"] = "model", ["Agent:WorkDir"] = root ?? Path.GetTempPath(),
            ["Telegram:MessageCopyEnabled"] = copy.ToString(), ["Telegram:BotToken"] = bot ? SyntheticBotToken : "",
            ["Telegram:AllowedUserIds:0"] = "1001", ["Telegram:AllowedUserIds:1"] = "2002",
            ["Journal:IngestToken"] = root is null ? null : "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddSingleton<IConfiguration>(config);
        services.AddAgentCoreServices(config); services.AddAgentDaemonServices(config);
        services.AddSingleton(Substitute.For<IAgentExecutor>());
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Transport_RealSdkGoldens_NoJournalOrTaskRouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "copy-wire-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var services = Provider(root: root);
            var transport = services.GetServices<IHostedService>().OfType<AgentTransport>().Single();
            var handler = new BotApi(); using var http = new HttpClient(handler);
            transport.BotForTesting = new TelegramBotClient(SyntheticBotToken, http);
            ITelegramCopyClient bot = transport;
            Assert.Equal("Synthetic recipient", await bot.GetChatAsync(2002, default));
            Assert.Equal(66, await bot.SendPromptAsync(1001, 77, "Synthetic recipient", "NONCE", default));
            Assert.Equal(88, await bot.CopyMessageAsync(2002, 1001, 77, default));
            await bot.AnswerCallbackAsync("callback", "copied", default);
            await bot.EditPromptAsync(1001, 66, "copied", default);
            foreach (var (method, fixture) in new[] { ("sendMessage", "prompt.json"), ("copyMessage", "copy.json") })
            {
                var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "message-copy", fixture)));
                Assert.True(JsonNode.DeepEquals(expected, handler.Requests.Single(r => r.Method == method).Body), handler.Requests.Single(r => r.Method == method).Body.ToJsonString());
            }
            var routed = 0; transport.RouterHookForTesting = _ => { routed++; return Task.CompletedTask; };
            var callback = new Update { CallbackQuery = new() { Id = "unrelated", From = new() { Id = 1001, FirstName = "Synthetic" }, Data = "other", ChatInstance = "synthetic" } };
            await (Task)typeof(AgentTransport).GetMethod("HandleTelegramUpdateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(transport, [callback])!;
            Assert.Equal(0, routed);
            Assert.Equal("answerCallbackQuery", handler.Requests[^1].Method);
            Assert.False(handler.Requests[^1].Body.AsObject().ContainsKey("text"));
            Assert.Empty(services.GetRequiredService<JournalSpool>().Pending());
            Assert.Equal(0, services.GetRequiredService<JournalCounters>().Get("journal_captured{direction=outbound}"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_OptIn_OnlyFlagEnablesListenerAndCallbackUpdates(bool enabled)
    {
        await using var services = Provider(copy: enabled);
        var hosted = services.GetServices<IHostedService>().ToArray();
        var transport = hosted.OfType<AgentTransport>().Single();
        Assert.Equal(enabled, transport.PollingUpdates.Contains(UpdateType.CallbackQuery));
        Assert.Equal(enabled, services.GetService<MessageCopyTools>() is not null);
        // Real production construction preserves the SDK retry policy for ordinary operations.
        var field = typeof(AgentTransport).GetField("_bot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bot = (TelegramBotClient)field.GetValue(transport)!;
        var options = (TelegramBotClientOptions)typeof(TelegramBotClient).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bot)!;
        Assert.Equal(new TelegramBotClientOptions(SyntheticBotToken).RetryCount, options.RetryCount);
        if (enabled)
        {
            Assert.Same(services.GetRequiredService<MessageCopyListener>(), hosted.OfType<MessageCopyListener>().Single());
            Assert.True(Array.FindIndex(hosted, s => s is MessageCopyListener) < Array.FindIndex(hosted, s => s is WarmupService));
        }
        else Assert.Equal(new[] { UpdateType.Message, UpdateType.MessageReaction }, transport.PollingUpdates);
    }

    [Fact]
    public async Task Startup_CopyEnabledWithoutBot_RefusesBeforeHealth()
    {
        await using var services = Provider(bot: false);
        Assert.Contains("message_copy_unavailable:no_telegram_bot", Assert.Throws<InvalidOperationException>(() => AgentHostRegistration.ValidateStartupConfiguration(services)).Message);
        Assert.Null(services.GetService<MessageCopyTools>());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Transport_SdkError_MakesOneRequest(int status)
    {
        await using var services = Provider(); var transport = services.GetServices<IHostedService>().OfType<AgentTransport>().Single();
        var handler = new BotApi { ErrorStatus = status }; using var http = new HttpClient(handler);
        transport.BotForTesting = new TelegramBotClient(SyntheticBotToken, http);
        var error = await Assert.ThrowsAsync<TelegramCopyException>(() => ((ITelegramCopyClient)transport).CopyMessageAsync(2002, 1001, 77, default));
        Assert.Equal(status, error.Status);
        Assert.Equal(1, error.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_OrdinaryReply429_RetriesWithAndWithoutCopy(bool enabled)
    {
        await using var services = Provider(copy: enabled);
        var transport = services.GetServices<IHostedService>().OfType<AgentTransport>().Single();
        var handler = new BotApi { ErrorStatus = 429, ErrorOnce = true };
        using var http = new HttpClient(handler);
        // Carry the production-constructed retry options into the wire test, rather
        // than silently replacing a broken production policy with SDK defaults.
        var polling = (TelegramBotClient)typeof(AgentTransport).GetField("_bot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(transport)!;
        var options = (TelegramBotClientOptions)typeof(TelegramBotClient).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(polling)!;
        transport.BotForTesting = new TelegramBotClient(options, http);
        await transport.SendHtmlTextAsync(1001, "Synthetic reply");
        Assert.Equal(2, handler.Requests.Count(r => r.Method == "sendMessage"));
    }

    private sealed class BotApi : HttpMessageHandler
    {
        public List<(string Method, JsonNode Body)> Requests { get; } = [];
        public int? ErrorStatus { get; init; }
        public bool ErrorOnce { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments[^1];
            Requests.Add((method, JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!));
            if (ErrorStatus is { } status && (!ErrorOnce || Requests.Count(r => r.Method == method) == 1)) return new((HttpStatusCode)status) { Content = new StringContent($"{{\"ok\":false,\"error_code\":{status},\"description\":\"Synthetic error\",\"parameters\":{{\"retry_after\":1}}}}", Encoding.UTF8, "application/json") };
            var result = method switch
            {
                "getChat" => "{\"id\":2002,\"type\":\"private\",\"first_name\":\"Synthetic\",\"last_name\":\"recipient\",\"accent_color_id\":0,\"max_reaction_count\":0}",
                "copyMessage" => "{\"message_id\":88}",
                "answerCallbackQuery" => "true",
                _ => "{\"message_id\":66,\"date\":1,\"chat\":{\"id\":1001,\"type\":\"private\"}}",
            };
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
        }
    }
}
