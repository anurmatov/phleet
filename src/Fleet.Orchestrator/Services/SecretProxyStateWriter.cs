using System.Runtime.CompilerServices;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Writes the one file <c>fleet-secrets</c> reads (<see cref="SecretProxyState"/>): the bindings with
/// their current <c>.env</c> values, and every grant a running container holds. The database and
/// <c>.env</c> are the source; the file is a projection, rebuilt whole and replaced atomically.
/// </summary>
/// <remarks>
/// <para>
/// It is rewritten when a provision mints placeholders (and must succeed before the container
/// starts), when a binding or an Env Ref is saved, and every few seconds by
/// <see cref="SecretProxyStateService"/>, which is also what picks up a value rotated in <c>.env</c>.
/// </para>
/// <para>
/// Disabled, it writes an empty state over an existing file, so a forwarder left running refuses
/// everything rather than serve the last grants it saw.
/// </para>
/// </remarks>
public sealed class SecretProxyStateWriter(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    SecretProxySettings settings,
    ILogger<SecretProxyStateWriter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastBody;

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
    /// Rebuilds and, when it changed, replaces the state file. Returns a fixed fault code when the
    /// file could not be brought up to date, null otherwise.
    /// </summary>
    public async Task<string?> WriteAsync(CancellationToken ct = default)
    {
        if (StatePath is not { } path)
            return settings.Enabled ? "secret_proxy_state_path_missing" : null;

        await _gate.WaitAsync(ct);
        try
        {
            if (!settings.Enabled)
            {
                // Nothing to maintain. A file from an earlier, enabled run is emptied once.
                if (!File.Exists(path))
                    return null;
                return Replace(path, Empty(), "disabled");
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

            return Replace(path, Build(bindings, held, env, settings, logger), "current");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.Common.DbException
                                   or InvalidOperationException)
        {
            logger.LogError("SecretProxy state not written ({Error})", ex.GetType().Name);
            return "secret_proxy_state_write_failed";
        }
        finally
        {
            _gate.Release();
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
            if (SecretBindingRules.DescribeFault(binding.EnvKeyName, binding.Kind, hosts, binding.HeaderName) is not null)
            {
                logger?.LogError("SecretProxy binding {Key} is invalid and was left out of the state", binding.EnvKeyName);
                continue;
            }

            stateBindings.Add(new SecretProxyBinding(
                binding.EnvKeyName, binding.Kind, hosts, binding.HeaderName,
                Usable(env, binding.EnvKeyName),
                binding.Kind == SecretBindingRules.KindGitHubApp && binding.AppIdEnvKey is { } appIdKey
                    ? Usable(env, appIdKey)
                    : null));
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

    private string? Replace(string path, SecretProxyState state, string label)
    {
        // Compared without the timestamp, so an unchanged state is not rewritten every few seconds.
        var body = state.Serialize();
        if (body == _lastBody && File.Exists(path))
            return null;

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
                writer.Write((state with { GeneratedAtUtc = DateTimeOffset.UtcNow }).Serialize());
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

        _lastBody = body;
        logger.LogInformation(
            "SecretProxy state written ({Label}): bindings={Bindings} grants={Grants}",
            label, state.Bindings.Count, state.Grants.Count);
        return null;
    }
}

/// <summary>
/// Keeps the state file current in the background: once at startup, then every few seconds. This is
/// what applies a secret rotated in <c>.env</c>, and what repairs the file if a write was missed.
/// </summary>
public sealed class SecretProxyStateService(
    SecretProxyStateWriter writer, SecretProxySettings settings, ILogger<SecretProxyStateService> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

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
                await writer.WriteAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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
        if (eventData.Context is { } context && _pending.Remove(context))
            await services.GetRequiredService<SecretProxyStateWriter>().WriteAsync(CancellationToken.None);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && _pending.Remove(context))
            services.GetRequiredService<SecretProxyStateWriter>().WriteAsync(CancellationToken.None).GetAwaiter().GetResult();
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
