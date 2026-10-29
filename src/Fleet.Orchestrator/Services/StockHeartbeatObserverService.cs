using Fleet.Orchestrator.Configuration;
using Fleet.Shared.Security;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>Optional, independently connected observer. Failure never stops ordinary consumption.</summary>
public sealed class StockHeartbeatObserverService(
    IOptions<RabbitMqOptions> options,
    AgentRegistry registry,
    AgentConfigPublisherService publisher,
    IConfiguration configuration,
    StockRuntimeMutationHold hold,
    ILogger<HeartbeatConsumerService> consumerLogger,
    ILogger<StockHeartbeatObserverService> logger) : BackgroundService
{
    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>>? Connect { get; init; }

    /// <summary>How often managed lanes (<c>StockOpenShell:AuthorityDirectory</c>, else <c>&lt;ConfigDir&gt;/managed/authority</c>) are reconciled.</summary>
    internal TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(30);

    private sealed record Lane(StockActiveRecord Source, HeartbeatConsumerService? Receiver);

    // Touched only by the single directory loop (or a test driving ReconcileAsync without starting the service).
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Agents with a lane, and whether that lane's receiver started.</summary>
    internal IReadOnlyDictionary<string, bool> Lanes => _lanes.ToDictionary(l => l.Key, l => l.Value.Receiver is not null);

    /// <summary>Same derivation as the mutation hold, so shipped ConfigDir-only wiring still observes managed lanes.</summary>
    internal string? ManagedDirectory => configuration["StockOpenShell:AuthorityDirectory"] ?? hold.ManagedDirectory;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = ManagedDirectory;
        return string.IsNullOrEmpty(directory) ? ObserveEnrolledAsync(stoppingToken)
            : Task.WhenAll(ObserveEnrolledAsync(stoppingToken), ObserveManagedAsync(directory, stoppingToken));
    }

    /// <summary>The single-file enrollment (<c>AuthorityFile</c>/<c>BrokerPasswordFile</c>), unchanged.</summary>
    private async Task ObserveEnrolledAsync(CancellationToken stoppingToken)
    {
        HeartbeatConsumerService? receiver = null;
        try
        {
            var identity = StockBrokerIdentity.Optional(configuration["StockOpenShell:AuthorityFile"],
                configuration["StockOpenShell:BrokerPasswordFile"], "observer");
            if (identity is null) return;
            // Select the enrolled endpoint without mutating the ordinary consumer's options.
            // Configure still checks this exact host against fresh authority before connecting.
            var observerOptions = Options.Create(new RabbitMqOptions
            {
                Host = identity.BrokerHost,
                Exchange = options.Value.Exchange
            });
            receiver = new(observerOptions, registry, publisher, configuration, consumerLogger, hold, identity) { Connect = Connect };
            await receiver.StartAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Do not expose credentials or exception details, retry, or fall back to the default broker.
            logger.LogWarning("Stock observer stopped; manual reconciliation required");
        }
        finally
        {
            if (receiver is not null)
            {
                try { await receiver.DisposeAsync(); }
                catch (Exception) { logger.LogWarning("Stock observer cleanup failed; manual reconciliation required"); }
            }
        }
    }

    /// <summary>One observer receiver per active managed epoch, each in its own vhost as <c>-observer</c>.</summary>
    private async Task ObserveManagedAsync(string directory, CancellationToken stoppingToken)
    {
        // Review V3: once at startup, before any lane needs it, move pre-W4 observer passwords out of the shared mount.
        MigrateLegacyObserverPasswords(directory, logger);
        try
        {
            while (true)
            {
                await ReconcileAsync(directory, stoppingToken);
                await Task.Delay(ReconcileInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await StopLanesAsync(); }
    }

    private const string ObserverSuffix = ".observer.password";

    /// <summary>
    /// Review V3: agents enrolled before W4 kept <c>&lt;agent&gt;.observer.password</c> in the shared authority directory
    /// that Telegram, Temporal and the Bridge mount. Move each one, once, into the orchestrator-only 0700 observer
    /// sibling as a 0600 file (atomic rename; copy+fsync+delete across devices). Links are refused and never followed:
    /// a linked authority/observer directory stops the migration, a linked entry is left in place. When the sibling
    /// already holds a newer password the stale shared copy is deleted. Returns how many entries were moved or removed.
    /// Review X5: each entry is handled on its own, so a race (a concurrent apply writing the sibling, the shared copy
    /// vanishing) or a failure on one entry never skips the rest; the directory is re-enumerated, up to
    /// <see cref="LegacyMigrationPasses"/> times, until no unhandled legacy entry remains (catching files created
    /// mid-move); and a cross-device temp is always removed.
    /// </summary>
    internal static int MigrateLegacyObserverPasswords(string authorityDirectory, ILogger logger, LegacyMigrationSeams? seams = null)
    {
        var migrated = 0;
        try
        {
            var authority = new DirectoryInfo(authorityDirectory);
            if (!authority.Exists) return 0;
            if (authority.LinkTarget is not null) { logger.LogWarning("Legacy observer password migration refused a linked authority directory"); return 0; }
            string? observer = null;
            // Entries deliberately left in place, or that failed, are not retried within this run.
            var settled = new HashSet<string>(StringComparer.Ordinal);
            for (var pass = 0; pass < LegacyMigrationPasses; pass++)
            {
                var legacy = authority.EnumerateFileSystemInfos("*" + ObserverSuffix).Where(e => !settled.Contains(e.Name)).ToList();
                if (legacy.Count == 0) return migrated;
                if (observer is null)
                {
                    observer = StockAuthoritySource.ObserverDirectory(authorityDirectory);
                    if (new DirectoryInfo(observer).LinkTarget is not null) { logger.LogWarning("Legacy observer password migration refused a linked observer directory"); return 0; }
                    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(observer);
                    else
                    {
                        Directory.CreateDirectory(observer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        File.SetUnixFileMode(observer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
                foreach (var entry in legacy)
                {
                    try
                    {
                        switch (MigrateLegacyEntry(entry, observer, logger, seams))
                        {
                            case true: migrated++; break;
                            case false: settled.Add(entry.Name); break;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        settled.Add(entry.Name);
                        logger.LogWarning("Legacy observer password for {Agent} was not moved ({Type}); manual reconciliation required",
                            entry.Name[..^ObserverSuffix.Length], ex.GetType().Name);
                    }
                }
            }
            if (authority.EnumerateFileSystemInfos("*" + ObserverSuffix).Any(e => !settled.Contains(e.Name)))
                logger.LogWarning("Legacy observer passwords kept appearing after {Passes} passes; manual reconciliation required", LegacyMigrationPasses);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StockIdentityDenied)
        { logger.LogWarning("Legacy observer password migration incomplete ({Type}); manual reconciliation required", ex.GetType().Name); }
        return migrated;
    }

    /// <summary>Bound on legacy-directory enumerations per migration run (review X5).</summary>
    internal const int LegacyMigrationPasses = 4;

    /// <summary>Test seams for the legacy migration's race windows; production passes none.</summary>
    internal sealed class LegacyMigrationSeams
    {
        public Action<string>? BeforeMove { get; init; }
        public Action<string>? BeforePublish { get; init; }
        public bool ForceCrossDevice { get; init; }
    }

    /// <summary>True: moved or stale copy removed; false: left in place; null: already gone (handled elsewhere).</summary>
    private static bool? MigrateLegacyEntry(FileSystemInfo entry, string observer, ILogger logger, LegacyMigrationSeams? seams)
    {
        var agent = entry.Name[..^ObserverSuffix.Length];
        if (!StockAuthoritySource.ValidAgent(agent)) return false;
        entry.Refresh();
        if (!entry.Exists && entry.LinkTarget is null) return null;
        // lstat semantics: only a plain regular file is moved; a link (even dangling) or other entry is refused.
        if (entry.LinkTarget is not null || entry is not FileInfo
            || (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            logger.LogWarning("Legacy observer password for {Agent} is not a regular file; left in place", agent);
            return false;
        }
        var target = Path.Combine(observer, entry.Name);
        if (new FileInfo(target).LinkTarget is not null) { logger.LogWarning("Observer password for {Agent} is a link; legacy copy left in place", agent); return false; }
        if (File.Exists(target))
        {
            // The private sibling already holds the current password (a later apply wrote it): drop the stale copy.
            File.Delete(entry.FullName);
            return true;
        }
        seams?.BeforeMove?.Invoke(agent);
        try
        {
            if (seams?.ForceCrossDevice == true) throw new IOException("synthetic_cross_device");
            File.Move(entry.FullName, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(entry.FullName) && !File.Exists(target))
        {
            // Cross-device: copy into a private temp, fsync, publish by rename, then delete the shared copy.
            var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temp, options))
                {
                    stream.Write(File.ReadAllBytes(entry.FullName));
                    stream.Flush(flushToDisk: true);
                }
                seams?.BeforePublish?.Invoke(agent);
                // Raced by a concurrent apply that wrote the sibling: that one is current, so only the stale copy goes.
                try { File.Move(temp, target, overwrite: false); }
                catch (IOException) when (File.Exists(target) && new FileInfo(target).LinkTarget is null) { }
                File.Delete(entry.FullName);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Observer password temp for {Agent} was not removed; manual reconciliation required", agent); }
            }
        }
        catch (IOException)
        {
            // Raced: the shared copy vanished (handled elsewhere), or a concurrent apply wrote the sibling first.
            if (!File.Exists(entry.FullName)) return null;
            if (new FileInfo(target).LinkTarget is not null) { logger.LogWarning("Observer password for {Agent} is a link; legacy copy left in place", agent); return false; }
            if (!File.Exists(target)) throw;
            File.Delete(entry.FullName);
            return true;
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogInformation("Moved the legacy observer password for {Agent} into the orchestrator-only directory", agent);
        return true;
    }

    /// <summary>
    /// Starts a receiver for each new or re-enrolled record and disposes those whose record vanished, was
    /// revoked or changed binding. A lane that failed to start, or whose connection later drops, is NOT
    /// retried by this reconcile: like <see cref="StockBrokerIdentity.Configure"/> (automatic recovery off,
    /// no stock reconnect loop) it stays held for manual reconciliation until that agent's record binding
    /// changes, e.g. a new epoch. One lane never affects another lane or the ordinary consumer.
    /// </summary>
    internal async Task ReconcileAsync(string directory, CancellationToken ct)
    {
        IReadOnlyDictionary<string, StockActiveRecord?> active;
        try { active = StockAuthoritySource.Active(directory); }
        catch (StockIdentityDenied)
        {
            // Existing receivers re-check their own record on every delivery; nothing new starts.
            logger.LogWarning("Stock observer directory unavailable; manual reconciliation required");
            return;
        }
        foreach (var (agent, lane) in _lanes.ToList())
        {
            if (active.GetValueOrDefault(agent) is { } fresh && lane.Source.SameBinding(fresh)) continue;
            _lanes.Remove(agent);
            await CloseAsync(lane.Receiver);
        }
        foreach (var (agent, fresh) in active)
        {
            if (fresh is null || _lanes.ContainsKey(agent)) continue;
            _lanes[agent] = new(fresh, await StartLaneAsync(directory, fresh, ct));
        }
    }

    private async Task<HeartbeatConsumerService?> StartLaneAsync(string directory, StockActiveRecord record, CancellationToken ct)
    {
        HeartbeatConsumerService? receiver = null;
        try
        {
            var identity = new StockBrokerIdentity(record.Path,
                StockAuthoritySource.RolePassword(directory, record.Record.Agent, "observer"), "observer");
            // The record's own BrokerHost, never the ordinary consumer's options; Configure rechecks it.
            receiver = new(Options.Create(new RabbitMqOptions { Host = identity.BrokerHost, Exchange = options.Value.Exchange }),
                registry, publisher, configuration, consumerLogger, hold, identity) { Connect = Connect };
            await receiver.StartAsync(ct);
            return receiver;
        }
        catch (Exception)
        {
            await CloseAsync(receiver);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            logger.LogWarning("Stock observer lane for {Agent} stopped; manual reconciliation required", record.Record.Agent);
            return null;
        }
    }

    internal async Task StopLanesAsync()
    {
        foreach (var lane in _lanes.Values) await CloseAsync(lane.Receiver);
        _lanes.Clear();
    }

    private async Task CloseAsync(HeartbeatConsumerService? receiver)
    {
        if (receiver is null) return;
        try { await receiver.DisposeAsync(); }
        catch (Exception) { logger.LogWarning("Stock observer cleanup failed; manual reconciliation required"); }
    }
}
