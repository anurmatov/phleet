using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Protocol;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests.WorkflowOutcomes;

/// <summary>
/// An in-memory stand-in for the orchestrator's coordinator routes (#476 HTTP contract). It keeps
/// batches, receipts, acks and decisions, checks the bearer token and generation, and can lose a
/// response after applying it — the ambiguity the receipt readback exists for. Never a real server.
/// </summary>
internal sealed class FakeOutcomeServer : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public string Token { get; set; } = WorkflowOutcomeFixture.Token;
    public long Generation { get; set; } = 1;
    public int PageSize { get; set; } = 2;

    public List<WorkflowOutcomeInboxBatch> Batches { get; } = [];
    public Dictionary<Guid, WorkflowOutcomeReceipt> Receipts { get; } = [];
    public List<(Guid OutcomeId, WorkflowOutcomeDecisionRequest Request)> Decisions { get; } = [];
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Route ("inbox", "delivered", "receipt", "ack", "decision") → status returned instead of handling.</summary>
    public Dictionary<string, HttpStatusCode> ForcedStatus { get; } = [];

    /// <summary>Routes whose next call is applied and then answered with a lost response (once each).</summary>
    public HashSet<string> LoseResponseAfterApply { get; } = [];

    /// <summary>Routes whose next call fails before reaching the server (once each).</summary>
    public HashSet<string> FailBeforeApply { get; } = [];

    /// <summary>Decision status per outcome id (default 201).</summary>
    public Dictionary<Guid, HttpStatusCode> DecisionStatus { get; } = [];

    public sealed record RecordedRequest(string Method, string Route, string PathAndQuery, string? Authorization, string? Body);

    public IEnumerable<RecordedRequest> Calls(string route) => Requests.Where(r => r.Route == route);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var route = RouteOf(request.Method, path);
        Requests.Enqueue(new RecordedRequest(request.Method.Method, route, request.RequestUri.PathAndQuery,
            request.Headers.Authorization?.ToString(), body));

        lock (_gate)
        {
            if (FailBeforeApply.Remove(route)) throw new HttpRequestException("connection reset before the server saw it");
            if (request.Headers.Authorization?.ToString() != "Bearer " + Token) return Status(HttpStatusCode.Unauthorized);
            if (ForcedStatus.TryGetValue(route, out var forced)) return Status(forced);

            var response = route switch
            {
                "inbox" => Inbox(query),
                "delivered" => Delivered(BatchIdOf(path), Deserialize<WorkflowOutcomeDeliveredRequest>(body)),
                "receipt" => Receipt(BatchIdOf(path), query["nonce"]),
                "ack" => Ack(BatchIdOf(path), Deserialize<WorkflowOutcomeAckRequest>(body)),
                "decision" => Decision(Guid.Parse(path.Split('/')[3]), Deserialize<WorkflowOutcomeDecisionRequest>(body)),
                _ => Status(HttpStatusCode.NotFound),
            };

            if (LoseResponseAfterApply.Remove(route)) throw new HttpRequestException("response lost after the server applied it");
            return response;
        }
    }

    private static string RouteOf(HttpMethod method, string path)
    {
        if (path == "/api/workflow-outcomes/inbox") return "inbox";
        if (path.EndsWith("/delivered", StringComparison.Ordinal)) return "delivered";
        if (path.EndsWith("/receipt", StringComparison.Ordinal)) return "receipt";
        if (path.EndsWith("/ack", StringComparison.Ordinal)) return "ack";
        if (path.EndsWith("/decisions", StringComparison.Ordinal) && method == HttpMethod.Post) return "decision";
        return "unknown";
    }

    private static Guid BatchIdOf(string path) => Guid.Parse(path.Split('/')[4]);

    private static T Deserialize<T>(string? body) => JsonSerializer.Deserialize<T>(body!, Json)!;

    private HttpResponseMessage Inbox(System.Collections.Specialized.NameValueCollection query)
    {
        if (query["generation"] != Generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return Status(HttpStatusCode.Conflict);
        var offset = int.TryParse(query["cursor"], System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var open = Batches.Where(b => !FullyAcked(b)).ToList();
        var page = open.Skip(offset).Take(PageSize).ToList();
        var next = offset + PageSize < open.Count ? (offset + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Ok(new WorkflowOutcomeInboxPage(page, next));
    }

    private bool FullyAcked(WorkflowOutcomeInboxBatch b) =>
        Receipts.TryGetValue(b.BatchId, out var r) && b.Items.All(i => r.AckedVersions.Contains(new WorkflowOutcomeItemRef(i.Id, i.Version)));

    private HttpResponseMessage Delivered(Guid batchId, WorkflowOutcomeDeliveredRequest request)
    {
        var batch = Batches.FirstOrDefault(b => b.BatchId == batchId);
        if (batch is null) return Status(HttpStatusCode.NotFound);
        if (request.Generation != Generation || request.Nonce != batch.Nonce || request.ItemVersionsHash != batch.ItemVersionsHash)
            return Status(HttpStatusCode.Conflict);
        if (!Receipts.TryGetValue(batchId, out var receipt))
        {
            var items = batch.Items.Select(i => new WorkflowOutcomeItemRef(i.Id, i.Version)).ToList();
            receipt = new WorkflowOutcomeReceipt(batchId, batch.Generation, batch.Nonce, batch.ItemVersionsHash, items, items, [],
                "receipt-" + batchId.ToString("N"), DateTimeOffset.UtcNow, null);
            Receipts[batchId] = receipt;
        }
        return Ok(receipt);
    }

    private HttpResponseMessage Receipt(Guid batchId, string? nonce)
    {
        var batch = Batches.FirstOrDefault(b => b.BatchId == batchId);
        if (batch is null || nonce != batch.Nonce || !Receipts.TryGetValue(batchId, out var receipt))
            return Status(HttpStatusCode.NotFound);
        return Ok(receipt);
    }

    private HttpResponseMessage Ack(Guid batchId, WorkflowOutcomeAckRequest request)
    {
        var batch = Batches.FirstOrDefault(b => b.BatchId == batchId);
        if (batch is null || !Receipts.TryGetValue(batchId, out var receipt)) return Status(HttpStatusCode.Conflict);
        if (request.Generation != Generation || request.Nonce != batch.Nonce) return Status(HttpStatusCode.Conflict);
        if (request.Items.Count == 0 || request.Items.Any(i => !receipt.DeliveredVersions.Contains(i))) return Status(HttpStatusCode.Conflict);
        receipt = receipt with
        {
            AckedVersions = receipt.AckedVersions.Union(request.Items).ToList(),
            AcknowledgedAt = DateTimeOffset.UtcNow,
        };
        Receipts[batchId] = receipt;
        return Ok(receipt);
    }

    private HttpResponseMessage Decision(Guid outcomeId, WorkflowOutcomeDecisionRequest request)
    {
        if (DecisionStatus.TryGetValue(outcomeId, out var status)) return Status(status);
        Decisions.Add((outcomeId, request));
        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { decisionId = Guid.NewGuid(), status = "accepted" }, Json),
                Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}

/// <summary>Builders for batches, options and the runtime under test.</summary>
internal static class WorkflowOutcomeFixture
{
    public const string Coordinator = "coordinator";
    public const string Token = "scoped-token-placeholder";
    public const string BaseUrl = "http://orchestrator.example:5000";

    public static WorkflowOutcomeInboxItem Item(Guid? id = null, int version = 1,
        string classification = WorkflowOutcomeClassifications.ConfirmedFailure, string workflowId = "wf-1") =>
        new(id ?? Guid.NewGuid(), version, "namespace-a", workflowId, "run-1", "ExampleWorkflow", classification,
            WorkflowOutcomeReasonCodes.WorkflowFailed, "WorkflowExecutionFailed", 7, DateTimeOffset.UtcNow, "project-a",
            false, "agent1", null, ["event:7"]);

    public static WorkflowOutcomeInboxBatch Batch(params WorkflowOutcomeInboxItem[] items) =>
        BatchWith(Guid.NewGuid(), 1, items);

    public static WorkflowOutcomeInboxBatch BatchWith(Guid batchId, long generation, params WorkflowOutcomeInboxItem[] items)
    {
        if (items.Length == 0) items = [Item()];
        return new(batchId, generation, "nonce-" + batchId.ToString("N")[..8], Coordinator, DateTimeOffset.UtcNow,
            WorkflowOutcomeHashes.ItemVersionsHash(items.Select(i => new WorkflowOutcomeItemRef(i.Id, i.Version))), items);
    }

    public static WorkflowOutcomeClientOptions Options(string inboxPath, long generation = 1) => new()
    {
        Enabled = true,
        BaseUrl = BaseUrl,
        ScopedToken = Token,
        Coordinator = Coordinator,
        CoordinatorGeneration = generation,
        PollSeconds = 30,
        InboxPath = inboxPath,
    };

    /// <summary>A valid schema v1 output acknowledging <paramref name="ack"/> and deciding <paramref name="decide"/>.</summary>
    public static string Output(WorkflowOutcomeInboxBatch batch, IEnumerable<WorkflowOutcomeInboxItem> ack,
        IEnumerable<WorkflowOutcomeInboxItem>? decide = null, string operation = "retry") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            batchId = batch.BatchId,
            generation = batch.Generation,
            ack = ack.Select(i => new { id = i.Id, version = i.Version }),
            decisions = (decide ?? []).Select(i => new
            {
                id = i.Id, version = i.Version, operation, owner = "agent1",
                reason = "the delegate failed; retry after the fix", evidenceRefs = new[] { "event:7" },
            }),
        });
}

