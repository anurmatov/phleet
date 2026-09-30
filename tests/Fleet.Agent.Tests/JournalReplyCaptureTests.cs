using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Fleet.Protocol;
using NSubstitute;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Tests;

/// <summary>
/// #394 A1: the journal records a turn's answer and nothing else, by construction. Only
/// <see cref="IMessageSink.SendReplyAsync"/> journals; every notice, progress post, status line and
/// echo goes through a send that cannot, whatever its text (AC1, AC2, AC10).
/// </summary>
public sealed class JournalReplyCaptureTests : IDisposable
{
    private const long User = GoldenRow.Chat;
    private const long Group = -1001000000001;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-reply-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(_root, name);

    // ── AC1: progress rows ───────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_turn_with_three_tool_events_journals_the_reply_and_no_progress_post(bool showStats)
    {
        var rig = ReplyRig.Build(Dir("ac1"), showStats: showStats);

        await rig.RunTurnAsync(User,
        [
            ReplyRig.ToolStep("Read", GoldenRow.ToolArgs),
            ReplyRig.ToolStep("Grep", "{\"pattern\":\"TODO\"}"),
            ReplyRig.ToolStep("Bash", "{\"command\":\"ls /workspace/example\"}"),
            Result("the answer", GoldenRow.Stats()),
        ]);

        // Telegram still got the progress post...
        Assert.Contains(rig.Bot.Calls, c => c.Text?.Contains("<blockquote expandable>... Using Read", StringComparison.Ordinal) == true);

        // ...and the journal has exactly the reply, body only.
        var record = Assert.Single(Records(rig));
        Assert.Equal("the answer", record["text"]!.GetValue<string>());
        Assert.Equal(showStats ? "html" : "plain", record["textFormat"]!.GetValue<string>());
        Assert.DoesNotContain(Records(rig), r => r.ToJsonString().Contains("blockquote", StringComparison.Ordinal));
        Assert.Equal(1, rig.Counters.Get("journal_captured{direction=outbound}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_two_part_reply_is_two_records_in_one_send_group(bool showStats)
    {
        var rig = ReplyRig.Build(Dir("ac1-split"), showStats: showStats);

        await rig.RunTurnAsync(User, [Result(new string('a', 4000) + new string('b', 1500), GoldenRow.Stats())]);

        Assert.Equal(2, rig.Bot.Calls.Count);
        var records = Records(rig);
        Assert.Equal(2, records.Count);
        Assert.Single(records.Select(r => r["sendGroup"]!["id"]!.GetValue<string>()).Distinct());
        Assert.All(records, r => Assert.Equal(2, r["sendGroup"]!["parts"]!.GetValue<int>()));
        Assert.Equal([1, 2], records.Select(r => r["sendGroup"]!["part"]!.GetValue<int>()).Order());
        Assert.Equal(new string('a', 4000) + new string('b', 1500), string.Concat(records.Select(r => r["text"]!.GetValue<string>())));
    }

    // ── AC2: every send that is not a reply ──────────────────────────────────

    public static TheoryData<string> NonReplySends() =>
    [
        "tool-progress",
        "provider-warning",
        "task-failed-error",
        "task-failed-no-output",
        "done-no-text-output",
        "task-cancelled",
        "error",
        "cancel-notice",
        "status-notice",
        "reset-notice",
        "busy-and-queue-notices",
        "router-usage-notice",
        "command-notice",
        "image-skip-notice",
        "photo-missing-hint",
        "photo-failed-notice",
        "transcript-echo",
    ];

    [Theory]
    [MemberData(nameof(NonReplySends))]
    public async Task A_send_that_is_not_a_reply_writes_no_record(string send)
    {
        var (sent, spool, expected) = await RunNonReplyAsync(send);

        // The send really happened, so the empty journal below is not vacuous...
        foreach (var text in expected)
            Assert.Contains(sent, s => s.Contains(text, StringComparison.Ordinal));

        // ...and it wrote no outbound record.
        Assert.DoesNotContain(spool.Pending(), e => e.Record["direction"]!.GetValue<string>() == "outbound");
    }

    private async Task<(IReadOnlyList<string> Sent, JournalSpool Spool, string[] Expected)> RunNonReplyAsync(string send)
    {
        if (send == "transcript-echo")
        {
            var voiceRig = JournalCaptureTests.Rig.Build(Dir(send), transcript: "hello there", persist: false);
            await voiceRig.Transport.OnMessage(new Message
            {
                Id = 30,
                Chat = new Chat { Id = JournalCaptureTests.User, Type = ChatType.Private },
                From = new User { Id = JournalCaptureTests.User, FirstName = "Ann" },
                Date = JournalCaptureTests.Rig.Now,
                Voice = new Voice { FileId = "v", FileUniqueId = "vq-1", Duration = 2, FileSize = 3 },
            }, UpdateType.Message);
            // The inbound record holds the transcript; that is the only record.
            Assert.Equal("hello there", voiceRig.Inbound()["transcript"]!.GetValue<string>());
            return (voiceRig.Bot.Texts, voiceRig.Spool, ["🎤 hello there"]);
        }

        var rig = ReplyRig.Build(Dir(send), showStats: true, extraAllowedGroups: [Group]);
        string[] expected;
        switch (send)
        {
            case "tool-progress":
                await rig.RunTurnAsync(User, [ReplyRig.ToolStep("Read", GoldenRow.ToolArgs), StatsOnly()]);
                expected = ["<blockquote expandable>... Using Read", "Done! (no text output)"];
                break;
            case "provider-warning":
                await rig.RunTurnAsync(User,
                [
                    new AgentProgress { Summary = "Provider notice: images are not supported", EventType = "warning", IsSignificant = true },
                    StatsOnly(),
                ]);
                expected = ["Provider notice: images are not supported"];
                break;
            case "task-failed-error":
                await rig.RunTurnAsync(User, [new AgentProgress { Summary = "boom in /workspace/example/run.log", EventType = "error" }]);
                expected = ["Task failed: boom in /workspace/example/run.log"];
                break;
            case "task-failed-no-output":
                await rig.RunTurnAsync(User, [new AgentProgress { Summary = "", EventType = "result", IsErrorResult = true, Stats = GoldenRow.Stats() }]);
                expected = ["Task failed: executor reported an error and produced no output"];
                break;
            case "done-no-text-output":
                await rig.RunTurnAsync(User, []);
                expected = ["Done! (no text output)"];
                break;
            case "task-cancelled":
                await RunThrowingTurnAsync(rig, new OperationCanceledException());
                expected = ["Task cancelled."];
                break;
            case "error":
                await RunThrowingTurnAsync(rig, new InvalidOperationException("boom in /workspace/example/run.log"));
                expected = ["Error: boom in /workspace/example/run.log"];
                break;
            case "cancel-notice":
                await rig.Manager([]).HandleCancel(User, "");
                expected = ["No active tasks to cancel."];
                break;
            case "status-notice":
                await rig.Manager([]).HandleStatus(User);
                expected = ["Agent: fleet-agent1"];
                break;
            case "reset-notice":
                await rig.Manager([]).HandleReset(User);
                expected = ["Session cleared."];
                break;
            case "busy-and-queue-notices":
                await RunQueuedBehindAnotherChatAsync(rig);
                expected = ["I'm busy right now", "Now processing your queued message...", "Done! (no text output)"];
                break;
            case "router-usage-notice":
                rig.Transport.RouterHookForTesting = null;
                await rig.Transport.OnMessage(Text(40, "/new"), UpdateType.Message);
                expected = ["Usage: /new <task description>"];
                break;
            case "command-notice":
                rig.Transport.RouterHookForTesting = null;
                await rig.Transport.OnMessage(Text(41, "/cancel_bg"), UpdateType.Message);
                expected = ["No active background tasks."];
                break;
            case "image-skip-notice":
                await rig.Transport.OnMessage(new Message
                {
                    Id = 42,
                    Chat = new Chat { Id = User, Type = ChatType.Private },
                    From = new User { Id = User, FirstName = "Ann" },
                    Date = JournalCaptureTests.Rig.Now,
                    Photo = [new PhotoSize { FileId = "big", FileUniqueId = "big-uq", FileSize = 50_000_000, Width = 1, Height = 1 }],
                }, UpdateType.Message);
                expected = ["(Image #1 exceeded size limit, skipped.)"];
                break;
            case "photo-missing-hint":
                await rig.Transport.SendReplyAsync(User, new AgentReply("[IMAGE:/workspace/example/missing.png]"), OutboundOrigin.Human);
                expected = ["[image from agent — view in their direct chat]"];
                break;
            case "photo-failed-notice":
                var image = Path.Combine(Dir(send), "chart.png");
                await File.WriteAllBytesAsync(image, [1, 2, 3]);
                rig.Bot.FailPhoto = true;
                await rig.Transport.SendReplyAsync(User, new AgentReply($"[IMAGE:{image}]"), OutboundOrigin.Human);
                expected = ["— send failed]"];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(send), send, null);
        }

        var sent = rig.Bot.Calls.Where(c => c.Ok).Select(c => c.Text ?? c.Caption ?? "").ToList();
        return (sent, rig.Spool!, expected);
    }

    /// <summary>
    /// MUST NOT 1: capture is decided by the API, never by the text. Reply-shaped text through a
    /// plain send writes nothing; notice-shaped text through the reply API is journaled.
    /// </summary>
    [Fact]
    public async Task Capture_follows_the_send_method_not_the_text()
    {
        var rig = ReplyRig.Build(Dir("by-api"));
        var image = Path.Combine(Dir("by-api"), "chart.png");
        await File.WriteAllBytesAsync(image, [1, 2, 3]);

        await rig.Holder.SendTextAsync(User, "Here is the answer you asked for.");
        await rig.Holder.SendTextAsync(User, "Here is the answer you asked for.", OutboundOrigin.Human);
        await rig.Holder.SendHtmlTextAsync(User, "<b>Here is the answer</b>");
        await rig.Holder.SendHtmlTextAsync(User, "<b>Here is the answer</b>", OutboundOrigin.Human);
        await rig.Holder.SendPhotoAsync(User, image, "the chart");
        await rig.Holder.SendPhotoAsync(User, image, "the chart", OutboundOrigin.Human);
        Assert.Equal(6, rig.Bot.Calls.Count);
        Assert.Empty(rig.Spool!.Pending());

        await rig.Holder.SendReplyAsync(User, new AgentReply("Task failed: this one is a reply"), OutboundOrigin.Human);
        Assert.Equal("Task failed: this one is a reply", Assert.Single(Records(rig))["text"]!.GetValue<string>());
    }

    // ── every reply site journals ────────────────────────────────────────────

    [Fact]
    public async Task A_recovered_answer_is_journaled_as_a_reply()
    {
        var rig = ReplyRig.Build(Dir("recovered"), showStats: true);

        await rig.RunTurnAsync(User,
        [
            new AgentProgress { Summary = "the stale answer", EventType = "recovered_answer" },
            Result("the fresh answer", GoldenRow.Stats()),
        ]);

        Assert.Equal(["the stale answer", "the fresh answer"], Records(rig).Select(r => r["text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_injected_message_answered_in_its_own_turn_is_journaled_as_a_reply()
    {
        var rig = ReplyRig.Build(Dir("injected"), showStats: true);
        var executor = new GatedExecutor(MidTurnInjectionResult.Injected, extraAnswers: ["answer to second"]);
        var manager = rig.Manager(executor);
        var done = Completed(manager);

        _ = manager.StartTask(User, "first", "first", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.Executed.Count >= 1);
        _ = manager.StartTask(User, "second", "second", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.Injected.Count >= 1);
        executor.Release();
        await done.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["answer to first", "answer to second"], Records(rig).Select(r => r["text"]!.GetValue<string>()));
        Assert.DoesNotContain(Records(rig), r => r.ToJsonString().Contains(GoldenRow.StatsLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_merged_turns_first_answer_is_journaled_as_a_reply()
    {
        var rig = ReplyRig.Build(Dir("merged"), showStats: true);
        var executor = new GatedExecutor(MidTurnInjectionResult.Unsupported, extraAnswers: []);
        var manager = rig.Manager(executor);
        var done = Completed(manager);

        _ = manager.StartTask(User, "first", "first", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.Executed.Count >= 1);
        _ = manager.StartTask(User, "second", "second", isSessionTask: true);
        await executor.WaitUntilAsync(() => executor.InjectAttempts >= 1);
        executor.Release();
        await done.WaitAsync(TimeSpan.FromSeconds(10));

        var texts = Records(rig).Select(r => r["text"]!.GetValue<string>()).ToList();
        Assert.Equal(2, executor.Executed.Count);
        Assert.Equal(2, texts.Count);
        Assert.Equal("answer to first", texts[0]);
        Assert.Contains("second", texts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relay_reply_is_sent_and_not_journaled()
    {
        var rig = ReplyRig.Build(Dir("relay"), showStats: true);

        await rig.RunTurnAsync(User, [Result("the relayed answer", GoldenRow.Stats())], source: TaskSource.Relay);

        Assert.Contains(rig.Bot.Calls, c => c.Text?.StartsWith("the relayed answer", StringComparison.Ordinal) == true);
        Assert.Empty(rig.Spool!.Pending());
        Assert.Equal(1, rig.Counters.Get("journal_excluded{reason=system_origin}"));
    }

    [Fact]
    public async Task A_reply_to_a_reserved_key_never_reaches_the_bot()
    {
        var rig = ReplyRig.Build(Dir("reserved"));

        await rig.Transport.SendReplyAsync(1L << 56, new AgentReply("private", "\n(stats)", "\n<blockquote expandable>x</blockquote>"), OutboundOrigin.Human);
        await rig.Transport.SendReplyAsync(1L << 56, new AgentReply("private"), OutboundOrigin.Human);

        Assert.Empty(rig.Bot.Calls);
        Assert.Empty(rig.Spool!.Pending());
    }

    [Fact]
    public async Task Without_a_journal_a_reply_sends_the_same_and_writes_nothing()
    {
        var on = ReplyRig.Build(Dir("on"), showStats: true);
        var off = ReplyRig.Build(Dir("off"), showStats: true, journal: false);
        var reply = new AgentReply("the answer", "\n" + GoldenRow.StatsLine, "\n<blockquote expandable>Read(x)</blockquote>");

        await on.Holder.SendReplyAsync(User, reply, OutboundOrigin.Human);
        await off.Holder.SendReplyAsync(User, reply, OutboundOrigin.Human);

        Assert.Equal(on.Bot.Calls.Select(c => c.ToJson().ToJsonString()), off.Bot.Calls.Select(c => c.ToJson().ToJsonString()));
        Assert.False(Directory.Exists(Path.Combine(Dir("off"), "spool")));
    }

    // ── AC10: the event path does not see the journal ────────────────────────

    [Fact]
    public async Task The_task_manager_publishes_the_same_events_with_the_journal_on_and_off()
    {
        async Task<List<string>> Run(bool journal)
        {
            var rig = ReplyRig.Build(Dir(journal ? "events-on" : "events-off"), showStats: true, journal: journal);
            var events = new RecordingPublisher();
            await rig.RunTurnAsync(User,
            [
                ReplyRig.ToolStep("Read", GoldenRow.ToolArgs),
                ReplyRig.ToolStep("Grep", "{\"pattern\":\"TODO\"}"),
                new AgentProgress { Summary = "the stale answer", EventType = "recovered_answer" },
                ReplyRig.ToolStep("Bash", "{\"command\":\"ls\"}"),
                Result("the answer", GoldenRow.Stats()),
            ], events);
            // The terminal event is published just after the completion callback RunTurnAsync waits on.
            await events.Terminal.WaitAsync(TimeSpan.FromSeconds(10));
            if (journal) Assert.Equal(2, Records(rig).Count);
            return events.Snapshot();
        }

        var on = await Run(journal: true);
        var off = await Run(journal: false);

        Assert.Equal(off, on);
        Assert.Equal(
            off.Count(e => e.StartsWith(ConversationEventKind.TurnProgress, StringComparison.Ordinal)),
            on.Count(e => e.StartsWith(ConversationEventKind.TurnProgress, StringComparison.Ordinal)));
        Assert.Contains(on, e => e.StartsWith(ConversationEventKind.TurnFinal, StringComparison.Ordinal));
    }

    // ── the zip rule ─────────────────────────────────────────────────────────

    [Fact]
    public void Body_pieces_zip_by_index_drop_footer_only_messages_and_append_a_surplus()
    {
        // Equal counts: piece k is message k.
        Assert.Equal("b", AgentTransport.JournalPiece(["a", "b"], 1, 2));
        // A sent message past the body is footer only.
        Assert.Null(AgentTransport.JournalPiece(["a"], 1, 2));
        Assert.Null(AgentTransport.JournalPiece([], 0, 1));
        // More body pieces than sent messages: the surplus rides on the last sent message.
        Assert.Equal("a", AgentTransport.JournalPiece(["a", "b", "c"], 0, 2));
        Assert.Equal("bc", AgentTransport.JournalPiece(["a", "b", "c"], 1, 2));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<JsonObject> Records(ReplyRig rig) =>
        rig.Spool!.Pending().Select(e => e.Record).OrderBy(r => r["telegram"]!["messageId"]!.GetValue<long>()).ToList();

    private static AgentProgress Result(string text, ExecutionStats? stats) =>
        new() { Summary = text, EventType = "result", FinalResult = text, Stats = stats };

    /// <summary>A result that carries stats and no text, so a status line gets the full footer.</summary>
    private static AgentProgress StatsOnly() => new() { Summary = "", EventType = "result", Stats = GoldenRow.Stats() };

    private static Message Text(int id, string text) => new()
    {
        Id = id,
        Chat = new Chat { Id = User, Type = ChatType.Private },
        From = new User { Id = User, FirstName = "Ann" },
        Date = JournalCaptureTests.Rig.Now,
        Text = text,
    };

    private static Task Completed(TaskManager manager, long chatId = User)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnTaskCompleted += (chat, _, _, _, _, _, _, _) =>
        {
            if (chat == chatId) done.TrySetResult();
        };
        return done.Task;
    }

    private static async Task RunThrowingTurnAsync(ReplyRig rig, Exception error)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor
            .ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MessageImage>?>(),
                Arg.Any<IReadOnlyList<MessageDocument>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Throwing(error));
        var manager = rig.Manager(executor);
        var done = Completed(manager);
        _ = manager.StartTask(User, "question", "question", isSessionTask: false);
        await done.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async IAsyncEnumerable<AgentProgress> Throwing(Exception error)
    {
        await Task.Yield();
        if (error is not null) throw error;
        yield break;
    }

    /// <summary>
    /// A human turn in the group holds the agent while the DM's message waits: the DM gets the busy
    /// notice, then the drain notice, then its own turn's status line.
    /// </summary>
    private static async Task RunQueuedBehindAnotherChatAsync(ReplyRig rig)
    {
        var executor = new GatedExecutor(MidTurnInjectionResult.Unsupported, extraAnswers: [], idleAnswers: true);
        var manager = rig.Manager(executor);
        var done = Completed(manager, User);

        _ = manager.StartTask(Group, "hold", "hold", isSessionTask: false);
        await executor.WaitUntilAsync(() => executor.Executed.Count >= 1);
        _ = manager.StartTask(User, "question", "question", isSessionTask: false);
        executor.Release();
        await done.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class RecordingPublisher : IConversationEventPublisher
    {
        private readonly List<string> _events = [];
        private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a terminal event has been published.</summary>
        public Task Terminal => _terminal.Task;

        public List<string> Snapshot()
        {
            lock (_events) return [.. _events];
        }

        public void Publish(ConversationEvent evt)
        {
            lock (_events) _events.Add(evt.Kind);
        }

        public bool Publish<TPayload>(long runtimeConversationKey, string kind, ConversationIdentity identity, TPayload? payload)
            where TPayload : class
        {
            // Kind, plus what a client reads from the payloads that carry text or a tool name.
            var detail = payload switch
            {
                TurnFinalPayload final => $" {final.Completion} {final.Text}",
                TurnProgressPayload progress => $" {progress.Activity} {progress.ToolName}",
                TurnRecoveredAnswerPayload recovered => $" {recovered.Text}",
                _ => "",
            };
            lock (_events) _events.Add(kind + detail);
            if (ConversationEventKind.Terminal.Contains(kind)) _terminal.TrySetResult();
            return true;
        }
    }

    /// <summary>
    /// Holds the first turn until released so a second message can arrive mid-turn, then answers
    /// every turn with <c>answer to {task}</c> (or IDLE), and replays <paramref name="extraAnswers"/>
    /// as injected messages Claude answered in turns of their own.
    /// </summary>
    private sealed class GatedExecutor(
        MidTurnInjectionResult injection, IReadOnlyList<string> extraAnswers, bool idleAnswers = false) : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Executed { get; } = new();
        public ConcurrentQueue<string> Injected { get; } = new();
        public int InjectAttempts;

        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Executed.Enqueue(task);
            await _release.Task.WaitAsync(ct);
            if (idleAnswers && task == "hold")
            {
                yield return new AgentProgress { Summary = "IDLE", EventType = "result", FinalResult = "IDLE" };
                yield break;
            }
            if (idleAnswers) yield break;
            yield return new AgentProgress
            {
                Summary = $"answer to {task}", EventType = "result", FinalResult = $"answer to {task}",
                Stats = GoldenRow.Stats(),
            };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null, IReadOnlyList<MessageDocument>? documents = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref InjectAttempts);
            if (injection.Status == MidTurnInjectionStatus.Injected) Injected.Enqueue(task);
            return Task.FromResult(injection);
        }

        public async IAsyncEnumerable<AgentProgress> ReadInjectedTurnAnswersAsync(
            int injectedMessages, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var answer in extraAnswers.Take(injectedMessages))
            {
                await Task.Yield();
                yield return new AgentProgress
                {
                    Summary = answer, EventType = "result", FinalResult = answer, Stats = GoldenRow.Stats(),
                };
            }
        }

        public void Release() => _release.TrySetResult();

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition())
                await Task.Delay(10, cts.Token);
        }

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default) => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
