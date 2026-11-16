using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Orchestrator.Tests.Support;
using Fleet.Orchestrator.Tools;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Orchestrator.Tests.Application;

/// <summary>
/// A deleted agent's workspace stays on disk. An agent created later with the same container name
/// takes that workspace over, and must not take over the deleted agent's provider session with it:
/// creating an agent removes the session file found there, on both surfaces, before any container
/// of the new agent can read it. An existing agent's <c>provision_agent</c> after
/// <c>deprovision_agent</c> is not a creation and keeps the file.
/// </summary>
/// <remarks>
/// The production create route and the tools, over the real creator and provisioning service, a
/// temp base directory and a fake Docker API. A co-cto is stored first, so the agents created here
/// are neither the first agent nor a co-cto and nothing reaches Temporal or <c>.env</c>.
/// </remarks>
public sealed class NewAgentSessionTests : IAsyncLifetime
{
    private ProvisioningHarness _harness = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _harness = ProvisioningHarness.Create();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:BaseDir"] = _harness.BaseDir,
            ["Provisioning:EnvFilePath"] = _harness.EnvFile,
        });
        var services = builder.Services;
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_harness.Connection));
        services.AddSingleton(_harness.Service);
        services.AddSingleton<ITelegramSetup>(new Writers.TelegramSetup());
        services.AddSingleton<IConfigWriter>(new Writers.RecordingConfigWriter());
        services.AddSingleton(Substitute.For<IAclChangeNotifier>());
        services.AddSingleton(new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance));
        services.AddSingleton(new TemporalClientRegistry(
            Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance));
        services.AddAgentWrites();

        _app = builder.Build();
        _app.MapAgentCreateEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();

        await _harness.SeedAsync(db => db.Agents.Add(Stored("cto", role: "co-cto")));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _harness.DisposeAsync();
    }

    private static Agent Stored(string name, string role = "developer") => new()
    {
        Name = name, DisplayName = name, Role = role,
        Model = "sonnet", Provider = "claude",
        ContainerName = $"fleet-{name}", MemoryLimitMb = 512,
    };

    private string SessionFile(string container) =>
        Path.Combine(_harness.BaseDir, "workspaces", container, AgentSessionFile.RelativePath);

    /// <summary>What a deleted agent with this container name left behind.</summary>
    private void LeaveSessionFile(string container, string content = """{"provider":"claude","sessionId":"the-deleted-agents"}""")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFile(container))!);
        File.WriteAllText(SessionFile(container), content);
    }

    private void MakeTheSessionFileUnremovable(string container)
    {
        Directory.CreateDirectory(SessionFile(container));
        File.WriteAllText(Path.Combine(SessionFile(container), "held"), "");
    }

    private T Tool<T>() where T : class => ActivatorUtilities.CreateInstance<T>(_app.Services);

    private Task<HttpResponseMessage> PostAsync(object body) => _client.PostAsJsonAsync("/api/agents", body);

    private async Task<bool> IsStoredAsync(string name)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AnyAsync(a => a.Name == name);
    }

    /// <summary>Records whether the file exists each time Docker is asked to create a container.</summary>
    private List<bool> FileAtEachContainerCreate(string container)
    {
        var seen = new List<bool>();
        _harness.OnDockerRequest = call =>
        {
            if (call.EndsWith("/containers/create", StringComparison.Ordinal)) seen.Add(File.Exists(SessionFile(container)));
            return Task.CompletedTask;
        };
        return seen;
    }

    // ── REST ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rest_ACreatedAgent_StartsWithoutTheSessionFileItsWorkspaceHeld()
    {
        LeaveSessionFile("fleet-dev1");
        var fileAtCreate = FileAtEachContainerCreate("fleet-dev1");

        var response = await PostAsync(new { name = "dev1", role = "developer", model = "sonnet" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Agent 'dev1' created and provisioned",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal([false], fileAtCreate);
        Assert.False(File.Exists(SessionFile("fleet-dev1")));
        // Only that file: the rest of the workspace is kept, as it always was.
        Assert.True(Directory.Exists(Path.GetDirectoryName(SessionFile("fleet-dev1"))));
    }

    [Fact]
    public async Task Rest_ACreatedAgent_NotProvisionedYet_HasItRemovedAtCreation()
    {
        LeaveSessionFile("fleet-dev1");

        var response = await PostAsync(new { name = "dev1", role = "developer", model = "sonnet", provision = false });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(File.Exists(SessionFile("fleet-dev1")));
        Assert.Empty(_harness.CreateBodies);
    }

    [Theory]
    [InlineData("/workspace/repo", "repo")]
    [InlineData("/root/.claude", "claude")]
    [InlineData("/root/.codex", "codex")]
    [InlineData("/root/.codex/repo", "codex/repo")]
    public async Task Rest_ACreatedAgent_WithItsOwnContainerNameAndWorkDirectory_HasThatFileRemoved(string workDir, string hostSubdir)
    {
        var file = Path.Combine(_harness.BaseDir, "workspaces", "custom-box", hostSubdir, AgentSessionFile.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{}");

        var response = await PostAsync(new
        {
            name = "dev1", role = "developer", model = "sonnet", provision = false,
            containerName = "custom-box", workDir,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Rest_WhenTheFileCannotBeRemoved_TheAgentIsNotCreated()
    {
        MakeTheSessionFileUnremovable("fleet-dev1");

        var response = await PostAsync(new { name = "dev1", role = "developer", model = "sonnet" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.StartsWith("session_file_not_removed: workspaces/fleet-dev1/.fleet/session.json could not be removed (", detail);
        Assert.Contains("Agent 'dev1' was not created", detail);
        Assert.False(await IsStoredAsync("dev1"));
        Assert.Null(_app.Services.GetRequiredService<AgentRegistry>().Get("dev1"));
        Assert.Empty(_harness.DockerRequests);
    }

    [Fact]
    public async Task Rest_ACreateRefusedForATakenName_RemovesNothing()
    {
        // The name is taken by a stored agent, whose workspace and session these are.
        LeaveSessionFile("fleet-cto");

        var response = await PostAsync(new { name = "cto", role = "developer", model = "sonnet" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(SessionFile("fleet-cto")));
    }

    [Fact]
    public async Task Rest_ACreateRefusedForItsOwnFault_RemovesNothing()
    {
        // The removal is the last thing before the row is saved: a create refused for any other
        // reason has changed nothing on disk either.
        LeaveSessionFile("fleet-dev1");

        var response = await PostAsync(new { name = "dev1", role = "developer", model = "sonnet", warmupTimeoutSeconds = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await IsStoredAsync("dev1"));
        Assert.True(File.Exists(SessionFile("fleet-dev1")));
    }

    // ── MCP ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mcp_ACreatedAgent_HasItRemovedAtCreation_SoItsFirstProvisionStartsWithoutIt()
    {
        LeaveSessionFile("fleet-dev1");
        var fileAtCreate = FileAtEachContainerCreate("fleet-dev1");

        var created = await Tool<CreateAgentTool>().CreateAgentAsync("dev1", "developer", "sonnet");

        Assert.StartsWith("Agent 'dev1' created in DB", created);
        Assert.False(File.Exists(SessionFile("fleet-dev1")));
        Assert.Empty(_harness.DockerRequests);

        var provisioned = await Tool<ProvisionAgentTool>().ProvisionAsync("dev1");

        Assert.StartsWith("✓ container 'fleet-dev1' created and started", provisioned);
        Assert.Equal([false], fileAtCreate);
    }

    [Fact]
    public async Task Mcp_WhenTheFileCannotBeRemoved_TheAgentIsNotCreated()
    {
        MakeTheSessionFileUnremovable("fleet-dev1");

        var answer = await Tool<CreateAgentTool>().CreateAgentAsync("dev1", "developer", "sonnet");

        Assert.StartsWith("session_file_not_removed: workspaces/fleet-dev1/.fleet/session.json could not be removed (", answer);
        Assert.Contains("Agent 'dev1' was not created", answer);
        Assert.DoesNotContain("created in DB", answer);
        Assert.False(await IsStoredAsync("dev1"));
        Assert.Equal("✗ provision_agent failed: agent not found in DB", await Tool<ProvisionAgentTool>().ProvisionAsync("dev1"));
    }

    [Fact]
    public async Task Mcp_ACreatedAgent_NamingAContainerAStoredAgentUses_LeavesThatAgentsSessionAlone()
    {
        LeaveSessionFile("fleet-cto", "the stored agent's own session");

        var created = await Tool<CreateAgentTool>().CreateAgentAsync("dev1", "developer", "sonnet", container_name: "fleet-cto");

        Assert.StartsWith("Agent 'dev1' created in DB", created);
        Assert.Equal("the stored agent's own session", await File.ReadAllTextAsync(SessionFile("fleet-cto")));
    }

    // ── Not a creation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExistingAgent_DeprovisionedAndProvisionedAgain_KeepsItsSession()
    {
        await _harness.SeedAsync(db => db.Agents.Add(Stored("dev1")));
        _harness.ContainerExists = true;
        LeaveSessionFile("fleet-dev1", "its own session");
        var fileAtCreate = FileAtEachContainerCreate("fleet-dev1");

        var deprovisioned = await Tool<DeprovisionAgentTool>().DeprovisionAsync("dev1");
        var provisioned = await Tool<ProvisionAgentTool>().ProvisionAsync("dev1");

        Assert.StartsWith("✓", deprovisioned);
        Assert.StartsWith("✓ container 'fleet-dev1' created and started", provisioned);
        Assert.Equal([true], fileAtCreate);
        Assert.Equal("its own session", await File.ReadAllTextAsync(SessionFile("fleet-dev1")));
    }
}
