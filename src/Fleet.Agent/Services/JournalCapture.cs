using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// One Telegram message as the transport saw it, with nothing Telegram.Bot-typed in it — the
/// transport stays the only file that imports Telegram.Bot.
/// </summary>
public sealed record JournalMessage
{
    public required long BotId { get; init; }
    public required long ChatId { get; init; }

    /// <summary>The Bot API chat type string (<c>private</c>, <c>group</c>, <c>supergroup</c>, …).</summary>
    public string? ChatType { get; init; }

    public string? ChatTitle { get; init; }
    public required long MessageId { get; init; }
    public long? ReplyToMessageId { get; init; }
    public string? MediaGroupId { get; init; }

    /// <summary>Telegram's date for the message. <see cref="DateTime.MinValue"/> means unknown.</summary>
    public DateTime Date { get; init; }

    public required JournalSenderKind SenderKind { get; init; }
    public required string SenderId { get; init; }
    public string? SenderDisplay { get; init; }

    /// <summary>The raw text or caption, never a placeholder or anything the agent derived.</summary>
    public string? Text { get; init; }

    public JournalTextFormat TextFormat { get; init; } = JournalTextFormat.Plain;
    public string? Transcript { get; init; }
    public IReadOnlyList<JournalMediaItem> Media { get; init; } = [];
}

/// <summary>
/// One attachment: its metadata, plus where its bytes are — a downloaded file (inbound) or what was
/// sent (outbound). The bytes go to the spool only; the record carries metadata.
/// </summary>
public sealed record JournalMediaItem(
    JournalAttachmentKind Kind,
    string MimeType,
    long? ByteSize,
    string? FileName,
    string? FileUniqueId,
    string? LocalPath = null,
    byte[]? Bytes = null,
    /// <summary>
    /// Set when the caller already knows there are no bytes to spool — a photo the agent skipped on
    /// its own size limit, a file the Bot API will not hand over, a kind the mapper declines. The
    /// attachment journals that reason instead of waiting for the drain to discover the file is
    /// missing.
    /// </summary>
    JournalMediaReason? Declined = null, string? FileId = null, JournalCopiedFrom? CopiedFrom = null);

/// <summary>
/// Turns what the Telegram transport sent and received into journal records in the spool
/// (#377). Registered only when <c>Journal__IngestToken</c> is set; without it the transport holds
/// null and nothing here runs.
/// </summary>
/// <remarks>
/// <para>
/// Classification happens before anything is written (the §2 rules, then the allowlist): an
/// excluded message costs a counter and nothing else.
/// </para>
/// <para>
/// ⚠️ This must never fail or slow a Telegram send or receive. Every public entry point catches
/// everything; a write error costs the record (<c>journal_capture_failed</c>), never the turn. Logs
/// carry reason codes only — never text, never a chat id.
/// </para>
/// </remarks>
public sealed class JournalCapture
{
    private static readonly TimeSpan SpoolFullWarnInterval = TimeSpan.FromMinutes(1);

    private readonly JournalSpool _spool;
    private readonly bool _sendEnabled;
    public bool SendEnabled => _sendEnabled;
    private readonly JournalCounters _counters;
    private readonly AllowlistHolder _allowlist;
    private readonly IReadOnlySet<long> _excluded;
    private readonly ILogger<JournalCapture> _logger;
    private readonly TimeProvider _time;
    private long _lastFullWarnTicks = long.MinValue;

