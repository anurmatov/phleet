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
    internal SqliteConnection Connection => _connection;
    public ServiceProvider Services { get; }
    public ContainerProvisioningService Service { get; }
    public DockerService Docker { get; }
    public ProvisioningLogSink Logs { get; } = new();
    public void StubInspectSequence(params int[] statuses) => _dockerApi.InspectStatuses = new Queue<int>(statuses);
    public int DockerRequestCount => _dockerApi.RequestCount;

    /// <summary>Every Docker API call, as <c>METHOD /path</c>, in order.</summary>
    public IReadOnlyList<string> DockerRequests => _dockerApi.Requests;

    /// <summary>The JSON body of every <c>/containers/create</c> call; its <c>HostConfig.Binds</c> are the binds.</summary>
    public IReadOnlyList<string> CreateBodies => _dockerApi.CreateBodies;

    private ProvisioningHarness(IReadOnlyDictionary<string, string?>? extraConfig, Action<IServiceCollection, SqliteConnection>? configure)
    {
        BaseDir = Path.Combine(Path.GetTempPath(), $"prov-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(BaseDir);
        var envFile = Path.Combine(BaseDir, ".env");
        File.WriteAllText(envFile, $"FLEET_CTO_AGENT={CtoAgent}\n");

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ContainerProvisioningService>(_ => Service!);
        services.AddSingleton<IAgentRuntimeLifecycle>(sp => new AgentRuntimeLifecycle(
            sp.GetRequiredService<IServiceScopeFactory>(), Service!, Docker!,
            NullLogger<AgentRuntimeLifecycle>.Instance, sp.GetService<OpenShellRetirementService>()));
        configure?.Invoke(services, _connection);
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
        Docker = new DockerService(
            NullLogger<DockerService>.Instance,
            new HttpClient(_dockerApi) { BaseAddress = new Uri("http://localhost") });

        Service = new ContainerProvisioningService(
            Services.GetRequiredService<IServiceScopeFactory>(), Docker, config,
            new JournalTokenService(config), Logs.For<ContainerProvisioningService>());
    }

    public static ProvisioningHarness Create(IReadOnlyDictionary<string, string?>? extraConfig = null,
        Action<IServiceCollection, SqliteConnection>? configure = null) => new(extraConfig, configure);

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
        public Queue<int> InspectStatuses { get; set; } = new();
        public int RequestCount { get; private set; }
        public List<string> Requests { get; } = [];
        public List<string> CreateBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/json", StringComparison.Ordinal) && InspectStatuses.TryDequeue(out var status))
                return new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent("{\"Id\":\"" + new string('a', 64) + "\"}"),
                };
            if ((request.Method == HttpMethod.Post && path.EndsWith("/stop", StringComparison.Ordinal))
                || request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
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
