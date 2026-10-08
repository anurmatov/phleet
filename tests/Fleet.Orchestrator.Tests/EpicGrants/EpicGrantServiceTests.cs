using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.EpicGrants;

// ─── Shared fakes (also used by the MySQL suite in Fleet.Orchestrator.Tests.Migrations) ──────────

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private long _ticks = now.UtcTicks;
    public DateTimeOffset Now
    {
        get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>One fake Temporal execution.</summary>
internal sealed class FakeRun
{
    public required string Namespace { get; init; }
    public required string WorkflowId { get; init; }
    public required string RunId { get; init; }
    public required string Type { get; set; }
    public string Status { get; set; } = "Running";
    public DateTimeOffset StartTime { get; set; }
    public string? ParentId { get; set; }
    public string? ParentRunId { get; set; }
    public string? GateVisit { get; set; }
    public string? ReviewRef { get; set; }
    public string? ReviewScrub { get; set; }
    public JsonObject? Input { get; set; } = new();
    public string? DefinitionName { get; set; }
    public int? DefinitionVersion { get; set; }
    public bool HistoryMissing { get; set; }

    public EpicRunDescription Describe() =>
        new(Status, RunId, Type, StartTime, ParentId, ParentRunId, GateVisit, ReviewRef, ReviewScrub);
}

internal sealed record SentSignal(string Namespace, string WorkflowId, string RunId, string SignalName, string PayloadJson);

/// <summary><see cref="IEpicGrantTemporal"/> over an in-memory list of runs.</summary>
internal sealed class FakeEpicTemporal : IEpicGrantTemporal
{
    private readonly object _lock = new();
    private readonly List<FakeRun> _runs = [];
    private int _signalAttempts;

    public ConcurrentQueue<SentSignal> Signals { get; } = new();
    public int SignalAttempts => Volatile.Read(ref _signalAttempts);
    public Exception? SignalError { get; set; }
    public Func<SentSignal, Task>? OnSignal { get; set; }

    /// <summary>Throws from describe/history for this workflow id when set.</summary>
    public string? FailWorkflowId { get; set; }

    public FakeRun Add(FakeRun run)
    {
        lock (_lock) _runs.Add(run);
        return run;
    }

    public Task<EpicRunDescription?> DescribeAsync(string @namespace, string workflowId, string? runId, CancellationToken ct)
    {
        if (workflowId == FailWorkflowId) throw new TimeoutException("describe timed out");
        lock (_lock)
        {
            var run = runId is null
                ? _runs.LastOrDefault(r => r.Namespace == @namespace && r.WorkflowId == workflowId)
                : _runs.FirstOrDefault(r => r.Namespace == @namespace && r.WorkflowId == workflowId && r.RunId == runId);
            return Task.FromResult(run?.Describe());
        }
    }

    public Task<EpicRunHistory?> ReadRunAsync(string @namespace, string workflowId, string runId, CancellationToken ct)
    {
        if (workflowId == FailWorkflowId) throw new TimeoutException("history timed out");
        lock (_lock)
        {
            var run = _runs.FirstOrDefault(r => r.Namespace == @namespace && r.WorkflowId == workflowId && r.RunId == runId);
            if (run is null || run.HistoryMissing) return Task.FromResult<EpicRunHistory?>(null);
            return Task.FromResult<EpicRunHistory?>(
                new EpicRunHistory(run.Input?.ToJsonString(), run.DefinitionName, run.DefinitionVersion));
        }
    }

    public async Task SignalAsync(string @namespace, string workflowId, string runId, string signalName, string payloadJson, CancellationToken ct)
    {
        Interlocked.Increment(ref _signalAttempts);
        var signal = new SentSignal(@namespace, workflowId, runId, signalName, payloadJson);
        if (OnSignal is { } hook) await hook(signal);
        if (SignalError is { } error) throw error;
        Signals.Enqueue(signal);
    }
}

internal sealed record GitHubCall(string Url, string? UserAgent, string? Accept, bool HasAuthorization);

/// <summary>A fake GitHub API: one response factory per <c>owner/name</c>; anything else 404s.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _repos = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<GitHubCall> Calls { get; } = new();

    /// <summary>Awaited before every response; used as a barrier and to interleave other work.</summary>
    public Func<Task>? BeforeResponse { get; set; }

    public void Set(string repo, Func<HttpResponseMessage> response) => _repos[repo] = response;

    public static Func<HttpResponseMessage> Public => () => Json(HttpStatusCode.OK, """{"id":1,"private":false}""");
    public static Func<HttpResponseMessage> Private => () => Json(HttpStatusCode.OK, """{"id":1,"private":true}""");
    public static Func<HttpResponseMessage> Status(HttpStatusCode code) => () => Json(code, """{"message":"x"}""");
    /// <summary>What <see cref="HttpClient"/> throws when its timeout elapses.</summary>
    public static Func<HttpResponseMessage> TimesOut =>
        () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException());

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls.Enqueue(new GitHubCall(
            request.RequestUri!.ToString(),
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString(),
            request.Headers.Authorization is not null));
        if (BeforeResponse is { } hook) await hook();
        var path = request.RequestUri.AbsolutePath;
        var repo = path.StartsWith("/repos/", StringComparison.Ordinal) ? Uri.UnescapeDataString(path["/repos/".Length..]) : "";
        return _repos.TryGetValue(repo, out var respond) ? respond() : Status(HttpStatusCode.NotFound)();
    }

    protected override void Dispose(bool disposing)
    {
        // Shared across clients in a test; never disposed by them.
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception));
    public IReadOnlyList<string> DecisionLines => Lines.Where(l => l.StartsWith("EpicGrant decision ", StringComparison.Ordinal)).ToList();
}

/// <summary>
/// A complete, valid epic: stored guarded definitions, a running driver, one active grant and a
/// linked PR run sitting at <c>merge-approval:2</c> with an attested head. Every refusal test breaks
/// exactly one thing about it. Generic placeholders only.
/// </summary>
internal sealed class EpicGrantWorld(Func<OrchestratorDbContext> newDb)
{
    public const string Cto = "agent1";
    public const string Author = "agent2";
    public const string Ns = "fleet";
    public const string DriverId = "epic-driver-1";
    public const string DriverRun = "0d1f3a10-0000-4000-8000-000000000001";
    public const string PrivateRepo = "example-org/example-repo";
    public const string PublicRepo = "example-org/example-public";
    public const string PrType = "UwePrImplementationWorkflow";
    public const string DesignType = "UweDesignWorkflow";
    public const string DocType = "UweDocMaintenanceWorkflow";
    public const string PrId = "pr-impl-12";
    public const string PrRun = "0d1f3a10-0000-4000-8000-000000000002";
    public const string HeadSha = "0123456789abcdef0123456789abcdef01234567";
    public const string Evidence = "https://example.com/reviews/1";

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    public FakeEpicTemporal Temporal { get; } = new();
    public FakeGitHub GitHub { get; } = new();
    public EpicGrantOptions Options { get; } = new() { Enabled = true, MaxDays = 14 };
    public IConfigurationRoot Configuration { get; } = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CTO_AGENT"] = Cto }).Build();
    public CapturingLogger<EpicGrantService> Log { get; } = new();
    public Dictionary<(string Type, int Version), string> Hashes { get; } = new();
    public DateTimeOffset GrantCreatedAt { get; private set; }
    public string GrantId { get; private set; } = "";
    public FakeRun PrRunRecord { get; private set; } = null!;
    public FakeRun DriverRunRecord { get; private set; } = null!;

