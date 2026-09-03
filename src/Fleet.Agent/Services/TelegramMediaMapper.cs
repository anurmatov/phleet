using Fleet.Agent.Models;
using Telegram.Bot.Types;

namespace Fleet.Agent.Services;

/// <summary>
/// Maps an incoming Telegram <see cref="Message"/> onto a single
/// <see cref="TelegramMediaFile"/> descriptor so every file-backed media type flows
/// through one download/persist path.
///
/// Forwarded messages carry the identical media payload plus <c>ForwardOrigin</c>
/// metadata, so nothing here inspects forward state — a forwarded video maps exactly
/// like a directly sent one, which is what makes the two behave the same downstream.
/// </summary>
internal static class TelegramMediaMapper
{
    /// <summary>
    /// Returns the descriptor for the message's downloadable file, or null when the
    /// message carries no supported file (text, photo, or a non-file event such as a
    /// location, contact, poll or venue share).
    ///
    /// Photos are excluded on purpose: they keep <c>DownloadPhotoAsync</c>, which also
    /// retains the bytes for provider vision blocks.
    /// </summary>
    internal static TelegramMediaFile? TryMap(Message message)
    {
        // Animation is checked before Document because the Bot API sets `document`
        // alongside `animation` for backward compatibility — checking Document first
        // would misclassify every GIF.
        if (message.Animation is { } animation)
            return new TelegramMediaFile(
                TelegramMediaKind.Animation,
                animation.FileId,
                animation.FileName,
                animation.MimeType,
                animation.FileSize ?? 0,
                ".mp4");

        if (message.Video is { } video)
            return new TelegramMediaFile(
                TelegramMediaKind.Video,
                video.FileId,
                video.FileName,
                video.MimeType,
                video.FileSize ?? 0,
                ".mp4");

        // Video notes and voice messages carry no filename at all, so their default
        // extension is the only thing that makes the persisted file openable.
        if (message.VideoNote is { } videoNote)
            return new TelegramMediaFile(
                TelegramMediaKind.VideoNote,
                videoNote.FileId,
                FileName: null,
                MimeType: null,
                videoNote.FileSize ?? 0,
                ".mp4");

        if (message.Audio is { } audio)
            return new TelegramMediaFile(
                TelegramMediaKind.Audio,
                audio.FileId,
                audio.FileName,
                audio.MimeType,
                audio.FileSize ?? 0,
                ".mp3");

        if (message.Voice is { } voice)
            return new TelegramMediaFile(
                TelegramMediaKind.Voice,
                voice.FileId,
                FileName: null,
                voice.MimeType,
                voice.FileSize ?? 0,
                ".oga");

        if (message.Sticker is { } sticker)
            return new TelegramMediaFile(
                TelegramMediaKind.Sticker,
                sticker.FileId,
                FileName: null,
                MimeType: null,
                sticker.FileSize ?? 0,
                StickerExtension(sticker));

        if (message.Document is { } document)
            return new TelegramMediaFile(
                TelegramMediaKind.Document,
                document.FileId,
                document.FileName,
                document.MimeType,
                document.FileSize ?? 0,
                ".bin");

        return null;
    }

    /// <summary>
    /// Readable stand-in used as the message text when the media arrived without a caption,
    /// so the agent still receives a description of what was shared.
    /// </summary>
    internal static string DescribePlaceholder(TelegramMediaFile media, string? stickerEmoji = null) => media.Kind switch
    {
        TelegramMediaKind.Audio => "(audio message)",
        TelegramMediaKind.VideoNote => "(video note)",
        TelegramMediaKind.Video => "(video message)",
        TelegramMediaKind.Voice => "(voice message)",
        TelegramMediaKind.Animation => "(animation)",
        TelegramMediaKind.Sticker => string.IsNullOrEmpty(stickerEmoji) ? "(sticker)" : $"(sticker {stickerEmoji})",
        _ => media.FileName is { } fn ? $"(document: {fn})" : "(document)",
    };

    // Static stickers are WebP, animated ones are gzipped Lottie (.tgs), video ones are WebM.
    private static string StickerExtension(Sticker sticker)
    {
        if (sticker.IsVideo) return ".webm";
        if (sticker.IsAnimated) return ".tgs";
        return ".webp";
    }
}
