using System.Data.Common;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The database stays unreachable while placeholders are enabled. With the production retry strategy
/// that surfaces as <see cref="RetryLimitExceededException"/>, which is not a DbException. The real
/// writer and the real hosted service must turn it into a fault, keep the orchestrator running, and
/// publish again once the database is back.
/// </summary>
public sealed class SecretProxyStateServiceTests : IAsyncLifetime
{
    private const string Key = "EXAMPLE_API_KEY";

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"secret-state-service-{Guid.NewGuid():N}");
    private readonly ExhaustedRetries _database = new();
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private SecretProxyStateWriter _writer = null!;
    private SecretProxySettings _settings = null!;
    private string _hash = "";

    private string StateFile => _writer.StatePath!;

    /// <summary>Fails every query the way an exhausted execution strategy does, until healed.</summary>
    private sealed class ExhaustedRetries : DbCommandInterceptor
    {
        public volatile bool Down;
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Down)
                return base.ReaderExecutingAsync(command, eventData, result, ct);
            Interlocked.Increment(ref _attempts);
            throw new RetryLimitExceededException(
                "synthetic: the maximum number of retries (5) was exceeded", new TimeoutException("synthetic: connection refused"));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_baseDir);
        var envFile = Path.Combine(_baseDir, ".env");
        File.WriteAllText(envFile, $"{Key}=synthetic-value-71c0\n");
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_database));
        _services = services.BuildServiceProvider();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:BaseDir"] = _baseDir,
            ["Provisioning:EnvFilePath"] = envFile,
            ["SecretProxy:Enabled"] = "true",
        }).Build();
        _settings = SecretProxySettings.FromConfiguration(config);
        _writer = new SecretProxyStateWriter(
            _services.GetRequiredService<IServiceScopeFactory>(), config, _settings, NullLogger<SecretProxyStateWriter>.Instance);

        _hash = SecretPlaceholders.Hash(SecretPlaceholders.Create());
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.SecretBindings.Add(new SecretBinding { EnvKeyName = Key, Hosts = "api.example.test" });
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "developer", Provider = "claude", Model = "opus",
            ContainerName = "fleet-agent1",
            EnvRefs = [new AgentEnvRef { EnvKeyName = Key, PlaceholderSha256 = _hash }],
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
    }

    private SecretProxyState State() => SecretProxyState.TryParse(File.ReadAllText(StateFile), out _)!;

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Writer_TurnsExhaustedRetriesIntoAFault_AndTheBackgroundPassLeavesTheStateAlone()
    {
        Assert.Null(await _writer.WriteAsync());
        var before = File.ReadAllText(StateFile);
        _database.Down = true;

        var fault = await _writer.WriteAsync(failClosed: false);

        Assert.Equal("secret_proxy_state_write_failed", fault);
        Assert.Equal(before, File.ReadAllText(StateFile));
    }

    [Fact]
    public async Task Writer_OnBehalfOfAChange_WithdrawsTheStateWhenRetriesAreExhausted()
    {
        Assert.Null(await _writer.WriteAsync());
        _database.Down = true;

        var fault = await _writer.WriteAsync();

        Assert.Equal("secret_proxy_state_write_failed", fault);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public async Task Writer_StillLetsTheCallersOwnCancellationThrough()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _writer.WriteAsync(cancelled.Token, failClosed: false));
    }

    [Fact]
    public async Task HostedRefresh_SurvivesExhaustedRetries_KeepsTheHostRunning_AndPublishesAgainOnRecovery()
    {
        // The default host behaviour: an unhandled exception in a BackgroundService stops the host.
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddHostedService(_ => new SecretProxyStateService(
                _writer, _settings, NullLogger<SecretProxyStateService>.Instance) { Interval = TimeSpan.FromMilliseconds(25) }))
            .Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        _database.Down = true;

        await host.StartAsync();
        // Several whole passes, each ending in exhausted retries.
        await UntilAsync(() => _database.Attempts >= 5, "the refresh did not keep retrying");

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        var service = host.Services.GetServices<IHostedService>().OfType<SecretProxyStateService>().Single();
        Assert.False(service.ExecuteTask!.IsCompleted);
        // Nothing could be read, so nothing was published or withdrawn.
        Assert.False(File.Exists(StateFile));

        _database.Down = false;
        await UntilAsync(() => File.Exists(StateFile), "the state was not published after the database came back");

        var state = State();
        Assert.Equal(Key, state.Bindings.Single().Key);
        Assert.Equal(_hash, state.Grants.Single().PlaceholderSha256);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        await host.StopAsync();
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HostedRefresh_KeepsAnAlreadyPublishedStateInPlace_WhileTheDatabaseIsDown()
    {
        // It has no change of its own to enforce. The forwarder's age bound, not this loop, ends a
        // state that can no longer be refreshed.
        Assert.Null(await _writer.WriteAsync());
        var published = State();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddHostedService(_ => new SecretProxyStateService(
                _writer, _settings, NullLogger<SecretProxyStateService>.Instance) { Interval = TimeSpan.FromMilliseconds(25) }))
            .Build();
        _database.Down = true;

        await host.StartAsync();
        await UntilAsync(() => _database.Attempts >= 5, "the refresh did not keep retrying");

        var kept = State();
        Assert.Equal(published.GeneratedAtUtc, kept.GeneratedAtUtc);
        Assert.Equal(published.Grants, kept.Grants);
        await host.StopAsync();
    }
}
