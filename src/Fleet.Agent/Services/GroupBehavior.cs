using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

public sealed class GroupBehavior
{
    private readonly AgentOptions _agentConfig;
    private readonly TelegramOptions _telegramConfig;
    private readonly IAgentExecutor _executor;
    private readonly GroupRelayService _relay;
    private readonly TaskManager _taskManager;
    private readonly CommandDispatcher _commands;
    private readonly PromptAssembler _prompts;
    private readonly ILogger<GroupBehavior> _logger;

    private readonly ConcurrentDictionary<long, GroupChatBuffer> _groupBuffers = new();

    // Per-group debounce timers (fixes global-timer bug)
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _debounceTimers = new();

    // Pending images buffered from all-mode non-direct-trigger messages; drained at next check-in.
    private readonly ConcurrentDictionary<long, PendingImagesEntry> _pendingImages = new();

    private readonly string _historyPath;
    private bool _historyLoaded;
    private CancellationToken _shutdownToken;

    private string _botUsername = "";

    /// <summary>Set by AgentTransport after construction to break circular DI.</summary>
    public IMessageSink Sink { get; set; } = null!;

    public GroupBehavior(
        IOptions<AgentOptions> agentConfig,
        IOptions<TelegramOptions> telegramConfig,
        IAgentExecutor executor,
        GroupRelayService relay,
        TaskManager taskManager,
        CommandDispatcher commands,
        PromptAssembler prompts,
        ILogger<GroupBehavior> logger)
    {
        _agentConfig = agentConfig.Value;
        _telegramConfig = telegramConfig.Value;
        _executor = executor;
        _relay = relay;
        _taskManager = taskManager;
        _commands = commands;
        _prompts = prompts;
        _logger = logger;

        _historyPath = Path.Combine(_agentConfig.WorkDir, ".fleet", "chat-history.json");
    }

    public void SetBotUsername(string username) => _botUsername = username;

    public void SetShutdownToken(CancellationToken ct) => _shutdownToken = ct;

    public GroupChatBuffer GetGroupBuffer(long chatId)
    {
        if (!_historyLoaded)
            LoadBuffersFromDisk();

        return _groupBuffers.GetOrAdd(chatId, _ => new GroupChatBuffer());
    }

    public void BufferBotResponse(long chatId, string text, long telegramMessageId = 0)
    {
        var buffer = GetGroupBuffer(chatId);
        var truncated = text.Length > 200 ? text[..200] + "..." : text;
        buffer.Add($"@{_botUsername}", truncated, replyTo: null, DateTimeOffset.UtcNow,
            telegramMessageId: telegramMessageId);
        SaveBuffers();
    }

    public void BufferToolUse(long chatId, string toolName, string description)
    {
        var buffer = GetGroupBuffer(chatId);
        buffer.AddToolUse(toolName, description, DateTimeOffset.UtcNow);
        SaveBuffers();
    }

    public void AddAndPersist(long chatId, string sender, string text, string? replyTo,
        long telegramMessageId = 0, long? replyToTelegramMessageId = null)
    {
        var buffer = GetGroupBuffer(chatId);
        buffer.Add(sender, text, replyTo, DateTimeOffset.UtcNow,
            telegramMessageId: telegramMessageId,
            replyToTelegramMessageId: replyToTelegramMessageId);
        SaveBuffers();
    }

