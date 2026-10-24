using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellExpectedProviderStoreTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("environment")]
    [InlineData("configuration")]
    [InlineData("policy")]
    public async Task RelationalSecurityReady_RejectsProofNotBoundToImmutableApprovedTuple(string fault)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Agents.Add(new() { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "unused" });
        await db.SaveChangesAsync();
        var plan = OpenShellPolicyCompilerTests.Plan();
        var scope = new OpenShellPreparationScope(1, "start", null, plan.Hash, DateTime.UtcNow.AddSeconds(100), plan);
        var store = new OpenShellLaunchStore(db);
        var launch = await store.ReserveAsync(1, null, DateTime.UtcNow, TimeSpan.FromSeconds(90), preparation: scope);
        await store.ConfirmLegacyStoppedAsync(launch.Id, DateTime.UtcNow, new AbsentLegacy());
        await store.RecordPreparationAttemptAsync(launch.Id, create: true);
        await store.BindCreatedSandboxAsync(launch.Id, "gateway1", "sandbox1", DateTime.UtcNow);
        var target = OpenShellPreparationTests.Target();
        var proof = OpenShellSupervisorProof.Verify(target, "provider1", OpenShellPreparationTests.Ready(), DateTime.UtcNow);
        var proofList = new List<OpenShellSupervisorProof> { proof };
        switch (fault)
        {
            case "missing": proofList.Clear(); break;
            case "duplicate": proofList.Add(proof); break;
            case "foreign": proofList[0] = proof with { Provider = "foreign1" }; break;
            case "id": proof.Desired.ProviderId = "other1"; break;
            case "version": proof.Desired.ProviderResourceVersion++; break;
            case "environment": proof.Desired.ProviderEnvRevision++; break;
            case "configuration": proof.Desired.ConfigRevision++; break;
            case "policy": proof.Desired.PolicyHash = new string('d', 64); break;
        }
        var original = launch.PreparationJson;
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() => store.RecordSecurityReadyAsync(launch.Id, target, proofList));
        await db.Entry(launch).ReloadAsync();
        Assert.Equal("created", launch.Phase); Assert.Null(launch.SecurityReadyJson); Assert.Equal(original, launch.PreparationJson);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<OpenShellPreparationScope>(original!)!;
        Assert.Equal(OpenShellPolicyCompilerTests.Expected(), persisted.Plan.ReadExpectedProviders());
        Assert.Equal(plan.Hash, persisted.Plan.Hash);
    }

    private sealed class AbsentLegacy : IOpenShellRuntimeEvidenceReader
    {
        public Task<OpenShellRuntimeEvidence> ReadAsync(OpenShellRuntimeTarget t, CancellationToken ct) =>
            Task.FromResult(new OpenShellRuntimeEvidence(t.LaunchId, t.IdentityEpoch, t.GatewayId, t.RuntimeId, true, true, 0, 0, DateTime.UtcNow));
    }
}
