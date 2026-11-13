using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Agent.Services.TelegramShell;

/// <summary>
/// Turns Telegram messages into conversation-journal records (#377): what the bot sent, what a
/// person sent it, and the copy of a file the bot re-sent. It builds the record and hands it to
/// <see cref="JournalCapture"/>; it sends nothing and decides nothing about delivery. Every method
/// is a no-op without a journal, and none of them throws.
/// </summary>
internal sealed class TelegramJournalRecorder(JournalCapture? journal, Func<ITelegramBotClient?> bot, ILogger logger)
{
    private readonly JournalCapture? _journal = journal;
    private readonly ILogger _logger = logger;

    private ITelegramBotClient? Bot => bot();

    /// <summary>
    /// Adds a message Telegram accepted to <paramref name="journal"/>. Never throws: a journal
    /// problem must not turn a delivered message into a failed send.
    /// </summary>
    internal void Sent(
        JournalCapture.OutboundBatch? journal, long chatId, Message? sent, string? text,
        JournalTextFormat format, JournalMediaItem? media = null)
    {
        if (journal is null || sent is null) return;
        try
        {
            journal.Add(new JournalMessage
            {
                BotId = Bot!.BotId,
                ChatId = sent.Chat?.Id is { } id && id != 0 ? id : chatId,
                ChatType = ChatTypeName(sent.Chat?.Type),
                ChatTitle = sent.Chat?.Title,
                MessageId = sent.Id,
                ReplyToMessageId = sent.ReplyToMessage?.Id,
                Date = sent.Date,
                SenderKind = JournalSenderKind.Agent,
                SenderId = (sent.From?.Id ?? Bot.BotId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SenderDisplay = sent.From?.Username ?? sent.From?.FirstName,
                Text = text,
                TextFormat = format,
                Media = media is null ? [] : [media],
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("journal: outbound capture skipped ({Error})", ex.GetType().Name);
        }
    }

    /// <summary>A human-facing message sent outside any IMessageSink call: the TTS voice reply.</summary>
    internal void SentAlone(long chatId, Message? sent, string? text, JournalTextFormat format, JournalMediaItem? media = null)
    {
        var journal = _journal?.Outbound(OutboundOrigin.Human);
        if (journal is null) return;
        Sent(journal, chatId, sent, text, format, media);
        journal.Flush();
    }

    /// <summary>
    /// Journals one raw inbound message. Classification (rule 4 reads the live allowlist) happens
    /// inside the capture before anything is written, so an unauthorized chat writes nothing.
    /// </summary>
    /// <param name="photoDecline">
    /// Why the photo has no bytes on disk, when there are none. The download path already decided
    /// this — the agent's own size cap refused it, or the fetch failed — and passing the answer
    /// through is what lets the journal say <c>over_size_cap</c> rather than guessing at drain time
    /// that a file is simply not there.
    /// </param>
    internal void Received(
        Message message, string? transcript, string? photoPath, string? mediaPath,
        Fleet.Journal.Client.JournalMediaReason? photoDecline = null, Fleet.Journal.Client.JournalMediaReason? mediaDecline = null)
    {
        if (_journal is null) return;
        try
        {
            var items = new List<JournalMediaItem>();
            if (message.Photo is { Length: > 0 } photos)
            {
                var largest = photos.OrderByDescending(p => p.FileSize ?? 0).First();
                items.Add(new JournalMediaItem(JournalAttachmentKind.Photo, "image/jpeg", largest.FileSize,
                    FileName: null, largest.FileUniqueId, LocalPath: photoPath,
                    Declined: photoPath is null ? photoDecline : null, FileId: largest.FileId));
            }

            if (TelegramMediaMapper.TryMap(message) is { } file)
            {
                items.Add(new JournalMediaItem(
                    JournalKind(file.Kind),
                    file.MimeType ?? AgentTransport.InferMimeType(AgentTransport.ExtractSafeExtension(file.FileName, file.DefaultExtension)),
                    file.FileSize > 0 ? file.FileSize : null,
                    file.FileName,
                    FileUniqueIdOf(message, file.Kind),
                    LocalPath: mediaPath,
                    Declined: mediaPath is null ? mediaDecline : null, FileId: FileIdOf(message, file.Kind)));
            }

            // A media type the mapper declines has no download path at all, so there is nothing to
            // have failed: the kind itself is the reason its bytes are absent.
            if (items.Count == 0 && HasJournalMediaMetadata(message))
            {
                items.Add(new JournalMediaItem(JournalAttachmentKind.Other, "application/octet-stream",
                    null, null, null, Declined: Fleet.Journal.Client.JournalMediaReason.UnsupportedKind));
            }

            var from = message.From;
            _journal.Inbound(new JournalMessage
            {
                BotId = Bot?.BotId ?? 0,
                ChatId = message.Chat.Id,
                ChatType = ChatTypeName(message.Chat.Type),
                ChatTitle = message.Chat.Title,
                MessageId = message.MessageId,
                ReplyToMessageId = message.ReplyToMessage?.MessageId,
                MediaGroupId = message.MediaGroupId,
                Date = message.Date,
                SenderKind = from?.IsBot == true ? JournalSenderKind.Agent : JournalSenderKind.Human,
                SenderId = (from?.Id ?? message.SenderChat?.Id ?? message.Chat.Id).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SenderDisplay = from?.Username ?? from?.FirstName ?? message.SenderChat?.Title,
                Text = message.Text ?? message.Caption,
                Transcript = transcript,
                Media = items,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("journal: inbound capture skipped ({Error})", ex.GetType().Name);
        }
    }

    internal static string? ChatTypeName(ChatType? type) => type switch
    {
        ChatType.Private => "private",
        ChatType.Group => "group",
        ChatType.Supergroup => "supergroup",
        ChatType.Channel => "channel",
        _ => null,
    };

    private static JournalAttachmentKind JournalKind(TelegramMediaKind kind) => kind switch
    {
        TelegramMediaKind.Document => JournalAttachmentKind.Document,
        TelegramMediaKind.Video => JournalAttachmentKind.Video,
        TelegramMediaKind.VideoNote => JournalAttachmentKind.VideoNote,
        TelegramMediaKind.Audio => JournalAttachmentKind.Audio,
        TelegramMediaKind.Voice => JournalAttachmentKind.Voice,
        TelegramMediaKind.Animation => JournalAttachmentKind.Animation,
        TelegramMediaKind.Sticker => JournalAttachmentKind.Sticker,
        _ => JournalAttachmentKind.Other,
    };

    /// <summary>The largest photo size the update declared, or null when it declared none.</summary>
    internal static long? PhotoSizeOf(Message message) =>
        message.Photo is { Length: > 0 } photos
            ? photos.OrderByDescending(p => p.FileSize ?? 0).First().FileSize
            : null;

    /// <summary>
    /// Why the bytes are not on disk, from the platform's declared size and the agent's own cap.
    /// </summary>
    internal static Fleet.Journal.Client.JournalMediaReason MediaAbsence(long? platformFileSize, long maxLocalBytes) =>
        Fleet.Journal.Client.JournalMediaAbsent.Reason(platformFileSize, maxLocalBytes);

    /// <summary>
    /// True when the update carries media the agent has no descriptor for. A mapper decline is a
    /// decision, not an oversight, and the journal should say so rather than drop the attachment.
    /// </summary>
    private static bool HasJournalMediaMetadata(Message message) =>
        message.Contact is not null || message.Location is not null || message.Poll is not null
        || message.Venue is not null || message.Game is not null || message.ProximityAlertTriggered is not null
        || message.Invoice is not null || message.SuccessfulPayment is not null;

    private static string? FileIdOf(Message message, TelegramMediaKind kind) => kind switch
    {
        TelegramMediaKind.Animation => message.Animation?.FileId,
        TelegramMediaKind.Document => message.Document?.FileId,
        TelegramMediaKind.Video => message.Video?.FileId,
        TelegramMediaKind.VideoNote => message.VideoNote?.FileId,
        TelegramMediaKind.Audio => message.Audio?.FileId,
        TelegramMediaKind.Voice => message.Voice?.FileId,
        TelegramMediaKind.Sticker => message.Sticker?.FileId,
        _ => null,
    };

    internal JournalMessage Copied(Message sent, long chatId)
    {
        JournalMediaItem? media = null;
        if (sent.Photo?.LastOrDefault() is { } photo)
            media = new(JournalAttachmentKind.Photo, "image/jpeg", photo.FileSize, null, photo.FileUniqueId, FileId: photo.FileId);
        else if (TelegramMediaMapper.TryMap(sent) is { } file)
            media = new(JournalKind(file.Kind), file.MimeType ?? "application/octet-stream", file.FileSize, file.FileName,
                FileUniqueIdOf(sent, file.Kind), FileId: FileIdOf(sent, file.Kind));
        return new JournalMessage { BotId = Bot!.BotId, ChatId = sent.Chat.Id, ChatType = ChatTypeName(sent.Chat.Type),
            MessageId = sent.Id, Date = sent.Date, SenderKind = JournalSenderKind.Agent,
            SenderId = Bot.BotId.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = null, Media = media is null ? [] : [media] };
    }

    /// <summary>The file_unique_id the descriptor does not carry, read from the raw message.</summary>
    private static string? FileUniqueIdOf(Message message, TelegramMediaKind kind) => kind switch
    {
        TelegramMediaKind.Animation => message.Animation?.FileUniqueId,
        TelegramMediaKind.Document => message.Document?.FileUniqueId,
        TelegramMediaKind.Video => message.Video?.FileUniqueId,
        TelegramMediaKind.VideoNote => message.VideoNote?.FileUniqueId,
        TelegramMediaKind.Audio => message.Audio?.FileUniqueId,
        TelegramMediaKind.Voice => message.Voice?.FileUniqueId,
        TelegramMediaKind.Sticker => message.Sticker?.FileUniqueId,
        _ => null,
    };
}