    private void LoadBuffersFromDisk()
    {
        _historyLoaded = true;

        if (!File.Exists(_historyPath))
            return;

        try
        {
            var json = File.ReadAllText(_historyPath);
            var data = JsonSerializer.Deserialize<Dictionary<long, List<SerializedEntry>>>(json);
            if (data is null)
                return;

            foreach (var (chatId, entries) in data)
            {
                var buffer = _groupBuffers.GetOrAdd(chatId, _ => new GroupChatBuffer());
                buffer.LoadEntries(entries);
            }

            _logger.LogInformation("Loaded chat history from disk ({Count} chats)", data.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load chat history from {Path}", _historyPath);
        }
    }

    private void SaveBuffers()
    {
        try
        {
            var data = new Dictionary<long, List<SerializedEntry>>();
            foreach (var (chatId, buffer) in _groupBuffers)
                data[chatId] = buffer.GetEntries();

            var dir = Path.GetDirectoryName(_historyPath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save chat history to {Path}", _historyPath);
        }
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
                StartGroupCheckIn(chatId, "All-messages check-in", """
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
    public void AddPendingImages(long chatId, IReadOnlyList<MessageImage> images, int maxImages)
    {
        if (images.Count == 0) return;
        var entry = _pendingImages.GetOrAdd(chatId, _ => new PendingImagesEntry());
        var hadOverflow = entry.AddRange(images, maxImages);
        if (hadOverflow)
            _logger.LogWarning("Pending image buffer for group {ChatId} exceeded cap ({Cap}), overflow images dropped", chatId, maxImages);
    }

    private IReadOnlyList<MessageImage> DrainPendingImages(long chatId)
    {
        if (!_pendingImages.TryRemove(chatId, out var entry))
            return [];
        if (entry.IsExpired)
        {
            _logger.LogDebug("Pending images for group {ChatId} exceeded TTL, dropping without forwarding", chatId);
            return [];
        }
        return entry.Drain();
    }

    // --- Relay handling ---

    public void OnRelayMessage(long chatId, string sender, string text, string type, string? correlationId = null, string? taskId = null, string? workflowId = null, string? signalName = null)
    {
        if (type == RelayMessageType.TokenUpdate)
        {
            _logger.LogInformation("Token update received from {Sender}", sender);
            _ = Task.Run(async () =>
            {
                try { await ApplyTokenUpdateAsync(text); }
                catch (Exception ex) { _logger.LogError(ex, "Token update failed"); }
            });
            return;
        }

        if (type == RelayMessageType.BridgeRequest)
        {
            _logger.LogInformation("Bridge request from {Sender}, correlationId={CorrelationId}", sender, correlationId);
            CancelDebounce(chatId);

            _ = Task.Run(() =>
            {
                var buffer = GetGroupBuffer(chatId);
                var prompt = _prompts.ForRelayDirective(buffer, sender, text);
                buffer.MarkChecked();
                var displayText = $"[Bridge: {sender}] {TaskManager.TruncateText(text, 500)}";
                _taskManager.StartTask(chatId, prompt, displayText, isSessionTask: true,
                    source: TaskSource.Bridge, relaySender: "bridge", correlationId: correlationId, taskId: taskId);
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
            var buffer = GetGroupBuffer(chatId);
            var prompt = _prompts.ForRelayDirective(buffer, sender, text);
            buffer.MarkChecked();
            var displayText = $"[From: {sender}] {TaskManager.TruncateText(text, 500)}";
            _taskManager.StartTask(chatId, prompt, displayText, isSessionTask: true,
                source: TaskSource.Relay, relaySender: sender, taskId: taskId);
        });
    }

    private async Task ApplyTokenUpdateAsync(string tokenJson)
    {
        var shared = JsonNode.Parse(tokenJson);
        var newAccessToken = shared?["accessToken"]?.GetValue<string>();
        var newRefreshToken = shared?["refreshToken"]?.GetValue<string>();
        var newExpiresAt = shared?["expiresAt"]?.GetValue<long>();
        if (newAccessToken is null || newExpiresAt is null)
        {
            _logger.LogWarning("Invalid token update payload");
            return;
        }

        // Route by provider — default to "claude" for backward compat with old broadcasts
        var provider = shared?["provider"]?.GetValue<string>() ?? "claude";
        var agentProvider = _agentConfig.Provider.ToLowerInvariant();

        if (!provider.Equals(agentProvider, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Token update for provider '{Provider}' ignored — this agent uses '{AgentProvider}'",
                provider, agentProvider);
            return;
        }

        if (provider == "codex")
        {
            await ApplyCodexTokenUpdateAsync(shared!, newAccessToken, newRefreshToken, newExpiresAt.Value);
        }
        else if (provider == "gemini")
        {
            await ApplyGeminiTokenUpdateAsync(newAccessToken, newExpiresAt.Value);
        }
        else
        {
            await ApplyClaudeTokenUpdateAsync(newAccessToken, newRefreshToken, newExpiresAt.Value);
        }

        if (!await _executor.TryStopProcessAsync())
            _executor.RequestRestart();
    }

    private async Task ApplyClaudeTokenUpdateAsync(string newAccessToken, string? newRefreshToken, long newExpiresAt)
    {
        const string credsPath = "/root/.claude/.credentials.json";

        JsonNode creds;
        if (File.Exists(credsPath))
        {
            creds = JsonNode.Parse(await File.ReadAllTextAsync(credsPath))!;
        }
        else
        {
            _logger.LogInformation("Claude credentials file not found, creating from token update");
            Directory.CreateDirectory(Path.GetDirectoryName(credsPath)!);
            creds = JsonNode.Parse("{}")!;
        }

        var oauth = creds["claudeAiOauth"];
        if (oauth is null)
        {
            oauth = JsonNode.Parse("{}")!;
            creds["claudeAiOauth"] = oauth;
        }

        oauth["accessToken"] = newAccessToken;
        oauth["expiresAt"] = newExpiresAt;
        if (newRefreshToken is not null)
            oauth["refreshToken"] = newRefreshToken;

        var tmpPath = credsPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, creds!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, credsPath, overwrite: true);

        _logger.LogInformation("Claude credentials updated, new expiry: {ExpiresAt}", newExpiresAt);
    }

    private async Task ApplyCodexTokenUpdateAsync(JsonNode payload, string newAccessToken, string? newRefreshToken, long newExpiresAt)
    {
        const string authPath = "/root/.codex/auth.json";

        var newIdToken = payload["idToken"]?.GetValue<string>();
        var newAccountId = payload["accountId"]?.GetValue<string>();

        JsonNode auth;
        if (File.Exists(authPath))
        {
            auth = JsonNode.Parse(await File.ReadAllTextAsync(authPath))!;
        }
        else
        {
            _logger.LogInformation("Codex auth.json not found, creating from token update");
            Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
            auth = JsonNode.Parse("""{"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{}}""")!;
        }

        var tokens = auth["tokens"];
        if (tokens is null)
        {
            tokens = JsonNode.Parse("{}")!;
            auth["tokens"] = tokens;
        }

        tokens["access_token"] = newAccessToken;
        if (newRefreshToken is not null)
            tokens["refresh_token"] = newRefreshToken;
        if (newIdToken is not null)
            tokens["id_token"] = newIdToken;
        if (newAccountId is not null)
            tokens["account_id"] = newAccountId;
        auth["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");

        var tmpPath = authPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmpPath, authPath, overwrite: true);

        _logger.LogInformation("Codex auth.json updated, new expiry: {ExpiresAt}", newExpiresAt);
    }

    private async Task ApplyGeminiTokenUpdateAsync(string newAccessToken, long newExpiresAt)
    {
        const string credsPath = "/root/.gemini/oauth_creds.json";

        JsonNode creds;
        if (File.Exists(credsPath))
        {
            creds = JsonNode.Parse(await File.ReadAllTextAsync(credsPath))!;
        }
        else
        {
            _logger.LogInformation("Gemini oauth_creds.json not found, creating from token update");
            Directory.CreateDirectory(Path.GetDirectoryName(credsPath)!);
            creds = JsonNode.Parse("{}")!;
        }

        // Only update access_token and expiry_date — refresh_token, client_id, client_secret
        // must be preserved exactly as-is (Google does not rotate the refresh token).
        creds["access_token"] = newAccessToken;
        creds["expiry_date"] = newExpiresAt;

        var content = creds.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        try
        {
            await WriteCredentialFileAsync(credsPath, content);
            _logger.LogInformation("Gemini oauth_creds.json updated, new expiry: {ExpiresAt}", newExpiresAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write Gemini oauth_creds.json (path: {Path})", credsPath);
        }
    }

    // Write credential content to a bind-mounted file via copy-then-delete.
    // File.Move / rename(2) fails with EBUSY on Docker bind-mount targets; writing
    // through an open file descriptor (File.Copy overwrite) succeeds because it
    // opens the existing inode for writing rather than replacing the directory entry.
    internal static async Task WriteCredentialFileAsync(string finalPath, string content)
    {
        var tmpPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, content);
        try
        {
            File.Copy(tmpPath, finalPath, overwrite: true);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(finalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

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

    // --- Proactive loop (iterates ALL groups — fixes single-group bug) ---

    public async Task RunProactiveLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(_agentConfig.ProactiveIntervalMinutes);
        _logger.LogInformation("Proactive loop started with interval {Interval}", interval);

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await RunProactiveCheckIns(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Proactive check-in failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        _logger.LogInformation("Proactive loop stopped");
    }

    private Task RunProactiveCheckIns(CancellationToken ct)
    {
        // Iterate ALL allowed groups with new messages (fixes first-group-only bug)
        foreach (var groupId in _telegramConfig.AllowedGroupIds)
        {
            if (!_groupBuffers.TryGetValue(groupId, out var buffer) || !buffer.HasMessagesSinceLastCheck())
                continue;

            if (_taskManager.HasRunningTasks(groupId))
            {
                _logger.LogDebug("Proactive check-in: tasks running in group {ChatId}, skipping", groupId);
                continue;
            }

            buffer.MarkChecked();

            var isAllMode = _agentConfig.GroupListenMode.Equals("all", StringComparison.OrdinalIgnoreCase);

            if (isAllMode && _executor.IsProcessWarm)
            {
                StartGroupCheckIn(groupId, "Proactive check-in", """
                    Review your memory and session context. Are there pending tasks,
                    follow-ups, or overdue items? If nothing needs attention: IDLE
                    """, ct);
            }
            else
            {
                var context = buffer.FormatContext();
                if (context.Length == 0)
                {
                    _logger.LogDebug("Proactive check-in: buffer empty after format for group {ChatId}, skipping", groupId);
                    continue;
                }

                StartGroupCheckIn(groupId, "Proactive check-in", $"""
                    Review the recent conversation and your memory. Track all tasks — ones
                    the CEO assigned to any agent, ones you delegated, and ones assigned to you.
                    Are there pending items, follow-ups, or overdue tasks? If nothing needs
                    attention, respond with just: IDLE
                    """, ct);
            }
        }

        return Task.CompletedTask;
    }

    // --- Shared check-in method ---

    public void StartGroupCheckIn(long chatId, string label, string instruction, CancellationToken ct)
    {
        if (_taskManager.HasRunningTasks(chatId))
        {
            _logger.LogDebug("{Label} skipped — tasks running in group {ChatId}", label, chatId);
            return;
        }

        var buffer = GetGroupBuffer(chatId);
        var prompt = _prompts.ForCheckIn(buffer, label, instruction);
        buffer.MarkChecked();
        var pendingImages = DrainPendingImages(chatId);
        _logger.LogInformation("{Label} triggered for group {ChatId}", label, chatId);

        // Delegate entirely to TaskManager — it handles typing, execution,
        // session tracking, tool buffering, IDLE suppression, and completion events.
        _taskManager.StartTask(chatId, prompt, $"[{label}]", isSessionTask: true,
            source: TaskSource.CheckIn,
            images: pendingImages.Count > 0 ? pendingImages : null);
    }

    public string BuildGroupTask(long chatId, string sender, string taskText,
        string? replyToUsername = null, string? replyToText = null, long telegramMessageId = 0) =>
        _prompts.ForGroupMessage(GetGroupBuffer(chatId), sender, taskText, replyToUsername, replyToText, telegramMessageId);

    public string BuildDmTask(long chatId, string taskText, string? replyToText = null, long telegramMessageId = 0) =>
        _prompts.ForDm(GetGroupBuffer(chatId), taskText, replyToText, telegramMessageId);

    // --- Pending images buffer entry ---

    private sealed class PendingImagesEntry
    {
        /// Images older than this are considered stale and dropped on drain.
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        private readonly Lock _lock = new();
        private readonly List<MessageImage> _images = [];
        private DateTimeOffset _storedAt = DateTimeOffset.UtcNow;

        /// <summary>
        /// Appends <paramref name="incoming"/> up to <paramref name="cap"/>.
        /// Returns <c>true</c> if any images were dropped due to the cap.
        /// </summary>
        public bool AddRange(IReadOnlyList<MessageImage> incoming, int cap)
        {
            lock (_lock)
            {
                _storedAt = DateTimeOffset.UtcNow;
                var hadOverflow = false;
                foreach (var img in incoming)
                {
                    if (_images.Count >= cap) { hadOverflow = true; break; }
                    _images.Add(img);
                }
                return hadOverflow;
            }
        }

        public IReadOnlyList<MessageImage> Drain()
        {
            lock (_lock)
            {
                if (_images.Count == 0) return [];
                var result = _images.ToArray();
                _images.Clear();
                return result;
            }
        }

        public bool IsExpired => DateTimeOffset.UtcNow - _storedAt > Ttl;
    }
}
