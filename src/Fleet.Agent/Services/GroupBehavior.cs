using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services.Credentials;
using Fleet.Agent.Services.GroupChat;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

public sealed class GroupBehavior
{
    private readonly AgentOptions _agentConfig;
    private readonly TelegramOptions _telegramConfig;
    private readonly AllowlistHolder _allowlist;
    private readonly IAgentExecutor _executor;
    private readonly GroupRelayService _relay;
    private readonly TaskManager _taskManager;
    private readonly CommandDispatcher _commands;
    private readonly PromptAssembler _prompts;
    private readonly ILogger<GroupBehavior> _logger;

    // Per-group debounce timers (fixes global-timer bug)
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _debounceTimers = new();

    private readonly GroupChatHistoryStore _history;
    private readonly PendingGroupImages _pendingImages;
    private readonly ProviderTokenUpdater _tokens;
    private readonly ProactiveCheckInLoop _proactive;

    private CancellationToken _shutdownToken;

    private string _botUsername = "";

    /// <summary>
    /// Outbound text destination. Constructor-injected via <see cref="MessageSinkHolder"/>
    /// instead of assigned by <c>AgentTransport</c> after construction (#277 D-1). Never null.
    ///
    /// GroupBehavior currently calls no send path of its own — every outbound message it causes
    /// goes through TaskManager or CommandDispatcher. It is wired anyway so the four sink
    /// consumers share one shape and a future send site cannot reintroduce the settable property.
    /// </summary>
    private readonly IMessageSink _sink;

    public GroupBehavior(
        IOptions<AgentOptions> agentConfig,
        IOptions<TelegramOptions> telegramConfig,
        AllowlistHolder allowlist,
        IAgentExecutor executor,
        GroupRelayService relay,
        TaskManager taskManager,
        CommandDispatcher commands,
        PromptAssembler prompts,
        ILogger<GroupBehavior> logger,
        IMessageSink? sink = null)
    {
        _agentConfig = agentConfig.Value;
        _telegramConfig = telegramConfig.Value;
        _allowlist = allowlist;
        _executor = executor;
        _relay = relay;
        _taskManager = taskManager;
        _commands = commands;
        _prompts = prompts;
        _logger = logger;
        _sink = sink ?? NullMessageSink.Instance;

        _history = new GroupChatHistoryStore(Path.Combine(_agentConfig.WorkDir, ".fleet", "chat-history.json"), _logger);
        _pendingImages = new PendingGroupImages(_logger);
        _tokens = new ProviderTokenUpdater(_agentConfig, _executor, _logger);
        _proactive = new ProactiveCheckInLoop(_agentConfig, _allowlist, _executor, _history, StartGroupCheckIn, _logger);
    }

    public GroupChatBuffer GetGroupBuffer(long chatId) => _history.GetGroupBuffer(chatId);

    public void SetBotUsername(string username) => _botUsername = username;

    public void SetShutdownToken(CancellationToken ct) => _shutdownToken = ct;

    /// <summary>
    /// The shutdown token currently in force. Exposed so a test can prove it is the one that
    /// fires on application STOP rather than the one that aborts startup — a distinction that is
    /// invisible from the outside and easy to get wrong.
    /// </summary>
    internal CancellationToken ShutdownTokenForTesting => _shutdownToken;

    public void BufferBotResponse(long chatId, string text, long telegramMessageId = 0)
    {
        var buffer = GetGroupBuffer(chatId);
        var truncated = text.Length > 200 ? text[..200] + "..." : text;
        buffer.Add($"@{_botUsername}", truncated, replyTo: null, DateTimeOffset.UtcNow,
            telegramMessageId: telegramMessageId);
        _history.Save();
    }

    public void BufferToolUse(long chatId, string toolName, string description)
    {
        var buffer = GetGroupBuffer(chatId);
        buffer.AddToolUse(toolName, description, DateTimeOffset.UtcNow);
        _history.Save();
    }

    public void AddAndPersist(long chatId, string sender, string text, string? replyTo,
        long telegramMessageId = 0, long? replyToTelegramMessageId = null)
    {
        var buffer = GetGroupBuffer(chatId);
        buffer.Add(sender, text, replyTo, DateTimeOffset.UtcNow,
            telegramMessageId: telegramMessageId,
            replyToTelegramMessageId: replyToTelegramMessageId);
        _history.Save();
    }

