using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

// Real relational store tests, not gateway/agent acceptance or MySQL qualification.
public sealed class OpenShellLaunchStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private OrchestratorDbContext _db = null!;
    private OpenShellLaunchStore _store = null!;
    private readonly DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _agent;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "test" };
        _db.Agents.Add(agent);
        await _db.SaveChangesAsync();
        _agent = agent.Id;
        _store = new(_db, new FixedClock(_now));
    }

    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task Reserve_CreatesPreparedLaunchAndLease_WithoutChangingRuntime()
    {
        var launch = await _store.ReserveAsync(_agent, "old-runtime", _now, TimeSpan.FromSeconds(120));
        Assert.NotEqual(Guid.Empty, launch.Id);
        Assert.Equal("prepared", launch.Phase);
        Assert.Equal("old-runtime", launch.ExpectedOldRuntimeId);
        Assert.Null(launch.SandboxId);
        Assert.Null(launch.RevokedAt);
        Assert.Equal(_agent, launch.ActiveAgentId);
        var lease = await _db.OpenShellLeases.SingleAsync();
        Assert.Equal(launch.Id, lease.Owner);
        Assert.Equal(_now.AddSeconds(120), lease.ExpiresAt);
        Assert.Equal("test", (await _db.Agents.SingleAsync()).ContainerName);
    }

    [Fact]
    public async Task Reserve_ExistingActiveLaunch_RefusesEvenAfterLeaseExpiry()
    {
        await _store.ReserveAsync(_agent, null, _now, TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.ReserveAsync(_agent, null, _now.AddMinutes(5), TimeSpan.FromSeconds(30)));
        Assert.Single(await _db.OpenShellLaunches.ToListAsync());
    }

    [Fact]
    public async Task Bind_ExactRetryIsIdempotent_ButRebindingIsForbidden()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox2", _now));
        Assert.Equal("sandbox1", (await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).SandboxId);
    }

    [Fact]
    public async Task Bind_ExpiredLease_RefusesWithoutMapping()
    {
        var launch = await Reserve();
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now.AddMinutes(3)));
        Assert.Null((await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).SandboxId);
    }

    [Fact]
    public async Task Retire_RevokesAndDurablyTombstones_ButDoesNotClaimStopped()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await _store.RetireAsync(launch.Id, _now);
        await _store.RetireAsync(launch.Id, _now.AddSeconds(1));
        var retired = await _db.OpenShellRetiredIds.SingleAsync();
        Assert.Equal(_now, retired.RetiredAt);
        var saved = await _db.OpenShellLaunches.AsNoTracking().SingleAsync();
        Assert.Equal("cleanup_pending", saved.Phase);
        Assert.Equal(_now, saved.RevokedAt);
        Assert.Equal(_agent, saved.ActiveAgentId);
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now));
    }

    [Fact]
    public async Task Tombstone_RemainsAfterAgentAndLaunchDeletion_AndPreventsReuse()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await _store.RetireAsync(launch.Id, _now);
        // Simulate a later independently verified cleanup, not an implemented stop/delete path.
        _db.ChangeTracker.Clear();
        await _db.OpenShellLeases.ExecuteDeleteAsync();
        await _db.OpenShellLaunches.ExecuteDeleteAsync();
        await _db.Agents.ExecuteDeleteAsync();
        Assert.Single(await _db.OpenShellRetiredIds.ToListAsync());
        var agent = new Agent { Name = "agent2", DisplayName = "Agent 2", Role = "test", Model = "test", ContainerName = "test2" };
        _db.Agents.Add(agent); await _db.SaveChangesAsync();
        var next = await _store.ReserveAsync(agent.Id, null, _now, TimeSpan.FromSeconds(120));
        await _store.ConfirmLegacyStoppedAsync(next.Id, _now, new EvidenceReader(_now));
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.BindCreatedSandboxAsync(next.Id, "gateway1", "sandbox1", _now));
    }

    [Fact]
    public async Task Lease_RenewalRequiresExactOwnerAndUnexpiredLease()
    {
        var launch = await Reserve();
        Assert.False(await _store.RenewAsync(_agent, Guid.NewGuid(), _now, TimeSpan.FromSeconds(120)));
        Assert.True(await _store.RenewAsync(_agent, launch.Id, _now.AddSeconds(10), TimeSpan.FromSeconds(120)));
        Assert.False(await _store.RenewAsync(_agent, launch.Id, _now.AddMinutes(3), TimeSpan.FromSeconds(120)));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(121)]
    public async Task Lease_InvalidDuration_RefusesBeforeWrite(int seconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _store.ReserveAsync(_agent, null, _now, TimeSpan.FromSeconds(seconds)));
        Assert.Empty(await _db.OpenShellLaunches.ToListAsync());
    }

    [Fact]
    public void Model_TombstoneHasNoForeignKey_ActiveAgentAndMappingAreUnique()
    {
        var retired = _db.Model.FindEntityType(typeof(OpenShellRetiredId))!;
        Assert.Empty(retired.GetForeignKeys());
        var launch = _db.Model.FindEntityType(typeof(OpenShellLaunch))!;
        Assert.Contains(launch.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "ActiveAgentId" }));
        Assert.Contains(launch.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "GatewayId", "SandboxId" }));
    }

    [Fact]
    public async Task TrackedMappingMutation_RefusesAtWriteBoundary()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        launch.SandboxId = "sandbox2";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task TrackedTombstoneDeletion_RefusesAtWriteBoundary()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await _store.RetireAsync(launch.Id, _now);
        _db.OpenShellRetiredIds.Remove(await _db.OpenShellRetiredIds.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task TrackedRevocationUndo_RefusesAtWriteBoundary()
    {
        var launch = await Reserve();
        await _store.RetireAsync(launch.Id, _now);
        launch.RevokedAt = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public void DesiredMetadata_RequiredDefaults_DoNotDescribeRunningState()
    {
        var entity = _db.Model.FindEntityType(typeof(Agent))!;
        foreach (var (name, value) in new[] { ("ExecutionShell", "openshell"), ("OpenShellPolicy", "normal"), ("OpenShellMigrationState", "pending") })
        {
            var property = entity.FindProperty(name)!;
            Assert.False(property.IsNullable);
            Assert.Equal(value, property.GetDefaultValue());
        }
    }

    [Fact]
    public async Task ExternalBind_ThenRetireReloadsAuthoritativeMapping()
    {
        var launch = await Reserve();
        await using var other = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options);
        await new OpenShellLaunchStore(other).BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        Assert.Null(launch.SandboxId); // this context still holds the pre-bind instance
        await _store.RetireAsync(launch.Id, _now);
        var tombstone = await _db.OpenShellRetiredIds.AsNoTracking().SingleAsync();
        Assert.Equal("gateway1", tombstone.GatewayId);
        Assert.Equal("sandbox1", tombstone.SandboxId);
    }

    [Fact]
    public async Task ExternalRetirement_ThenIdempotentBindRejectsRevokedLaunch()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await using var other = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options);
        await new OpenShellLaunchStore(other).RetireAsync(launch.Id, _now);
        Assert.Null(launch.RevokedAt); // stale positive state must not authorize a bind
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now));
        Assert.Single(await _db.OpenShellRetiredIds.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PreparedLaunch_CannotBindBeforeConfirmedLegacyStop()
    {
        var launch = await _store.ReserveAsync(_agent, "legacy1", _now, TimeSpan.FromSeconds(120));
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now));
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.ConfirmLegacyStoppedAsync(launch.Id, _now, new EvidenceReader(_now, drained: false)));
        Assert.Equal("prepared", (await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).Phase);
    }

    [Fact]
    public async Task CleanupRequiresAbsenceAndDrain_ThenFreshLaunchCanReserve()
    {
        var launch = await Reserve();
        await _store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", _now);
        await _store.RetireAsync(launch.Id, _now);
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.CompleteCleanupAsync(launch.Id, _now, new EvidenceReader(_now, absent: false)));
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.CompleteCleanupAsync(launch.Id, _now, new EvidenceReader(_now, drained: false)));
        Assert.Equal(_agent, (await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).ActiveAgentId);
        await _store.CompleteCleanupAsync(launch.Id, _now, new EvidenceReader(_now));
        Assert.Empty(await _db.OpenShellLeases.ToListAsync());
        Assert.Null((await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).ActiveAgentId);
        var next = await _store.ReserveAsync(_agent, null, _now.AddSeconds(1), TimeSpan.FromSeconds(120));
        Assert.NotEqual(launch.Id, next.Id);
        Assert.NotEqual(launch.IdentityEpoch, next.IdentityEpoch);
        Assert.Single(await _db.OpenShellRetiredIds.ToListAsync());
    }

    [Fact]
    public async Task CleanupRefusesStaleOrMissingRuntimeEvidence()
    {
        var launch = await Reserve();
        await _store.RetireAsync(launch.Id, _now);
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.CompleteCleanupAsync(launch.Id, _now, new EvidenceReader(_now.AddSeconds(-6))));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _store.CompleteCleanupAsync(launch.Id, _now, null!));
        Assert.Equal(_agent, (await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).ActiveAgentId);
    }

    [Fact]
    public async Task CleanupRejectsForeignEpochBeforeReleasingSlot()
    {
        var launch = await Reserve();
        await _store.RetireAsync(launch.Id, _now);
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() =>
            _store.CompleteCleanupAsync(launch.Id, _now, new ForeignEpochReader(_now)));
        Assert.Equal(_agent, (await _db.OpenShellLaunches.AsNoTracking().SingleAsync()).ActiveAgentId);
        Assert.Single(await _db.OpenShellLeases.ToListAsync());
    }

    private sealed class ForeignEpochReader(DateTime now) : IOpenShellRuntimeEvidenceReader
    {
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken ct) =>
            Task.FromResult(new OpenShellRuntimeEvidence(target.LaunchId, Guid.NewGuid(), target.GatewayId,
                target.RuntimeId, true, true, 0, 0, now));
    }

    private async Task<OpenShellLaunch> Reserve()
    {
        var launch = await _store.ReserveAsync(_agent, null, _now, TimeSpan.FromSeconds(120));
        await _store.ConfirmLegacyStoppedAsync(launch.Id, _now, new EvidenceReader(_now));
        return launch;
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    private sealed class EvidenceReader(DateTime now, bool drained = true,
        bool absent = true) : IOpenShellRuntimeEvidenceReader
    {
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget target, CancellationToken ct) =>
            Task.FromResult(new OpenShellRuntimeEvidence(target.LaunchId, target.IdentityEpoch,
                target.GatewayId, target.RuntimeId,
                true, absent, drained ? 0 : 1, 0, now));
    }
}