    public JournalCapture(
        JournalSpool spool,
        JournalCounters counters,
        AllowlistHolder allowlist,
        IOptions<JournalOptions> options,
        ILogger<JournalCapture> logger,
        TimeProvider? time = null)
    {
        _spool = spool;
        _sendEnabled = options.Value.SendEnabled;
        _counters = counters;
        _allowlist = allowlist;
        _excluded = JournalOptions.ParseExcludedChatIds(options.Value.ExcludedChatIds);
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>One raw inbound Telegram message. Its media are hardlinked into the spool.</summary>
    public void Inbound(JournalMessage message)
    {
        try
        {
            if (!Include(JournalDirection.Inbound, message, OutboundOrigin.Human)) return;
            Write(JournalDirection.Inbound, message, sendGroup: null, SpoolMediaMode.Hardlink, JournalRecordOrigin.TelegramUpdate);
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    /// <summary>
    /// Starts collecting what one send call puts on Telegram. Messages are classified as they are
    /// added and written on <see cref="OutboundBatch.Flush"/>, which gives a reply split into
    /// several messages one shared <c>sendGroup</c>.
    /// </summary>
    /// <param name="origin">The task origin the classifier applies (only <c>Human</c> is kept).</param>
    /// <param name="recordOrigin">
    /// The wire <c>origin</c> the records carry: <c>agent_runtime</c> for what the transport sent,
    /// <c>agent_tool</c> for a message sent through a Telegram MCP tool (#394).
    /// </param>
    public OutboundBatch Outbound(OutboundOrigin origin, JournalRecordOrigin recordOrigin = JournalRecordOrigin.AgentRuntime) =>
        new(this, origin, recordOrigin);

    public sealed class OutboundBatch
    {
        private readonly JournalCapture _owner;
        private readonly OutboundOrigin _origin;
        private readonly JournalRecordOrigin _recordOrigin;
        private readonly List<JournalMessage> _kept = [];
        public bool SpoolSucceeded { get; private set; }

        internal OutboundBatch(JournalCapture owner, OutboundOrigin origin, JournalRecordOrigin recordOrigin)
        {
            _owner = owner;
            _origin = origin;
            _recordOrigin = recordOrigin;
        }

        /// <summary>A message Telegram accepted. Kept only if the classifier includes it.</summary>
        public void Add(JournalMessage message)
        {
            try
            {
                if ((_recordOrigin != JournalRecordOrigin.AgentCopy || _owner._sendEnabled) && _owner.Include(JournalDirection.Outbound, message, _origin)) _kept.Add(message);
            }
            catch (Exception e)
            {
                _owner.Failed(e);
            }
        }

        /// <summary>Writes every kept message. Safe to call more than once; never throws.</summary>
        public void Flush()
        {
            if (_kept.Count == 0) return;

            var messages = _kept.ToArray();
            _kept.Clear();

            // parts is capped at 64 by the contract; a longer split goes without a group rather
            // than being refused.
            var groupId = messages.Length is > 1 and <= 64 ? Ulid.NewUlid(_owner._time.GetUtcNow()) : null;

            for (var i = 0; i < messages.Length; i++)
            {
                var group = groupId is null ? null : new JournalSendGroup { Id = groupId, Part = i + 1, Parts = messages.Length };
                try
                {
                    SpoolSucceeded |= _owner.Write(JournalDirection.Outbound, messages[i], group, SpoolMediaMode.Copy, _recordOrigin);
                }
                catch (Exception e)
                {
                    _owner.Failed(e);
                }
            }
        }
    }

    private bool Include(JournalDirection direction, JournalMessage message, OutboundOrigin origin)
    {
        var allowlist = _allowlist.JournalSnapshot();
        var kind = Fleet.Journal.Client.JournalClassifier.ChatKind(message.ChatType);
        var taskOrigin = origin switch
        {
            OutboundOrigin.Relay => JournalTaskOrigin.Relay,
            OutboundOrigin.Bridge => JournalTaskOrigin.Bridge,
            _ => JournalTaskOrigin.Human,
        };

        if (Fleet.Journal.Client.JournalClassifier.ShouldCapture(
                direction, message.ChatId, kind, taskOrigin, allowlist, _excluded, out var reason))
            return true;

        _counters.Excluded(reason);
        return false;
    }

    private bool Write(
        JournalDirection direction, JournalMessage message, JournalSendGroup? sendGroup, SpoolMediaMode mode,
        JournalRecordOrigin recordOrigin)
    {
        var now = _time.GetUtcNow();
        var attachments = new List<JournalAttachment>();
        var media = new List<SpoolMedia>();

        foreach (var item in message.Media.Take(JournalRecordJson.MaxAttachments))
        {
            var ordinal = attachments.Count;
            attachments.Add(new JournalAttachment
            {
                Ordinal = ordinal,
                Kind = item.Kind,
                MimeType = item.MimeType,
                ByteSize = item.ByteSize ?? item.Bytes?.LongLength,
                FileName = item.FileName,
                FileUniqueId = item.FileUniqueId,
                FileId = _sendEnabled ? item.FileId : null,
                CopiedFrom = _sendEnabled && recordOrigin == JournalRecordOrigin.AgentCopy ? item.CopiedFrom : null,
                // The wire contract requires exactly one of `notArchivedReason` or `uploadId` on
                // every attachment, so capture states which one it is leaving to the drainer:
                // `media_disabled` is the placeholder for "this has no upload yet and no known
                // refusal", and the drainer replaces it with an `uploadId` the moment it has proven
                // bytes. A record that reaches the listener with the placeholder still on it is
                // stored as not-archived, which is the honest answer for a deployment with no
                // bucket — and it is why a pre-drain record is postable at all.
                NotArchivedReason = _sendEnabled && recordOrigin == JournalRecordOrigin.AgentCopy ? JournalNotArchivedReason.Copied : item.Declined is { } declined && declined != JournalMediaReason.Uploaded
                    ? WireReason(declined)
                    : JournalNotArchivedReason.MediaDisabled,
            });

            if (item.Declined is null && (item.Bytes is not null || item.LocalPath is not null))
                media.Add(new SpoolMedia(ordinal, item.LocalPath, item.Bytes, mode));
        }

        var record = new JournalRecord
        {
            EventId = Ulid.NewUlid(now),
            Telegram = new JournalTelegramRef
            {
                BotId = message.BotId,
                ChatId = message.ChatId,
                ChatKind = Fleet.Journal.Client.JournalClassifier.ChatKind(message.ChatType)!.Value,
                ChatTitle = message.ChatTitle,
                MessageId = message.MessageId,
                ReplyToMessageId = message.ReplyToMessageId,
                MediaGroupId = message.MediaGroupId,
            },
            Direction = direction,
            Sender = new JournalSender
            {
                Kind = message.SenderKind,
                Id = message.SenderId,
                Display = message.SenderDisplay,
            },
            SentAt = SentAt(message.Date, now),
            Text = string.IsNullOrEmpty(message.Text) ? null : message.Text,
            TextFormat = string.IsNullOrEmpty(message.Text) ? null : message.TextFormat,
            Transcript = string.IsNullOrEmpty(message.Transcript) ? null : message.Transcript,
            Origin = recordOrigin,
            SendGroup = sendGroup,
            Attachments = attachments,
        };

        var body = JsonNode.Parse(JournalRecordJson.Serialize(record))!.AsObject();
        var directionCode = direction == JournalDirection.Inbound ? "inbound" : "outbound";

        switch (_spool.Write(body, directionCode, media))
        {
            case SpoolWriteOutcome.Written:
                _counters.Captured(directionCode);
                return true;

            case SpoolWriteOutcome.Full:
                _counters.SpoolFull();
                WarnSpoolFull();
                return false;

            default:
                _counters.CaptureFailed();
                _logger.LogWarning("journal capture could not write to the spool; the record is lost, the turn continues");
                return false;
        }
    }

    /// <summary>The wire code for a decline. The six codes only; never a made-up value.</summary>
    private static JournalNotArchivedReason WireReason(JournalMediaReason reason) => reason switch
    {
        JournalMediaReason.MediaDisabled => JournalNotArchivedReason.MediaDisabled,
        JournalMediaReason.OverBotApiLimit => JournalNotArchivedReason.OverBotApiLimit,
        JournalMediaReason.OverSizeCap => JournalNotArchivedReason.OverSizeCap,
        JournalMediaReason.UnsupportedKind => JournalNotArchivedReason.UnsupportedKind,
        JournalMediaReason.DownloadFailed => JournalNotArchivedReason.DownloadFailed,
        _ => JournalNotArchivedReason.SourceExpired,
    };

    /// <summary>
    /// Telegram's own date when there is one. The contract refuses anything before 2013, which is
    /// what an unset date would become.
    /// </summary>
    private static DateTimeOffset SentAt(DateTime date, DateTimeOffset now)
    {
        if (date.Year < 2013) return now;
        return new DateTimeOffset(DateTime.SpecifyKind(date, date.Kind == DateTimeKind.Local ? DateTimeKind.Local : DateTimeKind.Utc));
    }

    private void WarnSpoolFull()
    {
        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastFullWarnTicks);
        if (last != long.MinValue && now - last < SpoolFullWarnInterval.Ticks) return;
        if (Interlocked.CompareExchange(ref _lastFullWarnTicks, now, last) != last) return;

        _logger.LogWarning(
            "journal spool full ({Records} records or {Limit} bytes); new records are dropped until the drainer catches up",
            JournalOptions.MaxSpoolRecords, JournalOptions.MaxSpoolBytes);
    }

    private void Failed(Exception e)
    {
        _counters.CaptureFailed();
        // The type only: a message could carry text.
        _logger.LogWarning("journal capture failed ({Error}); the record is lost, the turn continues", e.GetType().Name);
    }
}
