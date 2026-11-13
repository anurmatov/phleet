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
/// One Telegram message in, one <see cref="IncomingMessage"/> out to the router: downloads its
/// photo or file, transcribes a voice message or video note, answers the <c>/tts</c> command,
/// journals the raw message, and holds an album's photos until the album is complete.
/// </summary>
internal sealed class TelegramMessageIntake(
    ITelegramShell shell,
    AgentOptions agentConfig,
    TelegramOptions telegramConfig,
    VoiceTranscriptionService voiceTranscription,
    TtsService tts,
    TurnBindingPublisher? turnBindings,
    TelegramJournalRecorder journalRecorder,
    ILogger logger)
{
    private readonly AgentOptions _agentConfig = agentConfig;
    private readonly TelegramOptions _telegramConfig = telegramConfig;
    private readonly VoiceTranscriptionService _voiceTranscription = voiceTranscription;
    private readonly TtsService _tts = tts;
    private readonly TurnBindingPublisher? _turnBindings = turnBindings;
    private readonly TelegramJournalRecorder _journalRecorder = journalRecorder;
    private readonly ILogger _logger = logger;

    private readonly MediaGroupBuffer _mediaGroupBuffer = new(telegramConfig.MaxGroupBufferMs, logger);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _groupSizeCapped = new();

    private ITelegramBotClient? Bot => shell.Bot;
    private string BotUsername => shell.BotUsername;

    internal async Task OnMessage(Message message, UpdateType type)
    {
        if (type != UpdateType.Message) return;

        // Support text messages and media messages (with optional caption).
        // Every file-backed type except photo is normalised to a single descriptor so one
        // download / size-gate / persist / retention path serves documents, video, video
        // notes, audio, voice, animations and stickers. Forwarded messages carry the same
        // media payload plus ForwardOrigin metadata, so they map identically to direct ones.
        var isPhoto = message.Photo is { Length: > 0 };
        var media = TelegramMediaMapper.TryMap(message);
        // Voice messages and round video notes are both spoken bubbles, so both are
        // transcribed (#327). The whisper service writes the upload to a temp file and lets
        // ffmpeg decode it, so the container does not matter to it. Nothing else is
        // transcribed: Video and Audio have different size and intent characteristics.
        var isSpokenMessage = media?.Kind is TelegramMediaKind.Voice or TelegramMediaKind.VideoNote;
        var isMediaAttachment = isPhoto || media is not null;
        var text = message.Text ?? message.Caption ?? "";

        if (!isMediaAttachment && string.IsNullOrEmpty(message.Text)) return;

        // TTS trigger: /tts command as a reply to any message → synthesize and send replied-to message as voice.
        // A spoken bubble is content, not a command — and this branch returns, so letting one
        // in here would swallow the message before transcription ever runs. Video notes carry
        // no caption today, so including them changes nothing now and keeps it that way if
        // that ever changes.
        if (_tts.IsEnabled && !isSpokenMessage
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
                        await Bot!.SendChatAction(message.Chat.Id, ChatAction.RecordVoice);
                        var audioBytes = await _tts.SynthesizeAsync(sourceText);
                        if (audioBytes is { Length: > 0 })
                        {
                            using var ms = new System.IO.MemoryStream(audioBytes);
                            var voice = await Bot!.SendVoice(
                                message.Chat.Id,
                                new Telegram.Bot.Types.InputFileStream(ms, "response.ogg"),
                                replyParameters: new Telegram.Bot.Types.ReplyParameters { MessageId = replied.MessageId });
                            _journalRecorder.SentAlone(message.Chat.Id, voice, null, JournalTextFormat.Plain, new JournalMediaItem(
                                JournalAttachmentKind.Voice, "audio/ogg", audioBytes.LongLength, "response.ogg",
                                voice.Voice?.FileUniqueId, Bytes: audioBytes, FileId: voice.Voice?.FileId));
                            _logger.LogInformation("TTS voice sent for message {MsgId} ({Chars} chars)", replied.MessageId, sourceText.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TTS voice send failed for message {MsgId}", replied.MessageId);
                    }
                });
            }

            // The command itself is a message a human sent: journaled like any other (#377).
            _journalRecorder.Received(message, transcript: null, photoPath: null, mediaPath: null);
            return;
        }

        var chatId = message.Chat.Id;
        var isGroupChat = message.Chat.Type is ChatType.Group or ChatType.Supergroup;

        // Download photo if present (largest available size, with size check and one retry)
        MessageImage? downloadedImage = null;
        if (isPhoto)
        {
            var largest = message.Photo!.OrderByDescending(p => p.FileSize ?? 0).First();
            downloadedImage = await DownloadPhotoAsync(largest, chatId, message.MessageId, photoIndex: 1);
        }

        // Download any file-backed media through the shared attachment pipeline. A null
        // result (persistence off, oversize, or download failure) is never fatal — the
        // message still reaches the agent with its caption or placeholder below.
        MediaDownloadResult? downloadedMedia = null;
        if (media is not null)
            downloadedMedia = await shell.DownloadHelper.DownloadMediaAsync(media, chatId, message.MessageId, index: 1);

        var downloadedDocument = downloadedMedia?.Document;

        // Set only when speech-to-text actually returned a transcript. A spoken message that
        // failed to transcribe, or arrived while the service is disabled, must NOT be marked
        // — a false marker would tell the agent to distrust text the user actually typed.
        var inputSource = MessageInputSource.Typed;
        string? transcript = null;

        // Transcribe voice messages and video notes if the whisper service is configured
        if (isSpokenMessage && _voiceTranscription.IsEnabled)
        {
            try
            {
                // Immediate feedback — let user know we received the spoken message
                await Bot!.SendChatAction(chatId, ChatAction.Typing);

                // Reuse the bytes the attachment pipeline already fetched. Only fetch
                // separately when persistence is off; a file the pipeline rejected as
                // oversize or failed to download must not be re-fetched here.
                var audioBytes = downloadedMedia?.Bytes;
                if (audioBytes is null && !_telegramConfig.PersistAttachments)
                {
                    using var ms = new System.IO.MemoryStream();
                    await Bot!.GetInfoAndDownloadFile(media!.FileId, ms);
                    audioBytes = ms.ToArray();
                }

                // The receiving end is suffix-agnostic, but mislabelling an MP4 as audio/ogg
                // is a lie the next reader of this code would have to unpick.
                var isVideoNote = media!.Kind == TelegramMediaKind.VideoNote;
                var uploadName = isVideoNote ? "video.mp4" : "voice.ogg";
                var uploadType = isVideoNote ? "video/mp4" : "audio/ogg";

                var transcribed = audioBytes is null
                    ? null
                    : await _voiceTranscription.TranscribeAsync(audioBytes, uploadName, uploadType);

                if (transcribed is not null)
                {
                    text = transcribed;
                    transcript = transcribed;
                    // A video-note transcript carries the same speech-to-text risk as a voice
                    // one, so it carries the same marker.
                    inputSource = MessageInputSource.VoiceTranscription;
                    _logger.LogInformation("{Kind} transcribed ({Chars} chars) from {Sender}",
                        media.Kind, text.Length, message.From?.Username ?? "unknown");

                    // Echo transcription back so user can verify whisper got it right. Not journaled
                    // (#394): the inbound record already holds the transcript.
                    await Bot!.SendMessage(
                        chatId,
                        $"🎤 {transcribed}",
                        replyParameters: new Telegram.Bot.Types.ReplyParameters { MessageId = message.MessageId });
                }
                else
                {
                    // Transcription unavailable — fall through so the persisted file
                    // and a readable placeholder still reach the agent.
                    _logger.LogWarning("Transcription returned no text for {Kind} message from {Sender}",
                        media!.Kind, message.From?.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to transcribe {Kind} message from {Sender}",
                    media!.Kind, message.From?.Username);
            }
        }

        // For non-photo media with no caption, synthesize a readable placeholder so the
        // agent receives a meaningful description of what was shared.
        if (string.IsNullOrEmpty(text) && media is not null)
            text = TelegramMediaMapper.DescribePlaceholder(media, message.Sticker?.Emoji);

        var isMentioned = BotUsername.Length > 0
            && text.Contains($"@{BotUsername}", StringComparison.OrdinalIgnoreCase);
        var isReplyToMe = message.ReplyToMessage?.From?.Username is { } replyUser
            && replyUser.Equals(BotUsername, StringComparison.OrdinalIgnoreCase);
        var isNameMentioned = _agentConfig.ShortName.Length > 0
            && text.Contains(_agentConfig.ShortName, StringComparison.OrdinalIgnoreCase);

        var stripped = BotUsername.Length > 0
            ? text.Replace($"@{BotUsername}", "", StringComparison.OrdinalIgnoreCase).Trim()
            : text.Trim();

        var sender = message.From?.Username is { } u ? $"@{u}" : message.From?.FirstName ?? "Unknown";

        // Record platform scope before routing, never from prompt text.
        if (TelegramJournalRecorder.ChatTypeName(message.Chat.Type) is { } observedKind)
            _turnBindings?.ObserveChat(chatId, Bot?.BotId ?? 0, observedKind);

        // Build the base IncomingMessage (no images/documents yet — filled in below)
        var baseMsg = new IncomingMessage
        {
            ChatId = chatId,
            UserId = message.From?.Id ?? 0,
            FromIsBot = message.From?.IsBot ?? true,
            HasSenderChat = message.SenderChat is not null,
            Text = text,
            Sender = sender,
            IsGroupChat = isGroupChat,
            TelegramMessageId = message.MessageId,
            ReplyToTelegramMessageId = message.ReplyToMessage?.MessageId is { } rtm ? (long)rtm : null,
            IsBotMentioned = isMentioned,
            IsReplyToBot = isReplyToMe,
            IsNameMentioned = isNameMentioned,
            StrippedText = stripped,
            HasMediaAttachment = isMediaAttachment,
            InputSource = inputSource,
            // Channel anchor fields — used by PromptAssembler to emit [channel: ...] tags
            ChatTitle = message.Chat.Title,
            ChatUsername = message.Chat.Username,
            ChatFirstName = message.Chat.FirstName,
        };

        // One journal record per raw Telegram message, from the message itself: the raw text or
        // caption and the downloaded files, never the placeholder, the image prompt or the hints
        // added below. An album is one record per photo, tied by its media group id (#377).
        _journalRecorder.Received(
            message, transcript, downloadedImage?.FilePath, downloadedDocument?.FilePath,
            photoDecline: isPhoto && downloadedImage is null
                ? TelegramJournalRecorder.MediaAbsence(TelegramJournalRecorder.PhotoSizeOf(message), _telegramConfig.MaxImageBytes)
                : null,
            mediaDecline: media is not null && downloadedMedia is null
                ? TelegramJournalRecorder.MediaAbsence(media.FileSize > 0 ? media.FileSize : null, _telegramConfig.MaxDocumentBytes)
                : null);

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
                await shell.RouteAsync(flushedMsg);
            };

            // TryAddPhotoWithCapAsync atomically checks and adds under the same lock.
            var accepted = await _mediaGroupBuffer.TryAddPhotoWithCapAsync(
                groupKey, downloadedImage, baseMsg, _telegramConfig.MaxImagesPerGroup, flushHandler);

            if (!accepted)
            {
                // Warn once when cap is first exceeded, then keep resetting the debounce
                if (_groupSizeCapped.TryAdd(groupKey, true))
                    await shell.SendTextAsync(chatId, $"({_telegramConfig.MaxImagesPerGroup} images received — only the first {_telegramConfig.MaxImagesPerGroup} will be processed.)");

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
        await shell.RouteAsync(msg);
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
            await shell.SendTextAsync(chatId, $"(Image #{photoIndex} exceeded size limit, skipped.)");
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(500);
                using var ms = new System.IO.MemoryStream();
                await Bot!.GetInfoAndDownloadFile(photo.FileId, ms);
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
                await shell.SendTextAsync(chatId, $"(Image #{photoIndex} download failed, skipped.)");
            }
        }
        return null;
    }
}