    public OrchestratorDbContext NewDb() => newDb();

    public EpicGrantService Service(OrchestratorDbContext db) => new(
        db, Temporal,
        new RepoVisibilityReader(new HttpClient(GitHub), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<RepoVisibilityReader>.Instance),
        Microsoft.Extensions.Options.Options.Create(Options), Configuration, Clock, Log);

    /// <summary>Seeds definitions, the driver, the grant and the default PR run.</summary>
    public async Task InitializeAsync()
    {
        GitHub.Set(PrivateRepo, FakeGitHub.Private);
        GitHub.Set(PublicRepo, FakeGitHub.Public);
        await SeedDefinitionsAsync();

        DriverRunRecord = Temporal.Add(new FakeRun
        {
            Namespace = Ns, WorkflowId = DriverId, RunId = DriverRun, Type = "EpicDriverWorkflow",
            StartTime = Clock.Now.AddHours(-1), Input = new JsonObject { ["Epic"] = "example" },
        });

        GrantCreatedAt = Clock.Now;
        GrantId = await CreateGrantAsync(Scope());
        Clock.Advance(TimeSpan.FromMinutes(1));
        PrRunRecord = Temporal.Add(PrRunFor(PrId, PrRun));
    }

    // ── Definitions ──────────────────────────────────────────────────────────

    public static JsonObject Wait(string signal, string? visitVar, bool guarded)
    {
        var wait = new JsonObject
        {
            ["type"] = "wait_for_signal",
            ["name"] = $"{signal}_gate",
            ["signalName"] = signal,
            ["timeoutMinutes"] = 1440,
        };
        if (visitVar is not null) wait["visitVar"] = visitVar;
        if (guarded)
            wait["delegatedGuard"] = new JsonObject
            {
                ["marker"] = "GrantId",
                ["require"] = new JsonObject
                {
                    ["VisitId"] = $"{{{{vars.{visitVar}}}}}",
                    ["ArtifactRef"] = "{{vars.review_ref}}",
                },
            };
        return wait;
    }

    public static string Definition(params JsonNode[] steps) => new JsonObject
    {
        ["type"] = "sequence",
        ["name"] = "root",
        ["steps"] = new JsonArray(
            [new JsonObject { ["type"] = "delegate", ["name"] = "author", ["agent"] = "{{input.TargetAgent}}", ["instruction"] = "Do the work." },
             .. steps]),
    }.ToJsonString();

    public static string PrDefinition() => Definition(Wait("merge-approval", "merge_visit", guarded: true));
    public static string PrDefinitionUnguarded() => Definition(Wait("merge-approval", null, guarded: false));
    public static string DesignDefinition() => Definition(Wait("design-approval", "design_visit", guarded: true));
    public static string DocDefinition() => Definition(Wait("doc-review", "doc_visit", guarded: true));

    private async Task SeedDefinitionsAsync()
    {
        await using var db = NewDb();
        var pr = new WorkflowDefinition
        {
            Name = PrType, Namespace = Ns, TaskQueue = "fleet", Definition = PrDefinition(), Version = 21,
            Versions = [new WorkflowDefinitionVersion { Version = 20, Definition = PrDefinitionUnguarded() }],
        };
        db.WorkflowDefinitions.AddRange(
            pr,
            new WorkflowDefinition { Name = DesignType, Namespace = Ns, TaskQueue = "fleet", Definition = DesignDefinition(), Version = 16 },
            new WorkflowDefinition { Name = DocType, Namespace = Ns, TaskQueue = "fleet", Definition = DocDefinition(), Version = 8 });
        await db.SaveChangesAsync();
        await RefreshHashesAsync();
    }

