using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

public sealed class MessageRouter
{
    private readonly AgentOptions _agentConfig;
    private readonly TelegramOptions _telegramConfig;
    private readonly AllowlistHolder _allowlist;
    private readonly TaskManager _taskManager;
    private readonly GroupBehavior _groupBehavior;
    private readonly GroupRelayService _relay;
    private readonly CommandDispatcher _commands;
    private readonly CtoAgentNameService _ctoAgentNameService;
    private readonly ILogger<MessageRouter> _logger;

    /// <summary>Set by AgentTransport after construction to break circular DI.</summary>
    public IMessageSink Sink { get; set; } = null!;

    public MessageRouter(
        IOptions<AgentOptions> agentConfig,
        IOptions<TelegramOptions> telegramConfig,
        AllowlistHolder allowlist,
        TaskManager taskManager,
        GroupBehavior groupBehavior,
        GroupRelayService relay,
        CommandDispatcher commands,
        CtoAgentNameService ctoAgentNameService,
        ILogger<MessageRouter> logger)
    {
        _agentConfig = agentConfig.Value;
        _telegramConfig = telegramConfig.Value;
        _allowlist = allowlist;
        _taskManager = taskManager;
        _groupBehavior = groupBehavior;
        _relay = relay;
        _commands = commands;
        _ctoAgentNameService = ctoAgentNameService;
        _logger = logger;
    }

