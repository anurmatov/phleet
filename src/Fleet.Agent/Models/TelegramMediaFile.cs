namespace Fleet.Agent.Models;

/// <summary>
/// Telegram message types that are backed by a downloadable Bot API file and are
/// persisted through the shared attachment pipeline. Photos are deliberately absent:
/// they keep their own download path because they also feed provider vision blocks.
/// </summary>
internal enum TelegramMediaKind
{
    Document,
    Video,
    VideoNote,
    Audio,
    Voice,
    Animation,
    Sticker,
}

/// <summary>
/// Provider-agnostic descriptor for a single downloadable Telegram file.
/// Normalises the differing shapes of <c>Document</c>, <c>Video</c>, <c>VideoNote</c>,
/// <c>Audio</c>, <c>Voice</c>, <c>Animation</c> and <c>Sticker</c> so one download,
/// size-gate, safe-filename and retention path serves all of them.
/// </summary>
/// <param name="Kind">Which Telegram media family this file came from.</param>
/// <param name="FileId">Bot API file id used to fetch the bytes.</param>
/// <param name="FileName">Telegram-supplied filename, when the type carries one. Never trusted as a path.</param>
/// <param name="MimeType">Telegram-supplied MIME type, when the type carries one.</param>
/// <param name="FileSize">Telegram-reported size in bytes; 0 when Telegram omits it.</param>
/// <param name="DefaultExtension">
/// Extension used when <paramref name="FileName"/> yields none. Types without a filename
/// (voice, video note, sticker) rely on this entirely, so a wrong value here is the
/// difference between a readable file and an opaque <c>.bin</c>.
/// </param>
internal sealed record TelegramMediaFile(
    TelegramMediaKind Kind,
    string FileId,
    string? FileName,
    string? MimeType,
    long FileSize,
    string DefaultExtension);
