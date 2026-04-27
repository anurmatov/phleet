namespace Fleet.Agent.Models;

/// <summary>A single image attachment within an incoming message.</summary>
public sealed record MessageImage(byte[] Bytes, string MimeType)
{
    /// <summary>
    /// Absolute path to the persisted file on disk, set when
    /// <c>Telegram:PersistAttachments</c> is enabled. Null when persistence
    /// is disabled or the image exceeded the size limit.
    /// </summary>
    public string? FilePath { get; init; }
}
