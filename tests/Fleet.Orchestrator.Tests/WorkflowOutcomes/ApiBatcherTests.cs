using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// The failure batcher (#476): exactly the confirmed-failure predicate, ≤BatchSize members
/// coalesced by failure incident, enqueued within 30 s of storage, unique per
/// (outcome, version, generation), with a deduplicated unacked-attention incident.
/// </summary>
public sealed class ApiBatcherTests : IAsyncLifetime
{
    private ApiHost _host = null!;

    public async Task InitializeAsync() => _host = await ApiHost.StartAsync(configure: c => c["WorkflowOutcomes:BatchSize"] = "3");

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _host.Database.DisposeAsync();
    }

    private Task<List<WorkflowOutcomeBatchItem>> ItemsAsync() =>
        _host.Database.QueryAsync(db => db.WorkflowOutcomeBatchItems.AsNoTracking().ToListAsync());

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public async Task Enqueue_OnlyConfirmedFailures_BusinessReasonsIncluded()
    {
        var failed = await _host.SeedRunAsync();
        var business = await _host.SeedRunAsync(r => { r.Status = "Completed"; r.ReasonCode = WorkflowOutcomeReasonCodes.BusinessReviewCap; });
        foreach (var classification in WorkflowOutcomeClassifications.All.Where(c => c != WorkflowOutcomeClassifications.ConfirmedFailure))
            await _host.SeedRunAsync(r => r.Classification = classification);
        await _host.SeedRunAsync(r => r.Quarantined = true);
        await _host.SeedRunAsync(r => r.ResolvedAt = DateTime.UtcNow);
        await _host.SeedRunAsync(r => r.EvidenceState = WorkflowOutcomeEvidenceStates.SuccessorPending);
        await _host.SeedRunAsync(r => r.Coordinator = "other-coordinator");
        await _host.SeedRunAsync(r => r.Coordinator = null);

        await _host.FlushAsync();

        var members = (await ItemsAsync()).Select(i => i.OutcomeId).Order().ToArray();
        Assert.Equal(new[] { failed.Id, business.Id }.Order().ToArray(), members);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "recursion")]
    public async Task Enqueue_TwoHundredSuccessfulOrVoidRuns_CreateNoBatch()
    {
        foreach (var type in new[] { "NotifyCtoWorkflow", "MemoryStoreRequestWorkflow", "AuthTokenRefreshWorkflow", "TaskDelegationWorkflow" })
            for (var i = 0; i < 50; i++)
                await _host.SeedRunAsync(r =>
                {
                    r.WorkflowType = type; r.Status = "Completed"; r.ReasonCode = i % 2 == 0 ? "normal_completion" : "null_result_only";
                    r.Classification = i % 2 == 0 ? WorkflowOutcomeClassifications.ExcludedNonfailure : WorkflowOutcomeClassifications.InsufficientEvidence;
                });

        Assert.Equal(0, await _host.FlushAsync());
        Assert.Equal(0, await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.CountAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "timing")]
    public async Task Enqueue_WaitsForTheCoalescingWindow_ButAlwaysWithin30Seconds()
    {
        var run = await _host.SeedRunAsync();
        var storedAt = _host.Clock.GetUtcNow();

        _host.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await _host.Batcher.RunOnceAsync(CancellationToken.None));

        // The pass that first sees the oldest member past the window flushes it; the timer ticks
        // every 2 s, so the worst case is still inside 30 s of storage.
        _host.Clock.Advance(_host.Batcher.FlushAge - TimeSpan.FromSeconds(10));
        Assert.Equal(1, await _host.Batcher.RunOnceAsync(CancellationToken.None));
        var batch = await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync());
        Assert.True(WorkflowOutcomeApi.Utc(batch.CreatedAt) - storedAt + WorkflowOutcomeBatcher.Tick <= TimeSpan.FromSeconds(30));
        Assert.Equal(run.Id, Assert.Single(await ItemsAsync()).OutcomeId);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "timing")]
    public async Task Enqueue_AFullBatchFlushesImmediately()
    {
        for (var i = 0; i < 3; i++) await _host.SeedRunAsync();
        Assert.Equal(1, await _host.Batcher.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "chains")]
    public async Task Enqueue_CoalescesByIncident_KeepsEachRunAndCapsBatchSize()
    {
        var incident = Guid.NewGuid();
        var chain = new[]
        {
            await _host.SeedRunAsync(r => r.FailureIncidentId = incident),
            await _host.SeedRunAsync(r => r.FailureIncidentId = incident),
        };
        _host.Clock.Advance(TimeSpan.FromSeconds(1));
        var lone = await _host.SeedRunAsync();
        var late = await _host.SeedRunAsync(r => r.FailureIncidentId = incident);
        var other = await _host.SeedRunAsync();

        Assert.Equal(2, await _host.FlushAsync());

        var batches = (await ItemsAsync()).GroupBy(i => i.BatchId).Select(g => g.Select(i => i.OutcomeId).ToHashSet()).ToList();
        Assert.All(batches, b => Assert.True(b.Count <= 3));
        Assert.Equal(5, batches.Sum(b => b.Count));
        // The three chain members ride together, each with its own membership row.
        Assert.Contains(batches, b => b.SetEquals([chain[0].Id, chain[1].Id, late.Id]));
        Assert.Contains(batches, b => b.SetEquals([lone.Id, other.Id]));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ordering")]
    public async Task Enqueue_IsUniquePerVersionAndGeneration_ANewVersionNeedsANewMembership()
    {
        var run = await _host.SeedRunAsync();
        Assert.Equal(1, await _host.FlushAsync());
        Assert.Equal(0, await _host.FlushAsync());

        await _host.Database.WithContextAsync(db => db.WorkflowOutcomeRuns.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, 2)));
        Assert.Equal(1, await _host.FlushAsync());
        Assert.Equal(0, await _host.FlushAsync());

        var items = (await ItemsAsync()).OrderBy(i => i.OutcomeVersion).ToList();
        Assert.Equal([(1, WorkflowOutcomeItemStates.Superseded), (2, WorkflowOutcomeItemStates.Pending)],
            items.Select(i => (i.OutcomeVersion, i.State)).ToArray());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "catchup")]
    public async Task Enqueue_CoordinatorOffline_BatchesStayPendingAndUnackedAttentionIsOneIncident()
    {
        await _host.SeedRunAsync();
        await _host.FlushAsync();

        _host.Clock.Advance(TimeSpan.FromMinutes(16));
        for (var i = 0; i < 3; i++) await _host.Batcher.RunOnceAsync(CancellationToken.None);

        var health = await _host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().ToListAsync());
        var incident = Assert.Single(health);
        Assert.Equal(("inbox", "unacked_attention", "open", 1L), (incident.Component, incident.OutageKey, incident.State, incident.KnownRunCount));
        var batch = await _host.Database.QueryAsync(db => db.WorkflowOutcomeBatches.AsNoTracking().SingleAsync());
        Assert.Equal(WorkflowOutcomeBatchStates.Pending, batch.State);

        // The coordinator comes back, delivers and acknowledges: the incident resolves, the row stays.
        var page = await _host.InboxAsync();
        var inbox = page.Batches.Single();
        (await _host.DeliverAsync(inbox)).EnsureSuccessStatusCode();
        (await _host.AckAsync(inbox, inbox.Items.Select(i => new WorkflowOutcomeItemRef(i.Id, i.Version)).ToArray())).EnsureSuccessStatusCode();
        await _host.Batcher.RunOnceAsync(CancellationToken.None);
        Assert.Equal("resolved", (await _host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().SingleAsync())).State);
        Assert.Equal(1, await _host.Database.QueryAsync(db => db.WorkflowOutcomeRuns.CountAsync()));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public async Task Enqueue_CoordinatorUnconfigured_EnqueuesNothingAndRaisesOneIncident()
    {
        await using var host = await ApiHost.StartAsync(configure: c => c["FLEET_CTO_AGENT"] = "");
        await host.SeedRunAsync();
        Assert.Equal(0, await host.FlushAsync());
        Assert.Equal(0, await host.FlushAsync());

        var incident = await host.Database.QueryAsync(db => db.WorkflowOutcomeHealth.AsNoTracking().SingleAsync());
        Assert.Equal(("coordinator_unconfigured", "open"), (incident.OutageKey, incident.State));
        await host.Database.DisposeAsync();
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void Pack_SplitsOnlyOversizeGroupsAndKeepsStorageOrder()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var incident = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 5).Select(i => new WorkflowOutcomeBatcher.Candidate(Guid.NewGuid(), 1, t.AddSeconds(i), incident))
            .Append(new WorkflowOutcomeBatcher.Candidate(Guid.NewGuid(), 1, t.AddSeconds(9), null)).ToList();

        var batches = WorkflowOutcomeBatcher.Pack(candidates, 2);

        Assert.Equal([2, 2, 2], batches.Select(b => b.Count).ToArray());
        Assert.Equal(candidates.Select(c => c.Id), batches.SelectMany(b => b).Select(c => c.Id));
    }
}
