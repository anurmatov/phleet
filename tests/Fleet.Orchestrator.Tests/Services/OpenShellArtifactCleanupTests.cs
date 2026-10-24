using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellArtifactCleanupTests
{
    [Theory]
    [InlineData("success", 1)]
    [InlineData("proof_error", 0)]
    [InlineData("wrong_nonce", 0)]
    [InlineData("read_error", 0)]
    [InlineData("delete_error", 1)]
    [InlineData("still_present", 1)]
    [InlineData("missing_adapter", 0)]
    [InlineData("filesystem_error", 1)]
    [InlineData("proof_changes", 1)]
    [InlineData("unbound_incomplete", 0)]
    [InlineData("unbound_owned", 0)]
    public async Task ActualRetirement_ReleasesOnlyAfterExactVolumeAbsenceAndPlaintextRetirement(string fault, int deletes)
    {
        using var engineWire = new OpenShellBootstrapArtifactTests.OwnedEngine();
        using var engine = new HttpClient(engineWire) { BaseAddress = new("http://owned-engine.invalid") };
        var artifacts = new OpenShellBootstrapArtifacts(engine, engineWire.Custody, OpenShellPolicyCompilerTests.Admission().PolicyAcknowledgement);
        await using var sql = new SqliteConnection("Data Source=:memory:"); await sql.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(sql).Options;
        await using var db = new OrchestratorDbContext(options); await db.Database.EnsureCreatedAsync();
        db.Agents.Add(new() { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "unused" }); await db.SaveChangesAsync();
        var plan = OpenShellPolicyCompilerTests.Plan(); var now = DateTime.UtcNow;
        var scope = new OpenShellPreparationScope(1, "start", null, plan.Hash, now.AddSeconds(100), plan);
        var reader = new Evidence();
        var store = new OpenShellLaunchStore(db, artifacts: fault == "missing_adapter" ? null : artifacts);
        var launch = await store.ReserveAsync(1, null, now, TimeSpan.FromSeconds(90), preparation: scope);
        await store.ConfirmLegacyStoppedAsync(launch.Id, DateTime.UtcNow, reader);
        await store.RecordPreparationAttemptAsync(launch.Id, true);
        await artifacts.DeliverAsync(plan, launch.Id, launch.IdentityEpoch, default);
        var secret = artifacts.ReadCapability(plan.Hash, launch.Id, launch.IdentityEpoch);
        if (!fault.StartsWith("unbound_")) await store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", DateTime.UtcNow);
        await store.RetireAsync(launch.Id, DateTime.UtcNow);
        var directory = Directory.GetDirectories(engineWire.Custody).Single();
        if (fault == "filesystem_error") Directory.CreateDirectory(Path.Combine(directory, "custody.json.delivered"));
        else File.Copy(Path.Combine(directory, "custody.json"), Path.Combine(directory, "custody.json.delivered"));
        engineWire.CleanupFault = fault; reader.InventoryFault = fault; reader.Fail = fault == "proof_error";
        if (fault == "success") await store.CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader);
        else await Assert.ThrowsAnyAsync<Exception>(() => store.CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader));
        Assert.Equal(deletes, engineWire.VolumeDeletes);
        db.ChangeTracker.Clear(); var row = await db.OpenShellLaunches.SingleAsync();
        if (fault != "success")
        {
            Assert.Equal("cleanup_pending", row.Phase); Assert.NotNull(row.ActiveAgentId); Assert.Single(await db.OpenShellLeases.ToListAsync());
            if (fault is "filesystem_error" or "proof_changes")
            {
                Assert.ThrowsAny<Exception>(() => artifacts.ReadCapability(plan.Hash, launch.Id, launch.IdentityEpoch));
                Assert.False(File.Exists(Path.Combine(directory, "helper-id"))); // Later owned paths still attempted after a deletion failure.
                if (fault == "filesystem_error") Directory.Delete(Path.Combine(directory, "custody.json.delivered")); // Explicit repair of this fixture-owned obstruction.
            }
            else
            {
                Assert.True(File.Exists(Path.Combine(directory, "custody.json")));
                Assert.Equal(secret, artifacts.ReadCapability(plan.Hash, launch.Id, launch.IdentityEpoch));
            }
            engineWire.CleanupFault = ""; reader.Fail = false; reader.InventoryFault = "";
            if (fault == "missing_adapter") store = new OpenShellLaunchStore(db, artifacts: artifacts);
            // A separate explicit reconciliation, never an automatic mutation retry in the adapter.
            await store.CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader);
        }
        db.ChangeTracker.Clear(); row = await db.OpenShellLaunches.SingleAsync();
        Assert.Equal("retired", row.Phase); Assert.Null(row.ActiveAgentId); Assert.Empty(await db.OpenShellLeases.ToListAsync());
        if (fault.StartsWith("unbound_")) Assert.Empty(await db.OpenShellRetiredIds.ToListAsync());
        else Assert.Single(await db.OpenShellRetiredIds.ToListAsync());
        Assert.False(File.Exists(Path.Combine(directory, "custody.json"))); Assert.False(File.Exists(Path.Combine(directory, "custody.json.delivered")));
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(directory, "retired.json")));
        Assert.ThrowsAny<Exception>(() => artifacts.ReadCapability(plan.Hash, launch.Id, launch.IdentityEpoch));
        await artifacts.VerifyRetiredAsync(new(launch.Id, launch.IdentityEpoch, plan.Hash, launch.CapabilitySecretRef!), default);
        var before = engineWire.VolumeDeletes;
        await store.CompleteCleanupAsync(launch.Id, DateTime.UtcNow, reader);
        Assert.Equal(before, engineWire.VolumeDeletes); // Retired custody retains an immutable nonsecret marker.
    }
    private sealed class Evidence : IOpenShellRuntimeEvidenceReader
    {
        public bool Fail;
        public string InventoryFault = "";
        private int _absenceReads;
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget t, CancellationToken ct)
        {
            if (t.RequireAbsence && ++_absenceReads == 2 && InventoryFault == "proof_changes") throw new IOException("owned proof changed");
            return Task.FromResult(new OpenShellRuntimeEvidence(t.LaunchId, t.IdentityEpoch, t.GatewayId, t.RuntimeId,
                !Fail, !Fail, 0, 0, DateTime.UtcNow, t.RequireLaunchInventory && InventoryFault != "unbound_incomplete", InventoryFault == "unbound_owned" ? 1 : 0));
        }
    }
}
