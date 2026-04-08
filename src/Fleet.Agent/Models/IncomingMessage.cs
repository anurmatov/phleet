namespace Fleet.Agent.Models;

public sealed class IncomingMessage
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

    // Image support
    public byte[]? ImageBytes { get; init; }
    public string? ImageMimeType { get; init; }
    public bool HasImage => ImageBytes is not null && ImageBytes.Length > 0;
}
