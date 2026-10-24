using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// A real <c>/api/workflow-outcomes</c> host over TestServer, built from the same three pieces a
/// live orchestrator uses: <see cref="WorkflowOutcomeEndpoints.AddWorkflowOutcomeApi"/>, the
/// global admin middleware gated on <see cref="OrchestratorAuth.RequiresBearerToken"/> and
/// <see cref="WorkflowOutcomeAuthorizationMiddleware.UseWorkflowOutcomeAuthorization"/>, then
/// <see cref="WorkflowOutcomeEndpoints.MapWorkflowOutcomeEndpoints"/>. The store is SQLite (or
/// real MySQL when a test passes a MySQL configuration); Temporal is the fake probe.
/// </summary>
public sealed class ApiHost : IAsyncDisposable
{
    public const string AdminToken = "admin-test-token-0001";
    public const string CoordinatorToken = "coordinator-test-token-0001";
    public const string TokenKey = "WO_TEST_COORDINATOR_TOKEN";
    public const string Coordinator = "coordinator";
    public const string Project = "project-a";

    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required ApiDatabase Database { get; init; }
    public required ManualClock Clock { get; init; }
    public required FakeRecoveryProbe Probe { get; init; }
    public required FakeArtifactReader Artifacts { get; init; }
    public required RecoveryApprovalsFixture Approvals { get; init; }
    public required CapturingLoggerProvider Logs { get; init; }

    public WorkflowOutcomeBatcher Batcher => App.Services.GetRequiredService<WorkflowOutcomeBatcher>();

    public static Dictionary<string, string?> DefaultConfig() => new()
    {
        ["WorkflowOutcomes:Enabled"] = "true",
        ["WorkflowOutcomes:Namespaces"] = "namespace-a,namespace-b",
        ["WorkflowOutcomes:CoordinatorTokenKey"] = TokenKey,
        ["WorkflowOutcomes:CoordinatorCredentialVersion"] = "1",
        ["WorkflowOutcomes:CoordinatorGeneration"] = "1",
        ["WorkflowOutcomes:BatchSeconds"] = "30",
        ["WorkflowOutcomes:BatchSize"] = "20",
        ["WorkflowOutcomes:UnackedAttentionMinutes"] = "15",
        ["Orchestrator:AuthToken"] = AdminToken,
        ["FLEET_CTO_AGENT"] = Coordinator,
        [TokenKey] = CoordinatorToken,
    };

    public static async Task<ApiHost> StartAsync(
        ApiDatabase? database = null,
        Action<Dictionary<string, string?>>? configure = null,
        IWorkflowOutcomePayloadProtector? protector = null,
        ManualClock? clock = null)
    {
        database ??= await ApiDatabase.CreateSqliteAsync();
        clock ??= new ManualClock();
        var config = DefaultConfig();
        configure?.Invoke(config);
        var logs = new CapturingLoggerProvider();
        var probe = new FakeRecoveryProbe();
        var artifacts = new FakeArtifactReader();
        // Deny-by-default: a host starts with an empty approvals file; tests add exact approvals.
        var approvals = new RecoveryApprovalsFixture(Path.Combine(Path.GetTempPath(), "wo-approvals-" + Guid.NewGuid().ToString("N") + ".json"));
        if (!config.ContainsKey("WorkflowOutcomes:RecoveryApprovalsFile")) config["WorkflowOutcomes:RecoveryApprovalsFile"] = approvals.Path;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Services.AddDbContext<OrchestratorDbContext>(database.Configure);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IWorkflowRecoveryProbe>(probe);
        builder.Services.AddSingleton<IWorkflowRecoveryArtifactReader>(artifacts);
        builder.Services.AddSingleton(protector ?? new DataProtectionWorkflowOutcomePayloadProtector(new EphemeralDataProtectionProvider()));
        builder.Services.AddWorkflowOutcomeApi(builder.Configuration, runBatcher: false);

        var app = builder.Build();
        var admin = config.GetValueOrDefault("Orchestrator:AuthToken") ?? "";
        // The production global gate, called rather than copied (Program.cs registers it only when set).
        if (!string.IsNullOrWhiteSpace(admin))
            app.Use(async (context, next) =>
            {
                if (!OrchestratorAuth.RequiresBearerToken(context.Request.Method, context.Request.Path.Value ?? ""))
                {
                    await next(context);
                    return;
                }
                var header = context.Request.Headers.Authorization.FirstOrDefault();
                var token = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? header["Bearer ".Length..].Trim() : null;
                if (token != admin)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next(context);
            });
        app.UseWorkflowOutcomeAuthorization();
        app.MapWorkflowOutcomeEndpoints();

        await database.EnsureCreatedAsync();
        await app.StartAsync();
        database.Commands.Reset();
        return new ApiHost { App = app, Client = app.GetTestClient(), Database = database, Clock = clock, Probe = probe, Artifacts = artifacts, Approvals = approvals, Logs = logs };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
        Approvals.Dispose();
    }

