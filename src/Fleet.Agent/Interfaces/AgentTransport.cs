using System.Text.RegularExpressions;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Interfaces;

/// <summary>
/// Thin Telegram shell. Only file that imports Telegram.Bot.*.
/// Builds IncomingMessage from Telegram updates, delegates to MessageRouter.
/// Implements IMessageSink for outbound messages (splits at 4000 chars).
/// When TELEGRAM_BOT_TOKEN is missing or empty the service enters headless mode:
/// RabbitMQ + MCP remain fully functional; Telegram poller is disabled.
/// </summary>
public sealed class AgentTransport : BackgroundService, IMessageSink
{
    private readonly TelegramBotClient? _bot;
    private readonly AgentOptions _agentConfig;
    private readonly TelegramOptions _telegramConfig;
    private readonly GroupRelayService _relay;
    private readonly TaskManager _taskManager;
    private readonly GroupBehavior _groupBehavior;
    private readonly MessageRouter _router;
    private readonly CommandDispatcher _commands;
    private readonly VoiceTranscriptionService _voiceTranscription;
    private readonly TtsService _tts;
    private readonly IFleetConnectionState _connectionState;
    private readonly ILogger<AgentTransport> _logger;

    private string _botUsername = "";
    private readonly MediaGroupBuffer _mediaGroupBuffer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _groupSizeCapped = new();

    // Tracks the last Telegram message_id sent per chatId (updated in SendTextAsync).
    // Consumed by OnTaskCompleted so BufferBotResponse can persist the outbound message_id.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _lastSentMessageIds = new();

    public AgentTransport(
        IOptions<AgentOptions> agentConfig,
        IOptions<TelegramOptions> telegramConfig,
        GroupRelayService relay,
        TaskManager taskManager,
        GroupBehavior groupBehavior,
        MessageRouter router,
        CommandDispatcher commands,
        VoiceTranscriptionService voiceTranscription,
        TtsService tts,
        IFleetConnectionState connectionState,
        ILogger<AgentTransport> logger)
    {
        _agentConfig = agentConfig.Value;
        _telegramConfig = telegramConfig.Value;
        _relay = relay;
        _taskManager = taskManager;
        _groupBehavior = groupBehavior;
        _router = router;
        _commands = commands;
        _voiceTranscription = voiceTranscription;
        _tts = tts;
        _connectionState = connectionState;
        _logger = logger;

        // Only create the bot client when a token is available.
        if (!string.IsNullOrWhiteSpace(telegramConfig.Value.BotToken))
            _bot = new TelegramBotClient(telegramConfig.Value.BotToken);

        // Inject self as IMessageSink to break circular DI
        _taskManager.Sink = this;
        _groupBehavior.Sink = this;
        _router.Sink = this;
        _commands.Sink = this;

        _mediaGroupBuffer = new MediaGroupBuffer(telegramConfig.Value.MaxGroupBufferMs);
    }

    private static readonly Regex ImageMarkerRegex = new(@"\[IMAGE:(.+?)\]", RegexOptions.Compiled);
    private static readonly Regex TaskFailedMarkerRegex = new(@"^\[TASK_FAILED:\s*([^\]]+)\]\s*", RegexOptions.Compiled);
    private static readonly Regex ReplyToTokenRegex = new(@"\[reply_to:\s*(-?\d+)\]", RegexOptions.Compiled);

    // --- IMessageSink ---

