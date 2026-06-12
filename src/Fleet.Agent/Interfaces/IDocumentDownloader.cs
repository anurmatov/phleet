namespace Fleet.Agent.Interfaces;

/// <summary>
/// Abstracts the Telegram file-download network call so the download+persist
/// logic in <see cref="Fleet.Agent.Services.DocumentDownloadHelper"/> can be
/// unit-tested without a live <see cref="Telegram.Bot.TelegramBotClient"/>.
/// </summary>
internal interface IDocumentDownloader
{
    /// <summary>Downloads the raw bytes for <paramref name="fileId"/>.</summary>
    Task<byte[]> DownloadAsync(string fileId, CancellationToken ct);
}
