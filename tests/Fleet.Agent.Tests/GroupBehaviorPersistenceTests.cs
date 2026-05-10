using System.Runtime.CompilerServices;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests that GroupChatBuffer._lastChecked is persisted and restored across restarts,
/// preventing agents from re-replying to messages they already processed (issue #160).
/// </summary>
public class GroupBehaviorPersistenceTests : IDisposable
{
    private readonly string _workDir;
    private const long ChatId = 99001;

    public GroupBehaviorPersistenceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"fleet-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private GroupBehavior BuildBehavior()
    {
        var agentOpts = Options.Create(new AgentOptions
        {
            Name = "test-agent",
            Role = "test",
            WorkDir = _workDir,
            GroupDebounceSeconds = 30,
            ShortName = "test",
        });
        var telegramOpts = Options.Create(new TelegramOptions());
        var rabbitOpts = Options.Create(new RabbitMqOptions());

        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(true);

        var relay = new GroupRelayService(agentOpts, rabbitOpts, NullLogger<GroupRelayService>.Instance);
        var sessions = new SessionManager();
        var taskManager = new TaskManager(agentOpts, executor, sessions, NullLogger<TaskManager>.Instance);
        var commands = (CommandDispatcher)RuntimeHelpers.GetUninitializedObject(typeof(CommandDispatcher));
        var prompts = new PromptAssembler(executor);

        var allowlist = new AllowlistHolder(telegramOpts);
        return new GroupBehavior(agentOpts, telegramOpts, allowlist, executor, relay,
            taskManager, commands, prompts, NullLogger<GroupBehavior>.Instance);
    }

    private string HistoryPath => Path.Combine(_workDir, ".fleet", "chat-history.json");

    // ── GroupChatBuffer.LoadState unit tests ──────────────────────────────────

    [Fact]
    public void LoadState_WithUtcNow_PreventsExistingEntriesFromAppearingNew()
    {
        var buffer = new GroupChatBuffer();
        var pastTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        buffer.LoadEntries([new SerializedEntry("alice", "old message", null, pastTime)]);

        // Before LoadState: messages appear unread (lastChecked = MinValue)
        Assert.True(buffer.HasMessagesSinceLastCheck());

        buffer.LoadState(DateTimeOffset.UtcNow);

        // After LoadState: existing messages no longer appear new
        Assert.False(buffer.HasMessagesSinceLastCheck());
    }

    [Fact]
    public void LoadState_DoesNotSuppressMessageAddedAfterwards()
    {
        var buffer = new GroupChatBuffer();
        buffer.LoadState(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));

        buffer.Add("bob", "new message", null, DateTimeOffset.UtcNow);

        Assert.True(buffer.HasMessagesSinceLastCheck());
    }

    // ── Backward-compat: legacy format (array values) ────────────────────────

    [Fact]
    public void LegacyFormat_LoadedWithLastCheckedNow_NoMessagesAppearNew()
    {
        // Write the old on-disk format: Dictionary<long, List<SerializedEntry>>
        var pastIso = (DateTimeOffset.UtcNow - TimeSpan.FromHours(2)).ToString("O");
        var legacyJson = $$"""
        {
          "{{ChatId}}": [
            {"Sender":"alice","Text":"old message","ReplyTo":null,"Timestamp":"{{pastIso}}"}
          ]
        }
        """;
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, legacyJson);

        var behavior = BuildBehavior();
        var buffer = behavior.GetGroupBuffer(ChatId);

        // LastChecked should have been defaulted to UtcNow, so the old message is not "new"
        Assert.False(buffer.HasMessagesSinceLastCheck());
    }

    [Fact]
    public void LegacyFormat_NewMessageAfterLoad_AppearsAsNew()
    {
        var pastIso = (DateTimeOffset.UtcNow - TimeSpan.FromHours(2)).ToString("O");
        var legacyJson = $$"""
        {
          "{{ChatId}}": [
            {"Sender":"alice","Text":"old message","ReplyTo":null,"Timestamp":"{{pastIso}}"}
          ]
        }
        """;
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, legacyJson);

        var behavior = BuildBehavior();
        var buffer = behavior.GetGroupBuffer(ChatId);

        // Simulate a new message arriving after restart
        buffer.Add("bob", "new message", null, DateTimeOffset.UtcNow);

        Assert.True(buffer.HasMessagesSinceLastCheck());
    }

    // ── New format: LastChecked roundtrip ─────────────────────────────────────

    [Fact]
    public void NewFormat_PersistedLastChecked_PreservesWatermarkAcrossRestart()
    {
        var pastEntry = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
        var lastChecked = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);

        // Write new-format JSON directly (simulates a post-fix save)
        var newJson = JsonSerializer.Serialize(new Dictionary<long, object>
        {
            [ChatId] = new { LastChecked = lastChecked, Entries = new[] { new { Sender = "alice", Text = "old message", ReplyTo = (string?)null, Timestamp = pastEntry, EntryType = "message", TelegramMessageId = 0L, ReplyToTelegramMessageId = (long?)null } } }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, newJson);

        var behavior = BuildBehavior();
        var buffer = behavior.GetGroupBuffer(ChatId);

        // Entry is before lastChecked — should not appear new
        Assert.False(buffer.HasMessagesSinceLastCheck());
    }

    [Fact]
    public void SaveAndReload_LastCheckedSurvivesRestart()
    {
        var behavior1 = BuildBehavior();
        var buffer1 = behavior1.GetGroupBuffer(ChatId);

        // Mark as checked (_lastChecked = UtcNow), then trigger SaveBuffers via BufferToolUse.
        // Tool-use entries are excluded from HasMessagesSinceLastCheck, so they don't
        // create a timing race between the saved LastChecked and the new entry's timestamp.
        buffer1.MarkChecked();
        behavior1.BufferToolUse(ChatId, "some-tool", "tool ran during check-in");

        // Simulate restart: new GroupBehavior instance reading the same WorkDir
        var behavior2 = BuildBehavior();
        var buffer2 = behavior2.GetGroupBuffer(ChatId);

        // Core fix: LastChecked is restored, not reset to MinValue
        Assert.NotEqual(DateTimeOffset.MinValue, buffer2.GetLastChecked());
        // Only tool_use entries (excluded from new-message check) — nothing appears new
        Assert.False(buffer2.HasMessagesSinceLastCheck());
    }
}
