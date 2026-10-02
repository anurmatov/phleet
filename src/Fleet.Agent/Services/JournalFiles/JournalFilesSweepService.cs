using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Fleet.Agent.Services.JournalFiles;

public sealed class JournalFilesSweepService(JournalFileStore files, ILogger<JournalFilesSweepService> logger,
    TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public override Task StartAsync(CancellationToken ct) { Sweep(); return base.StartAsync(ct); }
    private void Sweep()
    {
        try { files.Sweep(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning("Journal files sweep failed: {type}", ex.GetType().Name); }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), _time);
        try { while (await timer.WaitForNextTickAsync(ct)) Sweep(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
