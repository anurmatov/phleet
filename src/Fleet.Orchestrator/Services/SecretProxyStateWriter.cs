using System.Runtime.CompilerServices;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// A committed change to a binding, an Env Ref or an agent could not be published to
/// <c>fleet-secrets</c>. The database has the change; the forwarder was not told. The caller must not
/// report success: until the state can be written again, the change is not in force.
/// </summary>
public sealed class SecretStatePublicationException(string fault) : Exception(
    $"{fault}: the change was saved, but fleet-secrets could not be told. Its state was withdrawn where "
    + "possible, so placeholders are refused until the state can be written again; if it could not be "
    + $"withdrawn either, the forwarder refuses it once it is {SecretProxyState.DefaultMaxAgeSeconds} seconds old. "
    + "Check the secret-proxy/state directory under the fleet base directory.")
{
    public string Fault { get; } = fault;
}

/// <summary>
/// Writes the one file <c>fleet-secrets</c> reads (<see cref="SecretProxyState"/>): the bindings with
/// their current <c>.env</c> values, and every grant a running container holds. The database and
/// <c>.env</c> are the source; the file is a projection, rebuilt whole and replaced atomically.
/// </summary>
/// <remarks>
/// <para>
/// It is rewritten when a provision mints placeholders (and must succeed before the container
/// starts), when a binding or an Env Ref is saved, and every <see cref="SecretProxyState.HeartbeatSeconds"/>
/// seconds by <see cref="SecretProxyStateService"/>, changed or not: the forwarder refuses a state
/// that is too old, so a state that is still valid has to keep being rewritten. That pass is also what
/// picks up a value rotated in <c>.env</c>.
/// </para>
/// <para>
/// The file is the only way a revocation reaches the forwarder, so a failed write is never silent.
/// A write on behalf of a change returns its fault to the caller and first withdraws (deletes) the
/// old file, so the forwarder refuses everything rather than honour a grant that was just removed. If
/// even that fails, the forwarder's own age bound ends the old state.
/// </para>
/// <para>
/// Disabled, it writes an empty state over an existing file, so a forwarder left running refuses
/// every placeholder rather than serve the last grants it saw.
/// </para>
/// </remarks>
public sealed class SecretProxyStateWriter(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    SecretProxySettings settings,
    ILogger<SecretProxyStateWriter> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastContent;
    private string? _lastFault;
    private bool _emptied;

    /// <summary>Replaces the file at <c>path</c> with <c>content</c>, atomically. Tests substitute a failing one.</summary>
    internal Action<string, string> ReplaceFile { get; set; } = ReplaceAtomically;

    /// <summary>Deletes the state file. Tests substitute a failing one.</summary>
    internal Action<string> DeleteFile { get; set; } = File.Delete;

    /// <summary>The state file path, or null when the fleet base directory is not configured.</summary>
    public string? StatePath => config["Provisioning:BaseDir"] is { Length: > 0 } baseDir
        ? Path.Combine(baseDir, SecretProxyState.StateDirectory, SecretProxyState.FileName)
        : null;

    /// <summary>The CA certificate the forwarder created, as the orchestrator sees it on the host.</summary>
    public string? CaCertificatePath => config["Provisioning:BaseDir"] is { Length: > 0 } baseDir
        ? Path.Combine(baseDir, SecretProxyState.CaDirectory, SecretProxyState.CaCertificateFile)
        : null;

    public bool CaPresent => CaCertificatePath is { } path && File.Exists(path);

    /// <summary>
    /// Rebuilds the state and replaces the file. Returns a fixed fault code when the file could not be
    /// brought up to date, null otherwise.
    /// </summary>
    /// <param name="failClosed">
    /// True (the default) for a write on behalf of a change: on a fault the old file is withdrawn, so
    /// the forwarder refuses everything instead of honouring what was just revoked. False only for the
    /// background refresh, which has no change of its own to enforce; a state it cannot refresh ends
    /// at the forwarder's age bound.
    /// </param>
    public async Task<string?> WriteAsync(CancellationToken ct = default, bool failClosed = true)
    {
        if (StatePath is not { } path)
            return settings.Enabled ? "secret_proxy_state_path_missing" : null;

        await _gate.WaitAsync(ct);
        try
        {
            string? fault;
            try
            {
                fault = await BuildAndReplaceAsync(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Every failure is a fault, not only the ones foreseen: a database that stays down
                // surfaces as EF's RetryLimitExceededException, which is none of IOException,
                // DbException or InvalidOperationException. An exception that escaped here would stop
                // the orchestrator from the background refresh, and would bypass the withdrawal below
                // on the path that enforces a revocation. Only the caller's own cancellation passes.
                logger.LogError("SecretProxy state not written ({Error})", ex.GetType().Name);
                fault = "secret_proxy_state_write_failed";
            }

            if (fault is null)
            {
                if (_lastFault is not null)
                    logger.LogInformation("SecretProxy state is being written again");
                _lastFault = null;
                return null;
            }

            if (fault != _lastFault)
                logger.LogError("SecretProxy state is out of date ({Fault})", fault);
            _lastFault = fault;
            if (failClosed)
                Withdraw(path);
            return fault;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> BuildAndReplaceAsync(string path, CancellationToken ct)
    {
        if (!settings.Enabled)
        {
            // Nothing to maintain, and nothing here may fail a save: the feature is off. A file from
            // an earlier, enabled run is emptied once; if that cannot be done it is removed; if that
            // cannot be done either, a forwarder left running refuses it at its age bound.
            if (_emptied || !File.Exists(path))
                return null;
            try
            {
                Replace(path, Empty(), "disabled");
                _emptied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { DeleteFile(path); }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            }
            return null;
        }

        using var scope = scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<OrchestratorDbContext>() is not { } db)
            return "secret_proxy_state_database_missing";

        var bindings = await db.SecretBindings.AsNoTracking().OrderBy(b => b.EnvKeyName).ToListAsync(ct);
        var held = await db.AgentEnvRefs.AsNoTracking()
            .Where(r => r.PlaceholderSha256 != null)
            .Select(r => new HeldGrant(r.Agent.Id, r.Agent.Name, r.EnvKeyName, r.PlaceholderSha256!))
            .ToListAsync(ct);
        var env = ContainerProvisioningService.LoadEnvFile(config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env");

        Replace(path, Build(bindings, held, env, settings, logger), "current");
        return null;
    }

    /// <summary>
    /// Removes the state file after a failed write, so the forwarder refuses every request rather than
    /// keep honouring the state the failed write was meant to replace.
    /// </summary>
    private void Withdraw(string path)
    {
        try
        {
            DeleteFile(path);
            logger.LogCritical(
                "SecretProxy state withdrawn after a failed write: fleet-secrets refuses every request until the state is written again");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogCritical(
                "SecretProxy state could be neither written nor withdrawn ({Error}): fleet-secrets keeps the old state "
                + "until its age bound, {MaxAge} seconds by default",
                ex.GetType().Name, SecretProxyState.DefaultMaxAgeSeconds);
        }
    }

    internal sealed record HeldGrant(int AgentId, string Agent, string EnvKeyName, string PlaceholderSha256);

    private static SecretProxyState Empty() => new()
    {
        Version = SecretProxyState.CurrentVersion,
        GeneratedAtUtc = default,
        Bindings = [],
        Grants = [],
    };

    /// <summary>
    /// The state for these rows. A binding that no longer passes the shared rules, and a grant whose
    /// key has no binding or whose agent has no address, are left out: the forwarder refuses a state
    /// with one invalid entry, and one bad row must not take every other agent's secrets down.
    /// </summary>
    internal static SecretProxyState Build(
        IReadOnlyList<SecretBinding> bindings, IReadOnlyList<HeldGrant> held,
        IReadOnlyDictionary<string, string> env, SecretProxySettings settings, ILogger? logger = null)
    {
        var stateBindings = new List<SecretProxyBinding>();
        foreach (var binding in bindings)
        {
            var hosts = binding.HostList;
            var knownHosts = binding.KnownHostList;
            if (SecretBindingRules.DescribeFault(binding.EnvKeyName, binding.Kind, hosts, binding.HeaderName, knownHosts) is not null)
            {
                logger?.LogError("SecretProxy binding {Key} is invalid and was left out of the state", binding.EnvKeyName);
                continue;
            }

            stateBindings.Add(new SecretProxyBinding(
                binding.EnvKeyName, binding.Kind, hosts, binding.HeaderName,
                Usable(env, binding.EnvKeyName),
                binding.Kind == SecretBindingRules.KindGitHubApp && binding.AppIdEnvKey is { } appIdKey
                    ? Usable(env, appIdKey)
                    : null,
                knownHosts));
        }

        var known = stateBindings.Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
        var grants = held
            .Where(g => known.Contains(g.EnvKeyName) && settings.Network?.AgentAddress(g.AgentId) is not null)
            .OrderBy(g => g.Agent, StringComparer.Ordinal).ThenBy(g => g.EnvKeyName, StringComparer.Ordinal)
            .Select(g => new SecretProxyGrant(
                g.Agent, settings.Network!.AgentAddress(g.AgentId)!.ToString(), g.EnvKeyName, g.PlaceholderSha256))
            .ToList();

        return new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = default,
            Bindings = stateBindings,
            Grants = grants,
        };
    }

    private static string? Usable(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) && value != "<secret>" ? value : null;

    private void Replace(string path, SecretProxyState state, string label)
    {
        // Always written, even unchanged: the timestamp inside is what keeps the state fresh.
        ReplaceFile(path, (state with { GeneratedAtUtc = _time.GetUtcNow() }).Serialize());

        var content = state.Serialize();
        if (content != _lastContent)
            logger.LogInformation(
                "SecretProxy state written ({Label}): bindings={Bindings} grants={Grants}",
                label, state.Bindings.Count, state.Grants.Count);
        _lastContent = content;
    }

    private static void ReplaceAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var temp = Path.Combine(directory, $".{SecretProxyState.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}

/// <summary>
/// Keeps the state file current and fresh in the background: once at startup, then every
/// <see cref="SecretProxyState.HeartbeatSeconds"/> seconds. This is what keeps a valid state from
/// expiring at the forwarder, what applies a secret rotated in <c>.env</c>, and what brings the file
/// back after a failed write.
/// </summary>
public sealed class SecretProxyStateService(
    SecretProxyStateWriter writer, SecretProxySettings settings, ILogger<SecretProxyStateService> logger)
    : BackgroundService
{
    /// <summary>The pause between two refreshes. Tests shorten it.</summary>
    internal TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(SecretProxyState.HeartbeatSeconds);

    /// <summary>
    /// Never ends in an exception: an unhandled one in a hosted service stops the whole orchestrator,
    /// and a state that cannot be refreshed right now (the database is down, the directory is full)
    /// is exactly when the next pass matters. A failed pass is retried after the usual pause.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.Fault is { } fault)
            logger.LogCritical(
                "SecretProxy is misconfigured ({Fault}). Agents that hold a bound secret will not provision until it is fixed.", fault);
        else
            logger.LogInformation("SecretProxy enabled={Enabled} subnet={Subnet}", settings.Enabled, settings.Network?.Subnet ?? "-");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // No change of its own to enforce, so a failure here does not withdraw the state: the
                // forwarder's age bound ends a state that can no longer be refreshed.
                await writer.WriteAsync(stoppingToken, failClosed: false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The writer turns its own failures into a fault. This is for whatever it did not.
                logger.LogError("SecretProxy state refresh failed ({Error}); it is retried", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// Rewrites the state file as soon as a save changes a binding, an Env Ref or deletes an agent, so a
/// removed grant stops working when the call that removed it returns, whichever endpoint or tool
/// made the change.
/// </summary>
/// <remarks>
/// The save is already committed when the file is written. If the write fails, the save must not
/// look successful: the writer withdraws the old state and this throws
/// <see cref="SecretStatePublicationException"/>, so the caller reports a failure instead of a
/// revocation that is not in force.
/// </remarks>
public sealed class SecretStateSaveInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, object> _pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Mark(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.Remove(context)
            && await services.GetRequiredService<SecretProxyStateWriter>().WriteAsync(CancellationToken.None) is { } fault)
            throw new SecretStatePublicationException(fault);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && _pending.Remove(context)
            && services.GetRequiredService<SecretProxyStateWriter>().WriteAsync(CancellationToken.None).GetAwaiter().GetResult() is { } fault)
            throw new SecretStatePublicationException(fault);
        return base.SavedChanges(eventData, result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
            _pending.Remove(context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
            _pending.Remove(context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Mark(DbContext? context)
    {
        if (context is null)
            return;
        var relevant = context.ChangeTracker.Entries().Any(e =>
            (e.Entity is AgentEnvRef or SecretBinding && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || (e.Entity is Agent && e.State == EntityState.Deleted));
        if (relevant)
            _pending.AddOrUpdate(context, new object());
    }
}
