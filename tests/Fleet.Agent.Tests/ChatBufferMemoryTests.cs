using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// The chat buffers live in memory, for every provider. No agent reads or writes
/// <c>.fleet/chat-history.json</c>; one left by an earlier version is deleted when the agent
/// starts. The buffers hold messages only: a tool call is never stored. They are still what
/// unread tracking and check-ins read, and what a provider that keeps no conversation gets its
/// history block from.
/// </summary>
public sealed class ChatBufferMemoryTests : IDisposable
{
    private const long Dm = 4242;
    private const long Group = -1001;

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"fleet-buffers-{Guid.NewGuid():N}");

    public ChatBufferMemoryTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* teardown only */ }
    }

    private string HistoryPath => Path.Combine(_workDir, ".fleet", "chat-history.json");

    /// <summary>A file as an earlier version wrote it: two chats, one with tool-call entries.</summary>
    private void WriteOldHistoryFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, $$"""
            {
              "{{Dm}}": {
                "LastChecked": "2026-01-01T00:00:00+00:00",
                "Entries": [
                  { "Sender": "@u", "Text": "a message from before", "ReplyTo": null, "Timestamp": "2026-01-01T00:00:00+00:00", "EntryType": "message", "TelegramMessageId": 5, "ReplyToTelegramMessageId": null },
                  { "Sender": "Bash", "Text": "Running: ls", "ReplyTo": null, "Timestamp": "2026-01-01T00:00:01+00:00", "EntryType": "tool_use", "TelegramMessageId": 0, "ReplyToTelegramMessageId": null }
                ]
              },
              "{{Group}}": { "LastChecked": "2026-01-01T00:00:00+00:00", "Entries": [] }
            }
            """);
    }

    // ── the file ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    public void AnAgentOfAnyProvider_DeletesAnExistingHistoryFile_WhenItStarts_AndLogsThat(string provider, bool keepsConversation)
    {
        WriteOldHistoryFile();
        var rig = Build(provider, keepsConversation);

        Assert.False(File.Exists(HistoryPath));
        var line = Assert.Single(rig.Log(), l => l.Message.Contains("chat history file", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.StartsWith("Deleted the chat history file ", line.Message, StringComparison.Ordinal);
        Assert.Contains(HistoryPath, line.Message);

        // And it was deleted unread: nothing of it is in the buffers.
        Assert.Empty(rig.Behavior.GetGroupBuffer(Dm).GetEntries());
        Assert.False(rig.Behavior.GetGroupBuffer(Dm).HasMessagesSinceLastCheck());
        Assert.DoesNotContain("a message from before", rig.Behavior.BuildDmTask(Dm, "hi"));
    }

    [Fact]
    public void WithNoHistoryFile_NothingIsLoggedAboutOne()
    {
        var rig = Build("claude", keepsConversation: true);

        Assert.DoesNotContain(rig.Log(), l => l.Message.Contains("chat history file", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    public async Task NoProvider_WritesTheHistoryFile(string provider, bool keepsConversation)
    {
        var rig = Build(provider, keepsConversation);

        // Every path that used to rewrite the file: a message, the agent's answer, a relayed
        // answer, a check-in that moves the watermark, and a whole turn with tool calls.
        rig.Behavior.BufferMessage(Dm, "@u", "a message", replyTo: null, telegramMessageId: 7);
        rig.Behavior.BufferBotResponse(Dm, "an answer", telegramMessageId: 8);
        rig.Behavior.OnRelayMessage(Group, "agent2", "a relayed answer", RelayMessageType.Response);
        rig.Behavior.CancelAllDebounce();
        rig.Behavior.StartGroupCheckIn(Group, "Proactive check-in", "Anything pending?", CancellationToken.None);
        await rig.WaitForPromptsAsync(1);
        await rig.WaitUntilIdleAsync(Group);
        await rig.RunTurnAsync(Dm, "do the task");

        Assert.False(File.Exists(HistoryPath));
        Assert.False(Directory.Exists(Path.Combine(_workDir, ".fleet")));
    }

    // ── tool calls ───────────────────────────────────────────────────────────

    /// <summary>
    /// A turn that calls tools leaves one entry in the buffer: its answer. Tool calls were stored
    /// as entries of their own, on a real deployment most of the ring, where nothing read them.
    /// </summary>
    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    public async Task ATurnThatCallsTools_BuffersItsAnswerAndNoToolCall(string provider, bool keepsConversation)
    {
        var rig = Build(provider, keepsConversation);
        rig.Behavior.SetBotUsername("agent1_bot");

        await rig.RunTurnAsync(Dm, "do the task");

        var entry = Assert.Single(rig.Behavior.GetGroupBuffer(Dm).GetEntries());
        Assert.Equal("@agent1_bot", entry.Sender);
        Assert.Equal("the answer", entry.Text);
    }

    // ── what the buffers are still for ───────────────────────────────────────

    /// <summary>
    /// Group "all" mode: the messages people wrote since the last check-in reach the next one,
    /// for a provider that keeps the conversation as well, and on a process that has just started.
    /// </summary>
    [Fact]
    public async Task ADebouncedCheckIn_StillCarriesTheUnreadMessages_ForAProviderThatKeepsTheConversation()
    {
        var rig = Build("claude", keepsConversation: true);
        rig.Behavior.BufferMessage(Group, "@u", "first unread", replyTo: null, telegramMessageId: 11);
        rig.Behavior.BufferMessage(Group, "@v", "second unread", replyTo: null, telegramMessageId: 12);

        await rig.Behavior.TriggerDebouncedGroupBatchForTestAsync(Group, "All-messages check-in", "Review the conversation above.");
        await rig.WaitForPromptsAsync(1);
        await rig.WaitUntilIdleAsync(Group);

        var prompt = Assert.Single(rig.Prompts());
        Assert.Contains(
            "[New messages since last check-in]\n[telegram_message_id: 11] @u: first unread\n[telegram_message_id: 12] @v: second unread",
            prompt);
        Assert.DoesNotContain("[Recent group conversation]", prompt);

        // Shown once: the next check-in has nothing unread from people. (The agent's own answer
        // to the first one is buffered after the watermark, as it always was.)
        await rig.Behavior.TriggerDebouncedGroupBatchForTestAsync(Group, "All-messages check-in", "Review the conversation above.");
        await rig.WaitForPromptsAsync(2);
        Assert.DoesNotContain("first unread", rig.Prompts()[1]);
        Assert.DoesNotContain("second unread", rig.Prompts()[1]);
    }

    /// <summary>
    /// The proactive sweep chooses its wording by whether the provider has the conversation. One
    /// that keeps it has it on a process that just started too, so it is asked to review its
    /// session, not handed a "recent conversation" block.
    /// </summary>
    [Fact]
    public async Task TheProactiveSweep_TreatsAProviderThatKeepsTheConversation_AsHavingIt_OnAColdProcess()
    {
        var rig = Build("claude", keepsConversation: true, groupListenMode: "all", allowedGroups: [Group]);
        rig.Behavior.BufferMessage(Group, "@u", "a group message", replyTo: null, telegramMessageId: 11);

        await rig.Behavior.RunProactiveSweepForTestAsync();
        await rig.WaitForPromptsAsync(1);

        var prompt = Assert.Single(rig.Prompts());
        Assert.Contains("[Proactive check-in]", prompt);
        Assert.Contains("Review your memory and session context.", prompt);
        Assert.DoesNotContain("[Recent group conversation]", prompt);
        Assert.DoesNotContain("a group message", prompt);
    }

    /// <summary>
    /// A provider that keeps no conversation gets its history block from the buffer in memory: it
    /// holds what was said since this container started, and nothing from before.
    /// </summary>
    [Fact]
    public void AProviderThatKeepsNoConversation_GetsItsHistoryBlock_FromMemory_AndNothingFromTheOldFile()
    {
        WriteOldHistoryFile();
        var rig = Build("gemini", keepsConversation: false);
        rig.Behavior.BufferMessage(Dm, "@u", "said in this container", replyTo: null, telegramMessageId: 7);

        var prompt = rig.Behavior.BuildDmTask(Dm, "and now this", telegramMessageId: 8);

        Assert.StartsWith("[Recent conversation]\n", prompt, StringComparison.Ordinal);
        Assert.Contains("[telegram_message_id: 7] @u: said in this container", prompt);
        Assert.DoesNotContain("a message from before", prompt);
        Assert.DoesNotContain("Running: ls", prompt);
    }

    // ── rig ──────────────────────────────────────────────────────────────────

    private Rig Build(string provider, bool keepsConversation, string groupListenMode = "mention", long[]? allowedGroups = null)
    {
        var agentOptions = Options.Create(new AgentOptions
        {
            Name = "agent1",
            Role = "test",
            WorkDir = _workDir,
            Provider = provider,
            ShortName = "agent1",
            GroupListenMode = groupListenMode,
            GroupDebounceSeconds = 3600,
        });
        var telegramOptions = Options.Create(new TelegramOptions { AllowedGroupIds = [.. allowedGroups ?? []] });

        var executor = Substitute.For<IAgentExecutor>();
        executor.KeepsConversation.Returns(keepsConversation);
        executor.IsProcessWarm.Returns(false);
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => TurnWithTools());

        var logger = new RecordingLogger<GroupBehavior>();
        var relay = new GroupRelayService(agentOptions, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var manager = new TaskManager(agentOptions, executor, new SessionManager(), NullLogger<TaskManager>.Instance);
        var commands = (CommandDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(CommandDispatcher));
        var behavior = new GroupBehavior(agentOptions, telegramOptions, new AllowlistHolder(telegramOptions), executor, relay,
            manager, commands, new PromptAssembler(executor), logger);
        // The production owner of "the agent's answer goes into the chat buffer".
        var completions = new CompletionContextBuffer(manager, behavior, NullMessageSink.Instance);

        return new Rig(behavior, manager, executor, logger, completions);
    }

    /// <summary>Two tool calls, then the answer: what a provider turn looks like to the runtime.</summary>
    private static async IAsyncEnumerable<AgentProgress> TurnWithTools()
    {
        yield return new AgentProgress
        {
            EventType = "tool_use", Summary = "Running: ls", ToolName = "Bash", ToolArgs = "{\"command\":\"ls\"}", IsSignificant = true,
        };
        yield return new AgentProgress
        {
            EventType = "tool_use", Summary = "Reading notes.md", ToolName = "Read", ToolArgs = "{}", IsSignificant = true,
        };
        yield return new AgentProgress { EventType = "result", Summary = "the answer", FinalResult = "the answer", IsSignificant = true };
        await Task.CompletedTask;
    }

    private sealed record Rig(
        GroupBehavior Behavior, TaskManager Manager, IAgentExecutor Executor,
        RecordingLogger<GroupBehavior> Logger, CompletionContextBuffer Completions)
    {
        public IReadOnlyList<(LogLevel Level, string Message)> Log()
        {
            lock (Logger.Entries) return Logger.Entries.Select(e => (e.Level, e.Message)).ToList();
        }

        /// <summary>Every prompt the executor was given, in order.</summary>
        public IReadOnlyList<string> Prompts() =>
            Executor.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(IAgentExecutor.ExecuteAsync))
                .Select(c => (string)c.GetArguments()[0]!)
                .ToList();

        public async Task RunTurnAsync(long chatId, string task)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(long chat, string result, string? relaySender, TaskSource source, bool partial,
                string? correlationId, string? taskId, CompletionKind kind)
            {
                if (chat == chatId) completed.TrySetResult();
            }

            Manager.OnTaskCompleted += OnCompleted;
            try
            {
                await Manager.StartTask(chatId, task, task, isSessionTask: true);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitUntilIdleAsync(chatId);
            }
            finally
            {
                Manager.OnTaskCompleted -= OnCompleted;
            }
        }

        public async Task WaitForPromptsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Prompts().Count < count)
                await Task.Delay(10, timeout.Token);
        }

        public async Task WaitUntilIdleAsync(long chatId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Manager.HasRunningTasks(chatId))
                await Task.Delay(10, timeout.Token);
        }
    }
}
