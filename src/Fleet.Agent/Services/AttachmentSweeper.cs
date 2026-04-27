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

    // Extension-to-hint-prefix map. Unknown extensions produce no hint (silent skip).
    private static readonly Dictionary<string, string> _hintPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".jpg",  "image attachment" },
        { ".jpeg", "image attachment" },
        { ".png",  "image attachment" },
        { ".pdf",  "document attachment" },
    };

    /// <summary>
    /// Builds hint lines for attachments persisted to disk, using an extension-aware prefix map.
    /// Images produce <c>[image attachment: path]</c>; PDFs produce <c>[document attachment: path]</c>.
    /// Returns an empty string when no attachments have a file path (persistence disabled or skipped).
    /// Unknown file extensions are silently skipped for forward compatibility.
    /// </summary>
    internal static string BuildHints(
        IReadOnlyList<MessageImage> images,
        IReadOnlyList<MessageDocument>? documents = null)
    {
        var imagePaths = images
            .Where(i => i.FilePath is not null)
            .Select(i => (Path: i.FilePath!, Ext: Path.GetExtension(i.FilePath!)));

        var docPaths = (documents ?? [])
            .Where(d => d.FilePath is not null)
            .Select(d => (Path: d.FilePath!, Ext: Path.GetExtension(d.FilePath!)));

        var hints = imagePaths.Concat(docPaths)
            .Select(f => _hintPrefixes.TryGetValue(f.Ext, out var prefix)
                ? $"[{prefix}: {f.Path}]"
                : null)
            .Where(h => h is not null);

        return string.Join("\n", hints);
    }
}
