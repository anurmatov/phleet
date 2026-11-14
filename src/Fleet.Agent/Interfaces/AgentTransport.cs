using System.Text.RegularExpressions;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Shared;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Agent.Services.TelegramShell;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Interfaces;

/// <summary>
/// The Telegram transport: polling, and the outbound render path.
/// Implements IMessageSink for outbound messages (splits at 4000 chars). Inbound updates go to
/// the parts in <c>Services/TelegramShell</c>: <see cref="TelegramMessageIntake"/> builds an
/// IncomingMessage for MessageRouter, <see cref="TelegramReactionHandler"/> turns a reaction into
/// a task, and <see cref="TelegramJournalRecorder"/> builds the journal records for both directions.
/// When TELEGRAM_BOT_TOKEN is missing or empty the service enters headless mode:
/// RabbitMQ + MCP remain fully functional; Telegram poller is disabled.
/// </summary>
public sealed class AgentTransport : BackgroundService, IMessageSink, ITelegramMediaSender, ITelegramShell
{
    private ITelegramBotClient? _bot;

    /// <summary>Allows tests to inject a fake bot without a real token.</summary>
    internal ITelegramBotClient? BotForTesting { set => _bot = value; }
    private readonly AgentOptions _agentConfig;
    private readonly TelegramOptions _telegramConfig;
    private readonly GroupRelayService _relay;
    private readonly TaskManager _taskManager;
    private readonly GroupBehavior _groupBehavior;
    private readonly MessageRouter _router;
    private readonly CommandDispatcher _commands;
    private readonly IFleetConnectionState _connectionState;
    private readonly ILogger<AgentTransport> _logger;
    private readonly RichFallbackCounter _richFallbackCounter;

    // The conversation journal (#377). Null unless Journal__IngestToken is set, and every capture
    // site is a no-op when it is null — an agent without a token sends exactly what it sent before.
    private readonly JournalCapture? _journal;

    /// <summary>True when the journal was injected (a token is set). For the registration tests.</summary>
    internal bool JournalEnabled => _journal is not null;

    /// <summary>This agent's own bot id, or null without a bot. The tool-send receipt check (#394).</summary>
    internal long? BotId => _bot?.BotId;
    long? ITelegramMediaSender.BotId => BotId;

    // DocumentDownloadHelper wraps IDocumentDownloader so the download+persist path
    // can be unit-tested by injecting a fake downloader. Exposed as internal so tests
    // that directly construct AgentTransport can substitute the helper.
    internal DocumentDownloadHelper DownloadHelper { get; set; } = null!;

    /// <summary>
    /// Allows tests to observe the <see cref="IncomingMessage"/> that OnMessage builds
    /// without standing up the whole routing stack. Null in production, where every
    /// message goes to <see cref="MessageRouter.HandleAsync"/>.
    /// </summary>
    internal Func<IncomingMessage, Task>? RouterHookForTesting { get; set; }

    private Task RouteAsync(IncomingMessage msg)
        => (RouterHookForTesting ?? _router.HandleAsync)(msg);

    ITelegramBotClient? ITelegramShell.Bot => _bot;
    string ITelegramShell.BotUsername => _botUsername;
    DocumentDownloadHelper ITelegramShell.DownloadHelper => DownloadHelper;
    Task ITelegramShell.RouteAsync(IncomingMessage msg) => RouteAsync(msg);
    Task ITelegramShell.SendTextAsync(long chatId, string text) => SendTextAsync(chatId, text);

    private readonly TelegramJournalRecorder _journalRecorder;
    private readonly TelegramMessageIntake _intake;
    private readonly TelegramReactionHandler _reactions;

    private readonly ITelegramBotClient? _journalSendBot;
    private string _botUsername = "";

    // Tracks the last Telegram message_id sent per chatId (updated in SendTextAsync).
    // Published to CompletionContextBuffer through GetLastSentMessageId so BufferBotResponse can
    // persist the outbound message_id. The map stays HERE: its seven writers are render-path
    // sites that #274 Constraint 1 freezes (#277 MUST NOT 19).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _lastSentMessageIds = new();

