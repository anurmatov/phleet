using System.Reflection;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Tests;

public sealed class ReactionPromptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleReaction_BufferedOriginal_DoesNotQuoteContent(bool removed)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var rig = JournalCaptureTests.Rig.Build(root, journal: false);
            var group = Field<GroupBehavior>(rig.Transport, "_groupBehavior");
            group.GetGroupBuffer(111).Add("untrusted_sender", "untrusted_quote", null, DateTimeOffset.UtcNow, telegramMessageId: 5);
            var manager = Field<TaskManager>(rig.Transport, "_taskManager");
            var executor = Field<IAgentExecutor>(manager, "_executor");
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                    Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
                .Returns(call => Capture(call.ArgAt<string>(0), received));
            typeof(AgentTransport).GetMethod("HandleReaction", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(rig.Transport, [new MessageReactionUpdated
                {
                    Chat = new Chat { Id = 111, Type = ChatType.Private }, MessageId = 5,
                    User = new User { Id = 111, FirstName = "User" },
                    OldReaction = removed ? [new ReactionTypeEmoji { Emoji = "👍" }] : [],
                    NewReaction = removed ? [] : [new ReactionTypeEmoji { Emoji = "👍" }],
                }]);
            var prompt = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal($"[channel: dm chat_id=111]\n[reaction{(removed ? " removed" : "")}: 👍 on message_id=5 from user_id=111]", prompt);
            Assert.DoesNotContain("untrusted_quote", prompt);
            Assert.DoesNotContain("untrusted_sender", prompt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static T Field<T>(object obj, string name) =>
        (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;

    private static async IAsyncEnumerable<AgentProgress> Capture(string prompt, TaskCompletionSource<string> received)
    {
        received.TrySetResult(prompt);
        yield return new AgentProgress { EventType = "result", Summary = "ok", FinalResult = "ok" };
        await Task.CompletedTask;
    }
}
