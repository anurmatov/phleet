namespace Fleet.Agent.Models;

/// <summary>A single document attachment within an incoming message.</summary>
public sealed record MessageDocument(
    string FileId,
    string MimeType,
    long FileSize,
    string? FileName)
{
    /// <summary>
    /// Absolute path to the persisted file on disk, set when
    /// <c>Telegram:PersistAttachments</c> is enabled and the file passed the size check.
    /// Null when persistence is disabled or the file exceeded the size limit.
    /// </summary>
    public string? FilePath { get; init; }
}
