using System.Diagnostics;
using System.Net;
using System.Text;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.WorkflowOutcomes;
using Fleet.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations.WorkflowOutcomes;

/// <summary>
/// AC19 (#476) against real MySQL 8.0: <c>AddWorkflowOutcomeAccountability</c> fresh and as an
/// upgrade, its unique constraints, the guarded destructive Down, the once-only dispatch and
/// reservation under real concurrency, the API's SQL on the production provider, and a
/// dump+restore (with the keyring directory) that keeps pending failures. Fails rather than
/// skips without a server, like the other migration tests.
/// </summary>
public sealed class WorkflowOutcomeMigrationTests : IAsyncLifetime
{
    private const string Before = "20261014141428_RemoveAgentPiImageInput";
    private const string Target = "20261024050000_AddWorkflowOutcomeAccountability";
    private const string Root = "/api/workflow-outcomes";

    private static readonly string[] Tables =
    [
        "workflow_outcome_runs", "workflow_outcome_observations", "workflow_outcome_checkpoints", "workflow_outcome_batches",
        "workflow_outcome_batch_items", "workflow_outcome_decisions", "workflow_recovery_operations", "workflow_outcome_late_results",
        "workflow_outcome_health",
    ];

    private string _configured = "";
    private string _admin = "";
    private readonly List<string> _databases = [];
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(AddEpicGrantsMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{AddEpicGrantsMigrationTests.ConnectionVariable} is required; migration tests never skip");
        _configured = configured;
        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _connectionString = await CreateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var database in _databases)
            await ExecAsync(_admin, $"DROP DATABASE IF EXISTS `{database}`");
    }

    private async Task<string> CreateDatabaseAsync()
    {
        var name = $"orch_wo_{Guid.NewGuid():N}"[..30];
        await ExecAsync(_admin, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4");
        _databases.Add(name);
        return new MySqlConnectionStringBuilder(_configured) { Database = name }.ConnectionString;
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "migration")]
    public async Task Up_FromThePreviousMigration_AddsOnlyTheNineTablesAndTheirUniqueKeys()
    {
        await MigrateAsync(Before);
        var existing = await SchemaAsync();

        await MigrateAsync(Target);

        foreach (var table in Tables) Assert.Equal(1L, await ScalarAsync<long>(TableCount(table)));
        Assert.Equal(existing, await SchemaAsync(exclude: Tables));

        Assert.Equal("0|IdentityHash", await IndexAsync("workflow_outcome_runs", "IX_workflow_outcome_runs_IdentityHash"));
        Assert.Equal("ascii_bin", await ScalarAsync<string>("""
            SELECT COLLATION_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'workflow_outcome_runs' AND COLUMN_NAME = 'IdentityHash'
            """));
        Assert.Equal("0|OutcomeId,OutcomeVersion,DeliveryGeneration",
            await IndexAsync("workflow_outcome_batch_items", "IX_workflow_outcome_batch_items_OutcomeId_OutcomeVersion_Delive~"));
        Assert.Equal("0|DecisionId", await IndexAsync("workflow_recovery_operations", "IX_workflow_recovery_operations_DecisionId"));
        Assert.Equal("0|ReplacementWorkflowId", await IndexAsync("workflow_recovery_operations", "IX_workflow_recovery_operations_ReplacementWorkflowId"));
        Assert.Equal("0|Namespace,TaskId,ResultHash", await IndexAsync("workflow_outcome_late_results", "IX_workflow_outcome_late_results_Namespace_TaskId_ResultHash"));
        Assert.Equal("0|Component,OutageKey", await IndexAsync("workflow_outcome_health", "IX_workflow_outcome_health_Component_OutageKey"));
        Assert.Equal("0|RunRowId,EventId,EventType,EventHash",
            await IndexAsync("workflow_outcome_observations", "IX_workflow_outcome_observations_RunRowId_EventId_EventType_Eve~"));
        Assert.Equal("0|WindowKey", await IndexAsync("workflow_outcome_checkpoints", "IX_workflow_outcome_checkpoints_WindowKey"));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "migration")]
    public async Task FreshDatabase_EveryMigrationAppliesAndIncludesThisOne()
    {
        await using var db = NewContext(_connectionString);
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(Target, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "migration")]
    public async Task Down_OnEmptyTables_DropsOnlyTheNewTables_AndUpRestoresThem()
    {
        await MigrateAsync(Before);
        var existing = await SchemaAsync();
        await MigrateAsync(Target);

        await MigrateAsync(Before);

        foreach (var table in Tables) Assert.Equal(0L, await ScalarAsync<long>(TableCount(table)));
        Assert.Equal(existing, await SchemaAsync());
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE()"));

        await MigrateAsync(Target);
        foreach (var table in Tables) Assert.Equal(1L, await ScalarAsync<long>(TableCount(table)));
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "migration")]
    [InlineData("run")]
    [InlineData("health")]
    [InlineData("reservation")]
    public async Task Down_WhileUnresolvedRowsExist_IsRefusedAndDropsNothing(string unresolved)
    {
        await MigrateAsync(Target);
        var runId = Guid.NewGuid();
        await ExecAsync(_connectionString, $"""
            INSERT INTO workflow_outcome_runs (Id, Namespace, WorkflowId, RunId, IdentityHash, WorkflowType, Status, Classification, Version,
                                               EvidenceState, Quarantined, DetectedAt, StoredAt, UpdatedAt, ResolvedAt)
            VALUES ('{runId}', 'namespace-a', 'wf', 'run', REPEAT('a', 64), 'T', 'Failed', 'confirmed_failure', 1, 'complete', 0,
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), {(unresolved == "run" ? "NULL" : "UTC_TIMESTAMP(6)")})
            """);
        if (unresolved == "health")
            await ExecAsync(_connectionString, """
                INSERT INTO workflow_outcome_health (Component, OutageKey, State, Reason, KnownRunCount, UnknownRunCount, FirstSeenAt, LastSeenAt)
                VALUES ('observer', 'namespace-a', 'open', 'visibility_unavailable', 0, 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                """);
        if (unresolved == "reservation")
            await ExecAsync(_connectionString, $"""
                INSERT INTO workflow_recovery_operations (Id, DecisionId, OutcomeId, OutcomeVersion, Operation, SourceNamespace, SourceWorkflowId,
                    SourceRunId, TargetNamespace, ReplacementWorkflowId, Scope, Coordinator, StopProofRefsJson, AuthorityRef, State, ReservedBy,
                    ReservedAt, UpdatedAt)
                VALUES (UUID(), UUID(), '{runId}', 1, 'retry', 'namespace-a', 'wf', 'run', 'namespace-a', CONCAT('recovery-', UUID()), 's',
                        'coordinator', '[]', 'https://example.com/a', 'dispatch_unknown', 'operator', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                """);

        var refused = await Assert.ThrowsAsync<MySqlException>(() => MigrateAsync(Before));
        Assert.Equal("45000", refused.SqlState);
        Assert.Contains("workflow_outcomes_down_refused", refused.Message);
        foreach (var table in Tables) Assert.Equal(1L, await ScalarAsync<long>(TableCount(table)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM workflow_outcome_runs"));
        await using (var db = NewContext(_connectionString))
            Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync());

        // Resolved and exported by the operator: the rollback is then possible.
        await ExecAsync(_connectionString, "UPDATE workflow_outcome_runs SET ResolvedAt = UTC_TIMESTAMP(6)");
        await ExecAsync(_connectionString, "UPDATE workflow_outcome_health SET State = 'resolved'");
        await ExecAsync(_connectionString, "UPDATE workflow_recovery_operations SET State = 'blocked'");
        await MigrateAsync(Before);
        foreach (var table in Tables) Assert.Equal(0L, await ScalarAsync<long>(TableCount(table)));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "migration")]
    public async Task UniqueKeys_RejectASecondIdentityMembershipDecisionReservationLateResultAndIncident()
    {
        await MigrateAsync(Target);
        var now = DateTime.UtcNow;
        var run = Run(now);
        var batch = new WorkflowOutcomeBatch
        {
            Id = Guid.NewGuid(), Coordinator = "coordinator", CoordinatorGeneration = 1, State = "pending",
            Nonce = WorkflowOutcomeApi.NewNonce(), ItemVersionsHash = new string('b', 64), CreatedAt = now,
        };
        var decision = Decision(run.Id, now);
        await using (var db = NewContext(_connectionString))
        {
            db.AddRange(run, batch, decision);
            db.WorkflowOutcomeBatchItems.Add(new WorkflowOutcomeBatchItem { BatchId = batch.Id, OutcomeId = run.Id, OutcomeVersion = 1, DeliveryGeneration = 1, State = "pending" });
            db.WorkflowRecoveryOperations.Add(Operation(Guid.NewGuid(), decision.Id, run, now));
            db.WorkflowOutcomeLateResults.Add(Late(now));
            db.WorkflowOutcomeHealth.Add(new WorkflowOutcomeHealth { Component = "inbox", OutageKey = "unacked_attention", State = "open", Reason = "r", FirstSeenAt = now, LastSeenAt = now });
            db.WorkflowOutcomeObservations.Add(new WorkflowOutcomeObservation { RunRowId = run.Id, EventId = 12, EventType = "WorkflowExecutionFailed", EventHash = "h", Status = "Failed", Classification = "confirmed_failure", ObservedAt = now });
            await db.SaveChangesAsync();
        }

        async Task Duplicate(Action<OrchestratorDbContext> add)
        {
            await using var db = NewContext(_connectionString);
            add(db);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(1062, Assert.IsType<MySqlException>(error.InnerException).Number);
        }

        // Same exact run identity under a new row id.
        await Duplicate(db => db.WorkflowOutcomeRuns.Add(Run(now, run.WorkflowId, run.RunId)));
        await Duplicate(db => db.WorkflowOutcomeBatchItems.Add(new WorkflowOutcomeBatchItem { BatchId = batch.Id, OutcomeId = run.Id, OutcomeVersion = 1, DeliveryGeneration = 1, State = "pending" }));
        await Duplicate(db => db.WorkflowOutcomeDecisions.Add(Decision(run.Id, now, decision.Id)));
        await Duplicate(db => db.WorkflowRecoveryOperations.Add(Operation(Guid.NewGuid(), decision.Id, run, now)));
        await Duplicate(db => db.WorkflowOutcomeLateResults.Add(Late(now)));
        await Duplicate(db => db.WorkflowOutcomeHealth.Add(new WorkflowOutcomeHealth { Component = "inbox", OutageKey = "unacked_attention", State = "open", Reason = "r", FirstSeenAt = now, LastSeenAt = now }));
        await Duplicate(db => db.WorkflowOutcomeObservations.Add(new WorkflowOutcomeObservation { RunRowId = run.Id, EventId = 12, EventType = "WorkflowExecutionFailed", EventHash = "h", Status = "Failed", Classification = "confirmed_failure", ObservedAt = now }));

        // A new version or a new generation is a new membership; a differently-cased identity is a different run.
        await using (var db = NewContext(_connectionString))
        {
            db.WorkflowOutcomeBatchItems.Add(new WorkflowOutcomeBatchItem { BatchId = batch.Id, OutcomeId = run.Id, OutcomeVersion = 2, DeliveryGeneration = 1, State = "pending" });
            db.WorkflowOutcomeBatchItems.Add(new WorkflowOutcomeBatchItem { BatchId = batch.Id, OutcomeId = run.Id, OutcomeVersion = 1, DeliveryGeneration = 2, State = "pending" });
            db.WorkflowOutcomeRuns.Add(Run(now, run.WorkflowId.ToUpperInvariant(), run.RunId));
            await db.SaveChangesAsync();
        }
    }

    // ── Concurrency on the production provider ───────────────────────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "dispatch")]
    public async Task AuthorizeDispatch_ConcurrentCallsOnRealMySql_ExactlyOneWinner()
    {
        await MigrateAsync(Target);
        var now = DateTime.UtcNow;
        var run = Run(now);
        var decision = Decision(run.Id, now);
        var operationId = Guid.NewGuid();
        await using (var db = NewContext(_connectionString))
        {
            db.AddRange(run, decision, Operation(operationId, decision.Id, run, now));
            await db.SaveChangesAsync();
        }
        var probe = new FakeRecoveryProbe();
        probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Failed");

        var request = new WorkflowRecoveryDispatchRequest("coordinator", "temporal_start_workflow", "namespace-a", "TaskDelegationWorkflow", "fleet",
            new string('c', 64), null, null, null);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = NewContext(_connectionString);
            return await Recovery(db, probe).AuthorizeDispatchAsync(Bridge, operationId, request, CancellationToken.None);
        })));

        Assert.Single(results, r => r.Status == 200);
        Assert.Equal(7, results.Count(r => r.Status == 409));
        await using var check = NewContext(_connectionString);
        Assert.Equal(WorkflowRecoveryStates.Dispatching, (await check.WorkflowRecoveryOperations.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "decisions")]
    public async Task Reserve_ConcurrentDecisionsForOneFailureOnRealMySql_ExactlyOneReservation()
    {
        await MigrateAsync(Target);
        var now = DateTime.UtcNow;
        var run = Run(now);
        var decisions = Enumerable.Range(0, 6).Select(_ => Decision(run.Id, now)).ToList();
        await using (var db = NewContext(_connectionString))
        {
            db.Add(run);
            db.AddRange(decisions);
            await db.SaveChangesAsync();
        }
        var probe = new FakeRecoveryProbe();
        probe.SetRun(run.Namespace, run.WorkflowId, run.RunId, "Failed");

        var results = await Task.WhenAll(decisions.Select(d => Task.Run(async () =>
        {
            await using var db = NewContext(_connectionString);
            return await Recovery(db, probe).ReserveAsync(Operator, Guid.NewGuid(), new WorkflowRecoveryReserveRequest(
                d.Id, 1, "namespace-a", "TaskDelegationWorkflow", "fleet", null, new string('c', 64), "retry once",
                ["task:wf/step-a"], "https://example.com/approvals/1"), CancellationToken.None);
        })));

        Assert.Single(results, r => r.Status == 201);
        Assert.All(results.Where(r => r.Status != 201), r => Assert.Equal(409, r.Status));
        await using var check = NewContext(_connectionString);
        Assert.Equal(1, await check.WorkflowRecoveryOperations.CountAsync());
    }

    // ── The API on the production provider, then dump + restore ──────────────

    [Fact]
    [Trait("WorkflowOutcomeSuite", "migration")]
    public async Task Api_OnRealMySql_EveryWritePathTranslates_AndDumpRestoreWithTheKeyringKeepsPendingFailures()
    {
        var keyring = Directory.CreateTempSubdirectory("wo-keyring-");
        try
        {
            IWorkflowOutcomePayloadProtector Protector() => new DataProtectionWorkflowOutcomePayloadProtector(
                DataProtectionProvider.Create(keyring, b => b.SetApplicationName("phleet-workflow-outcomes")));

            var clock = new ManualClock();
            var source = ApiDatabase.ForMySql(_connectionString);
            string pendingNonce;
            Guid pendingBatch, pendingRun, receiptRun;
            string? receiptHash;
            await using (var host = await ApiHost.StartAsync(source, protector: Protector(), clock: clock))
            {
                await host.GrantProjectAsync();
                await host.SeedAgentsAsync("agent1", "agent2");

                // Batcher (insert + supersede UPDATEs with subqueries), delivered, partial and full ack.
                var a = await host.SeedRunAsync(r => r.PayloadCiphertext = Protector().Protect("run", Encoding.UTF8.GetBytes("encrypted evidence")));
                var b = await host.SeedRunAsync();
                Assert.Equal(1, await host.FlushAsync());
                var batch = (await host.InboxAsync()).Batches.Single();
                Assert.Equal(HttpStatusCode.OK, (await host.DeliverAsync(batch)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.AckAsync(batch, new WorkflowOutcomeItemRef(a.Id, 1))).StatusCode);
                await host.Database.WithContextAsync(db => db.WorkflowOutcomeRuns.Where(r => r.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, 2)));
                Assert.Equal(1, await host.FlushAsync());
                Assert.Equal(HttpStatusCode.Conflict, (await host.AckAsync(batch, new WorkflowOutcomeItemRef(b.Id, 1))).StatusCode);
                var receipt = await ApiHost.ReadAsync<WorkflowOutcomeReceipt>(await host.AsCoordinator(HttpMethod.Get, $"{Root}/batches/{batch.BatchId}/receipt?nonce={batch.Nonce}"));
                (receiptRun, receiptHash) = (a.Id, receipt.ReceiptHash);

                // Decisions (FOR UPDATE + conditional resolve) and the operator classification.
                Assert.Equal(HttpStatusCode.Created, (await host.AsCoordinator(HttpMethod.Post, $"{Root}/{a.Id}/decisions",
                    ApiHost.Decision(1, "hold", dueAt: clock.GetUtcNow().AddDays(1)))).StatusCode);
                var cause = await host.SeedRunAsync(r => { r.Classification = WorkflowOutcomeClassifications.CauseUnresolved; r.Status = "Terminated"; r.TerminalEventId = 7; });
                Assert.Equal(HttpStatusCode.OK, (await host.AsAdmin(HttpMethod.Post, $"{Root}/{cause.Id}/classification", new
                {
                    expectedVersion = 1, classification = "confirmed_failure", reasonCode = "failure_caused_terminate", terminalEventRef = "event:7",
                    causalEvidenceRefs = new[] { "event:3" }, authorityRef = "https://example.com/approvals/2",
                })).StatusCode);

                // Views: keyset list, detail with payload, coverage grouping, health.
                var list = await ApiHost.JsonAsync(await host.AsAdmin(HttpMethod.Get, $"{Root}?limit=1"));
                var next = await host.AsAdmin(HttpMethod.Get, $"{Root}?limit=1&cursor={Uri.EscapeDataString(list.GetProperty("nextCursor").GetString()!)}");
                Assert.Equal(HttpStatusCode.OK, next.StatusCode);
                var detail = await ApiHost.JsonAsync(await host.AsAdmin(HttpMethod.Get, $"{Root}/{a.Id}?detail=payload"));
                Assert.Equal("encrypted evidence", detail.GetProperty("payload").GetString());
                Assert.Equal(HttpStatusCode.OK, (await host.AsAdmin(HttpMethod.Get, $"{Root}/coverage")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.AsAdmin(HttpMethod.Get, $"{Root}/health")).StatusCode);

                // Late result, reservation, dispatch and receipt.
                var text = "late reply";
                Assert.Equal(HttpStatusCode.Created, (await host.AsAdmin(HttpMethod.Post, $"{Root}/late-results", new WorkflowOutcomeLateResultRequest(
                    "namespace-a", "unrelated/step", "m-1", WorkflowOutcomeHashes.Sha256Hex(text), "failed", text, 1, clock.GetUtcNow()))).StatusCode);
                var retried = await host.SeedRunAsync();
                await host.FlushAsync();
                var retryBatch = (await host.InboxAsync()).Batches.Single(x => x.Items.Any(i => i.Id == retried.Id));
                await host.DeliverAsync(retryBatch);
                await host.AckAsync(retryBatch, new WorkflowOutcomeItemRef(retried.Id, 1));
                host.Probe.SetRun(retried.Namespace, retried.WorkflowId, retried.RunId, "Failed");
                var decisionId = (await ApiHost.JsonAsync(await host.AsCoordinator(HttpMethod.Post, $"{Root}/{retried.Id}/decisions", ApiHost.Decision(1, "retry"))))
                    .GetProperty("decisionId").GetGuid();
                var operationId = Guid.NewGuid();
                Assert.Equal(HttpStatusCode.Created, (await host.AsAdmin(HttpMethod.Post, $"{Root}/recovery/{operationId}/reserve", new WorkflowRecoveryReserveRequest(
                    decisionId, 1, "namespace-a", "TaskDelegationWorkflow", "fleet", "agent2", new string('c', 64), "retry once",
                    ["task:" + retried.WorkflowId + "/step-a"], "https://example.com/approvals/3"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.AsAdmin(HttpMethod.Post, $"{Root}/recovery/{operationId}/authorize-dispatch",
                    new WorkflowRecoveryDispatchRequest("coordinator", "temporal_start_workflow", "namespace-a", "TaskDelegationWorkflow", "fleet", new string('c', 64), null, null, null))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await host.AsAdmin(HttpMethod.Post, $"{Root}/recovery/{operationId}/receipt",
                    new WorkflowRecoveryReceiptRequest("unknown", null, null, null, "deadline"))).StatusCode);

                // Leave one batch pending (enqueued, not delivered) for the restore check.
                var pending = await host.SeedRunAsync();
                await host.FlushAsync();
                var pendingInbox = (await host.InboxAsync()).Batches.Single(x => x.Items.Any(i => i.Id == pending.Id));
                (pendingBatch, pendingNonce, pendingRun) = (pendingInbox.BatchId, pendingInbox.Nonce, pending.Id);
            }

            // Dump the live database and restore it into a new one, keyring directory unchanged.
            var restored = await CreateDatabaseAsync();
            await RestoreAsync(await DumpAsync(_connectionString), restored);
            Assert.Equal(await CountsAsync(_connectionString), await CountsAsync(restored));

            await using var after = await ApiHost.StartAsync(ApiDatabase.ForMySql(restored), protector: Protector(), clock: clock);
            var inbox = await after.InboxAsync();
            var batchAfter = inbox.Batches.Single(x => x.BatchId == pendingBatch);
            Assert.Equal((pendingNonce, pendingRun), (batchAfter.Nonce, batchAfter.Items.Single().Id));
            Assert.Equal(HttpStatusCode.OK, (await after.DeliverAsync(batchAfter)).StatusCode);
            var detailAfter = await ApiHost.JsonAsync(await after.AsAdmin(HttpMethod.Get, $"{Root}/{receiptRun}?detail=payload"));
            Assert.Equal("encrypted evidence", detailAfter.GetProperty("payload").GetString());
            var unknown = await after.Database.QueryAsync(db => db.WorkflowRecoveryOperations.AsNoTracking().SingleAsync());
            Assert.Equal(WorkflowRecoveryStates.DispatchUnknown, unknown.State);
            Assert.NotNull(receiptHash);
            Assert.Equal(0, await after.FlushAsync());
        }
        finally
        {
            keyring.Delete(recursive: true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly WorkflowOutcomePrincipal Bridge = new(WorkflowOutcomePrincipal.BridgeKind, "bridge", 0);
    private static readonly WorkflowOutcomePrincipal Operator = new(WorkflowOutcomePrincipal.OperatorKind, "operator", 0);

    private static WorkflowRecoveryService Recovery(OrchestratorDbContext db, IWorkflowRecoveryProbe probe)
    {
        var options = Options.Create(new WorkflowOutcomeOptions { Enabled = true, Namespaces = "namespace-a" });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:AuthToken"] = "admin", ["FLEET_CTO_AGENT"] = "coordinator",
        }).Build();
        var auth = new WorkflowOutcomeAuthorization(options, configuration, NullLogger<WorkflowOutcomeAuthorization>.Instance);
        return new WorkflowRecoveryService(db, auth, probe, options, TimeProvider.System, NullLogger<WorkflowRecoveryService>.Instance);
    }

    private static WorkflowOutcomeRun Run(DateTime now, string? workflowId = null, string? runId = null)
    {
        workflowId ??= "wf";
        runId ??= Guid.NewGuid().ToString("D");
        return new WorkflowOutcomeRun
        {
            Id = Guid.NewGuid(), Namespace = "namespace-a", WorkflowId = workflowId, RunId = runId,
            IdentityHash = WorkflowOutcomeHashes.IdentityHash("namespace-a", workflowId, runId), WorkflowType = "TaskDelegationWorkflow",
            Status = "Failed", Classification = WorkflowOutcomeClassifications.ConfirmedFailure, Version = 1, Coordinator = "coordinator",
            EvidenceState = "complete", SourceTaskRefsJson = "[\"wf/step-a\"]", DetectedAt = now, StoredAt = now, UpdatedAt = now,
        };
    }

    private static WorkflowOutcomeDecision Decision(Guid outcomeId, DateTime now, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), OutcomeId = outcomeId, ExpectedVersion = 1, Operation = "retry", DecidedBy = "coordinator",
        DecidedVia = "coordinator", Owner = "agent1", Reason = "r", EvidenceRefsJson = "[]", Status = "accepted", CreatedAt = now,
    };

    private static WorkflowRecoveryOperation Operation(Guid id, Guid decisionId, WorkflowOutcomeRun run, DateTime now) => new()
    {
        Id = id, DecisionId = decisionId, OutcomeId = run.Id, OutcomeVersion = 1, Operation = "retry",
        SourceNamespace = run.Namespace, SourceWorkflowId = run.WorkflowId, SourceRunId = run.RunId,
        TargetNamespace = "namespace-a", TargetWorkflowType = "TaskDelegationWorkflow", TargetTaskQueue = "fleet", InputSha256 = new string('c', 64),
        ReplacementWorkflowId = "recovery-" + id.ToString("D"), Scope = "s", Coordinator = "coordinator", StopProofRefsJson = "[]",
        AuthorityRef = "https://example.com/a", State = WorkflowRecoveryStates.Reserved, ReservedBy = "operator", ReservedAt = now, UpdatedAt = now,
    };

    private static WorkflowOutcomeLateResult Late(DateTime now) => new()
    {
        Id = Guid.NewGuid(), Namespace = "", TaskId = "wf/step-a", ResultHash = new string('d', 64), MessageId = "m", Status = "stored",
        ResultStatus = "completed", ReceivedAt = now, StoredAt = now,
    };

    private async Task<string> CountsAsync(string connectionString)
    {
        var parts = new List<string>();
        foreach (var table in Tables)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand($"SELECT COUNT(*) FROM `{table}`", connection);
            parts.Add($"{table}={await command.ExecuteScalarAsync()}");
        }
        return string.Join(",", parts);
    }

    private static ProcessStartInfo MySqlTool(string tool, string connectionString, params string[] args)
    {
        var cs = new MySqlConnectionStringBuilder(connectionString);
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in new[] { "-h", cs.Server, "-P", cs.Port.ToString(), "-u", cs.UserID }.Concat(args)) psi.ArgumentList.Add(a);
        // The password goes through the environment, never the argument list.
        psi.Environment["MYSQL_PWD"] = cs.Password;
        return psi;
    }

    private static async Task<string> DumpAsync(string connectionString)
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        // --hex-blob: the encrypted payload columns are binary; a text round trip would corrupt them.
        using var process = Process.Start(MySqlTool("mysqldump", connectionString, "--single-transaction", "--routines", "--no-tablespaces", "--hex-blob", database))
            ?? throw new InvalidOperationException("mysqldump did not start");
        process.StandardInput.Close();
        var dump = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("mysqldump failed: " + error);
        Assert.Contains("workflow_outcome_runs", dump);
        return dump;
    }

    private static async Task RestoreAsync(string dump, string connectionString)
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        using var process = Process.Start(MySqlTool("mysql", connectionString, database))
            ?? throw new InvalidOperationException("mysql did not start");
        await process.StandardInput.WriteAsync(dump);
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("restore failed: " + error);
    }

    private static string TableCount(string table) => $"""
        SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'
        """;

    private Task<string> IndexAsync(string table, string index) => ScalarAsync<string>($"""
        SELECT CONCAT(MIN(NON_UNIQUE), '|', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX))
        FROM information_schema.STATISTICS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND INDEX_NAME = '{index}'
        """);

    private async Task<string> SchemaAsync(string[]? exclude = null)
    {
        var skip = string.Join(",", new[] { "__EFMigrationsHistory" }.Concat(exclude ?? []).Select(t => $"'{t}'"));
        var columns = await ScalarAsync<string>($"""
            SELECT GROUP_CONCAT(CONCAT_WS(' ', TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, IFNULL(COLUMN_DEFAULT, '~'), EXTRA)
                                ORDER BY TABLE_NAME, ORDINAL_POSITION SEPARATOR '\n')
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME NOT IN ({skip})
            """);
        var indexes = await ScalarAsync<string>($"""
            SELECT GROUP_CONCAT(CONCAT_WS(' ', TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME)
                                ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX SEPARATOR '\n')
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME NOT IN ({skip})
            """);
        return columns + "\n--\n" + indexes;
    }

    private static OrchestratorDbContext NewContext(string connectionString) => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)),
                mysql => mysql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null))
            .Options);

    private async Task MigrateAsync(string target)
    {
        await using var db = NewContext(_connectionString);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var raise = new MySqlCommand("SET SESSION group_concat_max_len = 1048576", connection))
            await raise.ExecuteNonQueryAsync();
        await using var command = new MySqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
