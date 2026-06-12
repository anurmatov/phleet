using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// Encapsulates the download-validate-persist logic for Telegram document attachments.
/// Extracted from <c>AgentTransport.DownloadDocumentAsync</c> so it can be unit-tested
/// via a fake <see cref="IDocumentDownloader"/> without constructing the full transport.
/// </summary>
internal sealed class DocumentDownloadHelper
{
    private readonly IDocumentDownloader _downloader;
    private readonly TelegramOptions _config;
    private readonly Func<long, string, Task> _sendText;
    private readonly ILogger _logger;

    internal DocumentDownloadHelper(
        IDocumentDownloader downloader,
        TelegramOptions config,
        Func<long, string, Task> sendText,
        ILogger logger)
    {
        _downloader = downloader;
        _config = config;
        _sendText = sendText;
        _logger = logger;
    }

    /// <summary>
    /// Downloads, validates, and persists a Telegram document to
    /// <c>{AttachmentDir}/{chatId}-{messageId}-{docIndex}{ext}</c>.
    /// Returns null when the kill switch is on, the file is oversized, or download fails.
    /// </summary>
    internal async Task<MessageDocument?> DownloadAsync(
        Telegram.Bot.Types.Document document,
        long chatId,
        long messageId,
        int docIndex,
        CancellationToken ct = default)
    {
        if (!_config.PersistAttachments)
            return null;

        var ext = AgentTransport.ExtractSafeExtension(document.FileName);
        var fileSize = document.FileSize ?? 0;

        if (fileSize > _config.MaxDocumentBytes)
        {
            _logger.LogWarning(
                "Document ({FileId}) pre-download size exceeds MaxDocumentBytes ({Size} > {Limit}), rejecting",
                document.FileId, fileSize, _config.MaxDocumentBytes);
            await _sendText(chatId, FileTooLargeMessage(fileSize));
            return null;
        }

        try
        {
            var bytes = await _downloader.DownloadAsync(document.FileId, ct);

            if (bytes.Length > _config.MaxDocumentBytes)
            {
                _logger.LogWarning(
                    "Document ({FileId}) actual size exceeds MaxDocumentBytes ({Size} > {Limit}) after download, rejecting",
                    document.FileId, bytes.Length, _config.MaxDocumentBytes);
                await _sendText(chatId, FileTooLargeMessage(bytes.Length));
                return null;
            }

            string? filePath = null;
            try
            {
                Directory.CreateDirectory(_config.AttachmentDir);
                filePath = Path.Combine(_config.AttachmentDir, $"{chatId}-{messageId}-{docIndex}{ext}");
                await File.WriteAllBytesAsync(filePath, bytes, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Document #{Index}: failed to persist to disk, continuing without file path", docIndex);
                filePath = null;
            }

            if (filePath != null)
                AttachmentSweeper.SweepExpired(_config.AttachmentDir, _config.AttachmentRetentionHours, _logger);

            // Use extension-inferred MIME when Telegram omits or sends empty — never default to
            // "application/pdf" for unknown types (would cause Anthropic API 400 in ClaudeExecutor).
            var mimeType = string.IsNullOrEmpty(document.MimeType)
                ? AgentTransport.InferMimeType(ext)
                : document.MimeType;

            return new MessageDocument(document.FileId, mimeType, fileSize, document.FileName)
            {
                FilePath = filePath,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Document ({FileId}) download failed, skipping", document.FileId);
            await _sendText(chatId, "(File download failed — please try again.)");
            return null;
        }
    }

    private string FileTooLargeMessage(long sizeBytes) =>
        $"(File too large — {sizeBytes / 1_048_576} MB exceeds the {_config.MaxDocumentBytes / 1_048_576} MB limit. Please send a smaller file.)";
}
