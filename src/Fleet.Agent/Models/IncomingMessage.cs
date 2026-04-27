namespace Fleet.Agent.Models;

public sealed record IncomingMessage
{
    public required long ChatId { get; init; }
    public required long UserId { get; init; }
    public required string Text { get; init; }
    public required string Sender { get; init; }
    public required bool IsGroupChat { get; init; }
    public string? ReplyToUsername { get; init; }
    public string? ReplyToText { get; init; }
    public bool IsBotMentioned { get; init; }
    public bool IsReplyToBot { get; init; }
    public bool IsNameMentioned { get; init; }
    public string StrippedText { get; init; } = "";

    // Image support — zero or more images (single photo or media group)
    public IReadOnlyList<MessageImage> Images { get; init; } = [];
    public bool HasImage => Images.Count > 0;

    // Document support — zero or more PDF documents
    public IReadOnlyList<MessageDocument> Documents { get; init; } = [];
    public bool HasDocument => Documents.Count > 0;

    /// <summary>
    /// True when the original Telegram message contained a media attachment
    /// (photo, voice, audio, video, video_note, or document).
    /// MessageRouter uses this to bypass the group mention gate for media messages.
    /// </summary>
    public bool HasMediaAttachment { get; init; }
}
