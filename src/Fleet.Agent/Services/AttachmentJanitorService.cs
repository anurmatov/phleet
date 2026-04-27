using Fleet.Agent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Background service that periodically deletes attachment files older than
/// <c>Telegram:AttachmentRetentionHours</c>. Runs once at startup then every hour.
/// No-ops when <c>Telegram:PersistAttachments</c> is false or the directory does not exist.
/// </summary>
public sealed class AttachmentJanitorService : BackgroundService
{
    private readonly TelegramOptions _telegramConfig;
    private readonly ILogger<AttachmentJanitorService> _logger;

    public AttachmentJanitorService(
        IOptions<TelegramOptions> telegramConfig,
        ILogger<AttachmentJanitorService> logger)
    {
        _telegramConfig = telegramConfig.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_telegramConfig.PersistAttachments)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            Sweep();
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Sweep()
    {
        var dir = _telegramConfig.AttachmentDir;
        if (!Directory.Exists(dir))
            return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(_telegramConfig.AttachmentRetentionHours);
        var deleted = 0;

        foreach (var file in Directory.GetFiles(dir))
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
                _logger.LogWarning(ex, "Attachment janitor: failed to delete {File}", file);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Attachment janitor: deleted {Count} expired file(s) from {Dir}", deleted, dir);
    }
}
