using System.Net;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellPreparationDispatchTests
{
    [Theory]
    [InlineData("unqualified")]
    [InlineData("agent")]
    [InlineData("intent")]
    [InlineData("hash")]
    [InlineData("expired")]
    [InlineData("unbounded")]
    [InlineData("authority_error")]
    public async Task ActualRestStart_RejectsBadSelectionBeforeDbOrDocker(string fault)
    {
        await using var h = ProvisioningHarness.Create();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        var peers = new ServiceCollection().BuildServiceProvider();
        using var preparationPeers = peers;
        var dbCalls = 0;
        var plan = OpenShellPolicyCompilerTests.Plan();
        var selection = new OpenShellPreparationScope(1, "start", null, plan.Hash, DateTime.UtcNow.AddSeconds(100), plan);
        selection = fault switch
        {
            "agent" => selection with { AgentId = 0 },
            "intent" => selection with { Intent = "provision" },
            "hash" => selection with { PlanHash = new string('c', 64) },
            "expired" => selection with { Deadline = DateTime.UtcNow.AddSeconds(-1) },
            "unbounded" => selection with { Deadline = DateTime.UtcNow.AddMinutes(5) },
            _ => selection,
        };
        var authority = new Selection(selection, fault == "authority_error");
        var driver = new OpenShellProvisioningService(() => { dbCalls++; throw new Exception("DB must not be reached"); },
            authority, peers.GetRequiredService<IServiceScopeFactory>());
        var services = h.Services;
        var lifecycle = new AgentRuntimeLifecycle(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<ContainerProvisioningService>(), h.Docker,
            NullLogger<AgentRuntimeLifecycle>.Instance, preparation: driver);
        builder.Services.AddSingleton<IAgentRuntimeLifecycle>(lifecycle);
        await using var app = builder.Build(); app.MapAgentRuntimeEndpoints(); await app.StartAsync();
        using var response = await app.GetTestClient().PostAsync("/api/agents/agent1/start", null);
        Assert.Equal(fault is "unqualified" or "authority_error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, dbCalls); Assert.Equal(0, h.DockerRequestCount); Assert.Equal(1, authority.Calls);
    }

    private sealed class Selection(OpenShellPreparationScope selection, bool unavailable) : IOpenShellPreparationAuthority
    {
        public int Calls;
        public Task<OpenShellPreparationScope?> SelectAsync(string agentName, string intent, CancellationToken ct)
        {
            Calls++; if (unavailable) throw new InvalidOperationException("unavailable");
            return Task.FromResult<OpenShellPreparationScope?>(selection);
        }
    }
}