    public async Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
    {
        // chatId==0 means "headless workflow delegation" — no Telegram destination.
        // The result still flows back to the caller via the relay (see OnTaskCompleted).
        if (chatId == 0) return;
        if (_bot is null) return;

        // Extract and strip [reply_to: N] token from agent output (Feature 3b).
        (text, var replyToMessageId) = ExtractReplyToToken(text, _logger);

        // Split on [IMAGE:...] markers; odd-indexed segments are file paths
        var parts = ImageMarkerRegex.Split(text);
        bool replyUsed = false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1)
            {
                // This is a captured file path — the surrounding text parts are caption candidates
                var filePath = parts[i].Trim();
                var caption = (i - 1 >= 0 ? parts[i - 1].Trim() : null) is { Length: > 0 } before ? before : null;
                if (caption is null && i + 1 < parts.Length && parts[i + 1].Trim() is { Length: > 0 } after)
                    caption = after;

                await SendPhotoAsync(chatId, filePath, caption, ct);
                // Photos consume the reply slot even though SendPhotoAsync doesn't pass replyParams.
                // Edge case: [reply_to: N][IMAGE:...] will silently drop the reply thread on the photo.
                // Acceptable for now — photo+reply threading is a rare combination.
                replyUsed = true;

                // Skip the adjacent text segment used as caption so it's not sent again
                if (caption is not null)
                {
                    if (i - 1 >= 0 && parts[i - 1].Trim() == caption) parts[i - 1] = "";
                    else if (i + 1 < parts.Length && parts[i + 1].Trim() == caption) { parts[i + 1] = ""; i++; }
                }
            }
            else
            {
                var segment = parts[i].Trim();
                if (segment.Length == 0) continue;

                // Prepend bold [ShortName] header when PrefixMessages is enabled
                if (_agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0)
                {
                    var displayName = $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}";
                    foreach (var chunk in SplitMessage(segment, 3990))
                    {
                        var escaped = System.Net.WebUtility.HtmlEncode(chunk);
                        var replyParams = !replyUsed && replyToMessageId.HasValue
                            ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                            : null;
                        var sentId = await SendMessageWithReplyFallbackAsync(chatId, $"<b>{displayName}:</b>\n{escaped}",
                            ParseMode.Html, replyParams, ct);
                        _lastSentMessageIds[chatId] = sentId;
                        replyUsed = true;
                    }
                }
                else
                {
                    foreach (var chunk in SplitMessage(segment, 4000))
                    {
                        var replyParams = !replyUsed && replyToMessageId.HasValue
                            ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                            : null;
                        var sentId = await SendMessageWithReplyFallbackAsync(chatId, chunk, null, replyParams, ct);
                        _lastSentMessageIds[chatId] = sentId;
                        replyUsed = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Sends a text message and returns the Telegram <c>message_id</c> of the sent message.
    /// Falls back to standalone (without reply threading) when the reply target is not found.
    /// </summary>
    private async Task<long> SendMessageWithReplyFallbackAsync(
        long chatId, string text, ParseMode? parseMode,
        Telegram.Bot.Types.ReplyParameters? replyParams,
        CancellationToken ct)
    {
        if (replyParams is null)
        {
            var m = parseMode.HasValue
                ? await _bot!.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                : await _bot!.SendMessage(chatId, text, cancellationToken: ct);
            return m.Id;
        }

        try
        {
            var m = parseMode.HasValue
                ? await _bot!.SendMessage(chatId, text, parseMode: parseMode.Value,
                    replyParameters: replyParams, cancellationToken: ct)
                : await _bot!.SendMessage(chatId, text, replyParameters: replyParams, cancellationToken: ct);
            return m.Id;
        }
        catch (Exception ex) when (ex.Message.Contains("message to be replied not found")
                                || ex.Message.Contains("reply message not found"))
        {
            _logger.LogWarning("Reply target not found for chat {ChatId} — sending as standalone", chatId);
            var m = parseMode.HasValue
                ? await _bot!.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                : await _bot!.SendMessage(chatId, text, cancellationToken: ct);
            return m.Id;
        }
    }

    /// <summary>
    /// Extracts and strips a <c>[reply_to: N]</c> token from agent output.
    /// The token is only used as a reply target if it appears at the start or end of the text.
    /// All occurrences are stripped regardless of position.
    /// </summary>
    internal static (string text, int? replyToMessageId) ExtractReplyToToken(string text, ILogger? logger = null)
    {
        var matches = ReplyToTokenRegex.Matches(text);
        if (matches.Count == 0) return (text, null);

        int? replyToMessageId = null;

        // Only use the token as a reply target when at the start or end of the output
        var first = matches[0];
        var atStart = first.Index == 0 || text[..first.Index].Trim().Length == 0;
        var atEnd = first.Index + first.Length == text.Length || text[(first.Index + first.Length)..].Trim().Length == 0;
        if (atStart || atEnd)
        {
            if (int.TryParse(first.Groups[1].Value, out var id) && id > 0)
                replyToMessageId = id;
            else
                logger?.LogDebug(
                    "reply_to token at start/end has non-positive id ({Id}) — stripped without reply target",
                    first.Groups[1].Value);
        }

        // Strip all [reply_to: N] tokens from the text
        text = ReplyToTokenRegex.Replace(text, "").Trim();
        return (text, replyToMessageId);
    }

    public async Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
    {
        if (chatId == 0) return;
        if (_bot is null) return;

        // Telegram doesn't support <br>, <br/>, or <br /> — replace all variants with newline.
        htmlText = Regex.Replace(htmlText, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        foreach (var chunk in SplitMessage(htmlText, 4000))
        {
            var balanced = BalanceBlockquotesInChunk(chunk);
            await _bot.SendMessage(chatId, balanced, parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    public async Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default)
    {
        if (chatId == 0) return;
        if (_bot is null) return;

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Photo file not found (likely from remote agent): {FilePath}", filePath);
            var agentHint = "[image from agent — view in their direct chat]";
            var message = caption is { Length: > 0 } ? $"{caption}\n{agentHint}" : agentHint;
            await _bot.SendMessage(chatId, message, cancellationToken: ct);
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            using var stream = new MemoryStream(bytes);
            var inputFile = InputFile.FromStream(stream, Path.GetFileName(filePath));
            await _bot.SendPhoto(chatId, inputFile, caption: caption, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send photo {FilePath}", filePath);
            await _bot.SendMessage(chatId, $"[photo: {filePath} — send failed]", cancellationToken: ct);
        }
    }

    public async Task SendTypingAsync(long chatId, CancellationToken ct = default)
    {
        if (chatId == 0) return;
        if (_bot is null) return;
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Headless mode: no bot token configured — Telegram poller is disabled.
        // RabbitMQ consumer and MCP server remain fully functional.
        if (_bot is null)
        {
            _logger.LogWarning(
                "TELEGRAM_BOT_TOKEN is not configured — Telegram poller disabled. " +
                "Agent will run in headless mode (RabbitMQ + MCP only). " +
                "Configure via the dashboard Setup panel.");
            _connectionState.TelegramConnected = false;
            await RunHeartbeatWaitAsync(stoppingToken);
            return;
        }

        _logger.LogInformation("Telegram bot starting for agent {AgentName} (SendOnly={SendOnly})",
            _agentConfig.Name, _telegramConfig.SendOnly);

        var me = await _bot.GetMe(stoppingToken);
        _botUsername = me.Username ?? "";
        _taskManager.SetBotUsername(_botUsername);
        _groupBehavior.SetBotUsername(_botUsername);
        _router.SetBotUsername(_botUsername);
        _logger.LogInformation("Bot username resolved: @{BotUsername}", _botUsername);

        _connectionState.TelegramConnected = true;

        // Propagate shutdown token for debounce cancellation
        _groupBehavior.SetShutdownToken(stoppingToken);
        _router.SetShutdownToken(stoppingToken);

        // Initialize relay if configured
        await _relay.InitializeAsync(stoppingToken);
        if (_relay.IsEnabled)
            _relay.MessageReceived += _groupBehavior.OnRelayMessage;

        // Wire up task completion handler for group buffering and relay
        _taskManager.OnTaskCompleted += OnTaskCompleted;
        _taskManager.OnToolUse += OnToolUse;

        if (_telegramConfig.SendOnly)
        {
            // Send-only mode: no polling, no message handling, no bot commands.
            // All other wiring (relay, task handlers, bot username) is active.
            _logger.LogInformation("Telegram bot in send-only mode — skipping polling and message handlers");
        }
        else
        {
            var commands = new List<BotCommand>
            {
                new() { Command = "new",    Description = "Start a parallel task: /new <task>" },
                new() { Command = "cancel", Description = "Cancel a task: /cancel [id|all]" },
                new() { Command = "status", Description = "Show running tasks and agent info" },
                new() { Command = "reset",  Description = "Clear session and start fresh" },
                new() { Command = "run",    Description = "Send command to executor: /run <command>" },
                new() { Command = "tts",    Description = "Synthesize speech: reply to any message with /tts" },
            };

            await _bot.SetMyCommands(commands, cancellationToken: stoppingToken);

            _bot.StartReceiving(
                updateHandler: (_, update, _) => HandleTelegramUpdateAsync(update),
                errorHandler: (_, ex, source, _) => OnError(ex, source),
                receiverOptions: new ReceiverOptions
                {
                    AllowedUpdates =
                    [
                        UpdateType.Message,
                        UpdateType.MessageReaction,
                    ]
                },
                cancellationToken: stoppingToken);

            _logger.LogInformation("Telegram bot is receiving messages (GroupListenMode={Mode})",
                _agentConfig.GroupListenMode);
        }

        Task? proactiveLoop = _agentConfig.ProactiveIntervalMinutes > 0
            ? _groupBehavior.RunProactiveLoopAsync(stoppingToken)
            : null;

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            _taskManager.OnTaskCompleted -= OnTaskCompleted;
            _taskManager.OnToolUse -= OnToolUse;
            if (_relay.IsEnabled)
                _relay.MessageReceived -= _groupBehavior.OnRelayMessage;
            _groupBehavior.CancelAllDebounce();
            if (proactiveLoop is not null)
                await proactiveLoop.ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// In headless mode (no Telegram token) we still need to keep the hosted service alive
    /// so that other background services (OrchestratorHeartbeatService, CliRunner) can run.
    /// </summary>
    private static async Task RunHeartbeatWaitAsync(CancellationToken ct)
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
    }

    private void OnToolUse(long chatId, string toolName, string description) =>
        _groupBehavior.BufferToolUse(chatId, toolName, description);

    private void OnTaskCompleted(long chatId, string result, string? relaySender, TaskSource source, bool isPartial, string? correlationId, string? taskId)
    {
        var lastSentId = _lastSentMessageIds.TryGetValue(chatId, out var id) ? id : 0L;
        _groupBehavior.BufferBotResponse(chatId, result, telegramMessageId: lastSentId);

        _ = Task.Run(async () =>
        {
            if (relaySender == "bridge" && correlationId is not null)
            {
                await _relay.PublishToAgentAsync("bridge", chatId, result,
                    type: RelayMessageType.BridgeResponse, correlationId: correlationId, taskId: taskId);
            }
            else if (relaySender is not null)
            {
                var type = isPartial ? RelayMessageType.PartialResponse : RelayMessageType.Response;
                var text = taskId is not null ? FormatTaskResponse(result, isPartial) : result;
                await _relay.PublishToAgentAsync(relaySender, chatId, text, type: type, taskId: taskId);
            }
        });
    }

    private static string FormatTaskResponse(string result, bool isPartial)
    {
        // Detect voluntary failure marker: [TASK_FAILED: reason]
        // Agents can emit this to signal that they refused or cannot complete a delegated task.
        var taskFailedMatch = TaskFailedMarkerRegex.Match(result);
        if (taskFailedMatch.Success)
        {
            var reason = taskFailedMatch.Groups[1].Value.Trim();
            var body = result[taskFailedMatch.Length..].TrimStart('\n', '\r');
            var text = string.IsNullOrEmpty(body) ? $"Task failed: {reason}" : $"Task failed: {reason}\n{body}";
            return $"[status: failed]\n{text}";
        }

        // Determine status from result content and isPartial flag
        var status = isPartial
            ? (result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
               || result.StartsWith("Task failed:", StringComparison.OrdinalIgnoreCase)
               ? "failed"
               : "incomplete")
            : "completed";

        return $"[status: {status}]\n{result}";
    }

    private async Task OnMessage(Message message, UpdateType type)
    {
        if (type != UpdateType.Message) return;

        // Support text messages and media messages (with optional caption)
        var isPhoto = message.Photo is { Length: > 0 };
        var isVoice = message.Voice is not null;
        var isAudio = message.Audio is not null;
        var isVideo = message.Video is not null;
        var isVideoNote = message.VideoNote is not null;
        var isDocument = message.Document is not null;
        var isMediaAttachment = isPhoto || isVoice || isAudio || isVideo || isVideoNote || isDocument;
        var text = message.Text ?? message.Caption ?? "";

        // Transcribe voice messages if the whisper service is configured
        if (isVoice && _voiceTranscription.IsEnabled)
        {
            // Immediate feedback — let user know we received the voice message
            await _bot!.SendChatAction(message.Chat.Id, ChatAction.Typing);

            try
            {
                using var ms = new System.IO.MemoryStream();
                await _bot!.GetInfoAndDownloadFile(message.Voice!.FileId, ms);
                var audioBytes = ms.ToArray();
                var transcribed = await _voiceTranscription.TranscribeAsync(audioBytes, "voice.ogg");
                if (transcribed is not null)
                {
                    text = transcribed;
                    _logger.LogInformation("Voice message transcribed ({Chars} chars) from {Sender}",
                        text.Length, message.From?.Username ?? "unknown");

                    // Echo transcription back so user can verify whisper got it right
                    await _bot!.SendMessage(
                        message.Chat.Id,
                        $"🎤 {transcribed}",
                        replyParameters: new Telegram.Bot.Types.ReplyParameters { MessageId = message.MessageId });
                }
                else
                {
                    _logger.LogWarning("Voice transcription returned null for message from {Sender}", message.From?.Username);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process voice message from {Sender}", message.From?.Username);
                return;
            }
        }
        else if (!isMediaAttachment && string.IsNullOrEmpty(message.Text)) return;

        // For non-photo media with no caption, synthesize a readable placeholder so the
        // agent receives a meaningful description of what was shared.
        if (string.IsNullOrEmpty(text) && isMediaAttachment)
        {
            if (isAudio) text = "(audio message)";
            else if (isVideoNote) text = "(video note)";
            else if (isVideo) text = "(video message)";
            else if (isDocument) text = message.Document!.FileName is { } fn ? $"(document: {fn})" : "(document)";
            else if (isVoice) text = "(voice message)";
        }

        // TTS trigger: /tts command as a reply to any message → synthesize and send replied-to message as voice
        if (_tts.IsEnabled && !isVoice
            && text.Equals("/tts", StringComparison.OrdinalIgnoreCase)
            && message.ReplyToMessage is { } replied)
        {
            var sourceText = replied.Text ?? replied.Caption;
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _bot!.SendChatAction(message.Chat.Id, ChatAction.RecordVoice);
                        var audioBytes = await _tts.SynthesizeAsync(sourceText);
                        if (audioBytes is { Length: > 0 })
                        {
                            using var ms = new System.IO.MemoryStream(audioBytes);
                            await _bot!.SendVoice(
                                message.Chat.Id,
                                new Telegram.Bot.Types.InputFileStream(ms, "response.ogg"),
                                replyParameters: new Telegram.Bot.Types.ReplyParameters { MessageId = replied.MessageId });
                            _logger.LogInformation("TTS voice sent for message {MsgId} ({Chars} chars)", replied.MessageId, sourceText.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TTS voice send failed for message {MsgId}", replied.MessageId);
                    }
                });
            }
            return;
        }

        var chatId = message.Chat.Id;
        var isGroupChat = message.Chat.Type is ChatType.Group or ChatType.Supergroup;

        var isMentioned = _botUsername.Length > 0
            && text.Contains($"@{_botUsername}", StringComparison.OrdinalIgnoreCase);
        var isReplyToMe = message.ReplyToMessage?.From?.Username is { } replyUser
            && replyUser.Equals(_botUsername, StringComparison.OrdinalIgnoreCase);
        var isNameMentioned = _agentConfig.ShortName.Length > 0
            && text.Contains(_agentConfig.ShortName, StringComparison.OrdinalIgnoreCase);

        var stripped = _botUsername.Length > 0
            ? text.Replace($"@{_botUsername}", "", StringComparison.OrdinalIgnoreCase).Trim()
            : text.Trim();

        var sender = message.From?.Username is { } u ? $"@{u}" : message.From?.FirstName ?? "Unknown";
        var replyToUsername = message.ReplyToMessage?.From?.Username is { } ru ? $"@{ru}" : null;
        var replyToText = message.ReplyToMessage?.Text ?? message.ReplyToMessage?.Caption;

        // Download photo if present (largest available size, with size check and one retry)
        MessageImage? downloadedImage = null;
        if (isPhoto)
        {
            var largest = message.Photo!.OrderByDescending(p => p.FileSize ?? 0).First();
            downloadedImage = await DownloadPhotoAsync(largest, chatId, message.MessageId, photoIndex: 1);
        }

        // Download PDF document if present and persistence is enabled
        MessageDocument? downloadedDocument = null;
        if (isDocument && message.Document!.MimeType == "application/pdf")
        {
            downloadedDocument = await DownloadDocumentAsync(message.Document, chatId, message.MessageId, docIndex: 1);
        }

        // Build the base IncomingMessage (no images/documents yet — filled in below)
        var baseMsg = new IncomingMessage
        {
            ChatId = chatId,
            UserId = message.From?.Id ?? 0,
            Text = text,
            Sender = sender,
            IsGroupChat = isGroupChat,
            TelegramMessageId = message.MessageId,
            ReplyToTelegramMessageId = message.ReplyToMessage?.MessageId is { } rtm ? (long)rtm : null,
            ReplyToUsername = replyToUsername,
            ReplyToText = replyToText,
            IsBotMentioned = isMentioned,
            IsReplyToBot = isReplyToMe,
            IsNameMentioned = isNameMentioned,
            StrippedText = stripped,
            HasMediaAttachment = isMediaAttachment,
        };

        // Media group: buffer all photos and flush as one IncomingMessage after debounce
        if (message.MediaGroupId is { } mediaGroupId && isPhoto)
        {
            var groupKey = $"{chatId}:{mediaGroupId}";

            Func<IncomingMessage, Task> flushHandler = async flushedMsg =>
            {
                _groupSizeCapped.TryRemove(groupKey, out _);
                var groupHints = AttachmentSweeper.BuildHints(flushedMsg.Images, flushedMsg.Documents);
                if (groupHints.Length > 0)
                {
                    var newText = flushedMsg.Text.Length > 0 ? $"{flushedMsg.Text}\n{groupHints}" : groupHints;
                    var newStripped = flushedMsg.StrippedText.Length > 0 ? $"{flushedMsg.StrippedText}\n{groupHints}" : groupHints;
                    flushedMsg = flushedMsg with { Text = newText, StrippedText = newStripped };
                }
                await _router.HandleAsync(flushedMsg);
            };

            // TryAddPhotoWithCapAsync atomically checks and adds under the same lock.
            var accepted = await _mediaGroupBuffer.TryAddPhotoWithCapAsync(
                groupKey, downloadedImage, baseMsg, _telegramConfig.MaxImagesPerGroup, flushHandler);

            if (!accepted)
            {
                // Warn once when cap is first exceeded, then keep resetting the debounce
                if (_groupSizeCapped.TryAdd(groupKey, true))
                    await SendTextAsync(chatId, $"({_telegramConfig.MaxImagesPerGroup} images received — only the first {_telegramConfig.MaxImagesPerGroup} will be processed.)");

                await _mediaGroupBuffer.AddPhotoAsync(groupKey, null, baseMsg, flushHandler);
            }
            return;
        }

        // Single photo, document, or text-only: process immediately
        var images = downloadedImage is not null
            ? (IReadOnlyList<MessageImage>)[downloadedImage]
            : [];
        var documents = downloadedDocument is not null
            ? (IReadOnlyList<MessageDocument>)[downloadedDocument]
            : [];
        var hints = AttachmentSweeper.BuildHints(images, documents);
        var msg = hints.Length > 0
            ? baseMsg with
            {
                Images = images,
                Documents = documents,
                Text = baseMsg.Text.Length > 0 ? $"{baseMsg.Text}\n{hints}" : hints,
                StrippedText = baseMsg.StrippedText.Length > 0 ? $"{baseMsg.StrippedText}\n{hints}" : hints,
            }
            : baseMsg with { Images = images, Documents = documents };
        await _router.HandleAsync(msg);
    }

    /// <summary>
    /// Download a Telegram photo to memory (and optionally persist to disk).
    /// Checks size limit, retries once on transient failure.
    /// Returns null and warns the user if the image is oversized or both download attempts fail.
    /// When <c>Telegram:PersistAttachments</c> is enabled the bytes are also written to
    /// <c>{AttachmentDir}/{chatId}-{messageId}-{photoIndex}.jpg</c> and the path is stored
    /// on <see cref="MessageImage.FilePath"/> so agent tools can reach the bytes later.
    /// </summary>
    private async Task<MessageImage?> DownloadPhotoAsync(Telegram.Bot.Types.PhotoSize photo, long chatId, long messageId, int photoIndex)
    {
        var sizeBytes = (long)(photo.FileSize ?? 0);
        if (sizeBytes > 0 && sizeBytes > _telegramConfig.MaxImageBytes)
        {
            _logger.LogWarning("Photo #{Index} ({FileId}) exceeds MaxImageBytes ({Size} > {Limit}), skipping",
                photoIndex, photo.FileId, sizeBytes, _telegramConfig.MaxImageBytes);
            await SendTextAsync(chatId, $"(Image #{photoIndex} exceeded size limit, skipped.)");
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(500);
                using var ms = new System.IO.MemoryStream();
                await _bot!.GetInfoAndDownloadFile(photo.FileId, ms);
                var bytes = ms.ToArray();

                string? filePath = null;
                if (_telegramConfig.PersistAttachments)
                {
                    try
                    {
                        Directory.CreateDirectory(_telegramConfig.AttachmentDir);
                        filePath = Path.Combine(_telegramConfig.AttachmentDir, $"{chatId}-{messageId}-{photoIndex}.jpg");
                        await File.WriteAllBytesAsync(filePath, bytes);
                    }
                    catch (Exception ex)
                    {
                        // Disk IO failure must not break vision: the agent still sees the image via
                        // multimodal content blocks; we just lose the filesystem reference.
                        _logger.LogWarning(ex, "Photo #{Index}: failed to persist attachment to disk, continuing without file path", photoIndex);
                        filePath = null;
                    }

                    // Opportunistic cleanup — called after the write try/catch so a sweep failure
                    // cannot misfire the catch and nullify a successfully written filePath.
                    if (filePath != null)
                        AttachmentSweeper.SweepExpired(_telegramConfig.AttachmentDir, _telegramConfig.AttachmentRetentionHours, _logger);
                }

                return new MessageImage(bytes, "image/jpeg") { FilePath = filePath };
            }
            catch (Exception ex) when (attempt == 0)
            {
                _logger.LogWarning(ex, "Photo #{Index} ({FileId}) download failed on attempt 1, retrying in 500ms", photoIndex, photo.FileId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Photo #{Index} ({FileId}) download failed after retry, skipping", photoIndex, photo.FileId);
                await SendTextAsync(chatId, $"(Image #{photoIndex} download failed, skipped.)");
            }
        }
        return null;
    }



    /// <summary>
    /// Download a Telegram document to memory (and optionally persist to disk).
    /// Only called for PDFs (mime_type == "application/pdf").
    /// Rejects files exceeding <c>Telegram:MaxDocumentBytes</c> (default: 32 MB) with a
    /// user-facing warning — no disk write, no LLM block.
    /// When <c>Telegram:PersistAttachments</c> is enabled the bytes are written to
    /// <c>{AttachmentDir}/{chatId}-{messageId}-{docIndex}.pdf</c> and the path is stored
    /// on <see cref="MessageDocument.FilePath"/> so agent tools can reach the bytes later.
    /// Returns null when persistence is disabled (kill switch) or the file is oversized.
    /// </summary>
    private async Task<MessageDocument?> DownloadDocumentAsync(
        Telegram.Bot.Types.Document document,
        long chatId,
        long messageId,
        int docIndex)
    {
        // Kill switch: when PersistAttachments is false, no download, no disk write, no LLM block.
        if (!_telegramConfig.PersistAttachments)
            return null;

        var fileSize = document.FileSize ?? 0;
        if (fileSize > _telegramConfig.MaxDocumentBytes)
        {
            _logger.LogWarning("Document ({FileId}) pre-download size exceeds MaxDocumentBytes ({Size} > {Limit}), rejecting",
                document.FileId, fileSize, _telegramConfig.MaxDocumentBytes);
            await SendTextAsync(chatId, PdfTooLargeMessage(fileSize));
            return null;
        }

        try
        {
            using var ms = new System.IO.MemoryStream();
            await _bot!.GetInfoAndDownloadFile(document.FileId, ms);
            var bytes = ms.ToArray();

            // Stage 2: actual download may exceed the pre-download estimate (Telegram's FileSize
            // is advisory). Guard again with the true byte count before touching disk.
            if (bytes.Length > _telegramConfig.MaxDocumentBytes)
            {
                _logger.LogWarning("Document ({FileId}) actual size exceeds MaxDocumentBytes ({Size} > {Limit}) after download, rejecting",
                    document.FileId, bytes.Length, _telegramConfig.MaxDocumentBytes);
                await SendTextAsync(chatId, PdfTooLargeMessage(bytes.Length));
                return null;
            }

            string? filePath = null;
            try
            {
                Directory.CreateDirectory(_telegramConfig.AttachmentDir);
                filePath = Path.Combine(_telegramConfig.AttachmentDir, $"{chatId}-{messageId}-{docIndex}.pdf");
                await File.WriteAllBytesAsync(filePath, bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Document #{Index}: failed to persist to disk, continuing without file path", docIndex);
                filePath = null;
            }

            if (filePath != null)
                AttachmentSweeper.SweepExpired(_telegramConfig.AttachmentDir, _telegramConfig.AttachmentRetentionHours, _logger);

            return new MessageDocument(
                document.FileId,
                document.MimeType ?? "application/pdf",
                fileSize,
                document.FileName)
            {
                FilePath = filePath,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Document ({FileId}) download failed, skipping", document.FileId);
            await SendTextAsync(chatId, "(PDF download failed — please try again.)");
            return null;
        }
    }

    private string PdfTooLargeMessage(long sizeBytes) =>
        $"(PDF too large — {sizeBytes / 1_048_576} MB exceeds the {_telegramConfig.MaxDocumentBytes / 1_048_576} MB limit. Please send a smaller file.)";

    /// <summary>
    /// Dispatch wrapper used by <see cref="TelegramBotClientExtensions.StartReceiving"/> to route
    /// each incoming <see cref="Update"/> to the appropriate handler.
    /// </summary>
    private Task HandleTelegramUpdateAsync(Update update)
    {
        if (update.Type == UpdateType.Message && update.Message is { } msg)
            return OnMessage(msg, UpdateType.Message);
        return OnUpdate(update);
    }

    /// <summary>
    /// Handles non-message update types. Currently processes:
    /// - <c>MessageReaction</c>: emits a synthetic message per changed emoji into the task queue.
    /// - <c>MessageReactionCount</c>: logged and skipped (aggregate counts, out of scope).
    /// All other update types are ignored (messages are handled by <see cref="OnMessage"/>).
    /// </summary>
    private Task OnUpdate(Update update)
    {
        if (update.Type == UpdateType.MessageReactionCount)
        {
            _logger.LogWarning("Received MessageReactionCount update — skipping (not supported)");
            return Task.CompletedTask;
        }

        if (update.Type == UpdateType.MessageReaction && update.MessageReaction is { } reaction)
            HandleReaction(reaction);

        return Task.CompletedTask;
    }

    private void HandleReaction(MessageReactionUpdated reaction)
    {
        // Anonymous group admins have no User context (Telegram uses actor_chat instead).
        // We can't attribute the reaction to a specific user, so discard it.
        if (reaction.User is null)
        {
            _logger.LogWarning(
                "Ignoring reaction on message {MsgId} in chat {ChatId} — anonymous admin (no user context)",
                reaction.MessageId, reaction.Chat.Id);
            return;
        }

        var chatId = reaction.Chat.Id;
        var userId = reaction.User.Id;
        var messageId = reaction.MessageId;

        // Authorization: same rules as regular messages
        var isGroupChat = reaction.Chat.Type is ChatType.Group or ChatType.Supergroup;
        if (isGroupChat)
        {
            if (!_telegramConfig.AllowedGroupIds.Contains(chatId))
            {
                _logger.LogDebug("Ignoring reaction from unauthorized group {ChatId}", chatId);
                return;
            }
        }
        else
        {
            if (userId == 0 || !_telegramConfig.AllowedUserIds.Contains(userId))
            {
                _logger.LogDebug("Ignoring reaction from unauthorized user {UserId}", userId);
                return;
            }
        }

        var (added, removed) = DiffReactions(reaction.NewReaction, reaction.OldReaction);
        if (added.Count == 0 && removed.Count == 0) return; // no net change

        var buffer = _groupBehavior.GetGroupBuffer(chatId);
        var hasOriginal = buffer.TryGetByMessageId(messageId, out _, out var origText);
        var contentSuffix = hasOriginal ? $": \"{TruncateForReaction(origText)}\"" : "";

        foreach (var emoji in added)
        {
            var text = $"[reaction: {emoji} on message_id={messageId} from user_id={userId}{contentSuffix}]";
            _taskManager.StartTask(chatId, text, text, isSessionTask: true, userId: userId);
        }

        foreach (var emoji in removed)
        {
            var text = $"[reaction removed: {emoji} on message_id={messageId} from user_id={userId}{contentSuffix}]";
            _taskManager.StartTask(chatId, text, text, isSessionTask: true, userId: userId);
        }
    }

    /// <summary>
    /// Normalizes a buffered message text for inline use in a reaction task:
    /// replaces line breaks and tabs with a single space, then caps at 200 chars with a trailing ellipsis.
    /// </summary>
    internal static string TruncateForReaction(string text)
    {
        // Normalize whitespace: CRLF, LF, CR, tab → single space
        var flat = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    /// <summary>
    /// Diffs two reaction lists, returning only standard emoji (<see cref="ReactionTypeEmoji"/>)
    /// that were added or removed. Custom emoji and paid reactions are silently ignored.
    /// </summary>
    internal static (HashSet<string> added, HashSet<string> removed) DiffReactions(
        IEnumerable<ReactionType>? newReaction,
        IEnumerable<ReactionType>? oldReaction)
    {
        var newEmojis = (newReaction ?? []).OfType<ReactionTypeEmoji>().Select(r => r.Emoji).ToHashSet();
        var oldEmojis = (oldReaction ?? []).OfType<ReactionTypeEmoji>().Select(r => r.Emoji).ToHashSet();
        return ([.. newEmojis.Except(oldEmojis)], [.. oldEmojis.Except(newEmojis)]);
    }

    private Task OnError(Exception exception, HandleErrorSource source)
    {
        _logger.LogError(exception, "Telegram bot error from {Source}", source);
        return Task.CompletedTask;
    }

    // Ensures a single chunk has balanced <blockquote> open/close tags.
    // Appends missing closers if opens > closes; strips dangling closers from the end if closes > opens.
    internal static string BalanceBlockquotesInChunk(string chunk)
    {
        var opens = Regex.Matches(chunk, @"<blockquote(\s[^>]*)?>", RegexOptions.IgnoreCase).Count;
        var closes = Regex.Matches(chunk, @"</blockquote>", RegexOptions.IgnoreCase).Count;
        if (opens > closes)
            return chunk + string.Concat(Enumerable.Repeat("</blockquote>", opens - closes));
        if (closes > opens)
        {
            var result = chunk;
            for (var i = 0; i < closes - opens; i++)
            {
                var lastClose = result.LastIndexOf("</blockquote>", StringComparison.OrdinalIgnoreCase);
                if (lastClose >= 0) result = result.Remove(lastClose, "</blockquote>".Length);
            }
            return result;
        }
        return chunk;
    }

    private static IEnumerable<string> SplitMessage(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        for (var i = 0; i < text.Length; i += maxLength)
        {
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
        }
    }
}
