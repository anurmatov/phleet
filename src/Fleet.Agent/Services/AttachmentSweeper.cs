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

    /// <summary>
    /// Builds the <c>[image attachment: path]</c> hint lines for images persisted to disk.
    /// Returns an empty string when no images have a file path (persistence disabled or skipped).
    /// </summary>
    internal static string BuildHints(IReadOnlyList<MessageImage> images)
    {
        var paths = images
            .Where(i => i.FilePath is not null)
            .Select(i => $"[image attachment: {i.FilePath}]");
        return string.Join("\n", paths);
    }
}
