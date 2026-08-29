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
/// Tests for the voice-transcription provenance marker (issue #245).
///
/// A transcript is otherwise indistinguishable from typed text, so an agent will
/// over-trust a name or number that speech-to-text rendered wrong. These tests pin
/// the three properties that make the marker trustworthy: it appears for transcripts,
/// it never appears for typed text, and it lives in prompt metadata rather than in the
/// user's message — which is what keeps it out of every outbound path.
/// </summary>
public class VoiceTranscriptionMarkerTests
{
    private const string Marker = "[voice_transcription: whisper; may_contain_errors=true]";

    private static PromptAssembler MakeAssembler(bool warm)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(warm);
        return new PromptAssembler(executor);
    }

    private static GroupChatBuffer DmBuffer() =>
        new() { ChatId = 100001L, ChatLabel = "user=@user1" };

    private static GroupChatBuffer GroupBuffer() =>
        new() { ChatId = -1009999L, ChatTitle = "Test Group" };

    // ── the marker constant itself ───────────────────────────────────────────
    // The exact string is the contract with the agent instruction, so pin the literal
    // rather than referencing the constant on both sides of the assertion.

    [Fact]
    public void VoiceTranscriptionMarker_MatchesTheSpecifiedLiteral()
    {
        Assert.Equal(Marker, PromptAssembler.VoiceTranscriptionMarker);
    }

    // ── IncomingMessage provenance ───────────────────────────────────────────

    [Fact]
    public void IncomingMessage_DefaultsToTyped()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hi", Sender = "@u", IsGroupChat = false,
        };

        Assert.Equal(MessageInputSource.Typed, msg.InputSource);
        Assert.False(msg.IsVoiceTranscription);
    }

    [Fact]
    public void IncomingMessage_VoiceTranscription_SetsFlag()
    {
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = "hi", Sender = "@u", IsGroupChat = false,
            InputSource = MessageInputSource.VoiceTranscription,
        };

        Assert.True(msg.IsVoiceTranscription);
    }

    [Fact]
    public void IncomingMessage_ProvenanceDoesNotAlterTheUserText()
    {
        // The marker must be structured state, not text concatenation — otherwise it would
        // ride along into the buffer, the display text and the echo.
        const string spoken = "deploy the thing";
        var msg = new IncomingMessage
        {
            ChatId = 1, UserId = 1, Text = spoken, Sender = "@u", IsGroupChat = false,
            StrippedText = spoken,
            InputSource = MessageInputSource.VoiceTranscription,
        };

        Assert.Equal(spoken, msg.Text);
        Assert.Equal(spoken, msg.StrippedText);
        Assert.DoesNotContain("voice_transcription", msg.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("voice_transcription", msg.StrippedText, StringComparison.Ordinal);
    }

    // ── PromptAssembler.ForDm ────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]   // warm process — slim prompt
    [InlineData(false)]  // cold process — prompt carries history
    public void ForDm_VoiceTranscription_EmitsMarker(bool warm)
    {
        var result = MakeAssembler(warm)
            .ForDm(DmBuffer(), "call me back", telegramMessageId: 42, isVoiceTranscription: true);

        Assert.Contains(Marker, result, StringComparison.Ordinal);
        Assert.Contains("call me back", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForDm_TypedMessage_EmitsNoMarker(bool warm)
    {
        var result = MakeAssembler(warm)
            .ForDm(DmBuffer(), "call me back", telegramMessageId: 42);

        Assert.DoesNotContain("voice_transcription", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ForDm_VoiceTranscription_MarkerSitsWithTheOtherPromptMetadata()
    {
        var result = MakeAssembler(warm: true).ForDm(
            DmBuffer(), "call me back", replyToText: "earlier message", telegramMessageId: 42,
            isVoiceTranscription: true);

        var idxMsgId   = result.IndexOf("[telegram_message_id: 42]", StringComparison.Ordinal);
        var idxChannel = result.IndexOf("[channel: dm", StringComparison.Ordinal);
        var idxMarker  = result.IndexOf(Marker, StringComparison.Ordinal);
        var idxTask    = result.IndexOf("call me back", StringComparison.Ordinal);

        Assert.True(idxMsgId >= 0 && idxChannel > idxMsgId, "existing metadata order changed");
        Assert.True(idxMarker > idxChannel, "marker must follow the channel anchor");
        Assert.True(idxMarker < idxTask, "marker must precede the message text");
    }

    [Fact]
    public void ForDm_ColdWithHistory_KeepsMarkerOnTheNewMessageOnly()
    {
        var buffer = DmBuffer();
        buffer.Add("@user1", "an older typed message", null, DateTimeOffset.UtcNow);

        var result = MakeAssembler(warm: false)
            .ForDm(buffer, "the spoken one", telegramMessageId: 7, isVoiceTranscription: true);

        // Exactly one marker: the history section describes past messages and must not
        // retroactively claim they were transcribed.
        Assert.Equal(1, CountOccurrences(result, Marker));
        Assert.Contains("an older typed message", result, StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("an older typed message", StringComparison.Ordinal)
                < result.IndexOf(Marker, StringComparison.Ordinal),
            "marker belongs to the new message, not the recalled history");
    }

    // ── PromptAssembler.ForGroupMessage ──────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForGroupMessage_VoiceTranscription_EmitsMarker(bool warm)
    {
        var result = MakeAssembler(warm)
            .ForGroupMessage(GroupBuffer(), "@user1", "ship it", telegramMessageId: 42,
                isVoiceTranscription: true);

        Assert.Contains(Marker, result, StringComparison.Ordinal);
        Assert.Contains("ship it", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForGroupMessage_TypedMessage_EmitsNoMarker(bool warm)
    {
        var result = MakeAssembler(warm)
            .ForGroupMessage(GroupBuffer(), "@user1", "ship it", telegramMessageId: 42);

        Assert.DoesNotContain("voice_transcription", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ForGroupMessage_VoiceTranscription_MarkerPrecedesTheFromLine()
    {
        var result = MakeAssembler(warm: true)
            .ForGroupMessage(GroupBuffer(), "@user1", "ship it", telegramMessageId: 42,
                isVoiceTranscription: true);

        var idxChannel = result.IndexOf("[channel: group", StringComparison.Ordinal);
        var idxMarker  = result.IndexOf(Marker, StringComparison.Ordinal);
        var idxFrom    = result.IndexOf("[From: @user1]", StringComparison.Ordinal);

        Assert.True(idxChannel >= 0 && idxMarker > idxChannel, "marker must follow the channel anchor");
        Assert.True(idxMarker < idxFrom, "marker must precede the [From:] line");
    }

    // ── GroupBehavior plumbing ───────────────────────────────────────────────
    // The leaf renderer being correct is not enough — the flag has to survive the
    // Build*Task hop that MessageRouter actually calls.

    [Fact]
    public void BuildDmTask_VoiceTranscription_EmitsMarker()
    {
        var behavior = BuildBehavior();
        var result = behavior.BuildDmTask(555L, "spoken text", telegramMessageId: 1,
            chatUsername: "user1", isVoiceTranscription: true);

        Assert.Contains(Marker, result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDmTask_Default_EmitsNoMarker()
    {
        var behavior = BuildBehavior();
        var result = behavior.BuildDmTask(555L, "typed text", telegramMessageId: 1,
            chatUsername: "user1");

        Assert.DoesNotContain("voice_transcription", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGroupTask_VoiceTranscription_EmitsMarker()
    {
        var behavior = BuildBehavior();
        var result = behavior.BuildGroupTask(-556L, "@user1", "spoken text",
            telegramMessageId: 1, chatTitle: "Test Group", isVoiceTranscription: true);

        Assert.Contains(Marker, result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGroupTask_Default_EmitsNoMarker()
    {
        var behavior = BuildBehavior();
        var result = behavior.BuildGroupTask(-556L, "@user1", "typed text",
            telegramMessageId: 1, chatTitle: "Test Group");

        Assert.DoesNotContain("voice_transcription", result, StringComparison.Ordinal);
    }

    // ── queued / coalesced continuation path ─────────────────────────────────

    [Fact]
    public async Task QueuedContinuation_MergedTurn_KeepsMarkerOnTheVoicePartOnly()
    {
        // The marker is baked into the assembled prompt before TaskManager ever sees it,
        // so queueing and coalescing carry it for free. "For free" is exactly the kind of
        // claim that stops being true after a refactor, so assert it against the real
        // TaskManager rather than reasoning about it.
        var assembler = MakeAssembler(warm: true);
        var voicePrompt = assembler.ForDm(DmBuffer(), "restart the worker",
            telegramMessageId: 2, isVoiceTranscription: true);
        var typedPrompt = assembler.ForDm(DmBuffer(), "actually leave it",
            telegramMessageId: 3);

        var executor = new BlockingTestExecutor();
        var manager = BuildManager(executor);

        _ = manager.StartTask(100001L, "first turn", "first turn", isSessionTask: true);
        await executor.WaitForExecuteCountAsync(1);

        // Both land in the chat-level queue while the first turn is running.
        _ = manager.StartTask(100001L, voicePrompt, "restart the worker", isSessionTask: true);
        _ = manager.StartTask(100001L, typedPrompt, "actually leave it", isSessionTask: true);

        executor.ReleaseAllTurns();
        await executor.WaitForExecuteCountAsync(2);

        var continuation = string.Join("\n", executor.ExecutedTasks.Skip(1));

        Assert.Contains("restart the worker", continuation, StringComparison.Ordinal);
        Assert.Contains(Marker, continuation, StringComparison.Ordinal);

        // The typed follow-up must not inherit the marker from its neighbour: the marker
        // has to sit before the spoken text and before the typed text ever begins.
        var idxMarker = continuation.IndexOf(Marker, StringComparison.Ordinal);
        var idxTyped  = continuation.IndexOf("actually leave it", StringComparison.Ordinal);
        if (idxTyped >= 0)
            Assert.True(idxMarker < idxTyped, "marker must not be attached to the typed message");
        Assert.Equal(1, CountOccurrences(continuation, Marker));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static GroupBehavior BuildBehavior()
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test-agent", Role = "test", WorkDir = Path.GetTempPath(),
            GroupDebounceSeconds = 30, ShortName = "test",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(true);

        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var sessions = new SessionManager();
        var taskManager = new TaskManager(agentOpts, executor, sessions, NullLogger<TaskManager>.Instance);
        // CommandDispatcher has deep sealed deps and is only reached for '/' text —
        // Build*Task never touches it. Same shortcut as GroupBehaviorBufferTests.
        var commands = (CommandDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(CommandDispatcher));
        var prompts = new PromptAssembler(executor);
        var allowlist = new AllowlistHolder(telegramOpts);

        return new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            taskManager, commands, prompts, NullLogger<GroupBehavior>.Instance);
    }

    private static TaskManager BuildManager(IAgentExecutor executor)
    {
        var options = Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude",
        });
        var manager = new TaskManager(options, executor, new SessionManager(),
            NullLogger<TaskManager>.Instance, new InjectionOutcomeCounter());
        manager.Sink = Substitute.For<IMessageSink>();
        return manager;
    }

    /// <summary>Holds every turn open until released, recording the task text it was given.</summary>
    private sealed class BlockingTestExecutor : IAgentExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _executed = new();

        public IReadOnlyList<string> ExecutedTasks => _executed.ToList();
        public string? LastSessionId => "session";
        public DateTimeOffset LastActivity => DateTimeOffset.UtcNow;
        public bool IsProcessWarm => true;

        public async IAsyncEnumerable<AgentProgress> ExecuteAsync(
            string task,
            IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _executed.Enqueue(task);
            await _release.Task.WaitAsync(ct);
            yield return new AgentProgress { EventType = "result", Summary = task, FinalResult = task };
        }

        public Task<MidTurnInjectionResult> TryInjectMessageAsync(
            string task, IReadOnlyList<MessageImage>? images = null,
            IReadOnlyList<MessageDocument>? documents = null, CancellationToken ct = default)
            => Task.FromResult(MidTurnInjectionResult.Unsupported);

        public void ReleaseAllTurns() => _release.TrySetResult();

        public Task StopProcessAsync() => Task.CompletedTask;
        public Task<bool> TryStopProcessAsync() => Task.FromResult(false);
        public void RequestRestart() { }
        public IAsyncEnumerable<AgentProgress> SendCommandAsync(string command, CancellationToken ct = default)
            => ExecuteAsync(command, ct: ct);
        public IReadOnlyCollection<BackgroundTaskInfo> GetActiveBackgroundTasks() => [];
        public Task<bool> CancelBackgroundTaskAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForExecuteCountAsync(int expected)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_executed.Count < expected)
                await Task.Delay(10, cts.Token);
        }
    }
}