    /// <summary>Hashes what the database returns, which on MySQL is the normalized JSON column text.</summary>
    public async Task RefreshHashesAsync()
    {
        await using var db = NewDb();
        Hashes.Clear();
        foreach (var d in await db.WorkflowDefinitions.AsNoTracking().Include(d => d.Versions).ToListAsync())
        {
            Hashes[(d.Name, d.Version)] = Sha256(d.Definition);
            foreach (var v in d.Versions) Hashes[(d.Name, v.Version)] = Sha256(v.Definition);
        }
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ── Scope and grant ──────────────────────────────────────────────────────

    public JsonObject Scope() => new()
    {
        ["driver"] = new JsonObject { ["namespace"] = Ns, ["workflowId"] = DriverId, ["runId"] = DriverRun },
        ["targets"] = new JsonArray(
            new JsonObject { ["repo"] = PrivateRepo, ["issues"] = new JsonArray(12, 13) },
            new JsonObject { ["repo"] = PublicRepo, ["issues"] = new JsonArray(7), ["allowPublic"] = true }),
        ["gates"] = new JsonArray("design-approval", "merge-approval", "doc-review"),
        ["workflows"] = new JsonArray(
            new JsonObject { ["type"] = PrType, ["version"] = 21, ["sha256"] = Hashes[(PrType, 21)] },
            new JsonObject { ["type"] = DesignType, ["version"] = 16, ["sha256"] = Hashes[(DesignType, 16)] },
            new JsonObject { ["type"] = DocType, ["version"] = 8, ["sha256"] = Hashes[(DocType, 8)] }),
        ["expiresAt"] = Clock.Now.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    public async Task<string> CreateGrantAsync(JsonObject scope)
    {
        await using var db = NewDb();
        var created = await Service(db).CreateAsync(scope.ToJsonString(), default);
        Assert.True(created.Report.Valid, string.Join("; ", created.Report.Errors));
        return created.Grant!.Id;
    }

    // ── Runs and requests ────────────────────────────────────────────────────

    public FakeRun PrRunFor(string workflowId, string runId, string repo = PrivateRepo, int issue = 12) => new()
    {
        Namespace = Ns, WorkflowId = workflowId, RunId = runId, Type = PrType,
        StartTime = GrantCreatedAt.AddMinutes(1),
        Input = new JsonObject
        {
            ["Repo"] = repo, ["IssueNumber"] = issue, ["TargetAgent"] = Author, ["WaiterWorkflowId"] = DriverId,
        },
        DefinitionName = PrType, DefinitionVersion = 21,
        GateVisit = "merge-approval:2", ReviewRef = HeadSha,
    };

    public static EpicGrantDecisionRequest Request(
        string workflowId = PrId, string gate = "merge-approval", string visitId = "merge-approval:2",
        string artifactRef = HeadSha, string evidence = Evidence, string caller = Cto, string decision = "approved",
        string ns = Ns) =>
        new(ns, workflowId, gate, decision, visitId, artifactRef, evidence, caller);

    public async Task<EpicGrantDecisionResult> DecideAsync(EpicGrantDecisionRequest? request = null, string? grantId = null)
    {
        await using var db = NewDb();
        return await Service(db).DecideAsync(grantId ?? GrantId, request ?? Request(), default);
    }

    public async Task<List<EpicGrantDecision>> DecisionRowsAsync()
    {
        await using var db = NewDb();
        return await db.EpicGrantDecisions.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }

    public async Task ExecAsync(Func<OrchestratorDbContext, Task> action)
    {
        await using var db = NewDb();
        await action(db);
    }
}

/// <summary>Scans the production sources (<c>src/**/*.cs</c>) for forbidden code shapes.</summary>
internal static class EpicSourceScan
{
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Fleet.sln"))) return dir.FullName;
        throw new InvalidOperationException($"no Fleet.sln above '{AppContext.BaseDirectory}'");
    }

    /// <summary>Every C# source under <c>src/</c>, generated migrations excluded.</summary>
    public static IReadOnlyList<(string Path, string Text)> Sources()
    {
        var src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Select(p => (Path.GetRelativePath(src, p), File.ReadAllText(p)))
            .ToList();
    }

    public static List<string> Matches(Regex pattern) =>
        Sources().SelectMany(s => pattern.Matches(s.Text).Select(m => $"{s.Path}: {m.Value}")).ToList();
}

// ─── AC-O1..O4: the service over SQLite, mocked Temporal and GitHub ─────────────────────────────

public sealed class EpicGrantServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    private EpicGrantWorld _world = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        await using (var db = new OrchestratorDbContext(_options))
            await db.Database.EnsureCreatedAsync();
        _world = new EpicGrantWorld(() => new OrchestratorDbContext(_options));
        await _world.InitializeAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task AssertRefusedAsync(EpicGrantDecisionResult result, string reason)
    {
        Assert.Equal(EpicGrantDecisionResults.Refused, result.Result);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.DecisionId);
        Assert.Empty(_world.Temporal.Signals);
        Assert.Equal(0, _world.Temporal.SignalAttempts);
        Assert.Empty(await _world.DecisionRowsAsync());
    }

    private Task<EpicGrantDecisionResult> DecideAsync(EpicGrantDecisionRequest? request = null, string? grantId = null) =>
        _world.DecideAsync(request, grantId);

    // ── AC-O2: the happy path ────────────────────────────────────────────────

    [Fact]
    public async Task Happy_path_sends_exactly_one_signal_with_the_five_fields_to_the_exact_run()
    {
        string? statusAtSend = null;
        _world.Temporal.OnSignal = async _ =>
        {
            await using var db = _world.NewDb();
            statusAtSend = (await db.EpicGrantDecisions.AsNoTracking().SingleAsync()).Status;
        };
        var result = await DecideAsync();

        Assert.Equal(EpicGrantDecisionResults.Sent, result.Result);
        Assert.Null(result.Reason);
        var signal = Assert.Single(_world.Temporal.Signals);
        Assert.Equal(EpicGrantWorld.Ns, signal.Namespace);
        Assert.Equal(EpicGrantWorld.PrId, signal.WorkflowId);
        Assert.Equal(EpicGrantWorld.PrRun, signal.RunId);
        Assert.Equal("merge-approval", signal.SignalName);
        Assert.Equal(
            $$"""{"Decision":"approved","GrantId":"{{_world.GrantId}}","VisitId":"merge-approval:2","ArtifactRef":"{{EpicGrantWorld.HeadSha}}","Evidence":"{{EpicGrantWorld.Evidence}}"}""",
            signal.PayloadJson);

        Assert.Equal(EpicGrantDecisionStatus.Reserved, statusAtSend);
        var row = Assert.Single(await _world.DecisionRowsAsync());
        Assert.Equal(result.DecisionId, row.Id);
        Assert.Equal(EpicGrantDecisionStatus.Sent, row.Status);
        Assert.Equal((_world.GrantId, "fleet", EpicGrantWorld.PrId, EpicGrantWorld.PrRun, "merge-approval", "merge-approval:2"),
            (row.GrantId.ToString("D"), row.Namespace, row.WorkflowId, row.RunId, row.Gate, row.VisitId));
        Assert.Equal((EpicGrantWorld.HeadSha, EpicGrantWorld.Evidence, EpicGrantWorld.Cto),
            (row.ArtifactRef, row.Evidence, row.Caller));

        var line = Assert.Single(_world.Log.DecisionLines);
        Assert.Equal(
            $"EpicGrant decision grant={_world.GrantId} workflow={EpicGrantWorld.PrId} gate=merge-approval result=sent reason=none",
            line);
        Assert.DoesNotContain(_world.Log.Lines, l => l.Contains(EpicGrantWorld.Evidence, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pins_the_latest_run_and_signals_that_run_id()
    {
        _world.PrRunRecord.Status = "Completed";
        var rerun = _world.Temporal.Add(_world.PrRunFor(EpicGrantWorld.PrId, "0d1f3a10-0000-4000-8000-0000000000aa"));

        var result = await DecideAsync();

        Assert.Equal(EpicGrantDecisionResults.Sent, result.Result);
        Assert.Equal(rerun.RunId, Assert.Single(_world.Temporal.Signals).RunId);
        Assert.Equal(rerun.RunId, Assert.Single(await _world.DecisionRowsAsync()).RunId);
    }

    [Fact]
    public async Task Design_gate_reads_ExistingIssueNumber_as_a_numeric_string()
    {
        _world.Temporal.Add(new FakeRun
        {
            Namespace = "fleet", WorkflowId = "design-13", RunId = "0d1f3a10-0000-4000-8000-000000000013",
            Type = EpicGrantWorld.DesignType, StartTime = _world.GrantCreatedAt.AddMinutes(2),
            Input = new JsonObject
            {
                ["Repo"] = EpicGrantWorld.PrivateRepo, ["ExistingIssueNumber"] = "13",
                ["TargetAgent"] = EpicGrantWorld.Author, ["WaiterWorkflowId"] = EpicGrantWorld.DriverId,
            },
            DefinitionName = EpicGrantWorld.DesignType, DefinitionVersion = 16,
            GateVisit = "design-approval:1", ReviewRef = new string('a', 64),
        });

        var result = await DecideAsync(EpicGrantWorld.Request("design-13", "design-approval", "design-approval:1", new string('a', 64)));

        Assert.Equal(EpicGrantDecisionResults.Sent, result.Result);
        Assert.Equal("design-approval", Assert.Single(_world.Temporal.Signals).SignalName);
    }

    [Fact]
    public async Task Doc_run_links_through_its_parent_pr_run_and_its_prep_agent_is_the_author()
    {
        AddDocRun(prepAgent: EpicGrantWorld.Author);

        var result = await DecideAsync(DocRequest());

        Assert.Equal(EpicGrantDecisionResults.Sent, result.Result);
        var signal = Assert.Single(_world.Temporal.Signals);
        Assert.Equal(("doc-12", "doc-review"), (signal.WorkflowId, signal.SignalName));
    }

    [Fact]
    public async Task A_run_whose_parent_is_the_driver_run_itself_is_linked()
    {
        _world.PrRunRecord.Input!.Remove("WaiterWorkflowId");
        _world.PrRunRecord.ParentId = EpicGrantWorld.DriverId;
        _world.PrRunRecord.ParentRunId = EpicGrantWorld.DriverRun;

        Assert.Equal(EpicGrantDecisionResults.Sent, (await DecideAsync()).Result);
    }

    [Fact]
    public async Task A_grandparent_with_the_driver_as_waiter_links_but_three_levels_do_not()
    {
        _world.PrRunRecord.Input!.Remove("WaiterWorkflowId");
        _world.PrRunRecord.ParentId = "p1";
        _world.PrRunRecord.ParentRunId = "r1";
        _world.Temporal.Add(new FakeRun { Namespace = "fleet", WorkflowId = "p1", RunId = "r1", Type = "X", ParentId = "p2", ParentRunId = "r2" });
        var p2 = _world.Temporal.Add(new FakeRun
        {
            Namespace = "fleet", WorkflowId = "p2", RunId = "r2", Type = "X",
            Input = new JsonObject { ["WaiterWorkflowId"] = EpicGrantWorld.DriverId },
        });

        Assert.Equal(EpicGrantDecisionResults.Sent, (await DecideAsync()).Result);

        // Push the linked ancestor one level further up: three levels is out of reach.
        p2.Input = new JsonObject();
        p2.ParentId = "p3";
        p2.ParentRunId = "r3";
        _world.Temporal.Add(new FakeRun
        {
            Namespace = "fleet", WorkflowId = "p3", RunId = "r3", Type = "X",
            Input = new JsonObject { ["WaiterWorkflowId"] = EpicGrantWorld.DriverId },
        });
        _world.PrRunRecord.GateVisit = "merge-approval:3";
        var result = await DecideAsync(EpicGrantWorld.Request(visitId: "merge-approval:3"));
        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.NotLinked), (result.Result, result.Reason));
        Assert.Single(_world.Temporal.Signals);
    }

    [Fact]
    public async Task A_public_target_with_allowPublic_and_a_pass_scrub_is_sent()
    {
        _world.Temporal.Add(PublicRun(scrub: "pass"));

        Assert.Equal(EpicGrantDecisionResults.Sent, (await DecideAsync(EpicGrantWorld.Request("pr-public-7"))).Result);
    }

    [Fact]
    public async Task The_archived_version_row_is_the_stored_definition_after_the_main_row_moves_on()
    {
        await _world.ExecAsync(async db =>
        {
            var main = await db.WorkflowDefinitions.SingleAsync(d => d.Name == EpicGrantWorld.PrType);
            db.WorkflowDefinitionVersions.Add(new WorkflowDefinitionVersion
            {
                WorkflowDefinitionId = main.Id, Version = 21, Definition = main.Definition,
            });
            main.Definition = EpicGrantWorld.PrDefinitionUnguarded();
            main.Version = 22;
            await db.SaveChangesAsync();
        });

        Assert.Equal(EpicGrantDecisionResults.Sent, (await DecideAsync()).Result);
    }

    // ── AC-O1: each check failing alone (D1) ─────────────────────────────────

    [Fact]
    public async Task D1_feature_disabled_is_refused_disabled()
    {
        _world.Options.Enabled = false;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Disabled);
    }

    [Fact]
    public async Task D1_enabled_with_non_positive_max_days_counts_as_disabled()
    {
        _world.Options.MaxDays = 0;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Disabled);
    }

    [Fact]
    public async Task D1_a_caller_other_than_the_configured_cto_is_refused()
    {
        await AssertRefusedAsync(await DecideAsync(EpicGrantWorld.Request(caller: EpicGrantWorld.Author)), EpicGrantRefusal.Caller);
    }

    [Fact]
    public async Task D1_a_blank_configured_cto_is_refused_caller()
    {
        _world.Configuration["FLEET_CTO_AGENT"] = " ";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Caller);
    }

    [Fact]
    public async Task D1_a_cto_changed_since_the_grant_was_created_is_refused_caller()
    {
        _world.Configuration["FLEET_CTO_AGENT"] = "agent3";
        await AssertRefusedAsync(await DecideAsync(EpicGrantWorld.Request(caller: "agent3")), EpicGrantRefusal.Caller);
    }

    [Theory]
    [InlineData("decision", "rejected")]
    [InlineData("decision", "Approved")]
    [InlineData("gate", "advisory-review")]
    [InlineData("visitId", "")]
    [InlineData("artifactRef", "")]
    [InlineData("evidence", "")]
    [InlineData("evidence", "http://example.com/reviews/1")]
    [InlineData("evidence", "/reviews/1")]
    [InlineData("evidence", "not a url")]
    [InlineData("evidence", "LONG")]
    [InlineData("visitId", "LONG")]
    public async Task D1_a_malformed_request_is_refused_bad_request(string field, string value)
    {
        if (value == "LONG") value = "https://example.com/" + new string('x', 500);
        var request = field switch
        {
            "decision" => EpicGrantWorld.Request(decision: value),
            "gate" => EpicGrantWorld.Request(gate: value),
            "visitId" => EpicGrantWorld.Request(visitId: value),
            "artifactRef" => EpicGrantWorld.Request(artifactRef: value),
            _ => EpicGrantWorld.Request(evidence: value),
        };
        await AssertRefusedAsync(await DecideAsync(request), EpicGrantRefusal.BadRequest);
    }

    // ── D2 ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("new")]
    [InlineData("not-a-grant-id")]
    [InlineData("")]
    public async Task D2_an_unknown_grant_is_refused_grant_inactive(string grantId)
    {
        if (grantId == "new") grantId = Guid.NewGuid().ToString();
        await AssertRefusedAsync(await DecideAsync(grantId: grantId), EpicGrantRefusal.GrantInactive);
    }

    [Fact]
    public async Task D2_a_revoked_grant_is_refused_grant_inactive()
    {
        await _world.ExecAsync(async db => await _world.Service(db).RevokeAsync(_world.GrantId, "done", default));
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.GrantInactive);
    }

    [Fact]
    public async Task D2_an_expired_grant_is_refused_expired()
    {
        _world.Clock.Now = _world.GrantCreatedAt.AddDays(7);
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Expired);
    }

    [Fact]
    public async Task D2_a_scope_that_no_longer_hashes_to_its_pin_is_refused_scope_tampered()
    {
        await _world.ExecAsync(async db =>
        {
            var grant = await db.EpicGrants.SingleAsync();
            grant.ScopeJson = grant.ScopeJson.Replace("[12,13]", "[12,13,14]", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        });
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.ScopeTampered);
    }

    // ── D3 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D3_an_ended_driver_is_refused_driver_not_running()
    {
        _world.DriverRunRecord.Status = "Completed";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.DriverNotRunning);
    }

    [Fact]
    public async Task D3_a_driver_describe_failure_is_refused_unknown()
    {
        _world.Temporal.FailWorkflowId = EpicGrantWorld.DriverId;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Unknown);
    }

    // ── D4 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D4_a_run_started_before_the_grant_is_refused_not_linked()
    {
        _world.PrRunRecord.StartTime = _world.GrantCreatedAt.AddSeconds(-1);
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.NotLinked);
    }

    [Fact]
    public async Task D4_an_unlinked_run_is_refused_not_linked()
    {
        _world.PrRunRecord.Input!["WaiterWorkflowId"] = "some-other-driver";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.NotLinked);
    }

    [Fact]
    public async Task D4_a_target_that_is_not_running_is_refused_not_linked()
    {
        _world.PrRunRecord.Status = "Completed";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.NotLinked);
    }

    [Fact]
    public async Task D4_a_missing_history_page_is_refused_unknown()
    {
        _world.PrRunRecord.HistoryMissing = true;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Unknown);
    }

    [Fact]
    public async Task D4_a_target_describe_failure_is_refused_unknown()
    {
        _world.Temporal.FailWorkflowId = EpicGrantWorld.PrId;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Unknown);
    }

    // ── D5 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D5_definition_version_n_plus_1_is_refused_definition_out_of_scope()
    {
        await _world.ExecAsync(async db =>
        {
            var main = await db.WorkflowDefinitions.SingleAsync(d => d.Name == EpicGrantWorld.PrType);
            db.WorkflowDefinitionVersions.Add(new WorkflowDefinitionVersion { WorkflowDefinitionId = main.Id, Version = 21, Definition = main.Definition });
            main.Version = 22;
            await db.SaveChangesAsync();
        });
        _world.PrRunRecord.DefinitionVersion = 22;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.DefinitionOutOfScope);
    }

    [Fact]
    public async Task D5_a_stored_definition_whose_content_changed_is_refused_definition_out_of_scope()
    {
        await _world.ExecAsync(async db =>
        {
            var main = await db.WorkflowDefinitions.SingleAsync(d => d.Name == EpicGrantWorld.PrType);
            main.Definition = EpicGrantWorld.Definition(EpicGrantWorld.Wait("merge-approval", "merge_visit", true),
                new JsonObject { ["type"] = "noop", ["name"] = "extra" });
            await db.SaveChangesAsync();
        });
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.DefinitionOutOfScope);
    }

    [Fact]
    public async Task D5_a_loaded_definition_name_other_than_the_run_type_is_refused_definition_out_of_scope()
    {
        _world.PrRunRecord.DefinitionName = EpicGrantWorld.DesignType;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.DefinitionOutOfScope);
    }

    [Fact]
    public async Task D5_a_run_without_a_LoadWorkflowDefinition_result_is_refused_definition_out_of_scope()
    {
        _world.PrRunRecord.DefinitionName = null;
        _world.PrRunRecord.DefinitionVersion = null;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.DefinitionOutOfScope);
    }

    [Fact]
    public async Task D5_a_gate_outside_the_scope_gates_is_refused_gate_out_of_scope()
    {
        var scope = _world.Scope();
        scope["gates"] = new JsonArray("design-approval", "merge-approval");
        var grantId = await _world.CreateGrantAsync(scope);
        AddDocRun(prepAgent: EpicGrantWorld.Author);

        await AssertRefusedAsync(await DecideAsync(DocRequest(), grantId), EpicGrantRefusal.GateOutOfScope);
    }

    [Fact]
    public async Task D5_a_gate_the_run_definition_does_not_guard_is_refused_gate_out_of_scope()
    {
        _world.PrRunRecord.GateVisit = "design-approval:2";
        await AssertRefusedAsync(
            await DecideAsync(EpicGrantWorld.Request(gate: "design-approval", visitId: "design-approval:2")),
            EpicGrantRefusal.GateOutOfScope);
    }

    // ── D6 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D6_an_issue_outside_the_targets_is_refused_target_out_of_scope()
    {
        _world.PrRunRecord.Input!["IssueNumber"] = 99;
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.TargetOutOfScope);
    }

    [Fact]
    public async Task D6_a_repo_outside_the_targets_is_refused_target_out_of_scope()
    {
        _world.PrRunRecord.Input!["Repo"] = "example-org/other";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.TargetOutOfScope);
    }

    [Fact]
    public async Task D6_a_public_repo_without_allowPublic_is_refused_target_out_of_scope()
    {
        // The repo was private at creation and has since been made public.
        _world.GitHub.Set(EpicGrantWorld.PrivateRepo, FakeGitHub.Public);
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.TargetOutOfScope);
    }

    [Fact]
    public async Task D6_a_denied_repo_is_refused_even_with_allowPublic()
    {
        _world.Temporal.Add(PublicRun(scrub: "pass"));
        _world.Options.DeniedRepos = $"example-org/unrelated, {EpicGrantWorld.PublicRepo.ToUpperInvariant()}";
        await AssertRefusedAsync(await DecideAsync(EpicGrantWorld.Request("pr-public-7")), EpicGrantRefusal.TargetOutOfScope);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(301)]
    [InlineData(0)]
    public async Task D6_an_unreadable_visibility_is_refused_visibility_unknown(int status)
    {
        _world.GitHub.Set(EpicGrantWorld.PrivateRepo,
            status == 0 ? FakeGitHub.TimesOut : FakeGitHub.Status((HttpStatusCode)status));
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.VisibilityUnknown);
    }

    [Fact]
    public async Task D6_the_visibility_read_is_unauthenticated_and_sends_the_github_headers()
    {
        Assert.Equal(EpicGrantDecisionResults.Sent, (await DecideAsync()).Result);
        var call = _world.GitHub.Calls.Last();
        Assert.Equal($"https://api.github.com/repos/{EpicGrantWorld.PrivateRepo}", call.Url);
        Assert.False(call.HasAuthorization);
        Assert.False(string.IsNullOrWhiteSpace(call.UserAgent));
        Assert.Equal("application/vnd.github+json", call.Accept);
    }

    // ── D7 ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D7_the_cto_as_author_is_refused_author_is_decider()
    {
        _world.PrRunRecord.Input!["TargetAgent"] = EpicGrantWorld.Cto.ToUpperInvariant();
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.AuthorIsDecider);
    }

    [Fact]
    public async Task D7_a_missing_author_is_refused_author_is_decider()
    {
        _world.PrRunRecord.Input!.Remove("TargetAgent");
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.AuthorIsDecider);
    }

    [Fact]
    public async Task D7_a_doc_run_with_no_prep_agent_defaults_to_the_cto_and_is_refused()
    {
        AddDocRun(prepAgent: "");
        await AssertRefusedAsync(await DecideAsync(DocRequest()), EpicGrantRefusal.AuthorIsDecider);
    }

    // ── D8 / D9 / D10 ────────────────────────────────────────────────────────

    [Fact]
    public async Task D8_an_advanced_gate_visit_is_refused_stale_visit()
    {
        _world.PrRunRecord.GateVisit = "merge-approval:3";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.StaleVisit);
    }

    [Fact]
    public async Task D8_an_ended_gate_visit_is_refused_stale_visit()
    {
        _world.PrRunRecord.GateVisit = "";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.StaleVisit);
    }

    [Fact]
    public async Task D8_a_visit_of_another_gate_is_refused_stale_visit()
    {
        _world.PrRunRecord.GateVisit = "doc-review:2";
        await AssertRefusedAsync(await DecideAsync(EpicGrantWorld.Request(visitId: "doc-review:2")), EpicGrantRefusal.StaleVisit);
    }

    [Fact]
    public async Task D9_a_review_ref_changed_after_a_new_push_and_review_is_refused_stale_artifact()
    {
        _world.PrRunRecord.ReviewRef = "fedcba9876543210fedcba9876543210fedcba98";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.StaleArtifact);
    }

    [Fact]
    public async Task D9_an_unpublished_review_ref_is_refused_stale_artifact()
    {
        _world.PrRunRecord.ReviewRef = "";
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.StaleArtifact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fail")]
    [InlineData("PASS")]
    public async Task D10_allowPublic_without_a_pass_scrub_is_refused_scrub_missing(string? scrub)
    {
        _world.Temporal.Add(PublicRun(scrub));
        await AssertRefusedAsync(await DecideAsync(EpicGrantWorld.Request("pr-public-7")), EpicGrantRefusal.ScrubMissing);
    }

    [Fact]
    public async Task Every_refusal_logs_exactly_one_line_and_never_the_evidence()
    {
        _world.PrRunRecord.GateVisit = "merge-approval:3";
        await DecideAsync();

        var line = Assert.Single(_world.Log.DecisionLines);
        Assert.Equal(
            $"EpicGrant decision grant={_world.GrantId} workflow={EpicGrantWorld.PrId} gate=merge-approval result=refused reason=stale_visit",
            line);
        Assert.DoesNotContain(_world.Log.Lines, l => l.Contains(EpicGrantWorld.Evidence, StringComparison.Ordinal));
    }

    // ── D11 on SQLite ────────────────────────────────────────────────────────

    [Fact]
    public async Task D11_a_second_request_for_the_same_visit_is_already_decided()
    {
        Assert.Equal(EpicGrantDecisionResults.Sent, (await DecideAsync()).Result);
        var second = await DecideAsync();

        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.AlreadyDecided), (second.Result, second.Reason));
        Assert.Single(_world.Temporal.Signals);
        Assert.Single(await _world.DecisionRowsAsync());
    }

    [Fact]
    public async Task D11_rechecks_expiry_inside_the_transaction()
    {
        // D2 passes; the clock crosses ExpiresAt before the reservation.
        _world.GitHub.BeforeResponse = () =>
        {
            _world.Clock.Now = _world.GrantCreatedAt.AddDays(7);
            return Task.CompletedTask;
        };
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.Expired);
    }

    [Fact]
    public async Task A_send_error_marks_the_row_send_failed_and_is_never_retried()
    {
        _world.Temporal.SignalError = new InvalidOperationException("signal failed");
        var result = await DecideAsync();

        Assert.Equal((EpicGrantDecisionResults.SendFailed, EpicGrantDecisionResults.SendFailed), (result.Result, result.Reason));
        Assert.NotNull(result.DecisionId);
        Assert.Equal(1, _world.Temporal.SignalAttempts);
        Assert.Equal(EpicGrantDecisionStatus.SendFailed, Assert.Single(await _world.DecisionRowsAsync()).Status);
    }

    // ── AC-O4: revocation ────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_then_decision_is_grant_inactive()
    {
        await _world.ExecAsync(async db =>
        {
            var (outcome, view) = await _world.Service(db).RevokeAsync(_world.GrantId, "epic paused", default);
            Assert.Equal(EpicGrantRevokeOutcome.Revoked, outcome);
            Assert.Equal(("revoked", "revoked", "epic paused"), (view!.Status, view.EffectiveStatus, view.RevokeReason));
            Assert.NotNull(view.RevokedAt);
        });

        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.GrantInactive);
    }

    [Fact]
    public async Task A_revoke_that_commits_before_the_reservation_wins()
    {
        // D2 has already seen the grant active; the revoke commits before D11 runs.
        _world.GitHub.BeforeResponse = async () =>
        {
            _world.GitHub.BeforeResponse = null;
            await _world.ExecAsync(async db => await _world.Service(db).RevokeAsync(_world.GrantId, null, default));
        };
        await AssertRefusedAsync(await DecideAsync(), EpicGrantRefusal.GrantInactive);
    }

    [Fact]
    public async Task A_decision_reserved_before_the_revoke_is_sent_once()
    {
        _world.Temporal.OnSignal = async _ =>
        {
            _world.Temporal.OnSignal = null;
            await _world.ExecAsync(async db =>
                Assert.Equal(EpicGrantRevokeOutcome.Revoked, (await _world.Service(db).RevokeAsync(_world.GrantId, null, default)).Outcome));
        };

        var result = await DecideAsync();

        Assert.Equal(EpicGrantDecisionResults.Sent, result.Result);
        Assert.Single(_world.Temporal.Signals);
        Assert.Equal(EpicGrantDecisionStatus.Sent, Assert.Single(await _world.DecisionRowsAsync()).Status);

        // The next visit is no longer decidable.
        _world.PrRunRecord.GateVisit = "merge-approval:3";
        var next = await DecideAsync(EpicGrantWorld.Request(visitId: "merge-approval:3"));
        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.GrantInactive), (next.Result, next.Reason));
        Assert.Single(_world.Temporal.Signals);
    }

    [Fact]
    public async Task Revoking_twice_conflicts_and_an_unknown_grant_is_not_found()
    {
        await _world.ExecAsync(async db =>
        {
            var service = _world.Service(db);
            Assert.Equal(EpicGrantRevokeOutcome.Revoked, (await service.RevokeAsync(_world.GrantId, "first", default)).Outcome);
            var (outcome, view) = await service.RevokeAsync(_world.GrantId, "second", default);
            Assert.Equal(EpicGrantRevokeOutcome.AlreadyRevoked, outcome);
            Assert.Equal("first", view!.RevokeReason);
            Assert.Equal(EpicGrantRevokeOutcome.NotFound, (await service.RevokeAsync(Guid.NewGuid().ToString(), null, default)).Outcome);
        });
    }

    // ── AC-O3: scope validation at creation ──────────────────────────────────

    public static TheoryData<string> InvalidScopes => new()
    {
        "unknown root field", "unknown driver field", "unknown target field", "unknown workflow field",
        "missing driver", "missing expiresAt", "empty targets", "empty gates", "empty workflows", "empty issues",
        "non-delegable gate", "duplicate gate", "zero issue", "negative issue", "string issue", "fractional issue",
        "repo without owner", "allowPublic not boolean", "version zero", "short sha256", "driver not running",
        "driver run id unknown", "driver describe fails", "expired", "beyond max days", "denied repo with allowPublic",
        "public repo without allowPublic", "allowPublic on private repo", "visibility unknown", "sha256 mismatch",
        "version not stored", "not delegation-capable", "gate not served", "no cto configured", "feature disabled",
        "not an object",
    };

    [Theory]
    [MemberData(nameof(InvalidScopes))]
    public async Task Creation_rejects_every_rule_and_stores_nothing(string rule)
    {
        var scope = _world.Scope();
        JsonObject Target(int i) => scope["targets"]![i]!.AsObject();
        JsonObject Workflow(int i) => scope["workflows"]![i]!.AsObject();
        var raw = (string?)null;
        switch (rule)
        {
            case "unknown root field": scope["note"] = "x"; break;
            case "unknown driver field": scope["driver"]!["Namespace"] = "fleet"; break;
            case "unknown target field": Target(0)["branch"] = "main"; break;
            case "unknown workflow field": Workflow(0)["name"] = "x"; break;
            case "missing driver": scope.Remove("driver"); break;
            case "missing expiresAt": scope.Remove("expiresAt"); break;
            case "empty targets": scope["targets"] = new JsonArray(); break;
            case "empty gates": scope["gates"] = new JsonArray(); break;
            case "empty workflows": scope["workflows"] = new JsonArray(); break;
            case "empty issues": Target(0)["issues"] = new JsonArray(); break;
            case "non-delegable gate": scope["gates"]!.AsArray().Add("advisory-review"); break;
            case "duplicate gate": scope["gates"]!.AsArray().Add("merge-approval"); break;
            case "zero issue": Target(0)["issues"] = new JsonArray(0); break;
            case "negative issue": Target(0)["issues"] = new JsonArray(-12); break;
            case "string issue": Target(0)["issues"] = new JsonArray("12"); break;
            case "fractional issue": Target(0)["issues"] = new JsonArray(12.5); break;
            case "repo without owner": Target(0)["repo"] = "example-repo"; break;
            case "allowPublic not boolean": Target(1)["allowPublic"] = "true"; break;
            case "version zero": Workflow(0)["version"] = 0; break;
            case "short sha256": Workflow(0)["sha256"] = "abc"; break;
            case "driver not running": _world.DriverRunRecord.Status = "Terminated"; break;
            case "driver run id unknown": scope["driver"]!["runId"] = "0d1f3a10-0000-4000-8000-0000000000ff"; break;
            case "driver describe fails": _world.Temporal.FailWorkflowId = EpicGrantWorld.DriverId; break;
            case "expired": scope["expiresAt"] = _world.Clock.Now.AddMinutes(-1).ToString("O"); break;
            case "beyond max days": scope["expiresAt"] = _world.Clock.Now.AddDays(14).AddMinutes(1).ToString("O"); break;
            case "denied repo with allowPublic": _world.Options.DeniedRepos = EpicGrantWorld.PublicRepo; break;
            case "public repo without allowPublic": Target(1).Remove("allowPublic"); break;
            case "allowPublic on private repo": Target(0)["allowPublic"] = true; break;
            case "visibility unknown": _world.GitHub.Set(EpicGrantWorld.PrivateRepo, FakeGitHub.Status(HttpStatusCode.Forbidden)); break;
            case "sha256 mismatch": Workflow(0)["sha256"] = new string('0', 64); break;
            case "version not stored": Workflow(0)["version"] = 99; break;
            case "not delegation-capable":
                Workflow(0)["version"] = 20;
                Workflow(0)["sha256"] = _world.Hashes[(EpicGrantWorld.PrType, 20)];
                break;
            case "gate not served": scope["workflows"]!.AsArray().RemoveAt(2); break;
            case "no cto configured": _world.Configuration["FLEET_CTO_AGENT"] = ""; break;
            case "feature disabled": _world.Options.Enabled = false; break;
            case "not an object": raw = "[1,2,3]"; break;
            default: throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }

        await using var db = _world.NewDb();
        var before = await db.EpicGrants.CountAsync();
        var created = await _world.Service(db).CreateAsync(raw ?? scope.ToJsonString(), default);

        Assert.Null(created.Grant);
        Assert.False(created.Report.Valid);
        Assert.NotEmpty(created.Report.Errors);
        Assert.Equal(before, await db.EpicGrants.CountAsync());
    }

    [Fact]
    public async Task Creation_rejects_a_duplicate_field()
    {
        var raw = _world.Scope().ToJsonString().Replace("\"gates\":", "\"gates\":[\"doc-review\"],\"gates\":", StringComparison.Ordinal);
        await using var db = _world.NewDb();
        var created = await _world.Service(db).CreateAsync(raw, default);
        Assert.Null(created.Grant);
        Assert.Contains(created.Report.Errors, e => e.Contains("duplicate field 'gates'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Creation_stores_the_body_verbatim_and_its_sha256()
    {
        // Odd but valid formatting must survive byte for byte.
        var raw = "{\n  \"driver\" : " + _world.Scope()["driver"]!.ToJsonString() + ",\n"
                  + string.Join(",\n", _world.Scope().Where(p => p.Key != "driver").Select(p => $"  \"{p.Key}\":   {p.Value!.ToJsonString()}"))
                  + "\n}\n";

        await using var db = _world.NewDb();
        var created = await _world.Service(db).CreateAsync(raw, default);

        Assert.True(created.Report.Valid, string.Join("; ", created.Report.Errors));
        var grant = await db.EpicGrants.AsNoTracking().SingleAsync(g => g.Id == Guid.Parse(created.Grant!.Id));
        Assert.Equal(raw, grant.ScopeJson);
        Assert.Equal(EpicGrantWorld.Sha256(raw), grant.ScopeSha256);
        Assert.Equal(created.Report.ScopeSha256, grant.ScopeSha256);
        Assert.Equal((EpicGrantStatus.Active, EpicGrantWorld.Cto), (grant.Status, grant.CtoAgent));
        Assert.Equal((EpicGrantWorld.Ns, EpicGrantWorld.DriverId, EpicGrantWorld.DriverRun),
            (grant.DriverNamespace, grant.DriverWorkflowId, grant.DriverRunId));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", created.Grant!.Id);
    }

    [Fact]
    public async Task Validation_reports_driver_workflows_and_targets_and_stores_nothing()
    {
        await using var db = _world.NewDb();
        var before = await db.EpicGrants.CountAsync();
        var report = await _world.Service(db).ValidateAsync(_world.Scope().ToJsonString(), default);

        Assert.True(report.Valid, string.Join("; ", report.Errors));
        Assert.Equal("Running", report.Driver!.Status);
        var pr = report.Workflows.Single(w => w.Type == EpicGrantWorld.PrType);
        Assert.True(pr.HashMatches && pr.DelegationCapable);
        Assert.Equal(["merge-approval"], pr.Gates);
        Assert.Equal(pr.Sha256, pr.StoredSha256);
        Assert.Equal("private", report.Targets.Single(t => t.Repo == EpicGrantWorld.PrivateRepo).Visibility);
        Assert.Equal("public", report.Targets.Single(t => t.Repo == EpicGrantWorld.PublicRepo).Visibility);
        Assert.Equal(before, await db.EpicGrants.CountAsync());
    }

    [Fact]
    public void No_production_code_assigns_ScopeJson_outside_grant_creation()
    {
        var assignments = EpicSourceScan.Matches(new Regex(@"\bScopeJson\s*=(?!=)[^,;\r\n]*"));
        var hit = Assert.Single(assignments);
        Assert.StartsWith(Path.Combine("Fleet.Orchestrator", "Services", "EpicGrantService.cs"), hit);
        Assert.Contains("ScopeJson = body", hit, StringComparison.Ordinal);
        Assert.Empty(EpicSourceScan.Matches(new Regex(@"SET\s+`?ScopeJson`?\s*=", RegexOptions.IgnoreCase)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private FakeRun PublicRun(string? scrub)
    {
        var run = _world.PrRunFor("pr-public-7", "0d1f3a10-0000-4000-8000-000000000007", EpicGrantWorld.PublicRepo, 7);
        run.ReviewScrub = scrub;
        return run;
    }

    private void AddDocRun(string prepAgent) => _world.Temporal.Add(new FakeRun
    {
        Namespace = "fleet", WorkflowId = "doc-12", RunId = "0d1f3a10-0000-4000-8000-000000000d12",
        Type = EpicGrantWorld.DocType, StartTime = _world.GrantCreatedAt.AddMinutes(5),
        ParentId = EpicGrantWorld.PrId, ParentRunId = EpicGrantWorld.PrRun,
        Input = new JsonObject
        {
            ["Repo"] = EpicGrantWorld.PrivateRepo, ["IssueNumber"] = 12, ["PrepAgent"] = prepAgent,
            ["ConsensusAgents"] = "agent2,agent3",
        },
        DefinitionName = EpicGrantWorld.DocType, DefinitionVersion = 8,
        GateVisit = "doc-review:1", ReviewRef = new string('b', 64),
    });

    private static EpicGrantDecisionRequest DocRequest() =>
        EpicGrantWorld.Request("doc-12", "doc-review", "doc-review:1", new string('b', 64));
}

// ─── The HTTP surface over the same service ────────────────────────────────────────────────────

public sealed class EpicGrantEndpointTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private EpicGrantWorld _world = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        await using (var db = new OrchestratorDbContext(options))
            await db.Database.EnsureCreatedAsync();
        _world = new EpicGrantWorld(() => new OrchestratorDbContext(options));
        await _world.InitializeAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["FLEET_CTO_AGENT"] = EpicGrantWorld.Cto;
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<IEpicGrantTemporal>(_world.Temporal);
        builder.Services.AddSingleton<TimeProvider>(_world.Clock);
        builder.Services.AddSingleton(Options.Create(_world.Options));
        builder.Services.AddHttpClient<RepoVisibilityReader>().ConfigurePrimaryHttpMessageHandler(() => _world.GitHub);
        builder.Services.AddScoped<EpicGrantService>();
        _app = builder.Build();
        _app.MapEpicGrantEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostDecisionAsync(object body) =>
        _client.PostAsync($"/api/epic-grants/{_world.GrantId}/decisions", Json(JsonSerializer.Serialize(body)));

    private static object Forward(string visitId = "merge-approval:2") => new
    {
        @namespace = EpicGrantWorld.Ns, workflowId = EpicGrantWorld.PrId, gate = "merge-approval", decision = "approved",
        visitId, artifactRef = EpicGrantWorld.HeadSha, evidence = EpicGrantWorld.Evidence, caller = EpicGrantWorld.Cto,
    };

    [Fact]
    public void Routes_are_exactly_the_contract_and_nothing_can_edit_or_delete()
    {
        var routes = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/epic-grants", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Select(m => $"{m} {e.RoutePattern.RawText}"))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                "GET /api/epic-grants", "GET /api/epic-grants/{id}",
                "POST /api/epic-grants", "POST /api/epic-grants/validate",
                "POST /api/epic-grants/{id}/decisions", "POST /api/epic-grants/{id}/revoke",
            }.OrderBy(r => r, StringComparer.Ordinal),
            routes);
        Assert.All(routes.Where(r => r.StartsWith("POST", StringComparison.Ordinal)),
            r => Assert.True(OrchestratorAuth.RequiresBearerToken("POST", r[5..].Replace("{id}", "x"))));
        Assert.False(OrchestratorAuth.RequiresBearerToken("GET", "/api/epic-grants"));
    }

    [Fact]
    public async Task Decision_sent_is_200_with_the_decision_id()
    {
        var response = await PostDecisionAsync(Forward());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sent", body.GetProperty("result").GetString());
        Assert.True(body.GetProperty("decisionId").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reason").ValueKind);
        Assert.Single(_world.Temporal.Signals);
    }

    [Fact]
    public async Task Decision_refused_is_409_with_the_reason()
    {
        _world.PrRunRecord.GateVisit = "merge-approval:3";
        var response = await PostDecisionAsync(Forward());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("""{"result":"refused","reason":"stale_visit"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Decision_send_failure_is_502_with_the_decision_id()
    {
        _world.Temporal.SignalError = new InvalidOperationException("unavailable");
        var response = await PostDecisionAsync(Forward());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("send_failed", "send_failed"), (body.GetProperty("result").GetString(), body.GetProperty("reason").GetString()));
        Assert.True(body.GetProperty("decisionId").GetInt64() > 0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"visitId":7}""")]
    public async Task Decision_with_an_unparsable_body_is_400_bad_request(string body)
    {
        var response = await _client.PostAsync($"/api/epic-grants/{_world.GrantId}/decisions", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"result":"refused","reason":"bad_request"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _world.Temporal.SignalAttempts);
    }

    [Fact]
    public async Task Create_is_503_when_disabled_400_when_invalid_and_201_when_valid()
    {
        var scope = _world.Scope();

        _world.Options.Enabled = false;
        var disabled = await _client.PostAsync("/api/epic-grants", Json(scope.ToJsonString()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal("""{"error":"epic grants are disabled"}""", await disabled.Content.ReadAsStringAsync());

        _world.Options.Enabled = true;
        var invalidScope = _world.Scope();
        invalidScope["gates"] = new JsonArray("human-review");
        var invalid = await _client.PostAsync("/api/epic-grants", Json(invalidScope.ToJsonString()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var report = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("valid").GetBoolean());
        Assert.NotEqual(0, report.GetProperty("errors").GetArrayLength());

        var created = await _client.PostAsync("/api/epic-grants", Json(scope.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var grant = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(EpicGrantWorld.Sha256(scope.ToJsonString()), grant.GetProperty("scopeSha256").GetString());
        Assert.Equal("active", grant.GetProperty("effectiveStatus").GetString());
        Assert.Equal(EpicGrantWorld.DriverId, grant.GetProperty("scope").GetProperty("driver").GetProperty("workflowId").GetString());
        Assert.Equal(0, grant.GetProperty("decisions").GetArrayLength());
        Assert.EndsWith("Z", grant.GetProperty("expiresAt").GetString());
        Assert.Equal($"/api/epic-grants/{grant.GetProperty("id").GetString()}", created.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Validate_is_200_for_any_json_and_400_otherwise()
    {
        var ok = await _client.PostAsync("/api/epic-grants/validate", Json(_world.Scope().ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var report = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(report.GetProperty("valid").GetBoolean());
        Assert.Equal("Running", report.GetProperty("driver").GetProperty("status").GetString());
        Assert.Equal(3, report.GetProperty("workflows").GetArrayLength());
        Assert.Equal(2, report.GetProperty("targets").GetArrayLength());

        var invalid = await _client.PostAsync("/api/epic-grants/validate", Json("""{"driver":1}"""));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.False((await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("valid").GetBoolean());

        var notJson = await _client.PostAsync("/api/epic-grants/validate", Json("{"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);

        await using var db = _world.NewDb();
        Assert.Equal(1, await db.EpicGrants.CountAsync());
    }

    [Fact]
    public async Task Revoke_is_200_then_409_and_404_for_an_unknown_grant()
    {
        var revoked = await _client.PostAsync($"/api/epic-grants/{_world.GrantId}/revoke", Json("""{"reason":"stop"}"""));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var view = await revoked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("revoked", "revoked", "stop"),
            (view.GetProperty("status").GetString(), view.GetProperty("effectiveStatus").GetString(), view.GetProperty("revokeReason").GetString()));

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/epic-grants/{_world.GrantId}/revoke", Json(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/epic-grants/{Guid.NewGuid()}/revoke", Json(""))).StatusCode);
    }

    [Fact]
    public async Task List_and_detail_show_grants_and_their_decisions()
    {
        Assert.Equal(HttpStatusCode.OK, (await PostDecisionAsync(Forward())).StatusCode);
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        await _world.CreateGrantAsync(_world.Scope());

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/epic-grants");
        Assert.Equal(2, list.GetArrayLength());
        Assert.NotEqual(_world.GrantId, list[0].GetProperty("id").GetString());
        Assert.Equal(_world.GrantId, list[1].GetProperty("id").GetString());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/epic-grants/{_world.GrantId}");
        var decision = Assert.Single(detail.GetProperty("decisions").EnumerateArray());
        Assert.Equal("sent", decision.GetProperty("status").GetString());
        Assert.Equal(EpicGrantWorld.PrRun, decision.GetProperty("runId").GetString());
        Assert.Equal("merge-approval:2", decision.GetProperty("visitId").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/epic-grants/{Guid.NewGuid()}")).StatusCode);

        _world.Clock.Now = _world.GrantCreatedAt.AddDays(7);
        var expired = await _client.GetFromJsonAsync<JsonElement>($"/api/epic-grants/{_world.GrantId}");
        Assert.Equal(("active", "expired"), (expired.GetProperty("status").GetString(), expired.GetProperty("effectiveStatus").GetString()));
    }
}
