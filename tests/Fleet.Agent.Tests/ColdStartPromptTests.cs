using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// A prompt is built when its message arrives, with the recent conversation above it only when
/// the provider process is cold at that moment. A message that waits in the queue can outlive
/// that process: a token broadcast restarts it. The turn then started on a process with no memory
/// and a prompt with no history. Which prompt a turn gets is decided when the turn starts.
/// </summary>
public sealed class ColdStartPromptTests
{
    private const string Warm = "PROMPT-BUILT-FOR-A-WARM-PROCESS";
    private const string Cold = "PROMPT-BUILT-FOR-A-COLD-PROCESS";

    [Fact]
    public async Task A_queued_message_starts_with_its_history_when_the_process_was_restarted_while_it_waited()
    {
        await using var executor = new RestartableExecutor { Warm = true };
        var manager = BuildManager(executor);
        try
        {
            await manager.StartTask(1, "workflow step", "workflow step", isSessionTask: true, source: TaskSource.Relay);
            await executor.WaitStarted(1);

            Assert.Equal(TaskDispatchOutcome.Queued,
                await manager.StartTask(2, Warm, "hello", isSessionTask: true, coldStartTask: Cold));

            // A token broadcast stops the provider process; the next turn starts a new one.
            executor.Warm = false;
            executor.Release();
            await executor.WaitStarted(2);

            Assert.Equal(Cold, executor.Tasks[1]);
        }
        finally { await Stop(manager, executor); }
    }

    [Fact]
    public async Task A_queued_message_keeps_the_prompt_it_was_built_with_while_the_process_stays_warm()
    {
        await using var executor = new RestartableExecutor { Warm = true };
        var manager = BuildManager(executor);
        try
        {
            await manager.StartTask(1, "workflow step", "workflow step", isSessionTask: true, source: TaskSource.Relay);
            await executor.WaitStarted(1);
            await manager.StartTask(2, Warm, "hello", isSessionTask: true, coldStartTask: Cold);

            executor.Release();
            await executor.WaitStarted(2);

            Assert.Equal(Warm, executor.Tasks[1]);
        }
        finally { await Stop(manager, executor); }
    }

