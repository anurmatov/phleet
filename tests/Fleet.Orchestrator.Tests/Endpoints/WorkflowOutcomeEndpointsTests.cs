using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// The <c>/api/workflow-outcomes</c> surface driven over real HTTP (#476): authorization of every
/// route and method before any store access, the failure inbox receipt/ack semantics, record-only
/// decisions, operator classification, views and late-result capture.
/// </summary>
public sealed class WorkflowOutcomeEndpointsTests : IAsyncLifetime
{
    private const string Root = "/api/workflow-outcomes";
    private const string AnyId = "11111111-1111-4111-8111-111111111111";
    private ApiHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await ApiHost.StartAsync();
        await _host.GrantProjectAsync();
        await _host.SeedAgentsAsync("agent1", "agent2");
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _host.Database.DisposeAsync();
    }

    // ── privacy-auth ─────────────────────────────────────────────────────────

    /// <summary>Every route of the surface (plus an unmapped sub-path and a case variant) with the credentials it accepts.</summary>
    public static readonly (string Path, WorkflowOutcomeAccess Access)[] Routes =
    [
        (Root, WorkflowOutcomeAccess.Operator),
        (Root + "/coverage", WorkflowOutcomeAccess.Operator),
        (Root + "/health", WorkflowOutcomeAccess.Operator),
        (Root + "/" + AnyId, WorkflowOutcomeAccess.Operator),
        (Root + "/" + AnyId + "?detail=payload", WorkflowOutcomeAccess.Operator),
        (Root + "/" + AnyId + "/classification", WorkflowOutcomeAccess.Operator),
        (Root + "/" + AnyId + "/recovery-plan", WorkflowOutcomeAccess.Operator),
        (Root + "/recovery/" + AnyId + "/reserve", WorkflowOutcomeAccess.Operator),
        (Root + "/inbox?generation=1", WorkflowOutcomeAccess.Coordinator),
        (Root + "/batches/" + AnyId + "/delivered", WorkflowOutcomeAccess.Coordinator),
        (Root + "/batches/" + AnyId + "/receipt?nonce=x", WorkflowOutcomeAccess.Coordinator | WorkflowOutcomeAccess.Operator),
        (Root + "/batches/" + AnyId + "/ack", WorkflowOutcomeAccess.Coordinator | WorkflowOutcomeAccess.Operator),
        (Root + "/" + AnyId + "/decisions", WorkflowOutcomeAccess.Coordinator | WorkflowOutcomeAccess.Operator),
        (Root + "/late-results", WorkflowOutcomeAccess.Bridge),
        (Root + "/recovery/" + AnyId, WorkflowOutcomeAccess.Operator | WorkflowOutcomeAccess.Bridge),
        (Root + "/recovery/" + AnyId + "/authorize-dispatch", WorkflowOutcomeAccess.Bridge),
        (Root + "/recovery/" + AnyId + "/receipt", WorkflowOutcomeAccess.Bridge),
        (Root + "/unmapped/sub/path", WorkflowOutcomeAccess.Operator),
        ("/API/Workflow-Outcomes/INBOX", WorkflowOutcomeAccess.Coordinator),
    ];

    private static readonly HttpMethod[] Methods = [HttpMethod.Get, HttpMethod.Head, HttpMethod.Options, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete];

    private static IEnumerable<(string Path, WorkflowOutcomeAccess Access, HttpMethod Method)> Matrix() =>
        Routes.SelectMany(r => Methods.Select(m => (r.Path, r.Access, m)));

    private async Task AssertEveryAsync(ApiHost host, IEnumerable<(string Path, WorkflowOutcomeAccess Access, HttpMethod Method)> cases,
        Func<WorkflowOutcomeAccess, string?> token, HttpStatusCode expected)
    {
        host.Database.Commands.Reset();
        var failures = new List<string>();
        foreach (var (path, access, method) in cases)
        {
            var body = method == HttpMethod.Post || method == HttpMethod.Put ? "{\"marker\":\"SECRET-BODY-MARKER\"}" : null;
            var response = await host.SendAsync(method, path, token(access), body);
            if (response.StatusCode != expected) failures.Add($"{method} {path} -> {(int)response.StatusCode}");
        }
        Assert.Empty(failures);
        Assert.Equal(0, host.Database.Commands.Count);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Authorize_FeatureDisabled_EveryRouteAndMethodIs503AndTheStoreIsNeverTouched()
    {
        await using var host = await ApiHost.StartAsync(configure: c => c["WorkflowOutcomes:Enabled"] = "false");
        var cases = Matrix().Concat(Methods.Select(m => (Root + "/health/public", WorkflowOutcomeAccess.Anonymous, m)));

        await AssertEveryAsync(host, cases, _ => ApiHost.AdminToken, HttpStatusCode.ServiceUnavailable);
        await AssertEveryAsync(host, Matrix(), _ => ApiHost.CoordinatorToken, HttpStatusCode.ServiceUnavailable);
        await host.Database.DisposeAsync();
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Authorize_AuthConfigMissing_EveryAuthenticatedRouteIs503WithoutStoreAccess()
    {
        await using var host = await ApiHost.StartAsync(configure: c =>
        {
            c["Orchestrator:AuthToken"] = "";
            c[ApiHost.TokenKey] = "";
        });

        await AssertEveryAsync(host, Matrix(), _ => ApiHost.AdminToken, HttpStatusCode.ServiceUnavailable);
        await AssertEveryAsync(host, Matrix(), _ => null, HttpStatusCode.ServiceUnavailable);
        await host.Database.DisposeAsync();
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Authorize_CoordinatorUnassigned_CoordinatorOnlyRoutesAre503()
    {
        await using var host = await ApiHost.StartAsync(configure: c => c["FLEET_CTO_AGENT"] = "");
        var coordinatorOnly = Matrix().Where(c => c.Access == WorkflowOutcomeAccess.Coordinator);

        await AssertEveryAsync(host, coordinatorOnly, _ => ApiHost.CoordinatorToken, HttpStatusCode.ServiceUnavailable);
        // With the coordinator unconfigured its token is not a credential anywhere.
        await AssertEveryAsync(host, Matrix().Where(c => c.Access == (WorkflowOutcomeAccess.Coordinator | WorkflowOutcomeAccess.Operator)),
            _ => ApiHost.CoordinatorToken, HttpStatusCode.Unauthorized);
        await host.Database.DisposeAsync();
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Authorize_MissingOrInvalidBearer_Is401OnEveryRouteAndMethodWithoutStoreAccess()
    {
        await AssertEveryAsync(_host, Matrix(), _ => null, HttpStatusCode.Unauthorized);
        await AssertEveryAsync(_host, Matrix(), _ => "not-a-token", HttpStatusCode.Unauthorized);
        await AssertEveryAsync(_host, Matrix(), _ => ApiHost.CoordinatorToken + "x", HttpStatusCode.Unauthorized);

        // No query-string identity.
        _host.Database.Commands.Reset();
        var query = await _host.SendAsync(HttpMethod.Get, Root + "/inbox?generation=1&access_token=" + ApiHost.CoordinatorToken + "&agent=coordinator", null);
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        Assert.Equal(0, _host.Database.Commands.Count);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Authorize_WrongScope_Is403WithoutStoreAccess()
    {
        // The scoped coordinator token cannot reach operator (plan/reserve/views) or bridge routes.
        await AssertEveryAsync(_host, Matrix().Where(c => (c.Access & WorkflowOutcomeAccess.Coordinator) == 0),
            _ => ApiHost.CoordinatorToken, HttpStatusCode.Forbidden);
        // The admin bearer is not the coordinator's inbox credential.
        await AssertEveryAsync(_host, Matrix().Where(c => c.Access == WorkflowOutcomeAccess.Coordinator),
            _ => ApiHost.AdminToken, HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Authorize_CoordinatorTokenEqualToAdmin_IsNeverACoordinatorCredential()
    {
        await using var host = await ApiHost.StartAsync(configure: c => c[ApiHost.TokenKey] = ApiHost.AdminToken);

        await AssertEveryAsync(host, Matrix().Where(c => c.Access == WorkflowOutcomeAccess.Coordinator),
            _ => ApiHost.AdminToken, HttpStatusCode.ServiceUnavailable);
        await host.Database.DisposeAsync();
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task PublicHealth_IsAnonymousAndCarriesOneBooleanOnly()
    {
        await _host.SeedRunAsync(r => r.Namespace = "namespace-secret");
        var response = await _host.SendAsync(HttpMethod.Get, Root + "/health/public", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiHost.JsonAsync(response);
        Assert.Equal(["healthy"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(body.GetProperty("healthy").GetBoolean());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Logs_NeverContainATokenOrARequestBody()
    {
        var (run, batch) = await _host.AckedFailureAsync();
        await _host.SendAsync(HttpMethod.Post, Root + "/" + run.Id + "/decisions", "not-a-token", "{\"reason\":\"SECRET-BODY-MARKER\"}");
        await _host.AsCoordinator(HttpMethod.Post, Root + "/" + run.Id + "/decisions",
            ApiHost.Decision(1, "wait", reason: "SECRET-BODY-MARKER", dueAt: _host.Clock.GetUtcNow().AddHours(1)));
        await _host.AsCoordinator(HttpMethod.Post, Root + "/" + run.Id + "/decisions", ApiHost.Decision(1, "wait", reason: "SECRET-BODY-MARKER"));
        await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", new WorkflowOutcomeLateResultRequest(
            "namespace-a", "unknown/step", "m-1", WorkflowOutcomeHashes.Sha256Hex("SECRET-BODY-MARKER"), "completed", "SECRET-BODY-MARKER", 1, _host.Clock.GetUtcNow()));

        var logs = _host.Logs.All;
        Assert.Contains("workflow_outcome_auth_refused", logs);
        Assert.DoesNotContain(ApiHost.AdminToken, logs);
        Assert.DoesNotContain(ApiHost.CoordinatorToken, logs);
        Assert.DoesNotContain("SECRET-BODY-MARKER", logs);
        Assert.DoesNotContain(batch.Nonce, logs);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    [InlineData("POST", "/api/workflow-outcomes/batches/x/ack", false)]
    [InlineData("POST", "/api/workflow-outcomes", false)]
    [InlineData("PUT", "/API/WORKFLOW-OUTCOMES/x", false)]
    [InlineData("POST", "/api/workflow-outcomesx", true)]
    [InlineData("POST", "/api/agents/x/stop", true)]
    [InlineData("GET", "/api/workflow-outcomes/inbox", false)]
    public void OrchestratorAuth_ExemptsOnlyTheSelfAuthorizedPrefix(string method, string path, bool gated) =>
        Assert.Equal(gated, OrchestratorAuth.RequiresBearerToken(method, path));

    [Fact]
    [Trait("WorkflowOutcomeSuite", "privacy-auth")]
    public async Task Inbox_AccessNeeded_ForAProjectTheCoordinatorCannotRead_IsOpaque()
    {
        var hidden = await _host.SeedRunAsync(r => { r.ProjectName = "project-private"; r.WorkflowId = "private-workflow"; });
        var unknown = await _host.SeedRunAsync(r => r.ProjectName = null);
        var visible = await _host.SeedRunAsync();
        await _host.FlushAsync();

        var items = (await _host.InboxAsync()).Batches.SelectMany(b => b.Items).ToDictionary(i => i.Id);
        foreach (var id in new[] { hidden.Id, unknown.Id })
        {
            var item = items[id];
            Assert.True(item.AccessNeeded);
            Assert.Equal(("", "", "", ""), (item.Namespace, item.WorkflowId, item.RunId, item.WorkflowType));
            Assert.Null(item.Project);
            Assert.Null(item.TaskOwner);
            Assert.Equal(["outcome:" + id.ToString("D")], item.EvidenceRefs);
        }
        Assert.False(items[visible.Id].AccessNeeded);
        Assert.Equal(visible.WorkflowId, items[visible.Id].WorkflowId);
    }

    // ── receipts ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Inbox_FetchIsNotDelivery_AndMutatesNothing()
    {
        var run = await _host.SeedRunAsync();
        await _host.FlushAsync();

        var first = await _host.InboxAsync();
        var second = await _host.InboxAsync();
        var batch = Assert.Single(first.Batches);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));

        var receipt = await _host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}");
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        var stored = await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync());
        Assert.Equal((WorkflowOutcomeBatchStates.Pending, (DateTime?)null), (stored.State, stored.DeliveredAt));
        Assert.Equal(WorkflowOutcomeHashes.ItemVersionsHash([new WorkflowOutcomeItemRef(run.Id, 1)]), batch.ItemVersionsHash);
        Assert.Matches("^[0-9a-f]{64}$", batch.Nonce);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Delivered_DuplicateReturnsTheSameReceipt_AndALostReplyReconcilesByReadback()
    {
        var run = await _host.SeedRunAsync();
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();

        // The first reply is "lost": the client never reads it and reconciles by GET.
        var lost = await _host.DeliverAsync(batch);
        Assert.Equal(HttpStatusCode.OK, lost.StatusCode);
        var readback = await ApiHost.ReadAsync<WorkflowOutcomeReceipt>(
            await _host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}"));
        var duplicate = await ApiHost.ReadAsync<WorkflowOutcomeReceipt>(await _host.DeliverAsync(batch));

        Assert.Equal(readback.ReceiptHash, duplicate.ReceiptHash);
        Assert.Equal(readback.DeliveredAt, duplicate.DeliveredAt);
        Assert.NotNull(readback.ReceiptHash);
        Assert.Equal([new WorkflowOutcomeItemRef(run.Id, 1)], readback.DeliveredVersions);
        Assert.Empty(readback.AckedVersions);

        // The operator can read the same receipt; a wrong nonce reads nothing.
        Assert.Equal(HttpStatusCode.OK, (await _host.AsAdmin(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce=other")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{Guid.NewGuid()}/receipt?nonce={batch.Nonce}")).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Delivered_WrongNonceHashOrGeneration_Is409AndRecordsNothing()
    {
        await _host.SeedRunAsync();
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();
        var url = $"{Root}/batches/{batch.BatchId}/delivered";

        Assert.Equal(HttpStatusCode.Conflict, (await _host.AsCoordinator(HttpMethod.Post, url,
            new WorkflowOutcomeDeliveredRequest(WorkflowOutcomeApi.NewNonce(), 1, batch.ItemVersionsHash))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.AsCoordinator(HttpMethod.Post, url,
            new WorkflowOutcomeDeliveredRequest(batch.Nonce, 1, new string('0', 64)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.AsCoordinator(HttpMethod.Post, url,
            new WorkflowOutcomeDeliveredRequest(batch.Nonce, 2, batch.ItemVersionsHash))).StatusCode);
        Assert.Null((await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync())).DeliveredAt);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Ack_ExplicitSubsetOnly_UnseenMembersAndWildcardsRefused()
    {
        var runs = new[] { await _host.SeedRunAsync(), await _host.SeedRunAsync(), await _host.SeedRunAsync() };
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();

        // Not delivered yet: nothing can be acknowledged.
        Assert.Equal(HttpStatusCode.Conflict, (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(runs[0].Id, 1))).StatusCode);
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();

        // Wildcard (empty), an unseen member, or a stale version: refused, nothing acked.
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AckAsync(batch)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(runs[0].Id, 1), new WorkflowOutcomeItemRef(Guid.NewGuid(), 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(runs[0].Id, 2))).StatusCode);
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatchItems.CountAsync(i => i.AckedAt != null)));

        var partial = await ApiHost.ReadAsync<WorkflowOutcomeReceipt>(await _host.AckAsync(batch, new WorkflowOutcomeItemRef(runs[1].Id, 1)));
        Assert.Equal([new WorkflowOutcomeItemRef(runs[1].Id, 1)], partial.AckedVersions);
        Assert.Null(partial.AcknowledgedAt);
        Assert.Equal(WorkflowOutcomeBatchStates.PartiallyAcknowledged,
            (await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync())).State);

        // A repeated ack is idempotent; acking the rest completes the batch.
        (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(runs[1].Id, 1))).EnsureSuccessStatusCode();
        var full = await ApiHost.ReadAsync<WorkflowOutcomeReceipt>(await _host.AckAsync(batch,
            new WorkflowOutcomeItemRef(runs[0].Id, 1), new WorkflowOutcomeItemRef(runs[2].Id, 1)));
        Assert.Equal(3, full.AckedVersions.Count);
        Assert.NotNull(full.AcknowledgedAt);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Ack_CoordinatorNeedsTheAdmittedTaskId_OperatorDoesNot()
    {
        var run = await _host.SeedRunAsync();
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();
        var url = $"{Root}/batches/{batch.BatchId}/ack";
        var items = new[] { new WorkflowOutcomeItemRef(run.Id, 1) };

        Assert.Equal((HttpStatusCode)422, (await _host.AsCoordinator(HttpMethod.Post, url,
            new WorkflowOutcomeAckRequest(1, batch.Nonce, items, "outcome/" + Guid.NewGuid()))).StatusCode);
        var byOperator = await _host.AsAdmin(HttpMethod.Post, url, new WorkflowOutcomeAckRequest(1, batch.Nonce, items, null));
        Assert.Equal(HttpStatusCode.OK, byOperator.StatusCode);
        var item = await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatchItems.AsNoTracking().SingleAsync());
        Assert.Equal(("operator", "operator"), (item.AckSource, item.AckedBy));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Ack_AfterAMaterialVersionChange_IsStaleAndANewMembershipNeedsItsOwnAck()
    {
        var run = await _host.SeedRunAsync();
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();

        // The observer bumps the material version (new terminal evidence).
        await _host.Database.WithContextAsync(db => db.WorkflowOutcomeRuns.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, 2)));
        Assert.Equal(1, await _host.FlushAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(run.Id, 1))).StatusCode);
        var fresh = (await _host.InboxAsync()).Batches.Single(b => b.BatchId != batch.BatchId);
        Assert.Equal(2, fresh.Items.Single().Version);
        Assert.Equal(WorkflowOutcomeBatchStates.Superseded,
            (await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync(b => b.Id == batch.BatchId))).State);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Rotation_OldTokenIs401_NewTokenReconcilesTheSameNonceAndMembership()
    {
        var run = await _host.SeedRunAsync();
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();
        var before = await ApiHost.JsonAsync(await _host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}"));
        await _host.App.StopAsync();

        // Approved restart with a rotated credential; ownership generation unchanged.
        await using var rotated = await ApiHost.StartAsync(_host.Database, c =>
        {
            c[ApiHost.TokenKey] = "coordinator-test-token-0002";
            c["WorkflowOutcomes:CoordinatorCredentialVersion"] = "2";
        }, clock: _host.Clock);
        var url = $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}";

        rotated.Database.Commands.Reset();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rotated.SendAsync(HttpMethod.Get, url, ApiHost.CoordinatorToken)).StatusCode);
        Assert.Equal(0, rotated.Database.Commands.Count);
        var after = await rotated.SendAsync(HttpMethod.Get, url, "coordinator-test-token-0002");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(before.GetRawText(), (await ApiHost.JsonAsync(after)).GetRawText());
        var ack = await rotated.SendAsync(HttpMethod.Post, $"{Root}/batches/{batch.BatchId}/ack", "coordinator-test-token-0002",
            new WorkflowOutcomeAckRequest(1, batch.Nonce, [new WorkflowOutcomeItemRef(run.Id, 1)], WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId)));
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task OwnerChange_GenerationBump_SupersedesOldBatchesAndRequiresReack()
    {
        var run = await _host.SeedRunAsync();
        await _host.FlushAsync();
        var old = (await _host.InboxAsync()).Batches.Single();
        (await _host.DeliverAsync(old)).EnsureSuccessStatusCode();
        (await _host.AckAsync(old, new WorkflowOutcomeItemRef(run.Id, 1))).EnsureSuccessStatusCode();
        await _host.App.StopAsync();

        await using var next = await ApiHost.StartAsync(_host.Database, c => c["WorkflowOutcomes:CoordinatorGeneration"] = "2", clock: _host.Clock);
        Assert.Equal(1, await next.FlushAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await next.AsCoordinator(HttpMethod.Get, $"{Root}/inbox?generation=1")).StatusCode);
        var receipt = await next.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{old.BatchId}/receipt?nonce={old.Nonce}");
        Assert.Equal(HttpStatusCode.Conflict, receipt.StatusCode);
        Assert.Equal(2, (await ApiHost.JsonAsync(receipt)).GetProperty("currentGeneration").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await next.AckAsync(old, new WorkflowOutcomeItemRef(run.Id, 1))).StatusCode);

        var fresh = (await next.InboxAsync(generation: 2)).Batches.Single();
        Assert.Equal((2L, run.Id), (fresh.Generation, fresh.Items.Single().Id));
        // The old ack does not carry over: a decision needs the reack in generation 2.
        var decision = await next.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "close"));
        Assert.Equal((HttpStatusCode)422, decision.StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Batches_ForeignCoordinator_Are403AndNeverListed()
    {
        await _host.SeedRunAsync(r => r.Coordinator = "other-coordinator");
        var foreign = new WorkflowOutcomeBatch
        {
            Id = Guid.NewGuid(), Coordinator = "other-coordinator", CoordinatorGeneration = 1, State = WorkflowOutcomeBatchStates.Pending,
            Nonce = WorkflowOutcomeApi.NewNonce(), ItemVersionsHash = new string('a', 64), CreatedAt = _host.Clock.GetUtcNow().UtcDateTime,
        };
        await _host.Database.WithContextAsync(async db => { db.WorkflowOutcomeBatches.Add(foreign); await db.SaveChangesAsync(); });
        await _host.FlushAsync();

        Assert.DoesNotContain((await _host.InboxAsync()).Batches, b => b.BatchId == foreign.Id);
        var url = $"{Root}/batches/{foreign.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.AsCoordinator(HttpMethod.Post, url + "/delivered",
            new WorkflowOutcomeDeliveredRequest(foreign.Nonce, 1, foreign.ItemVersionsHash))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.AsCoordinator(HttpMethod.Get, url + "/receipt?nonce=" + foreign.Nonce)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.AsCoordinator(HttpMethod.Post, url + "/ack",
            new WorkflowOutcomeAckRequest(1, foreign.Nonce, [new WorkflowOutcomeItemRef(Guid.NewGuid(), 1)], null))).StatusCode);
    }

    // ── decisions ────────────────────────────────────────────────────────────

    private async Task<(HttpResponseMessage Response, WorkflowOutcomeRun Run)> DecideAsync(object decision, Action<WorkflowOutcomeRun>? configure = null, bool asOperator = false)
    {
        var (run, _) = await _host.AckedFailureAsync(configure);
        var url = $"{Root}/{run.Id}/decisions";
        return (asOperator ? await _host.AsAdmin(HttpMethod.Post, url, decision) : await _host.AsCoordinator(HttpMethod.Post, url, decision), run);
    }

    private Task<List<WorkflowOutcomeDecision>> DecisionRowsAsync(Guid outcomeId) =>
        _host.Database.QueryAsync(db => db.WorkflowOutcomeDecisions.AsNoTracking().Where(d => d.OutcomeId == outcomeId).ToListAsync());

    [Theory]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    [InlineData("wait")]
    [InlineData("hold")]
    [InlineData("retry")]
    [InlineData("reassign")]
    [InlineData("cancel")]
    public async Task Decide_RecordOnlyOperation_IsAcceptedAndExecutesNothing(string operation)
    {
        var due = operation is "wait" or "hold" ? _host.Clock.GetUtcNow().AddHours(4) : (DateTimeOffset?)null;
        var owner = operation == "reassign" ? "agent2" : "agent1";
        var (response, run) = await DecideAsync(ApiHost.Decision(1, operation, owner: owner, dueAt: due));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiHost.JsonAsync(response);
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        var row = Assert.Single(await DecisionRowsAsync(run.Id));
        Assert.Equal((operation, "accepted", "coordinator", owner), (row.Operation, row.Status, row.DecidedVia, row.Owner));
        Assert.Equal(due?.UtcDateTime, row.DueAt);
        // Record-only: no reservation, no resolution, no Temporal call.
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowRecoveryOperations.CountAsync()));
        Assert.Null((await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id))).ResolvedAt);
        Assert.Empty(_host.Probe.Calls);
        if (operation == "cancel") Assert.Equal("closed_target_noop", body.GetProperty("effect").GetString());
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    [InlineData("finish")]
    [InlineData("close")]
    public async Task Decide_FinishOrCloseWithDispositionEvidence_ResolvesOnlyThisItem(string operation)
    {
        var sibling = await _host.SeedRunAsync();
        var (response, run) = await DecideAsync(ApiHost.Decision(1, operation,
            evidence: ["https://example.com/review/1"], artifactRef: operation == "finish" ? "artifact:owner/repo#pr/7" : null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rows = await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().ToDictionaryAsync(r => r.Id));
        Assert.NotNull(rows[run.Id].ResolvedAt);
        Assert.Null(rows[sibling.Id].ResolvedAt);
        Assert.Equal(1, rows[run.Id].Version);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_AcceptLate_NeedsAStoredLateResultRef()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var url = $"{Root}/{run.Id}/decisions";
        var refused = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "accept_late", evidence: ["late-result:" + Guid.NewGuid()]));
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("evidence_out_of_scope", (await ApiHost.JsonAsync(refused)).GetProperty("error").GetString());

        // A stored late result that belongs to ANOTHER outcome cannot justify this one.
        var other = await _host.SeedRunAsync();
        var foreignLate = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Post, $"{Root}/late-results", new WorkflowOutcomeLateResultRequest(
            "namespace-a", other.WorkflowId + "/step-a", "m-0", WorkflowOutcomeHashes.Sha256Hex("other text"), "completed", "other text", 1, _host.Clock.GetUtcNow())));
        Assert.Equal(other.Id, foreignLate.GetProperty("outcomeId").GetGuid());
        var foreign = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "accept_late", evidence: ["late-result:" + foreignLate.GetProperty("id").GetGuid()]));
        Assert.Equal((HttpStatusCode)422, foreign.StatusCode);
        Assert.Equal("evidence_out_of_scope", (await ApiHost.JsonAsync(foreign)).GetProperty("error").GetString());

        var taskId = run.WorkflowId + "/step-a";
        var late = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Post, $"{Root}/late-results", new WorkflowOutcomeLateResultRequest(
            "namespace-a", taskId, "m-1", WorkflowOutcomeHashes.Sha256Hex("late text"), "completed", "late text", 1, _host.Clock.GetUtcNow())));
        var accepted = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "accept_late", evidence: ["late-result:" + late.GetProperty("id").GetGuid()]));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        // Recorded evidence only: the late result is not auto-accepted.
        var lateId = late.GetProperty("id").GetGuid();
        Assert.Equal("stored", (await _host.Database.QueryAsync(db => db.WorkflowOutcomeLateResults.AsNoTracking().SingleAsync(l => l.Id == lateId))).Status);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    [InlineData("resume", "closed_run_cannot_resume")]
    [InlineData("wait", "due_at_required")]
    [InlineData("hold", "due_at_required")]
    [InlineData("finish", "disposition_evidence_missing")]
    [InlineData("reassign", "reassign_target_unknown")]
    [InlineData("approve", "operation_unknown")]
    [InlineData("Retry", "operation_unknown")]
    public async Task Decide_UnsafeOrInvalid_Is422AndAuditedAsRejected(string operation, string reason)
    {
        var (response, run) = await DecideAsync(ApiHost.Decision(1, operation, owner: operation == "reassign" ? "nobody" : "agent1",
            evidence: ["https://example.com/review/1"]));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var body = await ApiHost.JsonAsync(response);
        Assert.Equal((reason, "rejected"), (body.GetProperty("error").GetString(), body.GetProperty("status").GetString()));
        var row = Assert.Single(await DecisionRowsAsync(run.Id));
        Assert.Equal(("rejected", reason), (row.Status, row.RejectReason));
        Assert.Null((await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id))).ResolvedAt);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_PastDueAt_Close_WithOnlySelfEvidence_AreRefused()
    {
        var (past, _) = await DecideAsync(ApiHost.Decision(1, "hold", dueAt: _host.Clock.GetUtcNow().AddMinutes(-1)));
        Assert.Equal((HttpStatusCode)422, past.StatusCode);
        var (run, _) = await _host.AckedFailureAsync();
        var close = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "close", evidence: ["outcome:" + run.Id]));
        Assert.Equal((HttpStatusCode)422, close.StatusCode);
        Assert.Equal("disposition_evidence_missing", (await ApiHost.JsonAsync(close)).GetProperty("error").GetString());
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    [InlineData("owner", "owner_invalid")]
    [InlineData("reason", "reason_invalid")]
    [InlineData("evidence", "evidence_invalid")]
    [InlineData("evidence-prefix", "evidence_invalid")]
    [InlineData("owner-long", "owner_invalid")]
    public async Task Decide_MissingOwnerReasonOrEvidence_Is422AndAudited(string missing, string reason)
    {
        var decision = missing switch
        {
            "owner" => ApiHost.Decision(1, "close", owner: " "),
            "reason" => ApiHost.Decision(1, "close", reason: ""),
            "evidence" => ApiHost.Decision(1, "close", evidence: []),
            "evidence-prefix" => ApiHost.Decision(1, "close", evidence: ["javascript:alert(1)"]),
            _ => ApiHost.Decision(1, "close", owner: new string('o', WorkflowOutcomeLimits.MaxOwnerChars + 1)),
        };
        var (response, run) = await DecideAsync(decision);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal(reason, (await ApiHost.JsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal("rejected", Assert.Single(await DecisionRowsAsync(run.Id)).Status);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_StaleVersion_Is409AndAudited()
    {
        var (response, run) = await DecideAsync(ApiHost.Decision(2, "close", evidence: ["decision:x"]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ApiHost.JsonAsync(response);
        Assert.Equal(("stale_version", 1), (body.GetProperty("error").GetString(), body.GetProperty("currentVersion").GetInt32()));
        var row = Assert.Single(await DecisionRowsAsync(run.Id));
        Assert.Equal(("rejected", "stale_version"), (row.Status, row.RejectReason));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Decide_IdenticalResendAfterLostReply_ReturnsTheRecordedDecisionWithoutASecondRow()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var url = $"{Root}/{run.Id}/decisions";
        var decision = ApiHost.Decision(1, "close", evidence: ["https://example.com/review/1"]);
        var first = await ApiHost.JsonAsync(await _host.AsCoordinator(HttpMethod.Post, url, decision));
        var resent = await _host.AsCoordinator(HttpMethod.Post, url, decision);

        Assert.Equal(HttpStatusCode.Created, resent.StatusCode);
        var body = await ApiHost.JsonAsync(resent);
        Assert.Equal(first.GetProperty("decisionId").GetGuid(), body.GetProperty("decisionId").GetGuid());
        Assert.True(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal("accepted", Assert.Single(await DecisionRowsAsync(run.Id)).Status);

        // A different body is a new decision attempt, not a duplicate: the item is already closed.
        var other = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "close", evidence: ["https://example.com/review/2"]));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Decide_ResendAfterOwnerChange_IsNotAPriorGenerationDuplicate_AndNeedsTheNewAck()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var hold = ApiHost.Decision(1, "hold", dueAt: _host.Clock.GetUtcNow().AddHours(4));
        Assert.Equal(HttpStatusCode.Created, (await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", hold)).StatusCode);
        await _host.App.StopAsync();

        await using var next = await ApiHost.StartAsync(_host.Database, c => c["WorkflowOutcomes:CoordinatorGeneration"] = "2", clock: _host.Clock);
        var resent = await next.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", hold);

        // The generation-1 decision is not this request's duplicate: generation 2 has no ack yet.
        Assert.Equal((HttpStatusCode)422, resent.StatusCode);
        Assert.Equal("not_acknowledged", (await ApiHost.JsonAsync(resent)).GetProperty("error").GetString());
        var rows = await next.Database.QueryAsync(db => db.WorkflowOutcomeDecisions.AsNoTracking().Where(d => d.OutcomeId == run.Id).ToListAsync());
        Assert.Equal([(1L, "accepted"), (2L, "rejected")], rows.OrderBy(r => r.CoordinatorGeneration).Select(r => (r.CoordinatorGeneration ?? 0, r.Status)).ToArray());

        // After the generation-2 ack the same request is a new accepted decision, and its own resend is the duplicate.
        await next.FlushAsync();
        var fresh = (await next.InboxAsync(generation: 2)).Batches.Single();
        (await next.DeliverAsync(fresh)).EnsureSuccessStatusCode();
        (await next.AckAsync(fresh, new WorkflowOutcomeItemRef(run.Id, 1))).EnsureSuccessStatusCode();
        var accepted = await ApiHost.JsonAsync(await next.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", hold));
        var again = await ApiHost.JsonAsync(await next.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", hold));
        Assert.False(accepted.TryGetProperty("duplicate", out _));
        Assert.Equal(accepted.GetProperty("decisionId").GetGuid(), again.GetProperty("decisionId").GetGuid());
        Assert.True(again.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_EventRefNotRecordedForTheRun_IsOutOfScope()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var url = $"{Root}/{run.Id}/decisions";

        var fabricated = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "close", evidence: ["event:1"]));
        Assert.Equal((HttpStatusCode)422, fabricated.StatusCode);
        Assert.Equal("evidence_out_of_scope", (await ApiHost.JsonAsync(fabricated)).GetProperty("error").GetString());

        // The run's own recorded terminal event is in scope.
        var own = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "close", evidence: ["event:" + run.TerminalEventId, "https://example.com/r"]));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "receipts")]
    public async Task Decide_ResendWithChangedGenerationOrOversizedField_IsNeverADuplicate()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var url = $"{Root}/{run.Id}/decisions";
        var due = _host.Clock.GetUtcNow().AddHours(4);
        var owner = new string('o', WorkflowOutcomeLimits.MaxOwnerChars);
        var accepted = await _host.AsCoordinator(HttpMethod.Post, url, new
        {
            expectedVersion = 1, operation = "hold", owner, reason = "owned follow-up", evidenceRefs = new[] { "https://example.com/r" }, dueAt = due, generation = 1,
        });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        // Same body under another generation: refused on generation, not answered as a duplicate.
        var otherGeneration = await _host.AsCoordinator(HttpMethod.Post, url, new
        {
            expectedVersion = 1, operation = "hold", owner, reason = "owned follow-up", evidenceRefs = new[] { "https://example.com/r" }, dueAt = due, generation = 2,
        });
        Assert.Equal(HttpStatusCode.Conflict, otherGeneration.StatusCode);
        Assert.Equal("generation_mismatch", (await ApiHost.JsonAsync(otherGeneration)).GetProperty("error").GetString());

        // An owner one character over the limit whose stored prefix matches is invalid, not a duplicate.
        var oversized = await _host.AsCoordinator(HttpMethod.Post, url, new
        {
            expectedVersion = 1, operation = "hold", owner = owner + "o", reason = "owned follow-up", evidenceRefs = new[] { "https://example.com/r" }, dueAt = due, generation = 1,
        });
        Assert.Equal((HttpStatusCode)422, oversized.StatusCode);
        Assert.Equal("owner_invalid", (await ApiHost.JsonAsync(oversized)).GetProperty("error").GetString());

        var rows = await DecisionRowsAsync(run.Id);
        Assert.Equal(["accepted", "rejected", "rejected"], rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.Status).Select(r => r.Status).Order().ToArray());
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    [InlineData("outcome")]
    [InlineData("run")]
    [InlineData("task")]
    [InlineData("decision")]
    [InlineData("operation")]
    public async Task Decide_EvidenceOfAnotherOutcome_IsOutOfScope(string kind)
    {
        var other = await _host.SeedRunAsync();
        var (run, _) = await _host.AckedFailureAsync();
        var foreignRef = kind switch
        {
            "outcome" => "outcome:" + other.Id,
            "run" => $"run:{other.Namespace}/{other.WorkflowId}/{other.RunId}",
            "task" => "task:" + other.WorkflowId + "/step-a",
            "decision" => "decision:" + Guid.NewGuid(),
            _ => "operation:" + Guid.NewGuid(),
        };
        var refused = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "close", evidence: [foreignRef]));

        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("evidence_out_of_scope", (await ApiHost.JsonAsync(refused)).GetProperty("error").GetString());

        // The same kinds naming this outcome's own records are accepted.
        var own = kind switch
        {
            "run" => $"run:{run.Namespace}/{run.WorkflowId}/{run.RunId}",
            _ => "task:" + run.WorkflowId + "/step-a",
        };
        var accepted = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "close", evidence: [own, "https://example.com/r"]));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_RacingOperator_SecondCloseOfTheSameVersionIsRefused()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var url = $"{Root}/{run.Id}/decisions";
        var operatorClose = await _host.AsAdmin(HttpMethod.Post, url, ApiHost.Decision(1, "close", owner: "operator", evidence: ["https://example.com/review"]));
        var coordinatorClose = await _host.AsCoordinator(HttpMethod.Post, url, ApiHost.Decision(1, "close", evidence: ["decision:other"]));

        Assert.Equal(HttpStatusCode.Created, operatorClose.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, coordinatorClose.StatusCode);
        var rows = await DecisionRowsAsync(run.Id);
        Assert.Equal([("operator", "accepted"), ("coordinator", "rejected")],
            rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.Status).Select(r => (r.DecidedVia, r.Status)).ToArray());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_Unacknowledged_Is422_AndAckNeverApprovesOrDecides()
    {
        var unacked = await _host.SeedRunAsync();
        await _host.FlushAsync();
        var refused = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{unacked.Id}/decisions", ApiHost.Decision(1, "close", evidence: ["decision:x"]));
        Assert.Equal((HttpStatusCode)422, refused.StatusCode);
        Assert.Equal("not_acknowledged", (await ApiHost.JsonAsync(refused)).GetProperty("error").GetString());

        var (acked, _) = await _host.AckedFailureAsync();
        Assert.Empty(await DecisionRowsAsync(acked.Id));
        var view = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/{acked.Id}"));
        Assert.Equal(("acknowledged", "acknowledged"), (view.GetProperty("stage").GetString(), view.GetProperty("deliveryState").GetString()));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("lastDecision").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("recovery").ValueKind);
        Assert.Null((await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync(r => r.Id == acked.Id))).ResolvedAt);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Decide_ForeignOrNonFailureOutcome_IsRefused()
    {
        var foreign = await _host.SeedRunAsync(r => r.Coordinator = "other-coordinator");
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{foreign.Id}/decisions", ApiHost.Decision(1, "close"))).StatusCode);
        Assert.Empty(await DecisionRowsAsync(foreign.Id));

        var cause = await _host.SeedRunAsync(r => { r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; r.Status = "Terminated"; });
        var response = await _host.AsAdmin(HttpMethod.Post, $"{Root}/{cause.Id}/decisions", ApiHost.Decision(1, "close"));
        Assert.Equal("not_confirmed_failure", (await ApiHost.JsonAsync(response)).GetProperty("error").GetString());
    }

    // ── classification (operator cause review) ───────────────────────────────

    private static object Classification(int version, string classification, string reason, string terminalRef, string[] causal, string authority = "https://example.com/approvals/1") =>
        new { expectedVersion = version, classification, reasonCode = reason, terminalEventRef = terminalRef, causalEvidenceRefs = causal, authorityRef = authority };

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public async Task Classify_GenericTerminate_PromotesExactlyOnceWithCausalEvidence_ThenEnqueues()
    {
        var run = await _host.SeedRunAsync(r =>
        {
            r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; r.ReasonCode = WorkflowOutcomeReasonCodes.AbortCauseUnknown;
            r.Status = "Terminated"; r.TerminalEventType = "WorkflowExecutionTerminated"; r.TerminalEventId = 40;
        });
        Assert.Equal(0, await _host.FlushAsync());
        var url = $"{Root}/{run.Id}/classification";

        var promoted = await _host.AsAdmin(HttpMethod.Post, url, Classification(1, "confirmed_failure", "failure_caused_terminate", "event:40", ["event:31", "task:" + run.WorkflowId + "/step-a"]));
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        var view = await ApiHost.JsonAsync(promoted);
        Assert.Equal(("confirmed_failure", 2), (view.GetProperty("classification").GetString(), view.GetProperty("version").GetInt32()));
        var again = await _host.AsAdmin(HttpMethod.Post, url, Classification(1, "confirmed_failure", "failure_caused_terminate", "event:40", ["event:31"]));
        Assert.Equal((HttpStatusCode)422, again.StatusCode);

        var observation = await _host.Database.QueryAsync(db => db.WorkflowOutcomeObservations.AsNoTracking().SingleAsync());
        Assert.Equal(("operator_classification", 40L, "confirmed_failure"), (observation.EventType, observation.EventId, observation.Classification));
        Assert.Equal(1, await _host.FlushAsync());
        Assert.Equal(2, (await _host.InboxAsync()).Batches.Single().Items.Single().Version);
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "kill")]
    [InlineData("confirmed_failure", "failure_caused_terminate", "event:41", "event:31", "https://example.com/a", 422, "terminal_event_mismatch")]
    [InlineData("confirmed_failure", "failure_caused_terminate", "event:40", "event:40", "https://example.com/a", 422, "causal_evidence_missing")]
    [InlineData("confirmed_failure", "failure_caused_cancel", "event:40", "event:31", "https://example.com/a", 422, "reason_invalid")]
    [InlineData("confirmed_failure", "business_task_failed", "event:40", "event:31", "https://example.com/a", 422, "reason_invalid")]
    [InlineData("confirmed_failure", "failure_caused_terminate", "event:40", "event:31", "http://example.com/a", 422, "authority_missing")]
    [InlineData("excluded_nonfailure", "normal_completion", "event:40", "event:31", "https://example.com/a", 422, "classification_invalid")]
    [InlineData("intentional_nonfailure", "failure_caused_terminate", "event:40", "event:31", "https://example.com/a", 422, "reason_invalid")]
    public async Task Classify_InsufficientOrInvalid_Is422AndChangesNothing(string classification, string reason, string terminal, string causal, string authority, int status, string error)
    {
        var run = await _host.SeedRunAsync(r =>
        {
            r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; r.Status = "Terminated";
            r.TerminalEventType = "WorkflowExecutionTerminated"; r.TerminalEventId = 40;
        });
        var response = await _host.AsAdmin(HttpMethod.Post, $"{Root}/{run.Id}/classification", Classification(1, classification, reason, terminal, [causal], authority));

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(error, (await ApiHost.JsonAsync(response)).GetProperty("error").GetString());
        var stored = await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id));
        Assert.Equal((WorkflowOutcomeClassifications.CauseUnresolved, 1), (stored.Classification, stored.Version));
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowOutcomeObservations.CountAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "kill")]
    public async Task Classify_StaleVersion_Is409_AndDeliberateDispositionIsNeverEnqueued()
    {
        var run = await _host.SeedRunAsync(r =>
        {
            r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; r.Status = "Canceled";
            r.TerminalEventType = "WorkflowExecutionCanceled"; r.TerminalEventId = 9;
        });
        var url = $"{Root}/{run.Id}/classification";
        Assert.Equal(HttpStatusCode.Conflict, (await _host.AsAdmin(HttpMethod.Post, url, Classification(2, "intentional_nonfailure", "deliberate_disposition", "event:9", ["decision:hold-1"]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.AsAdmin(HttpMethod.Post, url, Classification(1, "intentional_nonfailure", "deliberate_disposition", "event:9", ["decision:hold-1"]))).StatusCode);
        Assert.Equal(0, await _host.FlushAsync());
        Assert.Empty((await _host.InboxAsync()).Batches);
    }

    // ── views (dashboard / pagination) ───────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    public async Task List_UncoveredTypeBookkeeping_IsNotACauseReview()
    {
        await _host.SeedRunAsync(r =>
        {
            r.Status = "Completed"; r.TerminalEventType = "WorkflowExecutionCompleted";
            r.Classification = WorkflowOutcomeClassifications.ClassificationPending; r.ReasonCode = WorkflowOutcomeReasonCodes.ClassifierUncoveredType;
        });
        var pinned = await _host.SeedRunAsync(r =>
        {
            r.Status = "Completed"; r.TerminalEventType = "WorkflowExecutionCompleted";
            r.Classification = WorkflowOutcomeClassifications.ClassificationPending; r.ReasonCode = WorkflowOutcomeReasonCodes.ClassifierMissing;
        });

        var page = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}?state=unresolved_cause"));

        Assert.Equal([pinned.Id], page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(1, page.GetProperty("counts").GetProperty("unresolvedCauses").GetInt32());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dashboard")]
    public async Task List_KeysetPagesEveryFailure_NeverASuccessfulRun()
    {
        var failures = new HashSet<Guid>();
        for (var i = 0; i < 120; i++)
        {
            failures.Add((await _host.SeedRunAsync()).Id);
            if (i % 3 == 0) _host.Clock.Advance(TimeSpan.FromSeconds(1)); // ties on StoredAt are common
        }
        for (var i = 0; i < 30; i++)
            await _host.SeedRunAsync(r => { r.Classification = WorkflowOutcomeClassifications.ExcludedNonfailure; r.Status = "Completed"; r.ReasonCode = "normal_completion"; });

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var response = await _host.AsAdmin(HttpMethod.Get, $"{Root}?limit=50" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ApiHost.JsonAsync(response);
            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            Assert.Equal(120, body.GetProperty("counts").GetProperty("failures").GetInt32());
            cursor = body.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(120, seen.Count);
        Assert.True(failures.SetEquals(seen));
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?state=success")]
    [InlineData("?cursor=bogus")]
    public async Task List_InvalidPagingOrState_Is400(string query) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AsAdmin(HttpMethod.Get, Root + query)).StatusCode);

    [Fact]
    [Trait("WorkflowOutcomeSuite", "pagination")]
    public async Task List_CursorFromAnotherFilter_Is400()
    {
        for (var i = 0; i < 3; i++) await _host.SeedRunAsync();
        var body = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}?limit=1"));
        var cursor = Uri.EscapeDataString(body.GetProperty("nextCursor").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AsAdmin(HttpMethod.Get, $"{Root}?limit=1&state=all_attention&cursor={cursor}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AsAdmin(HttpMethod.Get, $"{Root}?limit=1&namespace=namespace-b&cursor={cursor}")).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dashboard")]
    public async Task Views_StagesAttentionAndUnresolvedCausesAreSeparate()
    {
        var stored = await _host.SeedRunAsync();
        var cause = await _host.SeedRunAsync(r => { r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; r.Status = "Canceled"; });
        var success = await _host.SeedRunAsync(r => r.Classification = WorkflowOutcomeClassifications.ExcludedNonfailure);
        await _host.Database.WithContextAsync(async db =>
        {
            db.WorkflowOutcomeHealth.Add(new WorkflowOutcomeHealth
            {
                Component = "observer", OutageKey = "namespace-b:visibility", State = "open", Reason = "visibility_unavailable", Namespace = "namespace-b",
                GapStart = DateTime.UtcNow.AddHours(-1), FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow, UnknownRunCount = true,
            });
            await db.SaveChangesAsync();
        });

        var failures = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, Root));
        var item = failures.GetProperty("items").EnumerateArray().Single();
        Assert.Equal((stored.Id, "stored", "none"), (item.GetProperty("id").GetGuid(), item.GetProperty("stage").GetString(), item.GetProperty("deliveryState").GetString()));
        Assert.Equal(1, failures.GetProperty("counts").GetProperty("unresolvedCauses").GetInt32());
        Assert.Equal(1, failures.GetProperty("counts").GetProperty("coverageGaps").GetInt32());
        Assert.Equal("namespace-b", failures.GetProperty("namespaceErrors")[0].GetProperty("namespace").GetString());

        var causes = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, Root + "?state=unresolved_cause"));
        Assert.Equal([cause.Id], causes.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.AsAdmin(HttpMethod.Get, $"{Root}/{success.Id}")).StatusCode);

        // Past the attention threshold an unacknowledged failure escalates, and health says so once.
        _host.Clock.Advance(TimeSpan.FromMinutes(16));
        await _host.Batcher.RunOnceAsync(CancellationToken.None);
        await _host.Batcher.RunOnceAsync(CancellationToken.None);
        var escalated = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/{stored.Id}"));
        Assert.True(escalated.GetProperty("attentionOverdue").GetBoolean());
        Assert.Equal("escalated", escalated.GetProperty("stage").GetString());
        var health = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, Root + "/health"));
        Assert.Equal("degraded", health.GetProperty("state").GetString());
        Assert.Single(health.GetProperty("incidents").EnumerateArray(), i => i.GetProperty("outageKey").GetString() == "unacked_attention");
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dashboard")]
    public async Task Coverage_ReportsPendingAndDegradedNamespaces_NotEmptySuccess()
    {
        var now = _host.Clock.GetUtcNow().UtcDateTime;
        await _host.Database.WithContextAsync(async db =>
        {
            db.WorkflowOutcomeCheckpoints.Add(new WorkflowOutcomeCheckpoint
            {
                Namespace = "namespace-a", WindowKind = "recent", WindowKey = "namespace-a|recent|x", QueryHash = "q",
                PassCompletedAt = now, EnrolledAt = now.AddDays(-1), RetentionSeconds = 604800, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var coverage = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, Root + "/coverage"));
        var states = coverage.GetProperty("namespaces").EnumerateArray().ToDictionary(n => n.GetProperty("namespace").GetString()!, n => n.GetProperty("state").GetString());
        Assert.Equal("healthy", states["namespace-a"]);
        Assert.Equal("pending", states["namespace-b"]);
        Assert.True(coverage.GetProperty("degraded").GetBoolean());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dashboard")]
    public async Task Detail_RawPayloadOnlyOnExplicitRequest()
    {
        var protector = _host.App.Services.GetRequiredService<IWorkflowOutcomePayloadProtector>();
        var run = await _host.SeedRunAsync(r =>
        {
            r.PayloadKeyPurpose = "run";
            r.PayloadCiphertext = protector.Protect("run", System.Text.Encoding.UTF8.GetBytes("raw failure payload"));
        });

        var plain = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/{run.Id}"));
        Assert.False(plain.TryGetProperty("payload", out _));
        Assert.DoesNotContain("raw failure payload", plain.GetRawText());
        var detail = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/{run.Id}?detail=payload"));
        Assert.Equal("raw failure payload", detail.GetProperty("payload").GetString());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dashboard")]
    public async Task Detail_BatchMembershipCarriesNonceAndMemberVersion()
    {
        var (run, _) = await _host.AckedFailureAsync();
        var batch = await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync());

        var detail = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/{run.Id}"));
        var member = Assert.Single(detail.GetProperty("batches").EnumerateArray());
        Assert.Equal(batch.Nonce, member.GetProperty("nonce").GetString());
        Assert.Equal(1, member.GetProperty("version").GetInt32());
    }

    // ── late results ─────────────────────────────────────────────────────────

    private static WorkflowOutcomeLateResultRequest Late(string? ns, string taskId, string text, string status = "completed") =>
        new(ns, taskId, "message-1", WorkflowOutcomeHashes.Sha256Hex(text), status, text, 1, new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.Zero));

    [Fact]
    [Trait("WorkflowOutcomeSuite", "late-results")]
    public async Task LateResult_StoredEncryptedIdempotentAndCorrelatedToTheExactRun()
    {
        var run = await _host.SeedRunAsync();
        var request = Late("namespace-a", run.WorkflowId + "/step-a", "the late reply");

        var first = await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", request);
        var second = await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", request);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        Assert.Equal((await ApiHost.JsonAsync(first)).GetProperty("id").GetGuid(), (await ApiHost.JsonAsync(second)).GetProperty("id").GetGuid());

        var row = await _host.Database.QueryAsync(db => db.WorkflowOutcomeLateResults.AsNoTracking().SingleAsync());
        Assert.Equal((run.Id, "stored", false), (row.OutcomeId, row.Status, row.Quarantined));
        Assert.DoesNotContain("the late reply", System.Text.Encoding.UTF8.GetString(row.Ciphertext!));
        // The current attempt is never overwritten.
        Assert.Equal(run.Version, (await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id))).Version);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "late-results")]
    public async Task LateResult_UnknownOrAmbiguousCorrelation_IsQuarantined_NeverGuessed()
    {
        await _host.SeedRunAsync(r => { r.Namespace = "namespace-a"; r.WorkflowId = "shared"; r.SourceTaskRefsJson = "[\"shared/step\"]"; });
        await _host.SeedRunAsync(r => { r.Namespace = "namespace-b"; r.WorkflowId = "shared"; r.SourceTaskRefsJson = "[\"shared/step\"]"; });

        var ambiguous = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", Late(null, "shared/step", "a")));
        var unknown = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", Late("namespace-a", "nobody/step", "b")));

        Assert.Equal(("quarantined", "correlation_ambiguous"), (ambiguous.GetProperty("status").GetString(), ambiguous.GetProperty("quarantineReason").GetString()));
        Assert.Equal(("quarantined", "correlation_unknown"), (unknown.GetProperty("status").GetString(), unknown.GetProperty("quarantineReason").GetString()));
        Assert.All(await _host.Database.QueryAsync(db => db.WorkflowOutcomeLateResults.AsNoTracking().ToListAsync()), l => Assert.Null(l.OutcomeId));
        var incident = await _host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().SingleAsync(h => h.Component == "late_results"));
        Assert.Equal(("open", 2L), (incident.State, incident.KnownRunCount));
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "late-results")]
    [InlineData(1, "stored")]
    [InlineData(2, "quarantined")]
    public async Task LateResult_ExactMatchAfterManySubstringDecoys_IsNeverTruncatedAway(int exact, string expected)
    {
        // Five runs of the same workflow whose task refs merely CONTAIN the id, then the exact owner(s).
        for (var i = 0; i < 5; i++)
            await _host.SeedRunAsync(r => { r.WorkflowId = "chain"; r.SourceTaskRefsJson = $"[\"chain/step-{i}x\",\"x-chain/step\"]"; });
        var owners = new List<WorkflowOutcomeRun>();
        for (var i = 0; i < exact; i++)
            owners.Add(await _host.SeedRunAsync(r => { r.WorkflowId = "chain"; r.SourceTaskRefsJson = "[\"chain/step\"]"; }));

        var stored = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", Late("namespace-a", "chain/step", "late")));

        Assert.Equal(expected, stored.GetProperty("status").GetString());
        if (exact == 1) Assert.Equal(owners[0].Id, stored.GetProperty("outcomeId").GetGuid());
        else Assert.Equal("correlation_ambiguous", stored.GetProperty("quarantineReason").GetString());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "late-results")]
    public async Task LateResult_MalformedIs400_OversizeIs413WithQuarantineMetadata()
    {
        var good = Late("namespace-a", "t/step", "x");
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", "{not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", good with { ResultSha256 = new string('a', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", good with { TaskId = "" })).StatusCode);

        var big = new string('z', WorkflowOutcomeLimits.MaxLateResultBytes + 1);
        var oversize = await _host.AsAdmin(HttpMethod.Post, Root + "/late-results", Late("namespace-a", "t/step", big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversize.StatusCode);
        var row = await _host.Database.QueryAsync(db => db.WorkflowOutcomeLateResults.AsNoTracking().SingleAsync());
        Assert.Equal((true, "too_large", (byte[]?)null, WorkflowOutcomeLimits.MaxLateResultBytes + 1), (row.Quarantined, row.QuarantineReason, row.Ciphertext, row.SizeBytes));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task LateResult_KeyringUnavailable_Is503AndStoresNothing()
    {
        await using var host = await ApiHost.StartAsync(protector: new UnavailableWorkflowOutcomePayloadProtector());
        var response = await host.AsAdmin(HttpMethod.Post, Root + "/late-results", Late("namespace-a", "t/step", "x"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await host.Database.QueryAsync(db => db.WorkflowOutcomeLateResults.CountAsync()));
        await host.Database.DisposeAsync();
    }

    // ── failure-matrix ───────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Delivered_StoreFailsMidTransaction_Is503WithNoPartialReceipt()
    {
        await _host.SeedRunAsync();
        await _host.SeedRunAsync();
        await _host.FlushAsync();
        var batch = (await _host.InboxAsync()).Batches.Single();

        // The batch row update succeeds, the item update fails: the whole claim must roll back.
        _host.Database.Commands.FailWhen = c => c.CommandText.Contains("UPDATE \"workflow_outcome_batch_items\"", StringComparison.Ordinal);
        var response = await _host.DeliverAsync(batch);
        _host.Database.Commands.FailWhen = null;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null((await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync())).DeliveredAt);
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatchItems.CountAsync(i => i.DeliveredAt != null)));
        Assert.Equal(HttpStatusCode.NotFound, (await _host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}")).StatusCode);
        // The retry after recovery succeeds once.
        Assert.Equal(HttpStatusCode.OK, (await _host.DeliverAsync(batch)).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Views_StoreUnavailable_Is503NotEmptySuccess()
    {
        _host.Database.Commands.FailWhen = _ => true;
        var list = await _host.AsAdmin(HttpMethod.Get, Root);
        var inbox = await _host.AsCoordinator(HttpMethod.Get, Root + "/inbox?generation=1");
        var health = await _host.SendAsync(HttpMethod.Get, Root + "/health/public", null);
        _host.Database.Commands.FailWhen = null;

        Assert.Equal((HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable),
            (list.StatusCode, inbox.StatusCode, health.StatusCode));
        Assert.False((await ApiHost.JsonAsync(health)).GetProperty("healthy").GetBoolean());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public async Task Detail_KeyringUnavailable_FailsClosedAndKeepsTheCiphertext()
    {
        await using var host = await ApiHost.StartAsync(protector: new UnavailableWorkflowOutcomePayloadProtector());
        var run = await host.SeedRunAsync(r => { r.PayloadCiphertext = [1, 2, 3]; r.PayloadKeyPurpose = "run"; });

        var response = await host.AsAdmin(HttpMethod.Get, $"{Root}/{run.Id}?detail=payload");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal([1, 2, 3], (await host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync())).PayloadCiphertext);
        await host.Database.DisposeAsync();
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public void Preflight_EnabledWithoutKeyringOrToken_ListsEveryFaultByNameOnly()
    {
        var options = new Fleet.Orchestrator.Configuration.WorkflowOutcomeOptions { Enabled = true, KeyringDir = "/nonexistent/keys", CoordinatorTokenKey = "WO_UNSET_TOKEN_KEY" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var errors = WorkflowOutcomePreflight.Check(options, config);

        Assert.Contains("WorkflowOutcomes:KeyringDir does not exist", errors);
        Assert.Contains("WorkflowOutcomes:CoordinatorTokenKey names a variable with no value", errors);
        Assert.Contains("WorkflowOutcomes:Namespaces is required", errors);
        Assert.Empty(WorkflowOutcomePreflight.Check(new Fleet.Orchestrator.Configuration.WorkflowOutcomeOptions(), config));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "failure-matrix")]
    public void Preflight_KeyringMustBe0700()
    {
        var dir = Directory.CreateTempSubdirectory("wo-keys-").FullName;
        try
        {
            var options = new Fleet.Orchestrator.Configuration.WorkflowOutcomeOptions
            {
                Enabled = true, Namespaces = "namespace-a", OwnersFile = "/o", ClassifiersFile = "/c", KeyringDir = dir,
                CoordinatorTokenKey = "WO_PRESENT", CoordinatorBaseUrl = "http://orchestrator:3600",
            };
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WO_PRESENT"] = "secret-value" }).Build();

            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            var loose = WorkflowOutcomePreflight.Check(options, config);
            Assert.Equal(["WorkflowOutcomes:KeyringDir must have mode 0700"], loose);
            Assert.DoesNotContain("secret-value", string.Join(" ", loose));

            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Empty(WorkflowOutcomePreflight.Check(options, config));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }
}
