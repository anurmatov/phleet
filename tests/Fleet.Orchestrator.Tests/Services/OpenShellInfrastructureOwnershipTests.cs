using System.Net;
using System.Text.Json;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public class OpenShellInfrastructureOwnershipTests
{
    private static readonly string Id = new('a', 64);

    [Theory]
    [InlineData("start", "agent1", false, false)]
    [InlineData("stop", "agent1-container", false, false)]
    [InlineData("restart", "short-id", true, false)]
    [InlineData("restart", "short-id", false, true)]
    [InlineData("start", "short-id", false, false)]
    [InlineData("stop", "short-id", false, false)]
    [InlineData("restart", "short-id", false, false)]
    public async Task ActualDockerBoundary_RejectsAliasesAndSupervisor_AndPinsAllowedId(
        string operation, string target, bool supervisor, bool agentName)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(OpenShellLifecycleTests.Agent("active")));
        var handler = new InspectHandler(supervisor, agentName);
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://owned.invalid") },
            h.Services.GetRequiredService<IServiceScopeFactory>(), [Id]);
        var result = operation switch
        {
            "start" => await docker.StartContainerAsync(target),
            "stop" => await docker.StopContainerAsync(target),
            _ => await docker.RestartContainerAsync(target),
        };
        var expected = target == "short-id" && !supervisor && !agentName;
        Assert.Equal(expected, result);
        Assert.Equal(expected ? 1 : 0, handler.Mutations.Count);
        if (expected) Assert.Contains($"/containers/{Id}/{operation}", Assert.Single(handler.Mutations));
    }

    [Fact]
    public async Task MissingOwnership_DeniesWithoutDockerMutation()
    {
        var handler = new InspectHandler(false, false);
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://owned.invalid") });
        Assert.False(await docker.StartContainerAsync("anything"));
        Assert.False(await docker.StopContainerAsync(Id));
        Assert.False(await docker.RestartContainerAsync(Id));
        Assert.Empty(handler.Mutations);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task EmptyAllowlist_PreservesInfrastructureRestart_WithAuthoritativeOwnershipCheck()
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(OpenShellLifecycleTests.Agent()));
        var handler = new InspectHandler(false, false);
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://owned.invalid") },
            h.Services.GetRequiredService<IServiceScopeFactory>());
        Assert.True(await docker.RestartContainerAsync("infrastructure1"));
        Assert.Equal($"/v1.41/containers/{Id}/restart", Assert.Single(handler.Mutations));
    }

    [Theory]
    [InlineData("agent1", false)]
    [InlineData("agent1-container", false)]
    [InlineData("short-id", true)]
    public async Task EmptyAllowlist_NeverPermitsAgentOrOpenShellBypass(string target, bool supervisor)
    {
        await using var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(OpenShellLifecycleTests.Agent()));
        var handler = new InspectHandler(supervisor, false);
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://owned.invalid") },
            h.Services.GetRequiredService<IServiceScopeFactory>());
        Assert.False(await docker.RestartContainerAsync(target));
        Assert.Empty(handler.Mutations);
    }

    [Fact]
    public async Task NonemptyAllowlist_IsOnlyAnExplicitAdditionalRestriction()
    {
        await using var h = ProvisioningHarness.Create();
        var handler = new InspectHandler(false, false);
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri("http://owned.invalid") },
            h.Services.GetRequiredService<IServiceScopeFactory>(), [new string('b', 64)]);
        Assert.False(await docker.RestartContainerAsync("infrastructure1"));
        Assert.Empty(handler.Mutations);
    }

    private sealed class InspectHandler(bool supervisor, bool agentName) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public List<string> Mutations { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method != HttpMethod.Get)
            {
                Mutations.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    Id, Name = agentName ? "/agent1-container" : "/infrastructure1",
                    Config = new { Labels = supervisor ? new Dictionary<string, string> { ["openshell.ai/isolation-role"] = "supervisor" } : [] },
                })),
            });
        }
    }
}

public class OpenShellLegacyAbsenceTests
{
    [Theory]
    [InlineData(404, true)]
    [InlineData(500, false)]
    [InlineData(403, false)]
    [InlineData(200, false)]
    public async Task ActualReader_DistinguishesAbsenceFromReadFailure(int status, bool absent)
    {
        using var docker = new DockerService(NullLogger<DockerService>.Instance,
            new HttpClient(new ObservationHandler(status)) { BaseAddress = new Uri("http://owned.invalid") });
        var result = await docker.ReadLegacyContainerAsync("agent1", CancellationToken.None);
        if (absent) Assert.True(result!.Absent);
        else Assert.Null(result);
    }

    private sealed class ObservationHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("invalid") });
    }
}