    // ── HTTP helpers ───────────────────────────────────────────────────────────

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is string raw) request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        else if (body is not null) request.Content = JsonContent.Create(body, options: WorkflowOutcomeApi.Json);
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> AsCoordinator(HttpMethod method, string url, object? body = null) => SendAsync(method, url, CoordinatorToken, body);
    public Task<HttpResponseMessage> AsAdmin(HttpMethod method, string url, object? body = null) => SendAsync(method, url, AdminToken, body);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), WorkflowOutcomeApi.Json)!;

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ── Seeds and flows ────────────────────────────────────────────────────────

    private static int _seq;

    public async Task<WorkflowOutcomeRun> SeedRunAsync(Action<WorkflowOutcomeRun>? configure = null)
    {
        var n = Interlocked.Increment(ref _seq);
        var now = Clock.GetUtcNow().UtcDateTime;
        var run = new WorkflowOutcomeRun
        {
            Id = Guid.NewGuid(),
            Namespace = "namespace-a",
            WorkflowId = $"wf-{n}",
            RunId = Guid.NewGuid().ToString("D"),
            IdentityHash = "",
            WorkflowType = "TaskDelegationWorkflow",
            Status = "Failed",
            TerminalEventId = 12,
            TerminalEventType = "WorkflowExecutionFailed",
            Classification = WorkflowOutcomeClassifications.ConfirmedFailure,
            ReasonCode = WorkflowOutcomeReasonCodes.WorkflowFailed,
            Version = 1,
            Coordinator = Coordinator,
            TaskOwner = "agent1",
            ProjectName = Project,
            EvidenceState = WorkflowOutcomeEvidenceStates.Complete,
            SourceTaskRefsJson = JsonSerializer.Serialize(new[] { $"wf-{n}/step-a" }),
            DetectedAt = now,
            StoredAt = now,
            UpdatedAt = now,
        };
        configure?.Invoke(run);
        run.IdentityHash = WorkflowOutcomeHashes.IdentityHash(run.Namespace, run.WorkflowId, run.RunId);
        await Database.WithContextAsync(async db =>
        {
            db.WorkflowOutcomeRuns.Add(run);
            await db.SaveChangesAsync();
        });
        return run;
    }

    public Task SeedAgentsAsync(params string[] names) => Database.WithContextAsync(async db =>
    {
        foreach (var name in names)
            db.Agents.Add(new Agent { Name = name, DisplayName = name, Role = "developer", Model = "opus", ContainerName = "fleet-" + name });
        await db.SaveChangesAsync();
    });

    public Task GrantProjectAsync(string agent = Coordinator, string project = Project) => Database.WithContextAsync(async db =>
    {
        db.AgentProjectAccess.Add(new AgentProjectAccess { AgentName = agent, Project = project });
        await db.SaveChangesAsync();
    });

    /// <summary>Advances past the coalescing window and runs one batcher pass.</summary>
    public async Task<int> FlushAsync()
    {
        Clock.Advance(TimeSpan.FromSeconds(31));
        return await Batcher.RunOnceAsync(CancellationToken.None);
    }

    public async Task<WorkflowOutcomeInboxPage> InboxAsync(long generation = 1)
    {
        var response = await AsCoordinator(HttpMethod.Get, $"/api/workflow-outcomes/inbox?generation={generation}");
        response.EnsureSuccessStatusCode();
        return await ReadAsync<WorkflowOutcomeInboxPage>(response);
    }

    public Task<HttpResponseMessage> DeliverAsync(WorkflowOutcomeInboxBatch batch) =>
        AsCoordinator(HttpMethod.Post, $"/api/workflow-outcomes/batches/{batch.BatchId}/delivered",
            new WorkflowOutcomeDeliveredRequest(batch.Nonce, batch.Generation, batch.ItemVersionsHash));

    public Task<HttpResponseMessage> AckAsync(WorkflowOutcomeInboxBatch batch, params WorkflowOutcomeItemRef[] items) =>
        AsCoordinator(HttpMethod.Post, $"/api/workflow-outcomes/batches/{batch.BatchId}/ack",
            new WorkflowOutcomeAckRequest(batch.Generation, batch.Nonce, items, WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId)));

    /// <summary>Seeds one confirmed failure and drives it through enqueue, delivery and coordinator ack.</summary>
    public async Task<(WorkflowOutcomeRun Run, WorkflowOutcomeInboxBatch Batch)> AckedFailureAsync(Action<WorkflowOutcomeRun>? configure = null)
    {
        var run = await SeedRunAsync(configure);
        await FlushAsync();
        var batch = (await InboxAsync()).Batches.Single(b => b.Items.Any(i => i.Id == run.Id));
        (await DeliverAsync(batch)).EnsureSuccessStatusCode();
        (await AckAsync(batch, new WorkflowOutcomeItemRef(run.Id, run.Version))).EnsureSuccessStatusCode();
        return (run, batch);
    }

    public static object Decision(int version, string operation, string owner = "agent1", string reason = "owned follow-up",
        string[]? evidence = null, DateTimeOffset? dueAt = null, string? artifactRef = null, string? taskRef = null) => new
    {
        expectedVersion = version, operation, owner, reason, evidenceRefs = evidence ?? ["https://example.com/evidence/1"],
        dueAt, taskRef, artifactRef,
    };
}

