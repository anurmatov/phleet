using System.Text.Json;
using System.Reflection;
using Telegram.Bot;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Tests;

public sealed class ExternalReplyTests
{
    [Fact]
    public void Source_ReplyRenderers_HaveNoLegacyQuoteMarker()
    {
        var root = Fleet.Agent.Tests.Harness.RepoPaths.Resolve("src/Fleet.Agent");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("Replying to", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnMessage_ExternalReplyAndQuote_DoesNotSupplyReplyMetadata(bool group)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var rig = JournalCaptureTests.Rig.Build(root, journal: false);
            var json = $$$"""
                {"message_id":7,"date":1790848800,
                 "chat":{"id":{{{(group ? JournalCaptureTests.Group : JournalCaptureTests.User)}}},"type":"{{{(group ? "supergroup" : "private")}}}"},
                 "from":{"id":111,"is_bot":false,"first_name":"User"},"text":"hi",
                 "external_reply":{"origin":{"type":"user","date":1790848700,"sender_user":{"id":222,"is_bot":false,"first_name":"untrusted_sender"}},"message_id":5},
                 "quote":{"text":"untrusted_quote","position":0}}
                """;
            var message = JsonSerializer.Deserialize<Message>(json, JsonBotAPI.Options)!;
            await rig.Transport.OnMessage(message, UpdateType.Message);
            var routed = Assert.Single(rig.Routed);
            Assert.Null(routed.ReplyToTelegramMessageId);
            var executor = Substitute.For<IAgentExecutor>();
            var assembler = new PromptAssembler(executor);
            var buffer = new GroupChatBuffer { ChatId = routed.ChatId };
            var prompt = group
                ? assembler.ForGroupMessage(buffer, routed.Sender, routed.Text, routed.ReplyToTelegramMessageId, routed.TelegramMessageId)
                : assembler.ForDm(buffer, routed.Text, routed.ReplyToTelegramMessageId, routed.TelegramMessageId);
            Assert.DoesNotContain("reply_to_message_id", prompt);
            Assert.DoesNotContain("untrusted_quote", prompt);
            Assert.DoesNotContain("untrusted_sender", prompt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnMessage_LocalReply_RoutesIdsThroughRegularAndNewTasks(bool group, bool newCommand)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var rig = JournalCaptureTests.Rig.Build(root, journal: false);
            rig.Transport.RouterHookForTesting = null;
            typeof(Fleet.Agent.Interfaces.AgentTransport).GetField("_botUsername", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(rig.Transport, "bot1");
            var manager = (TaskManager)typeof(Fleet.Agent.Interfaces.AgentTransport)
                .GetField("_taskManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Transport)!;
            var executor = (IAgentExecutor)typeof(TaskManager).GetField("_executor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                    Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
                .Returns(call => Capture(call.ArgAt<string>(0), received));
            await rig.Transport.OnMessage(new Message
            {
                Id = 7, Chat = new Chat { Id = group ? JournalCaptureTests.Group : 111,
                    Type = group ? ChatType.Supergroup : ChatType.Private, Title = group ? "group A" : null },
                From = new User { Id = 111, Username = "u", FirstName = "User" },
                Text = $"{(group ? "@bot1 " : "")}{(newCommand ? "/new " : "")}hi",
                ReplyToMessage = new Message { Id = 5, Text = "untrusted_text", Caption = "untrusted_caption",
                    From = new User { Id = 222, FirstName = "untrusted_sender", Username = "untrusted_sender" } },
            }, UpdateType.Message);
            var prompt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("[telegram_message_id: 7] [reply_to_message_id: 5]", prompt);
            Assert.Contains(group ? "[channel: group" : "[channel: dm", prompt);
            if (group) Assert.Contains("[From: @u]", prompt);
            Assert.DoesNotContain("untrusted_", prompt);
            Assert.DoesNotContain("Replying to", prompt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async IAsyncEnumerable<Fleet.Agent.Models.AgentProgress> Capture(string prompt, TaskCompletionSource<string> received)
    {
        received.TrySetResult(prompt);
        yield return new Fleet.Agent.Models.AgentProgress { EventType = "result", Summary = "ok", FinalResult = "ok" };
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OnMessage_LocalReply_CarriesOnlyReplyId()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var rig = JournalCaptureTests.Rig.Build(root, journal: false);
            await rig.Transport.OnMessage(new Message
            {
                Id = 7, Chat = new Chat { Id = 111, Type = ChatType.Private },
                From = new User { Id = 111, FirstName = "User" }, Text = "hi",
                ReplyToMessage = new Message { Id = 5, Text = "untrusted_text", Caption = "untrusted_caption",
                    From = new User { Id = 222, FirstName = "untrusted_sender", Username = "untrusted_sender" } },
            }, UpdateType.Message);
            var routed = Assert.Single(rig.Routed);
            Assert.Equal(5, routed.ReplyToTelegramMessageId);
            Assert.Null(typeof(IncomingMessage).GetProperty("ReplyToText"));
            Assert.Null(typeof(IncomingMessage).GetProperty("ReplyToUsername"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
