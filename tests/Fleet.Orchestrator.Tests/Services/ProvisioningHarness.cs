using System.Net;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Runs the real <see cref="ContainerProvisioningService.ProvisionAsync"/> end to end: SQLite for the
/// DB, a temp directory for <c>Provisioning:BaseDir</c>, and a fake Docker API that reports no
/// existing container and accepts create/start. What it produces on disk is exactly what a live
/// provision writes under <c>workspaces/&lt;container&gt;/.generated/</c>.
/// </summary>
internal sealed class ProvisioningHarness : IAsyncDisposable
{
    public const string CtoAgent = "cto-agent";

    private readonly SqliteConnection _connection;
    private readonly FakeDockerApi _dockerApi = new();

    public string BaseDir { get; }
    public ServiceProvider Services { get; }
    public ContainerProvisioningService Service { get; }
    public ProvisioningLogSink Logs { get; } = new();
    public int DockerRequestCount => _dockerApi.RequestCount;

    /// <summary>Every Docker API call, as <c>METHOD /path</c>, in order.</summary>
    public IReadOnlyList<string> DockerRequests { get { lock (_dockerApi.Requests) return _dockerApi.Requests.ToList(); } }

    /// <summary>The JSON body of every <c>/containers/create</c> call; its <c>HostConfig.Binds</c> are the binds.</summary>
    public IReadOnlyList<string> CreateBodies => _dockerApi.CreateBodies;

    /// <summary>The JSON body of every <c>/networks/{name}/connect</c> call, with the network name.</summary>
    public IReadOnlyList<(string Network, string Body)> NetworkConnects => _dockerApi.NetworkConnects;

    /// <summary>Makes every network connect fail, as when <c>fleet-secrets-net</c> does not exist.</summary>
    public bool FailNetworkConnect { set => _dockerApi.FailNetworkConnect = value; }

    /// <summary>Whether the fake Docker reports the container as existing (needed to deprovision).</summary>
    public bool ContainerExists { set => _dockerApi.ContainerExists = value; }

    /// <summary>
    /// Runs before the fake Docker answers a call, with the call as <c>METHOD /path</c>. A test uses
    /// it to hold a call open, or to change the database at a known point of a provision.
    /// </summary>
    public Func<string, Task>? OnDockerRequest { set => _dockerApi.OnRequest = value; }

    public SecretProxySettings SecretSettings { get; }
    public SecretProxyStateWriter SecretState { get; }
    public string EnvFile { get; }

    private ProvisioningHarness(IReadOnlyDictionary<string, string?>? extraConfig)
    {
        BaseDir = Path.Combine(Path.GetTempPath(), $"prov-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(BaseDir);
        var envFile = EnvFile = Path.Combine(BaseDir, ".env");
        File.WriteAllText(envFile, $"FLEET_CTO_AGENT={CtoAgent}\n");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        Services = services.BuildServiceProvider();

        using (var scope = Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreated();

        var settings = new Dictionary<string, string?>
        {
            ["Provisioning:BaseDir"] = BaseDir,
            ["Provisioning:EnvFilePath"] = envFile,
            ["FleetMemory:McpUrl"] = "http://fleet-memory:3100",
        };
        if (extraConfig is not null)
            foreach (var (k, v) in extraConfig) settings[k] = v;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var docker = new DockerService(
            NullLogger<DockerService>.Instance,
            new HttpClient(_dockerApi) { BaseAddress = new Uri("http://localhost") });

        // Off unless a test sets SecretProxy:Enabled; then exactly what Program.cs registers.
        SecretSettings = SecretProxySettings.FromConfiguration(config);
        SecretState = new SecretProxyStateWriter(
            Services.GetRequiredService<IServiceScopeFactory>(), config, SecretSettings, Logs.For<SecretProxyStateWriter>());

        Service = new ContainerProvisioningService(
            Services.GetRequiredService<IServiceScopeFactory>(), docker, config,
            new JournalTokenService(config), Logs.For<ContainerProvisioningService>(), SecretSettings, SecretState);
    }

    public static ProvisioningHarness Create(IReadOnlyDictionary<string, string?>? extraConfig = null) => new(extraConfig);

    public async Task SeedAsync(Action<OrchestratorDbContext> seed)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    public async Task MutateAsync(Func<OrchestratorDbContext, Task> mutate)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await mutate(db);
        await db.SaveChangesAsync();
    }

    public string GeneratedDir(string containerName) =>
        Path.Combine(BaseDir, "workspaces", containerName, ".generated");

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
        try { Directory.Delete(BaseDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>No container exists; create and start succeed.</summary>
    private sealed class FakeDockerApi : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> Requests { get; } = [];
        public List<string> CreateBodies { get; } = [];
        public List<(string Network, string Body)> NetworkConnects { get; } = [];
        public bool FailNetworkConnect { get; set; }
        public bool ContainerExists { get; set; }
        public Func<string, Task>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests)
            {
                RequestCount++;
                Requests.Add($"{request.Method} {path}");
            }
            if (OnRequest is { } onRequest)
                await onRequest($"{request.Method} {path}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/containers/create", StringComparison.Ordinal))
            {
                CreateBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"Id":"0123456789abcdef0123"}""", System.Text.Encoding.UTF8, "application/json"),
                };
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/start", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Post && path.EndsWith("/connect", StringComparison.Ordinal) && path.Contains("/networks/", StringComparison.Ordinal))
            {
                var network = Uri.UnescapeDataString(path.Split('/')[^2]);
                NetworkConnects.Add((network, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
                return new HttpResponseMessage(FailNetworkConnect ? HttpStatusCode.NotFound : HttpStatusCode.OK);
            }
            if (ContainerExists && request.Method == HttpMethod.Get && path.EndsWith("/json", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"Id":"0123456789abcdef0123","State":{"Status":"running"}}""", System.Text.Encoding.UTF8, "application/json"),
                };
            if (ContainerExists && request.Method == HttpMethod.Post && path.EndsWith("/stop", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Delete && path.Contains("/containers/", StringComparison.Ordinal))
            {
                // Removed: a provision that follows sees no container, as after a real deprovision.
                ContainerExists = false;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

/// <summary>Collects formatted log lines with their level.</summary>
internal sealed class ProvisioningLogSink
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public ILogger<T> For<T>() => new Logger<T>(this);

    private void Add(LogLevel level, string message)
    {
        lock (_entries) _entries.Add((level, message));
    }

    private sealed class Logger<T>(ProvisioningLogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Add(logLevel, formatter(state, exception));
    }
}
