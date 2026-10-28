using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

public sealed class StockRuntimeMutationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedProvisioningAndMcp_RefuseBeforeAnyLegacyEffect(bool revoked)
    {
        using var enrollment = new Enrollment();
        await using var harness = ProvisioningHarness.Create(enrollment.Settings);
        await harness.SeedAsync(db =>
        {
            db.Agents.Add(new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
                ContainerName = "foreign-container", IsEnabled = true });
            db.Instructions.Add(new Instruction { Name = "synthetic", CurrentVersion = 1,
                Versions = [new InstructionVersion { VersionNumber = 1, Content = "synthetic one" },
                    new InstructionVersion { VersionNumber = 2, Content = "synthetic two" }] });
        });
        if (revoked) enrollment.Revoke();
        foreach (var result in new[] { await harness.Service.ProvisionAsync("agent1"),
            await harness.Service.DeprovisionAsync("agent1"), await harness.Service.ReprovisionAsync("agent1") })
        {
            Assert.False(result.Success);
            Assert.Equal(StockRuntimeMutationHold.Reason, result.Message);
        }
        var bulk = await harness.Service.ReprovisionRunningAsync();
        var held = Assert.Single(bulk);
        Assert.Equal("agent1", held.AgentName);
        Assert.False(held.Success);
        Assert.Equal(StockRuntimeMutationHold.Reason, held.Message);
        Assert.Contains(StockRuntimeMutationHold.Reason, await new ProvisionAgentTool(harness.Service).ProvisionAsync("agent1"));
        Assert.Contains(StockRuntimeMutationHold.Reason, await new DeprovisionAgentTool(harness.Service).DeprovisionAsync("agent1"));
        Assert.Contains(StockRuntimeMutationHold.Reason, await new ReprovisionAgentTool(harness.Service).ReprovisionAsync("agent1"));
        var restart = new RestartAgentWithVersionTool(harness.Service, harness.Services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(StockRuntimeMutationHold.Reason, await restart.RestartAgentWithVersionAsync("agent1", "synthetic", 2, pin: true));
        using var scope = harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(1, (await db.Instructions.SingleAsync()).CurrentVersion);
        var enrolled = Assert.Single(await db.Agents.ToListAsync());
        Assert.Equal(StockRuntimeMutationHold.Reason,
            Assert.Throws<InvalidOperationException>(() => harness.Service.GetAgentBaseUrl(enrolled)).Message);
        Assert.Equal(0, harness.DockerRequestCount);
        Assert.False(Directory.Exists(Path.Combine(harness.BaseDir, "workspaces")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMcpDockerEntryPoints_RefuseBeforeTargetSelection(bool revoked)
    {
        using var enrollment = new Enrollment();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(enrollment.Settings).Build();
        var hold = new StockRuntimeMutationHold(configuration);
        var registry = new AgentRegistry(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
        registry.PreloadFromDbConfig(new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
            ContainerName = "foreign-container" });
        using var handler = new DockerRecorder();
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        using var provider = new ServiceCollection().BuildServiceProvider();
        if (revoked) enrollment.Revoke();
        Assert.Equal(StockRuntimeMutationHold.Reason, await new StartAgentTool(registry, docker, hold).StartAgentAsync("AGENT1"));
        Assert.Equal(StockRuntimeMutationHold.Reason, await new StopAgentTool(registry, docker, hold).StopAgentAsync("agent1"));
        Assert.Equal(StockRuntimeMutationHold.Reason, await new RestartAgentTool(registry, docker,
            provider.GetRequiredService<IServiceScopeFactory>(), hold).RestartAgentAsync("agent1"));
        Assert.Equal(StockRuntimeMutationHold.Reason, await new AgentLogsTool(registry, docker, hold).GetAgentLogsAsync("agent1"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BulkMixedEnrollment_RetainsHeldRefusalWhenLegacyInventoryFails()
    {
        using var enrollment = new Enrollment();
        await using var harness = ProvisioningHarness.Create(enrollment.Settings);
        await harness.SeedAsync(db => db.Agents.AddRange(
            new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic", ContainerName = "foreign-container", IsEnabled = true },
            new Agent { Name = "agent2", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic", ContainerName = "allowed-container", IsEnabled = true }));
        enrollment.Revoke();
        var results = await harness.Service.ReprovisionRunningAsync();
        Assert.Contains(results, r => r.AgentName == "agent1" && !r.Success && r.Message == StockRuntimeMutationHold.Reason);
        // The owned fixture refuses global inventory; retain the specific stock hold alongside that error.
        Assert.All(harness.DockerRequests, request => Assert.DoesNotContain("foreign-container", request));
        Assert.Contains(results, r => r.AgentName == "(all)" && !r.Success);
        Assert.Equal(1, harness.DockerRequestCount);
    }

    [Theory]
    [InlineData(false, "agent1")]
    [InlineData(true, "agent2")]
    public async Task ActualMcpStart_DefaultOffAndUnrelatedAgentRetainLegacyBehavior(bool enabled, string name)
    {
        using var enrollment = new Enrollment();
        var config = new ConfigurationBuilder().AddInMemoryCollection(enabled ? enrollment.Settings : new()).Build();
        var hold = new StockRuntimeMutationHold(config);
        var registry = new AgentRegistry(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance);
        registry.PreloadFromDbConfig(new Agent { Name = name, DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
            ContainerName = "allowed-container" });
        using var handler = new DockerRecorder();
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") });
        var result = await new StartAgentTool(registry, docker, hold).StartAgentAsync(name);
        Assert.Contains("started successfully", result);
        Assert.Equal("/v1.41/containers/allowed-container/start", Assert.Single(handler.Requests));
    }

    [Fact]
    public void DependencyInjection_UsesSameImmutableHoldForMutationServicesAndTools()
    {
        using var enrollment = new Enrollment();
        var config = new ConfigurationBuilder().AddInMemoryCollection(enrollment.Settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<StockRuntimeMutationHold>();
        services.AddSingleton<JournalTokenService>();
        services.AddSingleton<ContainerProvisioningService>();
        services.AddSingleton<TaskHistoryStore>();
        services.AddSingleton<AgentRegistry>();
        using var handler = new DockerRecorder();
        services.AddSingleton(new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }));
        using var provider = services.BuildServiceProvider();
        var hold = provider.GetRequiredService<StockRuntimeMutationHold>();
        Assert.Same(hold, provider.GetRequiredService<ContainerProvisioningService>().RuntimeHold);
        enrollment.Revoke();
        foreach (var type in new[] { typeof(StartAgentTool), typeof(StopAgentTool), typeof(RestartAgentTool),
            typeof(RestartAgentWithVersionTool), typeof(ProvisionAgentTool), typeof(DeprovisionAgentTool), typeof(ReprovisionAgentTool) })
            Assert.NotNull(ActivatorUtilities.CreateInstance(provider, type));
        Assert.True(hold.Denies("agent1"));
        Assert.Empty(handler.Requests);
    }

    private sealed class DockerRecorder : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class Enrollment : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stock-mutations-" + Guid.NewGuid().ToString("N"));
        private readonly StockEnrollmentAuthority _record;
        public Dictionary<string, string?> Settings { get; }
        private string Authority => Path.Combine(_directory, "authority.json");
        public Enrollment()
        {
            Directory.CreateDirectory(_directory);
            _record = new()
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_directory, "jwks"),
                AssertionPrivateKeyPath = Path.Combine(_directory, "private"), AssertionPublicKeyPath = Path.Combine(_directory, "public"),
                MemoryHost = "memory.example.test", BrokerHost = "broker.example.test"
            };
            File.WriteAllText(Authority, JsonSerializer.Serialize(_record));
            Settings = new() { ["StockOpenShell:AuthorityFile"] = Authority, ["StockOpenShell:BrokerPasswordFile"] = Path.Combine(_directory, "password") };
        }
        public void Revoke() => File.WriteAllText(Authority, JsonSerializer.Serialize(_record with { State = "revoked" }));
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
