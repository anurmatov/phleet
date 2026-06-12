using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// Lightweight helpers for attachment file management.
/// No background timer — sweep is called lazily on each photo write and once at startup.
/// </summary>
internal static class AttachmentSweeper
{
    /// <summary>
    /// Deletes attachment files in <paramref name="dir"/> whose last-write time is older
    /// than <paramref name="retentionHours"/>. Called opportunistically on each photo write
    /// (amortised over existing work) and once at startup to clean up files from prior runs.
    /// No-ops when the directory does not exist.
    /// </summary>
    internal static void SweepExpired(string dir, int retentionHours, ILogger logger)
    {
        if (!Directory.Exists(dir))
            return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(retentionHours);
        var deleted = 0;

        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Attachment sweep: failed to enumerate {Dir}", dir);
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Attachment sweep: failed to delete {File}", file);
            }
        }

        if (deleted > 0)
            logger.LogInformation("Attachment sweep: deleted {Count} expired file(s) from {Dir}", deleted, dir);
    }

    // Maps a persisted file's extension to an attachment hint category.
    // ".jpg"/".jpeg"/".png" → "image", ".pdf" → "document", everything else → "file".
    private static string ClassifyAttachment(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" => "image",
            ".pdf" => "document",
            _ => "file",
        };
    }

    /// <summary>
    /// Builds hint lines for attachments persisted to disk.
    /// Images produce <c>[image attachment: path]</c>; PDFs produce <c>[document attachment: path]</c>;
    /// all other extensions produce <c>[file attachment: path]</c> so the agent can use Read/Bash.
    /// Returns an empty string when no attachments have a file path (persistence disabled or skipped).
    /// </summary>
    internal static string BuildHints(
        IReadOnlyList<MessageImage> images,
        IReadOnlyList<MessageDocument>? documents = null)
    {
        var imageHints = images
            .Where(i => i.FilePath is not null)
            .Select(i => $"[image attachment: {i.FilePath!}]");

        var docHints = (documents ?? [])
            .Where(d => d.FilePath is not null)
            .Select(d => $"[{ClassifyAttachment(d.FilePath!)} attachment: {d.FilePath!}]");

        return string.Join("\n", imageHints.Concat(docHints));
    }
}