/// <summary>
/// The coordinator runtime under test: a REAL TaskManager, registry, store, completion handler and
/// pull loop, a scripted executor, recording sink/event fakes and the fake server.
/// </summary>
internal sealed class CoordinatorRuntime : IAsyncDisposable
{
    public string Dir { get; }
    public string InboxPath => Path.Combine(Dir, ".fleet", "workflow-outcome-inbox.json");
    public FakeOutcomeServer Server { get; }
    public ScriptedExecutor Executor { get; }
    public IMessageSink Sink { get; } = Substitute.For<IMessageSink>();
    public RecordingEvents Events { get; } = new();
    public IConversationRegistry Registry { get; }
    public TaskManager Manager { get; }
    public WorkflowOutcomeLocalStore Store { get; }
    public WorkflowOutcomeCompletionHandler Handler { get; }
    public WorkflowOutcomePullService Pull { get; }
    public List<WorkflowOutcomeTurnEnded> Ended { get; } = [];
    public int TaskCompletedCount;

    public CoordinatorRuntime(FakeOutcomeServer? server = null, ScriptedExecutor? executor = null, string? dir = null,
        IConversationRegistry? registry = null, string token = WorkflowOutcomeFixture.Token, long generation = 1)
    {
        Dir = dir ?? Path.Combine(Path.GetTempPath(), $"wo-coordinator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Dir);
        Server = server ?? new FakeOutcomeServer();
        Executor = executor ?? new ScriptedExecutor();
        Registry = registry ?? new ConversationRegistry();

        var agent = Microsoft.Extensions.Options.Options.Create(new AgentOptions
        {
            Name = WorkflowOutcomeFixture.Coordinator, Role = "test", WorkDir = Dir, Provider = "claude",
        });
        Manager = new TaskManager(agent, Executor, new SessionManager(), NullLogger<TaskManager>.Instance,
            events: Events, sink: Sink, registry: Registry);
        Manager.OnTaskCompleted += (_, _, _, _, _, _, _, _) => Interlocked.Increment(ref TaskCompletedCount);
        Manager.OnWorkflowOutcomeTurnEnded += e => { lock (Ended) Ended.Add(e); };

        var options = Microsoft.Extensions.Options.Options.Create(WorkflowOutcomeFixture.Options(InboxPath, generation));
        Store = new WorkflowOutcomeLocalStore(InboxPath);
        Handler = new WorkflowOutcomeCompletionHandler(Manager, Registry, Store, options,
            NullLogger<WorkflowOutcomeCompletionHandler>.Instance);
        var http = new HttpClient(Server) { BaseAddress = new Uri(WorkflowOutcomeFixture.BaseUrl) };
        Pull = new WorkflowOutcomePullService(options, new WorkflowOutcomeInboxClient(http, token), Store, Handler,
            NullLogger<WorkflowOutcomePullService>.Instance);
    }

    /// <summary>The same work directory and server, as a fresh process would see them.</summary>
    public CoordinatorRuntime Restart(ScriptedExecutor? executor = null, string token = WorkflowOutcomeFixture.Token) =>
        new(Server, executor ?? new ScriptedExecutor(), Dir, token: token);

    public bool Start() => Pull.Initialize();

    public Task CycleAsync(bool fetch = true) => Pull.RunCycleAsync(fetch, CancellationToken.None);

    /// <summary>Waits until <paramref name="condition"/> holds, running local cycles meanwhile.</summary>
    public async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(20);
            await CycleAsync(fetch: false);
        }
    }

    public WorkflowOutcomeLocalBatch Local(Guid batchId) => Store.Get(batchId) ?? throw new InvalidOperationException("batch not stored");

    public ValueTask DisposeAsync()
    {
        Handler.Dispose();
        Pull.Dispose();
        return ValueTask.CompletedTask;
    }

    public void DeleteDir()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Records every conversation event publication. Internal turns must produce none.</summary>
internal sealed class RecordingEvents : IConversationEventPublisher
{
    public ConcurrentQueue<(long Key, string Kind, string Channel)> Published { get; } = new();

    public void Publish(ConversationEvent evt) => Published.Enqueue((0, evt.Kind, evt.Identity.ChannelId));

    public bool Publish<TPayload>(long runtimeConversationKey, string kind, ConversationIdentity identity, TPayload? payload)
        where TPayload : class
    {
        Published.Enqueue((runtimeConversationKey, kind, identity.ChannelId));
        return true;
    }
}
