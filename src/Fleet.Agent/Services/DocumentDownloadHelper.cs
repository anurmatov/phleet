using Fleet.Agent.Configuration;
using Fleet.Agent.Interfaces;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// The persisted attachment plus the bytes that were downloaded to produce it.
/// Voice transcription reuses <see cref="Bytes"/> instead of fetching the same file twice.
/// </summary>
internal sealed record MediaDownloadResult(MessageDocument Document, byte[] Bytes);

/// <summary>
/// Encapsulates the download-validate-persist logic for Telegram file attachments.
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
    /// Downloads, validates, and persists any file-backed Telegram media to
    /// <c>{AttachmentDir}/{chatId}-{messageId}-{index}{ext}</c>, returning both the
    /// attachment record and the downloaded bytes.
    /// Returns null when the kill switch is on, the file is oversized, or download fails —
    /// none of which is fatal: the caller still delivers the message's caption or placeholder.
    /// </summary>
    internal async Task<MediaDownloadResult?> DownloadMediaAsync(
        TelegramMediaFile media,
        long chatId,
        long messageId,
        int index,
        CancellationToken ct = default)
    {
        if (!_config.PersistAttachments)
            return null;

        var ext = AgentTransport.ExtractSafeExtension(media.FileName, media.DefaultExtension);
        var fileSize = media.FileSize;

        if (fileSize > _config.MaxDocumentBytes)
        {
            _logger.LogWarning(
                "{Kind} ({FileId}) pre-download size exceeds MaxDocumentBytes ({Size} > {Limit}), rejecting",
                media.Kind, media.FileId, fileSize, _config.MaxDocumentBytes);
            await _sendText(chatId, FileTooLargeMessage(fileSize));
            return null;
        }

        try
        {
            var bytes = await _downloader.DownloadAsync(media.FileId, ct);

            if (bytes.Length > _config.MaxDocumentBytes)
            {
                _logger.LogWarning(
                    "{Kind} ({FileId}) actual size exceeds MaxDocumentBytes ({Size} > {Limit}) after download, rejecting",
                    media.Kind, media.FileId, bytes.Length, _config.MaxDocumentBytes);
                await _sendText(chatId, FileTooLargeMessage(bytes.Length));
                return null;
            }

            string? filePath = null;
            try
            {
                Directory.CreateDirectory(_config.AttachmentDir);
                filePath = Path.Combine(_config.AttachmentDir, $"{chatId}-{messageId}-{index}{ext}");
                await File.WriteAllBytesAsync(filePath, bytes, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Kind} #{Index}: failed to persist to disk, continuing without file path", media.Kind, index);
                filePath = null;
            }

            if (filePath != null)
                AttachmentSweeper.SweepExpired(_config.AttachmentDir, _config.AttachmentRetentionHours, _logger);

            // Use extension-inferred MIME when Telegram omits or sends empty — never default to
            // "application/pdf" for unknown types (would cause Anthropic API 400 in ClaudeExecutor).
            var mimeType = string.IsNullOrEmpty(media.MimeType)
                ? AgentTransport.InferMimeType(ext)
                : media.MimeType;

            var document = new MessageDocument(media.FileId, mimeType, fileSize, media.FileName)
            {
                FilePath = filePath,
            };
            return new MediaDownloadResult(document, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Kind} ({FileId}) download failed, skipping", media.Kind, media.FileId);
            await _sendText(chatId, "(File download failed — please try again.)");
            return null;
        }
    }

    private string FileTooLargeMessage(long sizeBytes) =>
        $"(File too large — {sizeBytes / 1_048_576} MB exceeds the {_config.MaxDocumentBytes / 1_048_576} MB limit. Please send a smaller file.)";
}
