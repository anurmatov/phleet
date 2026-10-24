using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Orchestrator.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

public class OpenShellLifecycleTests
{
    [Theory]
    [InlineData("provision")]
    [InlineData("reprovision")]
    [InlineData("deprovision")]
    [InlineData("preview")]
    public async Task ExistingFacades_NeverUseLegacyBackendForMigratedAgent(string operation)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent("active")));
        if (operation == "preview")
        {
            var preview = await h.Service.PreviewAsync("agent1");
            Assert.Contains(preview.Diffs, d => d.Contains("compatibility_blocked"));
        }
        else
        {
            var result = operation switch
            {
                "provision" => await h.Service.ProvisionAsync("agent1"),
                "reprovision" => await h.Service.ReprovisionAsync("agent1"),
                _ => await h.Service.DeprovisionAsync("agent1"),
            };
            Assert.False(result.Success);
            Assert.Contains("compatibility_blocked", result.Message);
        }
        Assert.Equal(0, h.DockerRequestCount);
        Assert.False(Directory.Exists(h.GeneratedDir("agent1-container")));
    }

    [Fact]
    public async Task ResettingMetadata_DoesNotPermitLegacyFallbackAfterRetirement()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db =>
        {
            var agent = Agent("pending");
            db.Agents.Add(agent);
            db.OpenShellLaunches.Add(new()
            {
                Id = Guid.NewGuid(), Agent = agent, IdentityEpoch = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow, RevokedAt = DateTime.UtcNow, Phase = "retired",
            });
        });
        var result = await h.Service.ProvisionAsync("agent1");
        Assert.False(result.Success);
        Assert.Contains("compatibility_blocked", result.Message);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("created")]
    [InlineData("cleanup_pending")]
    [InlineData("unknown")]
    public async Task ActualMcpTools_AndBulk_CannotBypassSharedBoundary(string state)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent(state)));
        var lifecycle = h.Services.GetRequiredService<IAgentRuntimeLifecycle>();
        Assert.Contains("422", await new StartAgentTool(lifecycle).StartAgentAsync("agent1"));
        Assert.Contains("422", await new StopAgentTool(lifecycle).StopAgentAsync("agent1"));
        Assert.Contains("422", await new RestartAgentTool(lifecycle).RestartAgentAsync("agent1"));
        Assert.Contains("422", await new RestartAgentWithVersionTool(lifecycle,
            h.Services.GetRequiredService<IServiceScopeFactory>())
            .RestartAgentWithVersionAsync("agent1", "instruction1", 2, pin: true));
        Assert.Contains("compatibility_blocked", await new ProvisionAgentTool(lifecycle).ProvisionAsync("agent1"));
        Assert.Contains("compatibility_blocked", await new ReprovisionAgentTool(lifecycle).ReprovisionAsync("agent1"));
        Assert.Contains("compatibility_blocked", await new DeprovisionAgentTool(lifecycle).DeprovisionAsync("agent1"));
        Assert.Contains("compatibility_blocked", await new PreviewAgentProvisionTool(lifecycle).PreviewAsync("agent1"));
        Assert.False((await lifecycle.ValidateAsync("agent1")).Success);
        Assert.False(Assert.Single(await h.Service.ReprovisionRunningAsync()).Success);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task ActualRestEndpoints_UseAuthoritativeAdapter(string operation)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent("active")));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(h.Services.GetRequiredService<IAgentRuntimeLifecycle>());
        await using var app = builder.Build();
        app.MapAgentRuntimeEndpoints();
        await app.StartAsync();
        var response = await app.GetTestClient().PostAsync($"/api/agents/agent1/{operation}", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("compatibility_blocked", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Fact]
    public async Task FreshLookup_SeesExternalMigration_AndUnknownNeverFallsBackToRegistry()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent()));
        var lifecycle = h.Services.GetRequiredService<IAgentRuntimeLifecycle>();
        Assert.True((await lifecycle.ValidateAsync("agent1")).Success);
        await h.MutateAsync(async db => (await db.Agents.SingleAsync()).OpenShellMigrationState = "active");
        Assert.Equal(422, (await lifecycle.StartAsync("agent1")).StatusCode);
        Assert.Equal(404, (await lifecycle.StartAsync("unknown")).StatusCode);
        Assert.Equal(0, h.DockerRequestCount);
    }

    [Fact]
    public async Task PendingLegacyProvision_StillUsesActualExistingGenerationAndBackend()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent()));
        var result = await h.Service.ProvisionAsync("agent1");
        Assert.True(result.Success, result.Message);
        Assert.True(h.DockerRequestCount > 0);
        Assert.True(Directory.Exists(h.GeneratedDir("agent1-container")));
    }

    [Theory]
    [InlineData("active", 422)]
    [InlineData("pending", 503)]
    public async Task ActualDeleteEndpoint_BlockedCleanupPreservesAgentAndRecoveryRows(string state, int expectedStatus)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent(state)));
        h.StubInspectSequence(500);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(h.Connection));
        builder.Services.AddSingleton(h.Services.GetRequiredService<IAgentRuntimeLifecycle>());
        builder.Services.AddSingleton(new AgentRegistry(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRegistry>.Instance));
        builder.Services.AddSingleton(NSubstitute.Substitute.For<IAclChangeNotifier>());
        await using var app = builder.Build();
        app.MapAgentDeleteEndpoint();
        await app.StartAsync();
        var response = await app.GetTestClient().DeleteAsync("/api/agents/agent1");
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        using var scope = h.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AnyAsync());
        Assert.Equal(state == "active" ? 0 : 1, h.DockerRequestCount);
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(500, false)]
    [InlineData(200, false)]
    public async Task ActualDeprovision_ConfirmsAbsenceAfterExactIdDeletion(int afterStatus, bool success)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(Agent()));
        h.StubInspectSequence(200, afterStatus);
        var result = await h.Service.DeprovisionAsync("agent1");
        Assert.Equal(success, result.Success);
        var id = new string('a', 64);
        Assert.Contains($"POST /v1.41/containers/{id}/stop", h.DockerRequests);
        Assert.Contains($"DELETE /v1.41/containers/{id}", h.DockerRequests);
        Assert.Equal($"GET /v1.41/containers/{id}/json", h.DockerRequests.Last());
    }

    [Fact]
    public async Task MissingDatabase_DoesNotUseLegacyBackend()
    {
        await using var h = ProvisioningHarness.Create();
        using var empty = new ServiceCollection().BuildServiceProvider();
        var adapter = new AgentRuntimeLifecycle(empty.GetRequiredService<IServiceScopeFactory>(), h.Service, h.Docker,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRuntimeLifecycle>.Instance);
        Assert.Equal(503, (await adapter.StartAsync("agent1")).StatusCode);
        Assert.Equal(0, h.DockerRequestCount);
    }

    internal static Agent Agent(string migration = "pending") => new()
    {
        Name = "agent1", DisplayName = "Agent One", Role = "test", Model = "sonnet",
        ContainerName = "agent1-container", OpenShellMigrationState = migration,
    };
}