    // --- Debounce (per-group) ---

    public void ScheduleDebounce(long chatId)
    {
        var newCts = new CancellationTokenSource();
        var delay = TimeSpan.FromSeconds(_agentConfig.GroupDebounceSeconds);

        // Replace existing timer for this group (atomically)
        var oldCts = _debounceTimers.AddOrUpdate(chatId, newCts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return newCts;
        });

        // Link debounce timer to shutdown token so check-ins don't fire during shutdown
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(newCts.Token, _shutdownToken);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, linkedCts.Token);
                await StartDebouncedGroupCheckInAsync(chatId, "All-messages check-in", """
                    Review the conversation above. If anything needs your input, action, or
                    follow-up — respond. If nothing needs attention: IDLE
                    """, _shutdownToken);
            }
            catch (OperationCanceledException)
            {
                // Debounce was reset or cancelled — expected
            }
            finally
            {
                linkedCts.Dispose();
            }
        });
    }

    public void CancelDebounce(long chatId)
    {
        if (_debounceTimers.TryRemove(chatId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void CancelAllDebounce()
    {
        foreach (var (chatId, cts) in _debounceTimers)
        {
            if (_debounceTimers.TryRemove(chatId, out _))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    // --- Pending images (all-mode) ---

    /// <summary>
    /// Buffer images from a non-direct-trigger all-mode message so they can be forwarded
    /// to the LLM when the debounce check-in fires. Enforces MaxImagesPerGroup cap.
    /// </summary>
    public void AddPendingImages(long chatId, IReadOnlyList<MessageImage> images, int maxImages) =>
        _pendingImages.Add(chatId, images, maxImages);

    // --- Relay handling ---

    public void OnRelayMessage(long chatId, string sender, string text, string type, string? correlationId = null, string? taskId = null, string? workflowId = null, string? signalName = null)
    {
        if (type == RelayMessageType.ConfigUpdate)
        {
            _logger.LogInformation("config.update received from orchestrator");
            _ = Task.Run(async () =>
            {
                try
                {
                    var update = JsonSerializer.Deserialize<ConfigUpdateMessage>(text,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (update is null) return;

                    var changeset = _allowlist.Apply(new AllowlistDiff(
                        update.AddedUsers,
                        update.RemovedUserIds,
                        update.AddedGroupIds,
                        update.RemovedGroupIds));

                    foreach (var user in changeset.NewlyAddedUsers)
                    {
                        _logger.LogInformation("New user approved: {UserId} — sending welcome DM", user.UserId);
                        await SendWelcomeDmAsync(user);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Failed to apply config.update"); }
            });
            return;
        }

        if (type == RelayMessageType.TokenUpdate)
        {
            _logger.LogInformation("Token update received from {Sender}", sender);
            _ = Task.Run(async () =>
            {
                try { await _tokens.ApplyAsync(text); }
                catch (Exception ex) { _logger.LogError(ex, "Token update failed"); }
            });
            return;
        }

        if (type == RelayMessageType.StatusCheck)
        {
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                _logger.LogWarning("Status check from {Sender} rejected: correlationId is required", sender);
                return;
            }

            var (status, _, _) = _taskManager.GetOrchestratorStatus();
            _ = _relay.PublishToAgentAsync(
                "bridge",
                chatId,
                status,
                type: RelayMessageType.StatusResponse,
                correlationId: correlationId);
            return;
        }

        if (type == RelayMessageType.BridgeRequest)
        {
            _logger.LogInformation("Bridge request from {Sender}, correlationId={CorrelationId}", sender, correlationId);
            CancelDebounce(chatId);

            _ = Task.Run(() =>
            {
                try
                {
                    var buffer = GetGroupBuffer(chatId);
                    var prompt = _prompts.ForRelayDirective(buffer, sender, text);
                    buffer.MarkChecked();
                    var displayText = $"[Bridge: {sender}] {TaskManager.TruncateText(text, 500)}";
                    _taskManager.StartTaskDetached(chatId, prompt, displayText, isSessionTask: true,
                        source: TaskSource.Bridge, relaySender: "bridge", correlationId: correlationId, taskId: taskId);
                }
                catch (Exception ex)
                {
                    LogRelayDispatchFault(ex, chatId, sender, type, taskId);
                }
            });
            return;
        }

        if (type is RelayMessageType.Response or RelayMessageType.PartialResponse)
            AddAndPersist(chatId, sender, text, replyTo: null);

        if (type == RelayMessageType.PartialResponse)
        {
            _logger.LogWarning("Partial response from {Sender} (hit max turns limit)", sender);
            ScheduleDebounce(chatId);
            return;
        }

        if (type == RelayMessageType.Response)
        {
            _logger.LogInformation("Relay response received from {Sender}, buffered + scheduling check-in", sender);
            ScheduleDebounce(chatId);
            return;
        }

        _logger.LogInformation("Relay directive received from {Sender}, processing", sender);
        CancelDebounce(chatId);

        // Check if the relay contains a command (e.g., "Aops, /cancel all")
        var command = ExtractRelayCommand(text);
        if (command is not null)
        {
            _logger.LogInformation("Relay command from {Sender}: {Command}", sender, command);
            _ = Task.Run(async () => await _commands.TryHandleAsync(chatId, command));
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var buffer = GetGroupBuffer(chatId);
                var prompt = _prompts.ForRelayDirective(buffer, sender, text);
                buffer.MarkChecked();
                var displayText = $"[From: {sender}] {TaskManager.TruncateText(text, 500)}";
                _taskManager.StartTaskDetached(chatId, prompt, displayText, isSessionTask: true,
                    source: TaskSource.Relay, relaySender: sender, taskId: taskId);
            }
            catch (Exception ex)
            {
                LogRelayDispatchFault(ex, chatId, sender, type, taskId);
            }
        });
    }

    /// <summary>
    /// A relay or bridge directive that could not be turned into a task. The sender is waiting for
    /// an answer that will not come, so this must at least be visible. Identifiers only, never the
    /// directive's text.
    /// </summary>
    private void LogRelayDispatchFault(Exception ex, long chatId, string sender, string type, string? taskId) =>
        _logger.LogError(ex,
            "Relay message could not be dispatched and was dropped (chat={ChatId}, sender={Sender}, type={Type}, taskId={TaskId})",
            chatId, sender, type, taskId ?? "none");

    internal Task ApplyTokenUpdateForTestsAsync(string tokenJson) => _tokens.ApplyAsync(tokenJson);

    /// <summary>Where a codex token broadcast writes. Settable so tests do not touch the real home.</summary>
    internal string CodexAuthPath
    {
        get => _tokens.CodexAuthPath;
        set => _tokens.CodexAuthPath = value;
    }

    /// <summary>Where a claude token broadcast writes. Settable so tests do not touch the real home.</summary>
    internal string ClaudeCredentialsPath
    {
        get => _tokens.ClaudeCredentialsPath;
        set => _tokens.ClaudeCredentialsPath = value;
    }

    /// <summary>See <see cref="ProviderTokenUpdater.WriteCredentialFileAsync"/>.</summary>
    internal static Task WriteCredentialFileAsync(string finalPath, string content) =>
        ProviderTokenUpdater.WriteCredentialFileAsync(finalPath, content);

    private string? ExtractRelayCommand(string text)
    {
        // Strip @bot_username prefix (e.g., "@fleet_aops_bot, /cancel all")
        text = Regex.Replace(text, @"(?i)^\s*@\S+\s*[,:]\s*", "");

        // Also strip ShortName prefix for backwards compatibility
        var shortName = _agentConfig.ShortName;
        if (!string.IsNullOrEmpty(shortName))
        {
            var pattern = $@"(?i)^\s*{Regex.Escape(shortName)}\s*[,:]\s*";
            text = Regex.Replace(text, pattern, "");
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.StartsWith('/') ? firstLine : null;
    }

    // --- Proactive loop ---

    public Task RunProactiveLoopAsync(CancellationToken ct) => _proactive.RunAsync(ct);

    // --- Check-in dispatch methods ---

    /// <summary>
    /// Dispatches a check-in for the proactive timer sweep and the welcome DM.
    /// Stamps the watermark with wall-clock time before dispatch and drains pending
    /// images destructively (existing behaviour — these callers are not batch-sensitive).
    /// Uses <see cref="TaskSource.CheckIn"/> so IDLE and no-output suppression apply.
    /// </summary>
    public void StartGroupCheckIn(long chatId, string label, string instruction, CancellationToken ct)
    {
        var buffer = GetGroupBuffer(chatId);
        var prompt = _prompts.ForCheckIn(buffer, label, instruction);
        var coldStartPrompt = _prompts.ForCheckInColdStart(buffer, label, instruction);
        buffer.MarkChecked();
        var pendingImages = _pendingImages.Drain(chatId);
        _logger.LogInformation("{Label} triggered for group {ChatId}", label, chatId);

        // Delegate entirely to TaskManager — it handles typing, execution,
        // session tracking, tool buffering, IDLE suppression, and completion events.
        _taskManager.StartTaskDetached(chatId, prompt, $"[{label}]", isSessionTask: true,
            source: TaskSource.CheckIn,
            images: pendingImages.Count > 0 ? pendingImages : null,
            coldStartTask: coldStartPrompt);
    }

    /// <summary>
    /// Dispatches a human-originated group-chat batch after the debounce timer fires.
    /// Uses the peek/commit pattern so that images and the watermark are only advanced
    /// once the task manager has accepted the batch (Ran, Injected, or Queued).
    /// Uses <see cref="TaskSource.DebouncedGroupBatch"/> to enable same-chat mid-turn
    /// injection and to trigger IDLE and no-output suppression.
    /// </summary>
    private async Task StartDebouncedGroupCheckInAsync(long chatId, string label, string instruction, CancellationToken ct)
    {
        var buffer = GetGroupBuffer(chatId);
        var batchTime = DateTimeOffset.UtcNow;
        var pendingImages = _pendingImages.Peek(chatId);
        var prompt = _prompts.ForCheckIn(buffer, label, instruction);
        var coldStartPrompt = _prompts.ForCheckInColdStart(buffer, label, instruction);
        _logger.LogInformation("{Label} triggered for group {ChatId}", label, chatId);

        var outcome = await _taskManager.StartTask(chatId, prompt, $"[{label}]", isSessionTask: true,
            source: TaskSource.DebouncedGroupBatch,
            images: pendingImages.Count > 0 ? pendingImages : null,
            coldStartTask: coldStartPrompt);

        if (outcome is TaskDispatchOutcome.Ran or TaskDispatchOutcome.Injected or TaskDispatchOutcome.Queued)
        {
            buffer.MarkChecked(batchTime);
            // CommitPendingImages is unconditional: even when pendingImages is empty, committed
            // entries with storedAt ≤ batchTime need cleanup (e.g. images that expired mid-batch).
            _pendingImages.Commit(chatId, batchTime);
        }
        else
        {
            _logger.LogDebug("{Label}: dispatch outcome={Outcome} for group {ChatId} — not advancing watermark or committing images", label, outcome, chatId);
        }
    }

    public string BuildGroupTask(long chatId, string sender, string taskText,
        long? replyToTelegramMessageId = null, long telegramMessageId = 0,
        string? chatTitle = null, bool isVoiceTranscription = false) =>
        _prompts.ForGroupMessage(GroupBufferFor(chatId, chatTitle), sender, taskText, replyToTelegramMessageId,
            telegramMessageId, isVoiceTranscription);

    /// <summary>
    /// <see cref="BuildGroupTask"/> as a provider process with no memory of the conversation needs
    /// it. Passed to <see cref="TaskManager.StartTask"/> beside the prompt, for a turn that ends
    /// up starting on a process that was restarted while the message waited.
    /// </summary>
    public string BuildGroupColdStartTask(long chatId, string sender, string taskText,
        long? replyToTelegramMessageId = null, long telegramMessageId = 0,
        string? chatTitle = null, bool isVoiceTranscription = false) =>
        _prompts.ForGroupMessageColdStart(GroupBufferFor(chatId, chatTitle), sender, taskText, replyToTelegramMessageId,
            telegramMessageId, isVoiceTranscription);

    public string BuildDmTask(long chatId, string taskText, long? replyToTelegramMessageId = null,
        long telegramMessageId = 0, string? chatUsername = null, string? chatFirstName = null,
        bool isVoiceTranscription = false) =>
        _prompts.ForDm(DmBufferFor(chatId, chatUsername, chatFirstName), taskText, replyToTelegramMessageId,
            telegramMessageId, isVoiceTranscription);

    /// <summary><see cref="BuildDmTask"/> for a cold provider process. See <see cref="BuildGroupColdStartTask"/>.</summary>
    public string BuildDmColdStartTask(long chatId, string taskText, long? replyToTelegramMessageId = null,
        long telegramMessageId = 0, string? chatUsername = null, string? chatFirstName = null,
        bool isVoiceTranscription = false) =>
        _prompts.ForDmColdStart(DmBufferFor(chatId, chatUsername, chatFirstName), taskText, replyToTelegramMessageId,
            telegramMessageId, isVoiceTranscription);

    private GroupChatBuffer GroupBufferFor(long chatId, string? chatTitle)
    {
        var buffer = GetGroupBuffer(chatId);
        // Store title on first encounter (don't overwrite once set)
        if (chatTitle is not null && buffer.ChatTitle is null)
            buffer.ChatTitle = chatTitle;
        return buffer;
    }

    private GroupChatBuffer DmBufferFor(long chatId, string? chatUsername, string? chatFirstName)
    {
        var buffer = GetGroupBuffer(chatId);
        // Build and cache the DM label on first encounter
        if (buffer.ChatLabel is null)
        {
            if (chatUsername is { Length: > 0 })
                buffer.ChatLabel = $"user=@{chatUsername}";
            else if (chatFirstName is { Length: > 0 })
                buffer.ChatLabel = $"name=\"{chatFirstName.Replace("\"", "\\\"")}\"";
        }
        return buffer;
    }

    // --- Welcome DM on approval ---

    private Task SendWelcomeDmAsync(NewlyAddedUser user)
    {
        // Use the DM chat ID = UserId. The agent generates the welcome in its own voice.
        var displayName = user.Username is not null ? $"@{user.Username}"
            : user.FirstName ?? $"user {user.UserId}";
        var prompt = $"""
            [system] A new user ({displayName}, user_id={user.UserId}) was just approved to chat with you.
            Send them a brief, in-character welcome DM introducing yourself and letting
            them know they can now message you directly.
            """;
        var display = $"[Welcome DM → {displayName}]";
        _taskManager.StartTaskDetached(user.UserId, prompt, display, isSessionTask: false,
            source: TaskSource.CheckIn);
        return Task.CompletedTask;
    }

    // ─── Internal testing seams ─────────────────────────────────────────────────
    // Exposed via InternalsVisibleTo for Fleet.Agent.Tests. Production code never calls these.

    /// <summary>Invokes <see cref="StartDebouncedGroupCheckInAsync"/> directly, bypassing the debounce timer.</summary>
    internal Task TriggerDebouncedGroupBatchForTestAsync(long chatId, string label = "test", string instruction = "check-in")
        => StartDebouncedGroupCheckInAsync(chatId, label, instruction, CancellationToken.None);

    /// <summary>Returns <c>true</c> when there are pending images for <paramref name="chatId"/>.</summary>
    internal bool HasPendingImages(long chatId) => _pendingImages.Has(chatId);

    /// <summary>Exposes <see cref="PendingGroupImages.Commit"/> for boundary-logic tests.</summary>
    internal void CommitPendingImagesForTest(long chatId, DateTimeOffset boundary)
        => _pendingImages.Commit(chatId, boundary);

    /// <summary>Exposes <see cref="PendingGroupImages.Peek"/> for TTL tests.</summary>
    internal IReadOnlyList<MessageImage> PeekPendingImagesForTest(long chatId)
        => _pendingImages.Peek(chatId);

    /// <summary>Sets the pending-images entry for <paramref name="chatId"/> to appear older than <paramref name="age"/>.</summary>
    internal void BackdatePendingImagesForTest(long chatId, TimeSpan age) =>
        _pendingImages.BackdateForTest(chatId, age);
}