    public AgentTransport(
        IOptions<AgentOptions> agentConfig,
        IOptions<TelegramOptions> telegramConfig,
        AllowlistHolder allowlist,
        GroupRelayService relay,
        TaskManager taskManager,
        GroupBehavior groupBehavior,
        MessageRouter router,
        CommandDispatcher commands,
        VoiceTranscriptionService voiceTranscription,
        TtsService tts,
        IFleetConnectionState connectionState,
        ILogger<AgentTransport> logger,
        MessageSinkHolder sinkHolder,
        RichFallbackCounter? richFallbackCounter = null,
        SinkSuppressionCounter? sinkCounter = null,
        JournalCapture? journal = null,
        TurnBindingPublisher? turnBindings = null)
    {
        _journal = journal;
        _agentConfig = agentConfig.Value;
        _telegramConfig = telegramConfig.Value;
        _relay = relay;
        _taskManager = taskManager;
        _groupBehavior = groupBehavior;
        _router = router;
        _commands = commands;
        _connectionState = connectionState;
        _logger = logger;
        _richFallbackCounter = richFallbackCounter ?? new RichFallbackCounter();

        // Only create the bot client when a token is available.
        //
        // The try/catch is required, not defensive: the whitespace check above accepts any
        // non-blank string, and Telegram.Bot validates token FORMAT in the constructor. A
        // syntactically invalid token therefore threw out of this constructor, which the host
        // surfaces as a startup failure — taking relay, workflow completion and the first-party
        // adapter down with it. That is the same total-outage shape #277 §2 exists to remove,
        // arriving through configuration instead of registration (#277 MUST NOT 17, S2).
        //
        // A malformed token leaves _bot null, which is exactly the absent-token state: the poller
        // is disabled and everything else runs.
        if (!string.IsNullOrWhiteSpace(telegramConfig.Value.BotToken))
        {
            try
            {
                _bot = new TelegramBotClient(telegramConfig.Value.BotToken);
                // The same credential, separate request policy: ordinary replies retain SDK retries.
                if (journal?.SendEnabled == true)
                    _journalSendBot = new TelegramBotClient(new TelegramBotClientOptions(telegramConfig.Value.BotToken) { RetryCount = 0 });
                sinkCounter?.SetStartupTelegramState(SinkSuppressionCounter.TelegramConfigured);
            }
            catch (Exception ex)
            {
                // Never log the token or any part of it.
                _logger.LogWarning(ex,
                    "TELEGRAM_BOT_TOKEN is present but malformed — Telegram poller disabled. " +
                    "Relay, workflow completion and non-Telegram adapters are unaffected.");
                _bot = null;
                sinkCounter?.SetStartupTelegramState(SinkSuppressionCounter.TelegramMalformed);
            }
        }
        else
        {
            sinkCounter?.SetStartupTelegramState(SinkSuppressionCounter.TelegramAbsent);
        }

        // Attach self as the real IMessageSink.
        //
        // This replaces four assignments into already-constructed services, which existed to break
        // the circular DI AgentTransport -> TaskManager -> sink. The holder is dependency-free and
        // is what actually breaks that cycle, so consumers now constructor-inject IMessageSink and
        // nothing reaches backwards into a built object graph (#277 D-1).
        sinkHolder.Attach(this);

        // The completion and tool-use effects used to be attached here. They now live in
        // RelayCompletionPublisher and CompletionContextBuffer, which subscribe in their own
        // constructors — the same host-ordering property this class relied on, now verified by
        // RuntimeWiringService instead of assumed (#277 D-2, D-2a).
        //
        // _lastSentMessageIds deliberately did NOT move with them. It is written by seven sites
        // inside the Telegram render path that #274 Constraint 1 freezes, and it holds a Telegram
        // message id that nothing else in the process can know. The value is published through the
        // IMessageSink.GetLastSentMessageId override below instead (#277 MUST NOT 19, MUST NOT 20).

        _journalRecorder = new TelegramJournalRecorder(journal, () => _bot, logger);
        _intake = new TelegramMessageIntake(this, _agentConfig, _telegramConfig, voiceTranscription, tts, turnBindings,
            _journalRecorder, logger);
        _reactions = new TelegramReactionHandler(allowlist, taskManager, logger);

        // Wire up the download helper with the real bot downloader.
        // In headless mode (_bot == null) PersistAttachments should be false;
        // the NullDownloader throws if somehow reached without a bot.
        IDocumentDownloader botDownloader = _bot is not null
            ? new TelegramBotDownloader(_bot)
            : NullDocumentDownloader.Instance;
        DownloadHelper = new DocumentDownloadHelper(
            botDownloader,
            _telegramConfig,
            (chatId, text) => SendTextAsync(chatId, text),
            logger);
    }

    // ── IDocumentDownloader implementations (private, nested) ────────────────

    private sealed class TelegramBotDownloader : IDocumentDownloader
    {
        private readonly ITelegramBotClient _bot;
        internal TelegramBotDownloader(ITelegramBotClient bot) => _bot = bot;