/// <summary>A SQLite in-memory database (kept open across hosts) or a real MySQL one, with a command counter.</summary>
public sealed class ApiDatabase : IAsyncDisposable
{
    private readonly SqliteConnection? _sqlite;
    private readonly string? _mysql;

    private ApiDatabase(SqliteConnection? sqlite, string? mysql)
    {
        _sqlite = sqlite;
        _mysql = mysql;
    }

    public CommandCounter Commands { get; } = new();

    public static async Task<ApiDatabase> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // MySQL's binary collation for the identity hash; SQLite needs it declared.
        connection.CreateCollation("ascii_bin", (a, b) => string.CompareOrdinal(a, b));
        return new ApiDatabase(connection, null);
    }

    public static ApiDatabase ForMySql(string connectionString) => new(null, connectionString);

    public bool IsMySql => _mysql is not null;

    public void Configure(DbContextOptionsBuilder options)
    {
        if (_mysql is not null)
            options.UseMySql(_mysql, new MySqlServerVersion(new Version(8, 0, 0)),
                mysql => mysql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
        else
            options.UseSqlite(_sqlite!);
        options.AddInterceptors(Commands);
    }

    public OrchestratorDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<OrchestratorDbContext>();
        Configure(builder);
        return new OrchestratorDbContext(builder.Options);
    }

    public async Task WithContextAsync(Func<OrchestratorDbContext, Task> work)
    {
        await using var db = NewContext();
        await work(db);
    }

    public async Task<T> QueryAsync<T>(Func<OrchestratorDbContext, Task<T>> work)
    {
        await using var db = NewContext();
        return await work(db);
    }

    public async Task EnsureCreatedAsync()
    {
        await using var db = NewContext();
        if (_mysql is not null) await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_sqlite is not null) await _sqlite.DisposeAsync();
    }
}

/// <summary>Counts every command sent to the store and can inject a store failure.</summary>
public sealed class CommandCounter : DbCommandInterceptor
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public Func<DbCommand, bool>? FailWhen { get; set; }
    public void Reset() => Interlocked.Exchange(ref _count, 0);

    private void Seen(DbCommand command)
    {
        Interlocked.Increment(ref _count);
        if (FailWhen?.Invoke(command) == true) throw new InjectedStoreException();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r) { Seen(c); return r; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Seen(c); return ValueTask.FromResult(r); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r) { Seen(c); return r; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default) { Seen(c); return ValueTask.FromResult(r); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand c, CommandEventData e, InterceptionResult<object> r) { Seen(c); return r; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<object> r, CancellationToken ct = default) { Seen(c); return ValueTask.FromResult(r); }
}

public sealed class InjectedStoreException : DbException
{
    public InjectedStoreException() : base("injected store failure") { }
}

/// <summary>A settable clock; starts at a fixed instant so tests never depend on wall time.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Scripted Temporal Describe and agent task facts. It has no start/cancel/signal: the API cannot act through it.</summary>
public sealed class FakeRecoveryProbe : IWorkflowRecoveryProbe
{
    private readonly ConcurrentDictionary<string, WorkflowRecoveryRunState?> _runs = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public Exception? Error { get; set; }
    public HashSet<string> ActiveTasks { get; } = new(StringComparer.Ordinal);

    /// <summary>Records the exact run's status and makes it the latest run of its workflow id.</summary>
    public void SetRun(string ns, string workflowId, string runId, string status, string? workflowType = null, string? taskQueue = null)
    {
        _runs[Key(ns, workflowId, runId)] = new WorkflowRecoveryRunState(runId, status, workflowType, taskQueue);
        _runs[Key(ns, workflowId, null)] = new WorkflowRecoveryRunState(runId, status, workflowType, taskQueue);
    }

