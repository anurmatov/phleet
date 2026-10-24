using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellBootstrapControlTests
{
    [Theory]
    [InlineData("ready", 1)]
    [InlineData("revoked", 0)]
    [InlineData("wrong_epoch", 0)]
    [InlineData("wrong_runtime", 0)]
    [InlineData("lease_expired", 0)]
    [InlineData("session_changed", 0)]
    [InlineData("denied", 1)]
    [InlineData("redirect", 1)]
    public async Task ConcreteProbe_RechecksOwnershipAndSupervisorBeforeSendingOnlyFreshCapability(string fault, int requests)
    {
        using var owned = new OpenShellBootstrapArtifactTests.OwnedEngine();
        using var engine = new HttpClient(owned) { BaseAddress = new("http://owned-engine.invalid") };
        var artifacts = new OpenShellBootstrapArtifacts(engine, owned.Custody);
        var launch = Guid.NewGuid(); var epoch = Guid.NewGuid(); var plan = OpenShellPolicyCompilerTests.Plan();
        await artifacts.DeliverAsync(plan, launch, epoch, default);
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options;
        var scope = new OpenShellPreparationScope(1, "start", null, plan.Hash, DateTime.UtcNow.AddSeconds(55), plan);
        var target = OpenShellPreparationTests.Target();
        var gateway = new Gateway();
        var proof = await gateway.ReadCurrentSupervisorAsync(target, "provider1");
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(new() { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "unused" });
            await db.SaveChangesAsync();
            db.OpenShellLaunches.Add(new() { Id = launch, AgentId = 1, ActiveAgentId = 1, IdentityEpoch = epoch,
                GatewayId = target.GatewayId, SandboxId = target.Id, Phase = "security_ready", Workspace = target.Workspace,
                PreparationJson = JsonSerializer.Serialize(scope), PreparationDeadline = scope.Deadline,
                CreateAttemptedAt = DateTime.UtcNow, SecurityReadyJson = JsonSerializer.Serialize(new[] { proof }), CapabilitySecretRef = OpenShellBootstrapArtifacts.Reference(launch, epoch), CreatedAt = DateTime.UtcNow,
                PolicyHash = plan.PolicyHash, ImageHash = plan.ImageHash, RevokedAt = fault == "revoked" ? DateTime.UtcNow : null });
            db.OpenShellLeases.Add(new() { AgentId = 1, Owner = launch,
                ExpiresAt = DateTime.UtcNow.AddSeconds(fault == "lease_expired" ? -1 : 55) });
            await db.SaveChangesAsync();
        }
        var outside = new Control(fault, artifacts.ReadCapability(plan.Hash, launch, epoch));
        using var probe = new OpenShellBootstrapProbe(() => new(options), gateway, artifacts, outside);
        if (fault == "session_changed") proof = proof with { SessionId = "previous-session" };
        var operation = () => probe.VerifyAsync(launch, fault == "wrong_epoch" ? Guid.NewGuid() : epoch,
            fault == "wrong_runtime" ? target with { Id = "other1" } : target, [proof], default);
        if (fault == "ready") await operation(); else await Assert.ThrowsAnyAsync<Exception>(operation);
        Assert.Equal(requests, outside.Requests);
    }
    private sealed class Control(string fault, string capability) : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/bootstrap/probe", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer " + capability, request.Headers.Authorization!.ToString());
            Assert.Empty(await request.Content!.ReadAsByteArrayAsync(ct));
            Assert.False(request.Headers.Contains("X-Fleet-Assertion"));
            return new(fault == "redirect" ? HttpStatusCode.TemporaryRedirect : fault == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);
        }
    }
    private sealed class Gateway : IOpenShellGatewayClient
    {
        public Task<Uri> ReadBootstrapEndpointAsync(OpenShellSandboxSnapshot target, CancellationToken ct) => Task.FromResult(new Uri("https://owned-control.example.invalid/"));
        public Task<OpenShellSupervisorProof> ReadCurrentSupervisorAsync(OpenShellSandboxSnapshot target, string provider, CancellationToken ct = default) =>
            Task.FromResult(OpenShellSupervisorProof.Verify(target, provider, OpenShellPreparationTests.Ready(), DateTime.UtcNow));
        public Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<OpenShellSandboxSnapshot?> ReadExactAsync(string id, CancellationToken ct) => Task.FromResult<OpenShellSandboxSnapshot?>(OpenShellPreparationTests.Target());
        public Task<OpenShellSandboxSnapshot> StopExactAsync(OpenShellSandboxSnapshot target, Guid requestId, CancellationToken ct) => throw new NotSupportedException();
        public Task<OpenShellDeleteAcknowledgement> DeleteExactAsync(OpenShellSandboxSnapshot target, Guid requestId, bool allowMissing, CancellationToken ct) => throw new NotSupportedException();
    }
}
