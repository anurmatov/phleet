using System.Collections.Concurrent;
using System.Threading.Channels;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

public sealed class FileWatcherService(
    MemoryService memoryService,
    MemoryFileStore fileStore,
    VectorStore vectorStore,
    IOptions<StorageOptions> storageOptions,
    ILogger<FileWatcherService> logger) : BackgroundService
{
    private readonly string _basePath = storageOptions.Value.Path;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(storageOptions.Value.PollingIntervalSeconds);
    private readonly Channel<FileChangeEvent> _channel = Channel.CreateBounded<FileChangeEvent>(1000);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _debounce = new();
    private readonly ConcurrentDictionary<string, DateTime> _knownFiles = new();
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure directories and Qdrant collection exist
        fileStore.EnsureDirectories();
        await vectorStore.EnsureCollectionAsync(stoppingToken);

        // Clean up stale temp files from any prior crashed write operations
        fileStore.CleanStaleTempFiles();

        // Full index on startup
        await FullIndexAsync(stoppingToken);

        // Start watching for changes
        using var watcher = new FileSystemWatcher(_basePath)
        {
            IncludeSubdirectories = true,
            Filter = "*.md",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        watcher.Created += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Created);
        watcher.Changed += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Modified);
        watcher.Deleted += (_, e) => EnqueueChange(e.FullPath, FileChangeType.Deleted);
        watcher.Renamed += (_, e) =>
        {
            EnqueueChange(e.OldFullPath, FileChangeType.Deleted);
            EnqueueChange(e.FullPath, FileChangeType.Created);
        };

        watcher.EnableRaisingEvents = true;
        logger.LogInformation("File watcher started on {Path}", _basePath);

        // Start polling loop for Docker volume mount changes
        _ = Task.Run(() => PollForChangesAsync(stoppingToken), stoppingToken);

        // Process change events
        await foreach (var change in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessChangeAsync(change, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing file change: {Path} ({Type})", change.FilePath, change.ChangeType);
            }
        }
    }

    private async Task FullIndexAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting full index of {Path}", _basePath);

        var allDocs = await fileStore.ListAllAsync();
        var existingKeys = await vectorStore.GetAllFilePathKeysAsync(ct);

        var filePathsOnDisk = new HashSet<string>();

        var indexed = 0;
        foreach (var doc in allDocs)
        {
            filePathsOnDisk.Add(doc.FilePath);
            try
            {
                await memoryService.IndexFileAsync(doc.FilePath, ct);
                indexed++;

                // Track file timestamp for polling
                var fileInfo = new FileInfo(doc.FilePath);
                if (fileInfo.Exists)
                    _knownFiles[doc.FilePath] = fileInfo.LastWriteTimeUtc;
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex, "Corrupt memory file skipped during full scan: {Path}", doc.FilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to index {Path} during full scan", doc.FilePath);
            }
        }

        // Remove orphaned Qdrant points (files that no longer exist)
        var orphans = existingKeys.Except(filePathsOnDisk).ToList();
        foreach (var orphan in orphans)
        {
            try
            {
                await memoryService.RemoveFromIndexAsync(orphan, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove orphan {Path}", orphan);
            }
        }

        logger.LogInformation("Full index complete: {Indexed} indexed, {Orphans} orphans removed", indexed, orphans.Count);
    }

    private async Task PollForChangesAsync(CancellationToken ct)
    {
        logger.LogInformation("Polling loop started (interval: {Seconds}s)", _pollingInterval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollingInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var currentFiles = new HashSet<string>();
                var created = 0;
                var modified = 0;
                var deleted = 0;

                // Scan all .md files on disk
                foreach (var filePath in Directory.EnumerateFiles(_basePath, "*.md", SearchOption.AllDirectories))
                {
                    if (filePath.Contains("/_archived/") || filePath.Contains("\\_archived\\"))
                        continue;

                    // Skip temp files (belt-and-suspenders; *.md glob already excludes *.md.tmp.*)
                    if (Path.GetFileName(filePath).Contains(".tmp."))
                        continue;

                    currentFiles.Add(filePath);
                    var lastWrite = File.GetLastWriteTimeUtc(filePath);

                    if (_knownFiles.TryGetValue(filePath, out var tracked))
                    {
                        if (lastWrite != tracked)
                        {
                            // Modified
                            _knownFiles[filePath] = lastWrite;
                            _channel.Writer.TryWrite(new FileChangeEvent(filePath, FileChangeType.Modified));
                            modified++;
                        }
                    }
                    else
                    {
                        // New file
                        _knownFiles[filePath] = lastWrite;
                        _channel.Writer.TryWrite(new FileChangeEvent(filePath, FileChangeType.Created));
                        created++;
                    }
                }

                // Detect deleted files
                foreach (var tracked in _knownFiles.Keys)
                {
                    if (!currentFiles.Contains(tracked))
                    {
                        _knownFiles.TryRemove(tracked, out _);
                        _channel.Writer.TryWrite(new FileChangeEvent(tracked, FileChangeType.Deleted));
                        deleted++;
                    }
                }

                if (created > 0 || modified > 0 || deleted > 0)
                    logger.LogInformation("Poll detected changes: {Created} new, {Modified} modified, {Deleted} deleted", created, modified, deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Error during polling scan");
            }
        }
    }

    private void EnqueueChange(string filePath, FileChangeType changeType)
    {
        // Skip _archived directory
        if (filePath.Contains("/_archived/") || filePath.Contains("\\_archived\\"))
            return;

        // Skip temp files from atomic write operations (belt-and-suspenders; they don't
        // end in .md so the FileSystemWatcher filter already excludes them)
        if (Path.GetFileName(filePath).Contains(".tmp."))
            return;

        // Debounce: only queue if enough time has passed since last event for this path
        var now = DateTimeOffset.UtcNow;
        if (_debounce.TryGetValue(filePath, out var lastEvent) && now - lastEvent < DebounceInterval)
        {
            _debounce[filePath] = now;
            return;
        }

        _debounce[filePath] = now;
        _channel.Writer.TryWrite(new FileChangeEvent(filePath, changeType));
    }

    private async Task ProcessChangeAsync(FileChangeEvent change, CancellationToken ct)
    {
        // Additional debounce: wait a bit then check the latest timestamp
        await Task.Delay(DebounceInterval, ct);

        switch (change.ChangeType)
        {
            case FileChangeType.Created:
            case FileChangeType.Modified:
                logger.LogInformation("File {Type}: {Path}", change.ChangeType, change.FilePath);
                try
                {
                    await memoryService.IndexFileAsync(change.FilePath, ct);
                }
                catch (InvalidDataException ex)
                {
                    logger.LogError(ex, "Corrupt memory file skipped during watcher event: {Path}", change.FilePath);
                    // Update known files so the polling loop does not re-enqueue this corrupt file on every cycle.
                    var corruptInfo = new FileInfo(change.FilePath);
                    if (corruptInfo.Exists)
                        _knownFiles[change.FilePath] = corruptInfo.LastWriteTimeUtc;
                    break;
                }
                // Update known files snapshot
                var fileInfo = new FileInfo(change.FilePath);
                if (fileInfo.Exists)
                    _knownFiles[change.FilePath] = fileInfo.LastWriteTimeUtc;
                break;

            case FileChangeType.Deleted:
                logger.LogInformation("File deleted: {Path}", change.FilePath);
                await memoryService.RemoveFromIndexAsync(change.FilePath, ct);
                _knownFiles.TryRemove(change.FilePath, out _);
                break;
        }
    }

    private record FileChangeEvent(string FilePath, FileChangeType ChangeType);

    private enum FileChangeType
    {
        Created,
        Modified,
        Deleted
    }
}
