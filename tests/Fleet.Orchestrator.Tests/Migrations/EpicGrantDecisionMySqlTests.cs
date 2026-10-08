using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.EpicGrants;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace Fleet.Orchestrator.Tests.Migrations;

/// <summary>
/// AC-M2..M5 (#436): the at-most-once decision guard against real MySQL 8.0, on a context built the
/// way <c>Program.cs</c> builds it (retrying execution strategy included). Temporal and GitHub are
/// fakes; the database, its locks and its unique index are real. Lives in this namespace so the
/// "Orchestrator migrations (MySQL)" CI job runs it, and it fails rather than skips without a server.
/// </summary>
public sealed class EpicGrantDecisionMySqlTests : IAsyncLifetime
{
    private string _admin = "";
    private string _database = "";
    private string _connectionString = "";
    private EpicGrantWorld _world = null!;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(AddEpicGrantsMigrationTests.ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{AddEpicGrantsMigrationTests.ConnectionVariable} is required; migration tests never skip");
        _admin = new MySqlConnectionStringBuilder(configured) { Database = "" }.ConnectionString;
        _database = $"orch_epic_{Guid.NewGuid():N}"[..30];
        await ExecAsync(_admin, $"CREATE DATABASE `{_database}` CHARACTER SET utf8mb4");
        _connectionString = new MySqlConnectionStringBuilder(configured) { Database = _database }.ConnectionString;

        await using (var db = NewContext())
            await db.Database.MigrateAsync();

        _world = new EpicGrantWorld(NewContext);
        await _world.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database.Length > 0)
            await ExecAsync(_admin, $"DROP DATABASE IF EXISTS `{_database}`");
    }

    /// <summary>The production context shape: MySQL 8.0 with retry-on-failure.</summary>
    private OrchestratorDbContext NewContext() => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseMySql(_connectionString, new MySqlServerVersion(new Version(8, 0, 0)),
                mysql => mysql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null))
            .Options);

    // ── AC-M2 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_concurrent_decisions_for_one_visit_send_once_and_refuse_once()
    {
        // Both requests pass D1–D10, wait for each other at the last step before D11, and are
        // released together into the reservation.
        var arrivals = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.GitHub.BeforeResponse = async () =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };

        var results = await Task.WhenAll(
            Task.Run(() => _world.DecideAsync()),
            Task.Run(() => _world.DecideAsync()));

        Assert.Equal(2, arrivals);
        Assert.Single(results, r => r.Result == EpicGrantDecisionResults.Sent);
        Assert.Single(results, r => r.Result == EpicGrantDecisionResults.Refused && r.Reason == EpicGrantRefusal.AlreadyDecided);
        Assert.Single(_world.Temporal.Signals);
        Assert.Equal(1, _world.Temporal.SignalAttempts);
        var row = Assert.Single(await _world.DecisionRowsAsync());
        Assert.Equal(EpicGrantDecisionStatus.Sent, row.Status);
    }

    // ── AC-M3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_send_error_leaves_send_failed_and_a_later_request_is_already_decided_with_no_signal()
    {
        _world.Temporal.SignalError = new InvalidOperationException("temporal unavailable");
        var first = await _world.DecideAsync();

        Assert.Equal(EpicGrantDecisionResults.SendFailed, first.Result);
        Assert.Equal(EpicGrantDecisionStatus.SendFailed, Assert.Single(await _world.DecisionRowsAsync()).Status);
        Assert.Equal(1, _world.Temporal.SignalAttempts);

        _world.Temporal.SignalError = null;
        var later = await _world.DecideAsync();

        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.AlreadyDecided), (later.Result, later.Reason));
        Assert.Equal(1, _world.Temporal.SignalAttempts);
        Assert.Empty(_world.Temporal.Signals);
        Assert.Equal(EpicGrantDecisionStatus.SendFailed, Assert.Single(await _world.DecisionRowsAsync()).Status);
    }

    // ── AC-M4 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_crash_after_the_insert_and_before_the_send_leaves_the_visit_decided()
    {
        // D11 commits, then the process "dies": no send, no status update.
        await using (var db = NewContext())
        {
            var reservation = await _world.Service(db).ReserveAsync(
                Guid.Parse(_world.GrantId), EpicGrantWorld.Ns, EpicGrantWorld.PrId, EpicGrantWorld.PrRun, "merge-approval",
                "merge-approval:2", EpicGrantWorld.HeadSha, EpicGrantWorld.Evidence, EpicGrantWorld.Cto, default);
            Assert.NotNull(reservation.DecisionId);
        }

        var next = await _world.DecideAsync();

        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.AlreadyDecided), (next.Result, next.Reason));
        Assert.Equal(0, _world.Temporal.SignalAttempts);
        Assert.Equal(EpicGrantDecisionStatus.Reserved, Assert.Single(await _world.DecisionRowsAsync()).Status);
    }

    // ── AC-M5 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_visit_for_the_same_run_and_gate_gets_a_new_row()
    {
        Assert.Equal(EpicGrantDecisionResults.Sent, (await _world.DecideAsync()).Result);

        _world.PrRunRecord.GateVisit = "merge-approval:3";
        var second = await _world.DecideAsync(EpicGrantWorld.Request(visitId: "merge-approval:3"));

        Assert.Equal(EpicGrantDecisionResults.Sent, second.Result);
        var rows = await _world.DecisionRowsAsync();
        Assert.Equal(["merge-approval:2", "merge-approval:3"], rows.Select(r => r.VisitId));
        Assert.All(rows, r => Assert.Equal((EpicGrantWorld.PrRun, "merge-approval", EpicGrantDecisionStatus.Sent), (r.RunId, r.Gate, r.Status)));
        Assert.Equal(2, _world.Temporal.Signals.Count);
    }

    [Fact]
    public void No_source_deletes_or_releases_a_decision_row()
    {
        var sources = EpicSourceScan.Sources();
        var touching = sources.Where(s => s.Text.Contains("EpicGrantDecision", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(touching);

        // No delete of any kind in a file that touches decisions, and no delete aimed at them anywhere.
        var deletes = new Regex(@"\.\s*(Remove|RemoveRange|ExecuteDelete|ExecuteDeleteAsync)\s*\(|DELETE\s+FROM", RegexOptions.IgnoreCase);
        Assert.Empty(touching.SelectMany(s => deletes.Matches(s.Text).Select(m => $"{s.Path}: {m.Value}")));
        Assert.Empty(EpicSourceScan.Matches(new Regex(@"epic_grant_decisions[^;]*\b(DELETE|TRUNCATE)\b|\b(DELETE|TRUNCATE)\b[^;]*epic_grant_decisions", RegexOptions.IgnoreCase)));
        Assert.Empty(EpicSourceScan.Matches(new Regex(@"EpicGrantDecisions\s*\.\s*(Remove|RemoveRange|ExecuteDelete)")));

        // "reserved" is written exactly once: by the insert.
        var reservedWrites = EpicSourceScan.Matches(new Regex(@"(?<![=!<>])=\s*EpicGrantDecisionStatus\.Reserved\b"));
        var insert = Assert.Single(reservedWrites);
        Assert.StartsWith(Path.Combine("Fleet.Orchestrator", "Services", "EpicGrantService.cs"), insert);
        var service = sources.Single(s => s.Path == Path.Combine("Fleet.Orchestrator", "Services", "EpicGrantService.cs")).Text;
        Assert.Matches(new Regex(@"new EpicGrantDecision\s*\{[^}]*Status = EpicGrantDecisionStatus\.Reserved,[^}]*\};\s*db\.EpicGrantDecisions\.Add\(row\);"), service);
        Assert.Empty(EpicSourceScan.Matches(new Regex(@"SetProperty\([^;]*EpicGrantDecisionStatus\.Reserved")));

        // The literal lives only in the status constant (other projects use the word for their own rows).
        var orchestrator = sources.Where(s => s.Path.StartsWith("Fleet.Orchestrator" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
        IEnumerable<string> InOrchestrator(Regex pattern) =>
            orchestrator.SelectMany(s => pattern.Matches(s.Text).Select(m => $"{s.Path}: {m.Value}"));
        Assert.Empty(InOrchestrator(new Regex(
            @"\bStatus\s*=\s*""reserved""|SetProperty\([^;]*""reserved""|SET\s+`?Status`?\s*=\s*'reserved'", RegexOptions.IgnoreCase)));
        Assert.Equal(
            [$"{Path.Combine("Fleet.Orchestrator", "Data", "Entities.cs")}: \"reserved\""],
            InOrchestrator(new Regex(@"""reserved""")));
    }

    // ── D11 ordering under a real row lock ───────────────────────────────────

    [Fact]
    public async Task Revoke_on_mysql_stops_the_next_decision_and_keeps_the_sent_row()
    {
        Assert.Equal(EpicGrantDecisionResults.Sent, (await _world.DecideAsync()).Result);

        await using (var db = NewContext())
        {
            var (outcome, view) = await _world.Service(db).RevokeAsync(_world.GrantId, "epic closed", default);
            Assert.Equal(EpicGrantRevokeOutcome.Revoked, outcome);
            Assert.Equal(("revoked", "epic closed"), (view!.Status, view.RevokeReason));
            Assert.Equal(EpicGrantRevokeOutcome.AlreadyRevoked, (await _world.Service(db).RevokeAsync(_world.GrantId, null, default)).Outcome);
        }

        _world.PrRunRecord.GateVisit = "merge-approval:3";
        var next = await _world.DecideAsync(EpicGrantWorld.Request(visitId: "merge-approval:3"));

        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.GrantInactive), (next.Result, next.Reason));
        Assert.Single(_world.Temporal.Signals);
        Assert.Equal(EpicGrantDecisionStatus.Sent, Assert.Single(await _world.DecisionRowsAsync()).Status);
    }

    [Fact]
    public async Task A_revoke_committed_while_the_decision_waits_on_the_grant_lock_wins()
    {
        // The revoke holds the grant row; the decision passes D2 (the revoke is uncommitted, so it
        // still reads active) and blocks in D11's FOR UPDATE until the revoke commits.
        await using var revoker = new MySqlConnection(_connectionString);
        await revoker.OpenAsync();
        await using var tx = await revoker.BeginTransactionAsync();
        await using (var revoke = new MySqlCommand(
            "UPDATE epic_grants SET Status = 'revoked', RevokedAt = UTC_TIMESTAMP(6) WHERE Id = @id AND Status = 'active'",
            revoker, tx))
        {
            revoke.Parameters.AddWithValue("@id", _world.GrantId);
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        }

        var reachedReservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _world.GitHub.BeforeResponse = () =>
        {
            reachedReservation.TrySetResult();
            return Task.CompletedTask;
        };
        var decision = Task.Run(() => _world.DecideAsync());
        await reachedReservation.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForLockWaitAsync();
        Assert.False(decision.IsCompleted);

        await tx.CommitAsync();
        var result = await decision.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal((EpicGrantDecisionResults.Refused, EpicGrantRefusal.GrantInactive), (result.Result, result.Reason));
        Assert.Equal(0, _world.Temporal.SignalAttempts);
        Assert.Empty(await _world.DecisionRowsAsync());
    }

    private async Task WaitForLockWaitAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.INNODB_TRX WHERE trx_state = 'LOCK WAIT'", connection);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("the decision never waited on the grant row lock");
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