    /// <summary>Answers a Describe of (<paramref name="ns"/>, <paramref name="workflowId"/>, <paramref name="askedRunId"/>) with <paramref name="state"/>.</summary>
    public void SetAnswer(string ns, string workflowId, string? askedRunId, WorkflowRecoveryRunState? state) =>
        _runs[Key(ns, workflowId, askedRunId)] = state;

    public void SetLatest(string ns, string workflowId, string runId, string status) =>
        _runs[Key(ns, workflowId, null)] = new WorkflowRecoveryRunState(runId, status);

    public Task<WorkflowRecoveryRunState?> DescribeAsync(string @namespace, string workflowId, string? runId, CancellationToken ct)
    {
        Calls.Enqueue("describe:" + Key(@namespace, workflowId, runId));
        if (Error is { } error) return Task.FromException<WorkflowRecoveryRunState?>(error);
        return Task.FromResult(_runs.GetValueOrDefault(Key(@namespace, workflowId, runId)));
    }

    public IReadOnlyCollection<string> ActiveTaskIds() => ActiveTasks.ToList();

    private static string Key(string ns, string workflowId, string? runId) => ns + "|" + workflowId + "|" + (runId ?? "*");
}

/// <summary>A scripted issue/PR reader: one answer per artifact, unverified when none is set.</summary>
public sealed class FakeArtifactReader : IWorkflowRecoveryArtifactReader
{
    private readonly ConcurrentDictionary<string, WorkflowRecoveryArtifactRead> _reads = new();
    public ConcurrentQueue<string> Calls { get; } = new();

    public void Set(string repo, long? issue, long? pr, WorkflowRecoveryArtifactRead read) => _reads[Key(repo, issue, pr)] = read;

    /// <summary>A closed, unmerged pull request with finished checks and its head branch deleted: no existing work.</summary>
    public static WorkflowRecoveryArtifactRead ClosedPull(string sha = "0123456789abcdef0123456789abcdef01234567") =>
        new(new WorkflowRecoveryArtifactFacts(WorkflowRecoveryArtifactFacts.PullRequest, "closed", false, sha, "feat/x", null, 0, []), null);

    public static WorkflowRecoveryArtifactRead OpenIssue(params long[] openPulls) =>
        new(new WorkflowRecoveryArtifactFacts(WorkflowRecoveryArtifactFacts.Issue, "open", false, null, null, null, 0, openPulls), null);

    public Task<WorkflowRecoveryArtifactRead> ReadAsync(string repo, long? issueNumber, long? prNumber, CancellationToken ct)
    {
        Calls.Enqueue(Key(repo, issueNumber, prNumber));
        return Task.FromResult(_reads.GetValueOrDefault(Key(repo, issueNumber, prNumber)) ?? WorkflowRecoveryArtifactRead.Failed("artifact_read_failed"));
    }

    private static string Key(string repo, long? issue, long? pr) => repo + "|" + issue + "|" + pr;
}

/// <summary>The host's recovery-approvals.json: starts empty (deny-by-default) and is rewritten on every change.</summary>
public sealed class RecoveryApprovalsFixture : IDisposable
{
    private readonly List<object> _entries = [];

    public RecoveryApprovalsFixture(string path)
    {
        Path = path;
        Write();
    }

    public string Path { get; }

    /// <summary>Records a human approval covering exactly <paramref name="action"/>.</summary>
    public void Add(WorkflowRecoveryAction action, DateTimeOffset? expiresAt = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["authorityRef"] = action.AuthorityRef, ["operation"] = action.Operation,
            ["outcomeId"] = action.OutcomeId.ToString("D"), ["decisionId"] = action.DecisionId.ToString("D"),
            ["targetNamespace"] = action.TargetNamespace,
            ["expiresAt"] = (expiresAt ?? DateTimeOffset.UtcNow.AddDays(1)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (action.TargetWorkflowType is not null) entry["targetWorkflowType"] = action.TargetWorkflowType;
        if (action.TargetTaskQueue is not null) entry["targetTaskQueue"] = action.TargetTaskQueue;
        if (action.TargetAgent is not null) entry["targetAgent"] = action.TargetAgent;
        if (action.InputSha256 is not null) entry["inputSha256"] = action.InputSha256;
        _entries.Add(entry);
        Write();
    }

    public void Clear()
    {
        _entries.Clear();
        Write();
    }

    /// <summary>Replaces the file with arbitrary bytes (invalid-policy cases).</summary>
    public void WriteRaw(string content) => File.WriteAllText(Path, content);

    public void Delete() => File.Delete(Path);

    private void Write() => File.WriteAllText(Path, JsonSerializer.Serialize(new { schemaVersion = 1, approvals = _entries }));

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

/// <summary>Captures every formatted log line (and its exception text) for the no-secret assertions.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    public string All => string.Join("\n", Lines);

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