    [Fact]
    public async Task Only_the_first_message_of_a_merged_entry_brings_the_history()
    {
        await using var executor = new RestartableExecutor { Warm = true };
        var manager = BuildManager(executor);
        try
        {
            await manager.StartTask(1, "workflow step", "workflow step", isSessionTask: true, source: TaskSource.Relay);
            await executor.WaitStarted(1);
            await manager.StartTask(2, "WARM-ONE", "one", isSessionTask: true, coldStartTask: "COLD-ONE");
            await manager.StartTask(2, "WARM-TWO", "two", isSessionTask: true, coldStartTask: "COLD-TWO");
            Assert.Equal(2, Assert.Single(manager.GetQueueSnapshot()).PartCount);

            executor.Warm = false;
            executor.Release();
            await executor.WaitStarted(2);

            var prompt = executor.Tasks[1];
            Assert.Contains("COLD-ONE", prompt);
            Assert.Contains("WARM-TWO", prompt);
            Assert.DoesNotContain("WARM-ONE", prompt);
            Assert.DoesNotContain("COLD-TWO", prompt);
            Assert.True(prompt.IndexOf("COLD-ONE", StringComparison.Ordinal) < prompt.IndexOf("WARM-TWO", StringComparison.Ordinal));
        }
        finally { await Stop(manager, executor); }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task A_queued_prompt_is_selected_at_launch_after_the_process_changes_after_claim(
        bool merged, bool steered, bool warmAtClaim)
    {
        await using var executor = new RestartableExecutor { Warm = warmAtClaim };
        var manager = BuildManager(executor);
        try
        {
            await manager.StartTask(1, "workflow step", "workflow step", isSessionTask: true, source: TaskSource.Relay);
            await executor.WaitStarted(1);
            await manager.StartTask(2, Warm, "hello", isSessionTask: true, coldStartTask: Cold);
            if (merged)
                await manager.StartTask(2, "SECOND-WARM", "second", isSessionTask: true, coldStartTask: "SECOND-COLD");

            var entry = Assert.Single(manager.GetQueueSnapshot());
            foreach (var part in entry.Parts) part.Steered = steered;
            var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.QueueEntryClaimedForTest = () =>
            {
                executor.Warm = !warmAtClaim;
                claimed.SetResult();
            };
            executor.Release();
            await claimed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await executor.WaitStarted(2);

            Assert.Equal(!warmAtClaim, executor.IsProcessWarm);
            var prompt = executor.Tasks[1];
            var expected = warmAtClaim ? Cold : Warm;
            if (steered) expected = TaskManager.SteeredPartPrefix + "\n\n" + expected;
            if (!merged) Assert.Equal(expected, prompt);
            else
            {
                Assert.Contains(expected, prompt);
                Assert.Contains("SECOND-WARM", prompt);
                Assert.DoesNotContain("SECOND-COLD", prompt);
                Assert.True(prompt.IndexOf(expected, StringComparison.Ordinal)
                    < prompt.IndexOf("SECOND-WARM", StringComparison.Ordinal));
                Assert.Equal(steered ? 2 : 0,
                    prompt.Split(TaskManager.SteeredPartPrefix, StringSplitOptions.None).Length - 1);
            }
            Assert.DoesNotContain(warmAtClaim ? Warm : Cold, prompt);
        }
        finally { await Stop(manager, executor); }
    }

    [Theory]
    [InlineData(true, Warm)]
    [InlineData(false, Cold)]
    public async Task A_message_that_starts_at_once_gets_the_prompt_for_the_process_it_starts_on(bool warm, string expected)
    {
        await using var executor = new RestartableExecutor { Warm = warm };
        var manager = BuildManager(executor);
        try
        {
            Assert.Equal(TaskDispatchOutcome.Ran,
                await manager.StartTask(2, Warm, "hello", isSessionTask: true, coldStartTask: Cold));
            await executor.WaitStarted(1);

            Assert.Equal(expected, executor.Tasks[0]);
        }
        finally { await Stop(manager, executor); }
    }

    [Fact]
    public async Task A_message_without_a_cold_start_prompt_is_sent_as_it_was_built()
    {
        await using var executor = new RestartableExecutor { Warm = true };
        var manager = BuildManager(executor);
        try
        {
            await manager.StartTask(1, "workflow step", "workflow step", isSessionTask: true, source: TaskSource.Relay);
            await executor.WaitStarted(1);
            await manager.StartTask(2, Warm, "hello", isSessionTask: true);

            executor.Warm = false;
            executor.Release();
            await executor.WaitStarted(2);

            Assert.Equal(Warm, executor.Tasks[1]);
        }
        finally { await Stop(manager, executor); }
    }

    /// <summary>
    /// The whole path: the router builds the prompt while the process is warm, the message waits
    /// behind a workflow turn, the process is restarted, and the turn that then starts carries the
    /// conversation. Before the fix it carried the one line the person had just typed.
    /// </summary>
    [Fact]
    public async Task Through_the_router_a_message_queued_warm_reaches_a_restarted_process_with_the_conversation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cold-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await using var executor = new RestartableExecutor { Warm = true };
        var agent = Options.Create(new AgentOptions { Name = "agent1", Role = "test", WorkDir = directory });
        var telegram = Options.Create(new TelegramOptions { AllowedUserIds = [42] });
        var allowlist = new AllowlistHolder(telegram);
        var sink = Substitute.For<IMessageSink>();
        var manager = new TaskManager(agent, executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: sink);
        await using var relay = new GroupRelayService(agent, Options.Create(new RabbitMqOptions()), NullLogger<GroupRelayService>.Instance);
        var commands = new CommandDispatcher(manager, executor, agent, NullLogger<CommandDispatcher>.Instance, sink);
        var groups = new GroupBehavior(agent, telegram, allowlist, executor, relay, manager, commands, new PromptAssembler(executor), NullLogger<GroupBehavior>.Instance);
        var router = new MessageRouter(agent, telegram, allowlist, manager, groups, relay, commands, NullLogger<MessageRouter>.Instance, sink);
        try
        {
            groups.AddAndPersist(42, "@synthetic", "the deploy window is thursday", replyTo: null);
            await manager.StartTask(1, "workflow step", "workflow step", isSessionTask: true, source: TaskSource.Relay);
            await executor.WaitStarted(1);

            await router.HandleAsync(new IncomingMessage
            {
                ChatId = 42, UserId = 42, Text = "which day did we agree on?", StrippedText = "which day did we agree on?",
                IsGroupChat = false, Sender = "@synthetic", TelegramMessageId = 7,
            });
            var queued = Assert.Single(manager.GetQueueSnapshot());
            // Built while the process was warm: the message and its metadata, no history.
            Assert.DoesNotContain("the deploy window is thursday", queued.Task);

            executor.Warm = false;
            executor.Release();
            await executor.WaitStarted(2);

            var prompt = executor.Tasks[1];
            Assert.Contains("[Recent conversation]", prompt);
            Assert.Contains("the deploy window is thursday", prompt);
            Assert.Contains("[telegram_message_id: 7]", prompt);
            Assert.EndsWith("which day did we agree on?", prompt);
        }
        finally
        {
            await Stop(manager, executor);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// The cold-start rendering is not a new format: it is, byte for byte, what the same call
    /// returns today when the process is cold. Each case builds one buffer and asks both ways.
    /// </summary>
    [Theory]
    [InlineData("dm")]
    [InlineData("group")]
    [InlineData("check-in")]
    public void The_cold_start_prompt_is_exactly_what_a_cold_process_gets(string kind)
    {
        var warmExecutor = Substitute.For<IAgentExecutor>();
        warmExecutor.IsProcessWarm.Returns(true);
        var coldExecutor = Substitute.For<IAgentExecutor>();
        coldExecutor.IsProcessWarm.Returns(false);
        var onWarm = new PromptAssembler(warmExecutor);
        var onCold = new PromptAssembler(coldExecutor);

        var buffer = new GroupChatBuffer { ChatId = kind == "dm" ? 42 : -1001, ChatTitle = kind == "dm" ? null : "synthetic group" };
        buffer.Add("@synthetic", "the deploy window is thursday", replyTo: null, DateTimeOffset.UtcNow, telegramMessageId: 5);

        var (coldStart, coldToday, warmToday) = kind switch
        {
            "dm" => (
                onWarm.ForDmColdStart(buffer, "which day?", replyToTelegramMessageId: 5, telegramMessageId: 7, isVoiceTranscription: true),
                onCold.ForDm(buffer, "which day?", replyToTelegramMessageId: 5, telegramMessageId: 7, isVoiceTranscription: true),
                onWarm.ForDm(buffer, "which day?", replyToTelegramMessageId: 5, telegramMessageId: 7, isVoiceTranscription: true)),
            "group" => (
                onWarm.ForGroupMessageColdStart(buffer, "@synthetic", "which day?", replyToTelegramMessageId: 5, telegramMessageId: 7),
                onCold.ForGroupMessage(buffer, "@synthetic", "which day?", replyToTelegramMessageId: 5, telegramMessageId: 7),
                onWarm.ForGroupMessage(buffer, "@synthetic", "which day?", replyToTelegramMessageId: 5, telegramMessageId: 7)),
            _ => (
                onWarm.ForCheckInColdStart(buffer, "Proactive check-in", "anything pending?"),
                onCold.ForCheckIn(buffer, "Proactive check-in", "anything pending?"),
                onWarm.ForCheckIn(buffer, "Proactive check-in", "anything pending?")),
        };

        // Asked of a warm process, the cold-start rendering is the cold process's own prompt...
        Assert.Equal(coldToday, coldStart);
        // ...and it is not what the warm process is given.
        Assert.NotEqual(warmToday, coldStart);
        Assert.Contains("the deploy window is thursday", coldStart);
    }

    private static TaskManager BuildManager(IAgentExecutor executor)
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "codex" });
        return new TaskManager(options, executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            sink: Substitute.For<IMessageSink>());
    }

    private static async Task Stop(TaskManager manager, RestartableExecutor executor)
    {
        await manager.CancelAllAsync();
        executor.ReleaseAll();
    }

    /// <summary>An executor whose turns wait to be released and whose process can be declared gone.</summary>
    private sealed class RestartableExecutor : IAgentExecutor
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly ConcurrentQueue<string> _tasks = new();
        private volatile bool _warm;

        public bool Warm { get => _warm; set => _warm = value; }
        public IReadOnlyList<string> Tasks => _tasks.ToArray();

        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => _warm;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _tasks.Enqueue(task);
            await _release.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = "ok", FinalResult = "ok" };
        }

        public void Release() => _release.Release();
        public void ReleaseAll() => _release.Release(64);

        public async Task WaitStarted(int count)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_tasks.Count < count)
                await Task.Delay(5, deadline.Token);
        }

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) =>
            ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
