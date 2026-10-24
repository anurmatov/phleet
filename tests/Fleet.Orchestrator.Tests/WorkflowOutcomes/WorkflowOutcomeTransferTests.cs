using System.Net;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// #476 owner change: an unresolved failure stored under another coordinator (the previous owner,
/// a rule's or the policy default, or none) is re-owned only by an operator transfer to the current
/// coordinator in the current generation, and only then delivered for a fresh ack. Every stale,
/// replayed, foreign or unauthorized transfer is refused and changes nothing.
/// </summary>
public sealed class WorkflowOutcomeTransferTests : IAsyncLifetime
{
    private const string Root = "/api/workflow-outcomes";
    private const string Authority = "https://example.com/approvals/owner-change";
    private ApiHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await ApiHost.StartAsync();
        await _host.GrantProjectAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _host.Database.DisposeAsync();
    }

    private static WorkflowOutcomeTransferRequest Transfer(string? from, int version = 1, long generation = 1, string to = ApiHost.Coordinator,
        string reason = "coordinator changed; re-own for a fresh ack", string authority = Authority) =>
        new(version, generation, from, to, reason, authority);

    private Task<HttpResponseMessage> TransferAsync(Guid id, object? body) => _host.AsAdmin(HttpMethod.Post, $"{Root}/{id}/transfer", body);

    private async Task<string?> ErrorAsync(HttpResponseMessage response) => (await ApiHost.JsonAsync(response)).GetProperty("error").GetString();

    private Task<WorkflowOutcomeRun> RunAsync(Guid id) => _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.AsNoTracking().SingleAsync(r => r.Id == id));

    private Task<List<WorkflowOutcomeDecision>> TransfersAsync() => _host.Database.QueryAsync(db =>
        db.WorkflowOutcomeDecisions.AsNoTracking().Where(d => d.Operation == WorkflowOutcomeInboxService.TransferOperation).ToListAsync());

    private Task InsertMembershipAsync(WorkflowOutcomeRun run, string coordinator, long generation, string state = WorkflowOutcomeItemStates.Pending) =>
        _host.Database.WithContextAsync(async db =>
        {
            db.WorkflowOutcomeBatches.Add(new WorkflowOutcomeBatch
            {
                Id = Guid.NewGuid(), Coordinator = coordinator, CoordinatorGeneration = generation, State = WorkflowOutcomeBatchStates.Pending,
                Nonce = WorkflowOutcomeApi.NewNonce(), ItemVersionsHash = WorkflowOutcomeHashes.ItemVersionsHash([new WorkflowOutcomeItemRef(run.Id, run.Version)]),
                CreatedAt = DateTime.UtcNow,
                Items = [new WorkflowOutcomeBatchItem { OutcomeId = run.Id, OutcomeVersion = run.Version, DeliveryGeneration = generation, State = state }],
            });
            await db.SaveChangesAsync();
        });

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public async Task Transfer_AfterAnOwnerChange_RedeliversToTheCurrentCoordinatorForAFreshAck()
    {
        var run = await _host.SeedRunAsync(r => r.Coordinator = "old-coordinator");

        // Stranded before the transfer: never enqueued for the current coordinator, so no ack or decision.
        Assert.Equal(0, await _host.FlushAsync());
        Assert.Empty((await _host.InboxAsync()).Batches);
        Assert.Equal("foreign_outcome", await ErrorAsync(await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions", ApiHost.Decision(1, "hold"))));

        var transfer = await TransferAsync(run.Id, Transfer("old-coordinator"));
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
        Assert.Equal(ApiHost.Coordinator, (await RunAsync(run.Id)).Coordinator);
        var audit = Assert.Single(await TransfersAsync());
        Assert.Equal((run.Id, 1, WorkflowOutcomeInboxService.TransferredStatus, ApiHost.Coordinator, Authority, 1L),
            (audit.OutcomeId, audit.ExpectedVersion, audit.Status, audit.Owner, audit.AuthorityRef, audit.CoordinatorGeneration));
        Assert.Equal((WorkflowOutcomePrincipal.OperatorKind, WorkflowOutcomePrincipal.OperatorKind), (audit.DecidedBy, audit.DecidedVia));
        Assert.Contains("coordinator:old-coordinator", audit.EvidenceRefsJson);
        // The audit record is not shown as the run's last decision.
        var view = await ApiHost.JsonAsync(await _host.AsAdmin(HttpMethod.Get, $"{Root}/{run.Id}"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.GetProperty("lastDecision").ValueKind);

        // The batcher now delivers it in the current generation; only a fresh ack makes a decision possible.
        Assert.Equal(1, await _host.FlushAsync());
        var batch = (await _host.InboxAsync()).Batches.Single(b => b.Items.Any(i => i.Id == run.Id));
        Assert.Equal(1, batch.Generation);
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();
        Assert.Equal("not_acknowledged", await ErrorAsync(await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions",
            ApiHost.Decision(1, "hold", dueAt: _host.Clock.GetUtcNow().AddDays(1)))));
        (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(run.Id, 1))).EnsureSuccessStatusCode();
        var decision = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/decisions",
            ApiHost.Decision(1, "hold", dueAt: _host.Clock.GetUtcNow().AddDays(1)));
        Assert.Equal(HttpStatusCode.Created, decision.StatusCode);
        // The audit record is not an accepted decision and does not stand in for one.
        Assert.Single(await _host.Database.QueryAsync(db => db.WorkflowOutcomeDecisions.AsNoTracking().Where(d => d.Status == "accepted").ToListAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public async Task Transfer_SupersedesOldGenerationMemberships_AndAnOldGenerationIs409()
    {
        var run = await _host.SeedRunAsync(r => r.Coordinator = "old-coordinator");
        await InsertMembershipAsync(run, "old-coordinator", generation: 0);

        Assert.Equal("stale_generation", await ErrorAsync(await TransferAsync(run.Id, Transfer("old-coordinator", generation: 0))));
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(run.Id, Transfer("old-coordinator"))).StatusCode);
        var old = await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatchItems.AsNoTracking().SingleAsync(i => i.OutcomeId == run.Id && i.DeliveryGeneration == 0));
        Assert.Equal(WorkflowOutcomeItemStates.Superseded, old.State);

        Assert.Equal(1, await _host.FlushAsync());
        var batch = (await _host.InboxAsync()).Batches.Single(b => b.Items.Any(i => i.Id == run.Id));
        (await _host.DeliverAsync(batch)).EnsureSuccessStatusCode();
        // An ack sent for the previous generation is refused; the fresh one in the current generation lands.
        var stale = await _host.AsCoordinator(HttpMethod.Post, $"{Root}/batches/{batch.BatchId}/ack",
            new WorkflowOutcomeAckRequest(0, batch.Nonce, [new WorkflowOutcomeItemRef(run.Id, 1)], WorkflowOutcomeTurnIdentity.TaskId(batch.BatchId)));
        Assert.Equal((HttpStatusCode.Conflict, "stale_generation"), (stale.StatusCode, await ErrorAsync(stale)));
        Assert.Equal(HttpStatusCode.OK, (await _host.AckAsync(batch, new WorkflowOutcomeItemRef(run.Id, 1))).StatusCode);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public async Task Transfer_WithACurrentGenerationMembershipOfTheOldOwner_IsRefusedUntilTheGenerationIncrements()
    {
        var run = await _host.SeedRunAsync(r => r.Coordinator = "old-coordinator");
        await InsertMembershipAsync(run, "old-coordinator", generation: 1);

        var refused = await TransferAsync(run.Id, Transfer("old-coordinator"));

        Assert.Equal((HttpStatusCode.Conflict, "generation_not_incremented"), (refused.StatusCode, await ErrorAsync(refused)));
        Assert.Equal("old-coordinator", (await RunAsync(run.Id)).Coordinator);
        Assert.Empty(await TransfersAsync());
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    [InlineData("policy-default")]
    [InlineData("rule-owner")]
    [InlineData(null)]
    public async Task Transfer_FromAPolicyDefaultARuleOwnerOrUnassigned_ReachesTheCurrentCoordinator(string? stored)
    {
        var run = await _host.SeedRunAsync(r => r.Coordinator = stored);
        Assert.Equal(0, await _host.FlushAsync());

        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(run.Id, Transfer(stored))).StatusCode);

        Assert.Equal(1, await _host.FlushAsync());
        Assert.Contains((await _host.InboxAsync()).Batches, b => b.Items.Any(i => i.Id == run.Id));
        Assert.Contains("coordinator:" + (stored ?? "unassigned"), Assert.Single(await TransfersAsync()).EvidenceRefsJson);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public async Task Transfer_UnauthorizedMalformedStaleReplayedOrForeign_IsRefusedAndChangesNothing()
    {
        var run = await _host.SeedRunAsync(r => r.Coordinator = "old-coordinator");
        var resolved = await _host.SeedRunAsync(r => { r.Coordinator = "old-coordinator"; r.ResolvedAt = DateTime.UtcNow; });
        var unresolvedCause = await _host.SeedRunAsync(r => { r.Coordinator = "old-coordinator"; r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; });
        var current = await _host.SeedRunAsync();

        // No agent transfer authority: the coordinator token never reaches the handler, nor does an anonymous call.
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.AsCoordinator(HttpMethod.Post, $"{Root}/{run.Id}/transfer", Transfer("old-coordinator"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.SendAsync(HttpMethod.Post, $"{Root}/{run.Id}/transfer", null, Transfer("old-coordinator"))).StatusCode);

        var cases = new (Guid Id, object? Body, HttpStatusCode Status, string Error)[]
        {
            (run.Id, Transfer("old-coordinator", version: 0), HttpStatusCode.BadRequest, "malformed_request"),
            (run.Id, Transfer("", version: 1), HttpStatusCode.BadRequest, "malformed_request"),
            (run.Id, Transfer("old-coordinator", reason: " "), (HttpStatusCode)422, "reason_invalid"),
            (run.Id, Transfer("old-coordinator", authority: "operator said so"), (HttpStatusCode)422, "authority_missing"),
            (run.Id, Transfer("old-coordinator", to: "someone-else"), (HttpStatusCode)422, "foreign_coordinator"),
            (run.Id, Transfer("old-coordinator", generation: 2), HttpStatusCode.Conflict, "stale_generation"),
            (run.Id, Transfer("old-coordinator", version: 2), HttpStatusCode.Conflict, "stale_version"),
            (run.Id, Transfer("another-coordinator"), HttpStatusCode.Conflict, "coordinator_changed"),
            (run.Id, Transfer(null), HttpStatusCode.Conflict, "coordinator_changed"),
            (Guid.NewGuid(), Transfer("old-coordinator"), HttpStatusCode.NotFound, "outcome_not_found"),
            (resolved.Id, Transfer("old-coordinator"), HttpStatusCode.Conflict, "already_resolved"),
            (unresolvedCause.Id, Transfer("old-coordinator"), (HttpStatusCode)422, "not_confirmed_failure"),
            (current.Id, Transfer(ApiHost.Coordinator), HttpStatusCode.Conflict, "already_current"),
        };
        foreach (var (id, body, status, error) in cases)
        {
            var response = await TransferAsync(id, body);
            Assert.Equal((error, status), (error, response.StatusCode));
            Assert.Equal(error, await ErrorAsync(response));
        }
        Assert.Equal("old-coordinator", (await RunAsync(run.Id)).Coordinator);
        Assert.Empty(await TransfersAsync());

        // The first exact transfer lands once; replaying it is refused because the owner already moved.
        Assert.Equal(HttpStatusCode.OK, (await TransferAsync(run.Id, Transfer("old-coordinator"))).StatusCode);
        var replay = await TransferAsync(run.Id, Transfer("old-coordinator"));
        Assert.Equal((HttpStatusCode.Conflict, "coordinator_changed"), (replay.StatusCode, await ErrorAsync(replay)));
        Assert.Single(await TransfersAsync());
        Assert.Null((await RunAsync(run.Id)).ResolvedAt);
    }
}