    public async Task HandleAsync(IncomingMessage msg)
    {
        // --- Global kill switch --- checked before any auth or routing ---
        if (msg.IsGroupChat && _allowlist.IsGroupAllowed(msg.ChatId)
            && _allowlist.IsUserAllowed(msg.UserId)
            && msg.StrippedText.Equals("/stop", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("/stop detected from {Sender} in group {ChatId} — executing emergency halt", msg.Sender, msg.ChatId);
            await _commands.TryHandleAsync(msg.ChatId, "/stop");
            return;
        }

        // --- Authorization ---
        if (msg.IsGroupChat)
        {
            if (!_allowlist.IsGroupAllowed(msg.ChatId))
            {
                _logger.LogWarning("Message from unauthorized group {ChatId}", msg.ChatId);
                return;
            }

            // Buffer ALL allowed group messages for context
            _groupBehavior.AddAndPersist(msg.ChatId, msg.Sender, msg.Text, msg.ReplyToUsername,
                telegramMessageId: msg.TelegramMessageId,
                replyToTelegramMessageId: msg.ReplyToTelegramMessageId);

            if (_agentConfig.GroupListenMode.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var isDirectTrigger = msg.IsBotMentioned || msg.IsReplyToBot || msg.IsNameMentioned;
                if (isDirectTrigger)
                {
                    _groupBehavior.CancelDebounce(msg.ChatId);
                }
                else
                {
                    if (msg.Images.Count > 0)
                        _groupBehavior.AddPendingImages(msg.ChatId, msg.Images, _telegramConfig.MaxImagesPerGroup);
                    _groupBehavior.ScheduleDebounce(msg.ChatId);
                    return;
                }
            }
            else
            {
                // "mention" mode — only @mention or reply-to-me; media bypasses the mention check
                if (!_allowlist.IsUserAllowed(msg.UserId))
                    return;
                if (!(msg.IsBotMentioned || msg.IsReplyToBot || msg.HasMediaAttachment))
                    return;
            }
        }
        else
        {
            if (!_allowlist.IsUserAllowed(msg.UserId))
            {
                _logger.LogWarning("Unauthorized DM from user {UserId}", msg.UserId);
                await HandleUnauthorizedDmAsync(msg);
                return;
            }
        }

        var trimmed = msg.StrippedText;

        // Bare @mention with no content — ignore (unless it carries an image)
        if (string.IsNullOrEmpty(trimmed) && !msg.HasImage)
            return;

        // Image-only message — inject the default prompt so the LLM knows what to do
        if (string.IsNullOrEmpty(trimmed) && msg.HasImage)
            trimmed = _telegramConfig.DefaultImagePrompt;

        // --- Command dispatch (delegates to shared CommandDispatcher) ---

        // /cancel with empty arg from Telegram means "smart cancel" (differs from relay default of "all")
        if (trimmed.Equals("/cancel", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("/cancel ", StringComparison.OrdinalIgnoreCase))
        {
            var arg = trimmed.Length > 7 ? trimmed[8..].Trim() : "";
            await _taskManager.HandleCancel(msg.ChatId, arg, msg.UserId);
            return;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            // /new needs special handling (not a pure command — it starts a task)
            if (trimmed.StartsWith("/new ", StringComparison.OrdinalIgnoreCase))
            {
                var task = trimmed[5..].Trim();
                if (string.IsNullOrEmpty(task))
                {
                    await Sink.SendTextAsync(msg.ChatId, "Usage: /new <task description>");
                    return;
                }
                var displayText = task;
                if (msg.IsGroupChat)
                    task = _groupBehavior.BuildGroupTask(msg.ChatId, msg.Sender, task, msg.ReplyToUsername, msg.ReplyToText, msg.TelegramMessageId, msg.ChatTitle);
                _taskManager.StartTask(msg.ChatId, task, displayText, isSessionTask: false,
                    source: TaskSource.NewCommand, images: msg.Images.Count > 0 ? msg.Images : null,
                    documents: msg.Documents.Count > 0 ? msg.Documents : null);
                return;
            }

            if (trimmed.Equals("/new", StringComparison.OrdinalIgnoreCase))
            {
                await Sink.SendTextAsync(msg.ChatId, "Usage: /new <task description>");
                return;
            }

            // All other commands go through the shared dispatcher
            if (await _commands.TryHandleAsync(msg.ChatId, trimmed))
                return;
        }

        // --- Regular message ---

        // Buffer DM messages for context recovery
        if (!msg.IsGroupChat)
        {
            _groupBehavior.AddAndPersist(msg.ChatId, msg.Sender, trimmed, null,
                telegramMessageId: msg.TelegramMessageId,
                replyToTelegramMessageId: msg.ReplyToTelegramMessageId);
        }

        // Build display text that reflects image count for heartbeat/status visibility
        string messageDisplayText;
        if (msg.Images.Count > 1)
        {
            var imageDisplay = string.IsNullOrEmpty(msg.StrippedText)
                ? $"[{msg.Images.Count} images]"
                : $"[{msg.Images.Count} images + caption: {trimmed}]";
            messageDisplayText = msg.IsGroupChat ? $"[From: {msg.Sender}] {imageDisplay}" : imageDisplay;
        }
        else
        {
            messageDisplayText = msg.IsGroupChat ? $"[From: {msg.Sender}] {trimmed}" : trimmed;
        }

        if (msg.IsGroupChat)
            trimmed = _groupBehavior.BuildGroupTask(msg.ChatId, msg.Sender, trimmed, msg.ReplyToUsername, msg.ReplyToText, msg.TelegramMessageId, msg.ChatTitle);
        else
            trimmed = _groupBehavior.BuildDmTask(msg.ChatId, trimmed, msg.ReplyToText, msg.TelegramMessageId, msg.ChatUsername, msg.ChatFirstName);

        // When busy, StartTask enqueues the message and notifies the user automatically.
        // Use /new <task> for parallel tasks, or /cancel to stop the current one.
        _taskManager.StartTask(msg.ChatId, trimmed, messageDisplayText, isSessionTask: true,
            images: msg.Images.Count > 0 ? msg.Images : null,
            documents: msg.Documents.Count > 0 ? msg.Documents : null);
    }

    private CancellationToken _shutdownToken = CancellationToken.None;
    private string _botUsername = "";

    public void SetShutdownToken(CancellationToken ct) => _shutdownToken = ct;

    public void SetBotUsername(string username) => _botUsername = username;

    private async Task HandleUnauthorizedDmAsync(IncomingMessage msg)
    {
        if (!_telegramConfig.CanReceiveChatRequests)
            return; // silent drop (default)

        var targetAgent = _ctoAgentNameService.GetCtoAgentName();
        if (string.IsNullOrWhiteSpace(targetAgent))
        {
            _logger.LogError(
                "CanReceiveChatRequests=true but FLEET_CTO_AGENT is not configured — " +
                "access request from user {UserId} dropped", msg.UserId);
            return;
        }

        var payload = new AccessRequestPayload
        {
            RequestId    = Guid.NewGuid().ToString("N"),
            TargetAgent  = _agentConfig.ShortName,
            UserId       = msg.UserId,
            Username     = msg.ChatUsername,
            FirstName    = msg.ChatFirstName,
            MessageText  = msg.StrippedText,
        };

        var directive = $"An access request has arrived for bot '{payload.TargetAgent}'.\n\n" +
                        $"User: {(payload.Username is not null ? "@" + payload.Username : payload.FirstName ?? "unknown")} " +
                        $"(id={payload.UserId})\n" +
                        $"Message: {payload.MessageText}\n" +
                        $"Request ID: {payload.RequestId}\n\n" +
                        $"Review the request and, if approved, call manage_agent_telegram_users " +
                        $"with action=add to grant access.";

        await _relay.PublishToAgentAsync(targetAgent, chatId: 0, directive,
            type: RelayMessageType.AccessRequest);

        _logger.LogInformation(
            "Access request from user {UserId} forwarded to {TargetAgent} (request_id={RequestId})",
            msg.UserId, targetAgent, payload.RequestId);

        // Optionally reply to the requesting user
        if (Sink is not null)
        {
            var reply = string.IsNullOrWhiteSpace(_telegramConfig.RequestReceivedMessage)
                ? "Your request has been received and is awaiting approval."
                : _telegramConfig.RequestReceivedMessage;
            try { await Sink.SendTextAsync(msg.ChatId, reply); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send access-request acknowledgement to user {UserId}", msg.UserId);
            }
        }
    }
}