        public async Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await _bot.GetInfoAndDownloadFile(fileId, ms, cancellationToken: ct);
            return ms.ToArray();
        }
    }

    private sealed class NullDocumentDownloader : IDocumentDownloader
    {
        internal static readonly NullDocumentDownloader Instance = new();
        public Task<byte[]> DownloadAsync(string fileId, CancellationToken ct)
            => throw new InvalidOperationException("Cannot download documents: no Telegram bot client is configured.");
    }

    private static readonly Regex ImageMarkerRegex = new(@"\[IMAGE:(.+?)\]", RegexOptions.Compiled);
    private static readonly Regex ReplyToTokenRegex = new(@"\[reply_to:\s*(-?\d+)\]", RegexOptions.Compiled);

    // ── document download helpers (internal for testability) ─────────────────

    /// <summary>
    /// Sanitises a Telegram-supplied filename and extracts only the file extension.
    /// Defeats path traversal by taking only the last path component; strips control
    /// characters; falls back to ".bin" when no extension is present.
    /// </summary>
    internal static string ExtractSafeExtension(string? fileName)
        => ExtractSafeExtension(fileName, ".bin");

    /// <summary>
    /// As <see cref="ExtractSafeExtension(string?)"/>, but falls back to
    /// <paramref name="defaultExtension"/> instead of ".bin". Telegram types that carry no
    /// filename at all — voice, video note, sticker — depend entirely on this fallback to
    /// produce a file the agent can actually open.
    /// </summary>
    internal static string ExtractSafeExtension(string? fileName, string defaultExtension)
    {
        var safeName = (fileName ?? string.Empty).Replace('\\', '/');
        safeName = Path.GetFileName(safeName);
        safeName = new string(safeName.Where(c => c >= 0x20).ToArray());
        var ext = Path.GetExtension(safeName).ToLowerInvariant();
        return string.IsNullOrEmpty(ext) || ext == "." ? defaultExtension : ext;
    }

    /// <summary>
    /// Returns a safe MIME type for a known extension; falls back to
    /// "application/octet-stream" so unknown types never masquerade as PDF.
    /// </summary>
    internal static string InferMimeType(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".oga" or ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".tgs" => "application/gzip",
        _ => "application/octet-stream",
    };

    // --- IMessageSink ---

    public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        => SendTextAsync(chatId, text, OutboundOrigin.Human, ct);

    // Not journaled (#394). Notices, progress posts and command output come this way; a turn's
    // answer comes through SendReplyAsync, the only outbound send the journal records.
    public Task SendTextAsync(long chatId, string text, OutboundOrigin origin, CancellationToken ct = default)
        => SendTextCoreAsync(chatId, text, journalText: "", journal: null, ct);

    /// <summary>
    /// Sends a turn's answer: the one outbound send that is journaled (#394). Telegram gets exactly
    /// what the composed reply always produced. The journal gets the body alone, rendered through
    /// the same stage that sent each message, and never the stats line or the tool block.
    /// </summary>
    public async Task SendReplyAsync(long chatId, AgentReply reply, OutboundOrigin origin, CancellationToken ct = default)
    {
        // Everything one reply puts on Telegram is journaled together, so a reply split into
        // several messages shares one sendGroup. Flushed in finally: a send that fails half-way
        // still journals the parts Telegram accepted.
        var journal = _journal?.Outbound(origin);
        try
        {
            if (reply.HasToolBlock)
                await SendHtmlTextCoreAsync(chatId, reply.ComposeHtml(), reply.HtmlBody(), journal, ct);
            else
                await SendTextCoreAsync(chatId, reply.ComposeText(), reply.Content, journal, ct);
        }
        finally
        {
            journal?.Flush();
        }
    }

    /// <summary>The transport renders a reply itself, so it can journal the body apart from the footer.</summary>
    public bool RendersReplies => true;

    /// <param name="text">What Telegram gets, exactly as before #394.</param>
    /// <param name="journalText">
    /// The reply body without the footer (Path S), put through the same token strip, <c>[IMAGE:]</c>
    /// split and per-mode render as <paramref name="text"/>. Only its last part can differ, since
    /// the footer is appended at the very end. Unused without a journal.
    /// </param>
    private async Task SendTextCoreAsync(long chatId, string text, string journalText, JournalCapture.OutboundBatch? journal, CancellationToken ct)
    {
        // chatId==0 means "headless workflow delegation" — no Telegram destination.
        // The result still flows back to the caller via the relay (see OnTaskCompleted).
        if (chatId == 0) return;
        // Reserved-band keys belong to a non-Telegram conversation and have no Telegram
        // destination. This is the ONLY permitted edit to the render path: a pure early
        // return, before any formatting, splitting or bot call.
        if (Services.ConversationRegistry.IsReservedKey(chatId)) return;
        if (_bot is null) return;

        // Extract and strip [reply_to: N] token from agent output (Feature 3b).
        (text, var replyToMessageId) = ExtractReplyToToken(text, _logger);

        // Split on [IMAGE:...] markers; odd-indexed segments are file paths
        var parts = ImageMarkerRegex.Split(text);

        // The journal's view of each part (#394): the same part of the body alone. A part the body
        // does not reach holds only footer, and nothing of it is journaled.
        string[] journalParts = journal is null ? [] : ImageMarkerRegex.Split(ExtractReplyToToken(journalText).text);
        string JournalPart(int index) => index < journalParts.Length ? journalParts[index] : "";

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

                // The journal captions the photo from the same part of the body: a caption that was
                // only the stats line journals as none.
                var captionPart = caption is null ? -1 : i - 1 >= 0 && parts[i - 1].Trim() == caption ? i - 1 : i + 1;
                var journalCaption = captionPart >= 0 && JournalPart(captionPart).Trim() is { Length: > 0 } body ? body : null;

                await SendPhotoCoreAsync(chatId, filePath, caption, journalCaption, journal, ct);
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

                // The same segment of the body alone. Every stage below renders it exactly as it
                // renders the segment, and zips the two by index (JournalPiece).
                var journalSegment = JournalPart(i).Trim();

                // Prepend bold [ShortName] header when PrefixMessages is enabled
                if (_agentConfig.FormattingMode == FormattingMode.Rich)
                {
                    // Rich path: emit sendRichMessage blocks, fall back per-message to LegacyHtml
                    // then PlainText. Never drops a message. All fallbacks are logged as Warning.
                    var prefix = _agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0
                        ? $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}: "
                        : null;
                    var fullText = prefix is not null ? prefix + segment : segment;
                    var journalFullText = journalSegment.Length == 0 ? "" : prefix is not null ? prefix + journalSegment : journalSegment;
                    replyUsed = await SendRichWithFallbackAsync(chatId, fullText, journalFullText, replyToMessageId, replyUsed, ct, journal);
                }
                else if (_agentConfig.FormattingMode == FormattingMode.LegacyHtml)
                {
                    // LegacyHtml path: Markdown→HTML via TelegramFormatter, ParseMode.Html.
                    // The prefix (if any) is counted against every chunk's budget
                    // so that prefix + chunk ≤ 4096 after combination.
                    if (_agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0)
                    {
                        var displayName = $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}";
                        var prefix = $"<b>{displayName}:</b>\n";
                        var chunks = NonEmpty(TelegramFormatter.FormatAndSplit(segment, prefix.Length));
                        var bodyChunks = BodyPieces(journalSegment, s => TelegramFormatter.FormatAndSplit(s, prefix.Length));
                        for (var k = 0; k < chunks.Count; k++)
                        {
                            var replyParams = !replyUsed && replyToMessageId.HasValue
                                ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                                : null;
                            var sentId = await SendMessageWithReplyFallbackAsync(chatId, prefix + chunks[k],
                                JournalPiece(bodyChunks, k, chunks.Count) is { } body ? prefix + body : null,
                                ParseMode.Html, replyParams, ct, journal);
                            _lastSentMessageIds[chatId] = sentId;
                            replyUsed = true;
                        }
                    }
                    else
                    {
                        var chunks = NonEmpty(TelegramFormatter.FormatAndSplit(segment));
                        var bodyChunks = BodyPieces(journalSegment, s => TelegramFormatter.FormatAndSplit(s));
                        for (var k = 0; k < chunks.Count; k++)
                        {
                            var replyParams = !replyUsed && replyToMessageId.HasValue
                                ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                                : null;
                            var sentId = await SendMessageWithReplyFallbackAsync(chatId, chunks[k],
                                JournalPiece(bodyChunks, k, chunks.Count), ParseMode.Html, replyParams, ct, journal);
                            _lastSentMessageIds[chatId] = sentId;
                            replyUsed = true;
                        }
                    }
                }
                else if (_agentConfig.PrefixMessages && _agentConfig.ShortName.Length > 0)
                {
                    // Legacy prefix path — byte-identical to pre-formatter behavior.
                    var displayName = $"{char.ToUpperInvariant(_agentConfig.ShortName[0])}{_agentConfig.ShortName[1..]}";
                    var chunks = SplitMessage(segment, 3990).ToList();
                    var bodyChunks = BodyPieces(journalSegment, s => SplitMessage(s, 3990));
                    for (var k = 0; k < chunks.Count; k++)
                    {
                        var escaped = System.Net.WebUtility.HtmlEncode(chunks[k]);
                        var replyParams = !replyUsed && replyToMessageId.HasValue
                            ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                            : null;
                        var sentId = await SendMessageWithReplyFallbackAsync(chatId, $"<b>{displayName}:</b>\n{escaped}",
                            JournalPiece(bodyChunks, k, chunks.Count) is { } body
                                ? $"<b>{displayName}:</b>\n{System.Net.WebUtility.HtmlEncode(body)}"
                                : null,
                            ParseMode.Html, replyParams, ct, journal);
                        _lastSentMessageIds[chatId] = sentId;
                        replyUsed = true;
                    }
                }
                else
                {
                    // Legacy plain path — byte-identical to pre-formatter behavior.
                    var chunks = SplitMessage(segment, 4000).ToList();
                    var bodyChunks = BodyPieces(journalSegment, s => SplitMessage(s, 4000));
                    for (var k = 0; k < chunks.Count; k++)
                    {
                        var replyParams = !replyUsed && replyToMessageId.HasValue
                            ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                            : null;
                        var sentId = await SendMessageWithReplyFallbackAsync(chatId, chunks[k],
                            JournalPiece(bodyChunks, k, chunks.Count), null, replyParams, ct, journal);
                        _lastSentMessageIds[chatId] = sentId;
                        replyUsed = true;
                    }
                }
            }
        }
    }

    private static List<string> NonEmpty(IEnumerable<string> chunks) => chunks.Where(c => c.Length > 0).ToList();

    /// <summary>
    /// What one render stage makes of the reply body (#394): its own non-empty pieces, or none when
    /// the body is empty — a part that held only the footer. Never throws: the body is rendered
    /// next to the send, and a journal problem must cost the record, never change the send.
    /// </summary>
    private List<string> BodyPieces(string body, Func<string, IEnumerable<string>> render)
    {
        if (body.Length == 0) return [];
        try
        {
            return NonEmpty(render(body));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("journal: reply body render skipped ({Error})", ex.GetType().Name);
            return [];
        }
    }

    /// <summary>
    /// The journal text of sent message <paramref name="index"/> out of the <paramref name="sentCount"/>
    /// one render stage sends, zipped by index with that stage's <paramref name="bodyPieces"/> (#394).
    /// A sent message past the last body piece is footer only: null, not journaled. Body pieces
    /// beyond the sent count are appended to the last sent message.
    /// </summary>
    internal static string? JournalPiece(List<string> bodyPieces, int index, int sentCount)
    {
        if (index >= bodyPieces.Count) return null;
        return index == sentCount - 1 && bodyPieces.Count > sentCount
            ? string.Concat(bodyPieces.Skip(index))
            : bodyPieces[index];
    }

    /// <summary>
    /// Sends a text message and returns the Telegram <c>message_id</c> of the sent message.
    /// Falls back to standalone (without reply threading) when the reply target is not found.
    /// Falls back to plain text (strips HTML tags) when the API rejects parse-entities so the
    /// message is always delivered — never silently dropped on a formatting failure.
    /// </summary>
    /// <param name="journalText">
    /// What the journal keeps of this message, put through the same fallback as <paramref name="text"/>;
    /// null when the message is not journaled (a footer-only message, or not a reply at all).
    /// </param>
    private async Task<long> SendMessageWithReplyFallbackAsync(
        long chatId, string text, string? journalText, ParseMode? parseMode,
        Telegram.Bot.Types.ReplyParameters? replyParams,
        CancellationToken ct,
        JournalCapture.OutboundBatch? journal = null)
    {
        if (journalText is null) journal = null;
        var format = parseMode == ParseMode.Html ? JournalTextFormat.Html : JournalTextFormat.Plain;
        try
        {
            var m = replyParams is not null
                ? (parseMode.HasValue
                    ? await _bot!.SendMessage(chatId, text, parseMode: parseMode.Value,
                        replyParameters: replyParams, cancellationToken: ct)
                    : await _bot!.SendMessage(chatId, text, replyParameters: replyParams, cancellationToken: ct))
                : (parseMode.HasValue
                    ? await _bot!.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                    : await _bot!.SendMessage(chatId, text, cancellationToken: ct));
            JournalSent(journal, chatId, m, journalText, format);
            return m.Id;
        }
        catch (Exception ex) when (ex.Message.Contains("message to be replied not found")
                                || ex.Message.Contains("reply message not found"))
        {
            _logger.LogWarning("Reply target not found for chat {ChatId} — sending as standalone", chatId);
            var m = parseMode.HasValue
                ? await _bot!.SendMessage(chatId, text, parseMode: parseMode.Value, cancellationToken: ct)
                : await _bot!.SendMessage(chatId, text, cancellationToken: ct);
            JournalSent(journal, chatId, m, journalText, format);
            return m.Id;
        }
        catch (Exception ex) when (IsParseEntitiesError(ex) && parseMode == ParseMode.Html)
        {
            _logger.LogWarning(ex,
                "Parse-entities error for chat {ChatId} — falling back to plain text", chatId);
            var plain = TelegramFormatter.StripHtmlTagsToPlain(text);
            var m = replyParams is not null
                ? await _bot!.SendMessage(chatId, plain, replyParameters: replyParams, cancellationToken: ct)
                : await _bot!.SendMessage(chatId, plain, cancellationToken: ct);
            JournalSent(journal, chatId, m, journalText is null ? null : TelegramFormatter.StripHtmlTagsToPlain(journalText),
                JournalTextFormat.Plain);
            return m.Id;
        }
    }

    private static bool IsParseEntitiesError(Exception ex) =>
        ex.Message.Contains("can't parse entities") ||
        ex.Message.Contains("Bad Request: can't parse") ||
        ex.Message.Contains("parse_mode");

    /// <summary>
    /// Sends text using the Rich path (sendRichMessage) with automatic per-message fallback:
    /// Rich → LegacyHtml → PlainText. Every fallback is logged as Warning and counted.
    /// Never drops a message.
    /// </summary>
    // Returns true if the reply token was consumed (so the caller can set replyUsed).
    // journalText is the same text without the footer, empty when there is no body: each stage
    // renders it as it renders text, and the journal keeps what the stage that sent made of it.
    private async Task<bool> SendRichWithFallbackAsync(
        long chatId, string text, string journalText,
        int? replyToMessageId, bool replyAlreadyUsed,
        CancellationToken ct,
        JournalCapture.OutboundBatch? journal = null)
    {
        var agentName = _agentConfig.Name;
        bool replyUsed = replyAlreadyUsed;

        // ── Try sendRichMessage ───────────────────────────────────────────────
        try
        {
            var blocks = TelegramRichFormatter.ConvertToRichBlocks(text);
            var richMsg = new Telegram.Bot.Types.InputRichMessage { Blocks = blocks };
            var replyParams = !replyUsed && replyToMessageId.HasValue
                ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                : null;
            var m = replyParams is not null
                ? await _bot!.SendRichMessage(chatId, richMsg, replyParameters: replyParams, cancellationToken: ct)
                : await _bot!.SendRichMessage(chatId, richMsg, cancellationToken: ct);
            // The journal keeps the Markdown the blocks were rendered from, marked rich. One
            // message, so a body-less one (only the footer) is not journaled at all.
            JournalSent(journalText.Length == 0 ? null : journal, chatId, m, journalText, JournalTextFormat.Rich);
            _lastSentMessageIds[chatId] = m.Id;
            replyUsed = true;
            return replyUsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "sendRichMessage failed for agent {Agent} chat {Chat} (failureType=rich_to_html) — falling back to LegacyHtml",
                agentName, chatId);
            _richFallbackCounter.Increment(agentName, "rich_to_html");
        }

        // ── LegacyHtml fallback ───────────────────────────────────────────────
        try
        {
            var chunks = NonEmpty(TelegramFormatter.FormatAndSplit(text));
            var bodyChunks = BodyPieces(journalText, s => TelegramFormatter.FormatAndSplit(s));
            for (var k = 0; k < chunks.Count; k++)
            {
                var replyParams = !replyUsed && replyToMessageId.HasValue
                    ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                    : null;
                var sentId = await SendMessageWithReplyFallbackAsync(chatId, chunks[k],
                    JournalPiece(bodyChunks, k, chunks.Count), ParseMode.Html, replyParams, ct, journal);
                _lastSentMessageIds[chatId] = sentId;
                replyUsed = true;
            }
            return replyUsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LegacyHtml fallback failed for agent {Agent} chat {Chat} (failureType=html_to_plain) — falling back to PlainText",
                agentName, chatId);
            _richFallbackCounter.Increment(agentName, "html_to_plain");
        }

        // ── PlainText last resort ─────────────────────────────────────────────
        var slices = TelegramFormatter.SplitPlain(text);
        var bodySlices = BodyPieces(journalText, TelegramFormatter.SplitPlain);
        for (var k = 0; k < slices.Count; k++)
        {
            var replyParams = !replyUsed && replyToMessageId.HasValue
                ? new Telegram.Bot.Types.ReplyParameters { MessageId = replyToMessageId.Value }
                : null;
            var sentId = await SendMessageWithReplyFallbackAsync(chatId, slices[k],
                JournalPiece(bodySlices, k, slices.Count), null, replyParams, ct, journal);
            _lastSentMessageIds[chatId] = sentId;
            replyUsed = true;
        }
        return replyUsed;
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

    public Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        => SendHtmlTextAsync(chatId, htmlText, OutboundOrigin.Human, ct);

    // Not journaled (#394): the tool-progress blockquote comes this way. A reply with a tool block
    // reaches SendHtmlTextCoreAsync through SendReplyAsync instead.
    public Task SendHtmlTextAsync(long chatId, string htmlText, OutboundOrigin origin, CancellationToken ct = default)
        => SendHtmlTextCoreAsync(chatId, htmlText, journalHtml: null, journal: null, ct);

    /// <param name="journalHtml">
    /// Path T's body without the footer (<see cref="AgentReply.HtmlBody"/>), or null when nothing is
    /// journaled. See <see cref="ToolBlockJournalPieces"/>.
    /// </param>
    private async Task SendHtmlTextCoreAsync(long chatId, string htmlText, string? journalHtml, JournalCapture.OutboundBatch? journal, CancellationToken ct)
    {
        if (chatId == 0) return;
        // Reserved-band keys belong to a non-Telegram conversation and have no Telegram
        // destination. This is the ONLY permitted edit to the render path: a pure early
        // return, before any formatting, splitting or bot call.
        if (Services.ConversationRegistry.IsReservedKey(chatId)) return;
        if (_bot is null) return;

        List<string> bodyChunks = journal is null || journalHtml is null ? [] : ToolBlockJournalPieces(journalHtml);

        // Telegram doesn't support <br>, <br/>, or <br /> — replace all variants with newline.
        htmlText = ReplaceBreaks(htmlText);
        var k = 0;
        foreach (var chunk in SplitMessage(htmlText, 4000))
        {
            var balanced = BalanceBlockquotesInChunk(chunk);
            var m = await _bot.SendMessage(chatId, balanced, parseMode: ParseMode.Html, cancellationToken: ct);
            // Sent chunk k carries body chunk k and then footer; a chunk past the body, or one
            // whose body was only markers, is not journaled.
            if (k < bodyChunks.Count && !string.IsNullOrWhiteSpace(bodyChunks[k]))
                JournalSent(journal, chatId, m, BalanceBlockquotesInChunk(bodyChunks[k]), JournalTextFormat.Html);
            k++;
        }
    }

    private static string ReplaceBreaks(string html) => Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

    /// <summary>
    /// Path T's journal pieces before balancing (#394). <paramref name="bodyHtml"/> gets the same
    /// &lt;br&gt; normalisation and the same hard 4,000-character cuts as the full text Telegram
    /// got. Hard cuts are prefix-stable and the footer comes last, so piece k is exactly the body
    /// part of sent chunk k. Every character inside an <c>[IMAGE:…]</c> or <c>[reply_to: N]</c>
    /// match on the whole body is removed: Path T sends both as literal text, and an image marker
    /// holds a local path. Never throws; a failure leaves the reply unjournaled.
    /// </summary>
    internal List<string> ToolBlockJournalPieces(string bodyHtml)
    {
        try
        {
            var body = ReplaceBreaks(bodyHtml);
            var masked = new bool[body.Length];
            foreach (Match marker in ImageMarkerRegex.Matches(body).Concat(ReplyToTokenRegex.Matches(body)))
                Array.Fill(masked, true, marker.Index, marker.Length);

            var pieces = new List<string>();
            var start = 0;
            foreach (var chunk in SplitMessage(body, 4000))
            {
                var piece = new System.Text.StringBuilder(chunk.Length);
                for (var c = 0; c < chunk.Length; c++)
                {
                    if (!masked[start + c]) piece.Append(chunk[c]);
                }
                pieces.Add(piece.ToString());
                start += chunk.Length;
            }
            return pieces;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("journal: reply body render skipped ({Error})", ex.GetType().Name);
            return [];
        }
    }

    public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default)
        => SendPhotoAsync(chatId, filePath, caption, OutboundOrigin.Human, ct);

    // Not journaled (#394). A photo is journaled only as part of a reply, from its [IMAGE:] marker.
    public Task SendPhotoAsync(long chatId, string filePath, string? caption, OutboundOrigin origin, CancellationToken ct = default)
        => SendPhotoCoreAsync(chatId, filePath, caption, journalCaption: null, journal: null, ct);

    /// <param name="journalCaption">The caption as the reply body has it, without the footer (#394).</param>
    private async Task SendPhotoCoreAsync(long chatId, string filePath, string? caption, string? journalCaption,
        JournalCapture.OutboundBatch? journal, CancellationToken ct)
    {
        if (chatId == 0) return;
        // Reserved-band keys belong to a non-Telegram conversation and have no Telegram
        // destination. This is the ONLY permitted edit to the render path: a pure early
        // return, before any formatting, splitting or bot call.
        if (Services.ConversationRegistry.IsReservedKey(chatId)) return;
        if (_bot is null) return;

        if (!File.Exists(filePath))
        {
            // Not journaled (#394): the hint stands in for a photo this agent could not send.
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
            var sent = await _bot.SendPhoto(chatId, inputFile, caption: caption, cancellationToken: ct);
            // The bytes Telegram received, copied now: a workspace file can be overwritten later.
            JournalSent(journal, chatId, sent, journalCaption, JournalTextFormat.Plain, new JournalMediaItem(
                JournalAttachmentKind.Photo, InferMimeType(ExtractSafeExtension(filePath, ".jpg")), bytes.LongLength,
                Path.GetFileName(filePath), sent.Photo?.LastOrDefault()?.FileUniqueId, Bytes: bytes, FileId: sent.Photo?.LastOrDefault()?.FileId));
        }
        catch (Exception ex)
        {
            // Not journaled (#394): the notice names a local path.
            _logger.LogWarning(ex, "Failed to send photo {FilePath}", filePath);
            await _bot.SendMessage(chatId, $"[photo: {filePath} — send failed]", cancellationToken: ct);
        }
    }

    public async Task SendTypingAsync(long chatId, CancellationToken ct = default)
    {
        if (chatId == 0) return;
        // Reserved-band keys belong to a non-Telegram conversation and have no Telegram
        // destination. This is the ONLY permitted edit to the render path: a pure early
        // return, before any formatting, splitting or bot call.
        if (Services.ConversationRegistry.IsReservedKey(chatId)) return;
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

        // Relay initialization, the relay subscription and shutdown-token distribution now live
        // in RuntimeWiringService, which runs regardless of whether a bot token is configured.
        // The completion and tool-use handlers are attached in this class's constructor.
        // ExecuteAsync's early return above therefore disables ONLY the poller.

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

    /// <summary>
    /// Publishes the id of the last message this transport sent to <paramref name="chatId"/> so
    /// CompletionContextBuffer can persist it without _lastSentMessageIds moving anywhere.
    ///
    /// This is the same expression the completion handler used to evaluate inline. The map and its
    /// seven render-path writers stay here, untouched (#277 D-2a, MUST NOT 19).
    /// </summary>
    public long GetLastSentMessageId(long chatId) =>
        _lastSentMessageIds.TryGetValue(chatId, out var id) ? id : 0L;

    internal Task OnMessage(Message message, UpdateType type) => _intake.OnMessage(message, type);

    // ── conversation journal (#377) ──────────────────────────────────────────

    /// <summary>Adds a message Telegram accepted to <paramref name="journal"/>. See <see cref="TelegramJournalRecorder.Sent"/>.</summary>
    private void JournalSent(
        JournalCapture.OutboundBatch? journal, long chatId, Message? sent, string? text,
        JournalTextFormat format, JournalMediaItem? media = null) =>
        _journalRecorder.Sent(journal, chatId, sent, text, format, media);

    public async Task<JournalMessage> SendFileIdAsync(long chatId, string kind, string fileId, CancellationToken ct)
    {
        if (_bot is null) throw new JournalTelegramException(403);
        var field = kind == "other" ? "document" : kind;
        var method = field switch { "photo" => "sendPhoto", "video" => "sendVideo", "audio" => "sendAudio",
            "voice" => "sendVoice", "video_note" => "sendVideoNote", "animation" => "sendAnimation", "sticker" => "sendSticker", _ => "sendDocument" };
        try { return _journalRecorder.Copied(await (_journalSendBot ?? _bot).SendRequest(new JournalFileIdRequest(method, chatId, field, fileId), ct), chatId); }
        catch (Telegram.Bot.Exceptions.ApiRequestException e)
        { throw new JournalTelegramException(e.ErrorCode, e.ErrorCode == 400 && (e.Message.Contains("wrong file identifier", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("file_id", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("type of file", StringComparison.OrdinalIgnoreCase)), e.Parameters?.RetryAfter); }
    }

    public async Task<JournalMessage> SendUploadAsync(long chatId, bool photo, HttpContent content, string fileName, CancellationToken ct)
    {
        if (_bot is null) throw new JournalTelegramException(403);
        var request = new JournalUploadRequest(chatId, photo, content, fileName);
        try { return _journalRecorder.Copied(await (_journalSendBot ?? _bot).SendRequest(request, ct), chatId); }
        catch (Telegram.Bot.Exceptions.ApiRequestException e) { throw new JournalTelegramException(e.ErrorCode, retryAfter: e.Parameters?.RetryAfter); }
        catch (OperationCanceledException) { throw new JournalTelegramException(request.TerminatorWritten ? 0 : -1); }
        catch (Exception e) when (e is HttpRequestException or IOException && !HasIntegrityFailure(e)) { throw new JournalTelegramException(request.TerminatorWritten ? 0 : -2); }
    }
    private static bool HasIntegrityFailure(Exception e) => e is JournalUploadIntegrityException || e.InnerException is not null && HasIntegrityFailure(e.InnerException);
    private sealed class JournalFileIdRequest(string method, long chatId, string field, string fileId) : Telegram.Bot.Requests.FileRequestBase<Message>(method)
    {
        public override HttpContent ToHttpContent() => new FormUrlEncodedContent(new Dictionary<string, string>
        { ["chat_id"] = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture), [field] = fileId });
    }

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

    private void HandleReaction(MessageReactionUpdated reaction) => _reactions.Handle(reaction);

    /// <summary>See <see cref="TelegramReactionHandler.BuildChannelAnchorFromChat"/>.</summary>
    internal static string BuildChannelAnchorFromChat(Chat chat) => TelegramReactionHandler.BuildChannelAnchorFromChat(chat);

    /// <summary>See <see cref="TelegramReactionHandler.DiffReactions"/>.</summary>
    internal static (HashSet<string> added, HashSet<string> removed) DiffReactions(
        IEnumerable<ReactionType>? newReaction,
        IEnumerable<ReactionType>? oldReaction) => TelegramReactionHandler.DiffReactions(newReaction, oldReaction);

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
