using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// A reprovision may ask for a new session (<c>session: fresh</c>): the agent's session file is
/// then removed after the old container is gone and before the new one is created. Without the
/// word, or with <c>auto</c>, the file is left alone. The REST routes and the MCP tool are driven
/// over the real provisioning service, a temp base directory and a fake Docker API.
/// </summary>
public sealed class ReprovisionSessionTests : IAsyncLifetime
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
        builder.Services.AddSingleton(_harness.Service);
        // The lifecycle routes are mapped together and each names its service; only the two
        // reprovision routes are called here, and they take the provisioning service alone.
        builder.Services.AddSingleton<AgentLifecycle>(_ => throw new InvalidOperationException("not used by these tests"));

        _app = builder.Build();
        _app.MapAgentLifecycleEndpoints();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _harness.DisposeAsync();
    }

    private static Agent Claude(string name = "agent1") => new()
    {
        Name = name, DisplayName = name, Role = "tester",
        Model = "sonnet", Provider = "claude",
        ContainerName = $"fleet-{name}", MemoryLimitMb = 512,
    };

    private string WorkspaceDir(string container = "fleet-agent1") => Path.Combine(_harness.BaseDir, "workspaces", container);

    private string SessionFile(string container = "fleet-agent1") => Path.Combine(WorkspaceDir(container), AgentSessionFile.RelativePath);

    /// <summary>An agent with a running container and a session file, as after its first turn.</summary>
    private async Task SeedRunningAgentAsync(string name = "agent1", bool withSessionFile = true)
    {
        await _harness.SeedAsync(db => db.Agents.Add(Claude(name)));
        _harness.ContainerExists = true;
        if (!withSessionFile) return;
        var file = SessionFile($"fleet-{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """{"provider":"claude","sessionId":"s-1"}""");
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private ReprovisionAgentTool Tool() => new(_harness.Service);

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private void AssertNothingWasTouched()
    {
        Assert.Empty(_harness.DockerRequests);
        Assert.True(File.Exists(SessionFile()));
        Assert.False(Directory.Exists(_harness.GeneratedDir("fleet-agent1")));
    }

    // ── The path ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFileRemoved_IsTheSharedRelativePath_UnderTheDirectoryMountedAsTheWorkspace()
    {
        var agent = Claude();

        var path = SessionFileRemoval.HostPath(agent, _harness.BaseDir);

        Assert.Equal(Path.Combine(WorkspaceDir(), AgentSessionFile.RelativePath), path);
        Assert.Equal(Path.Combine(_harness.BaseDir, "workspaces", "fleet-agent1", ".fleet", "session.json"), path);
        // The agent writes the file under its work directory, which is the workspace mount by default.
        Assert.Equal(SessionFileRemoval.WorkspaceMount, agent.WorkDir);
        Assert.Contains($"./workspaces/fleet-agent1:{SessionFileRemoval.WorkspaceMount}", ContainerProvisioningService.BuildBinds(agent, _harness.BaseDir));
    }

    [Theory]
    [InlineData("/workspace/", "")]
    [InlineData("/workspace/repo", "repo")]
    [InlineData("/workspace/a/./b/../c/", "a/c")]
    public void AWorkDirectoryBelowTheMount_PutsTheFileBelowIt(string workDir, string below)
    {
        var agent = Claude();
        agent.WorkDir = workDir;

        var path = SessionFileRemoval.HostPath(agent, _harness.BaseDir);

        Assert.Equal(Path.Combine(WorkspaceDir(), below, AgentSessionFile.RelativePath), path);
    }

    [Theory]
    [InlineData("/srv/work")]
    [InlineData("/workspace/../srv")]
    [InlineData("/workspaces")]
    [InlineData("relative")]
    [InlineData("")]
    public async Task AWorkDirectoryOutsideTheMount_HasNoFileThatOutlivesTheContainer_SoFreshRemovesNothing(string workDir)
    {
        var agent = Claude();
        agent.WorkDir = workDir;
        await _harness.SeedAsync(db => db.Agents.Add(agent));
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFile())!);
        await File.WriteAllTextAsync(SessionFile(), "{}");

        Assert.Null(SessionFileRemoval.HostPath(agent, _harness.BaseDir));
        var result = await _harness.Service.ReprovisionAsync("agent1", session: AgentSessionFile.Fresh);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(SessionFile()));
    }

    // ── REST: one agent ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rest_WithNoBody_KeepsTheFile_AndAnswersAsBefore()
    {
        await SeedRunningAgentAsync();

        var response = await _client.PostAsync("/api/agents/agent1/reprovision", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.StartsWith("reprovision complete — container 'fleet-agent1' created and started", message);
        Assert.True(File.Exists(SessionFile()));
        Assert.Single(_harness.CreateBodies);
    }

    [Theory]
    [InlineData("""{"session":"auto"}""", "")]
    [InlineData("""{"session":null}""", "")]
    [InlineData("""{"session":""}""", "")]
    [InlineData("{}", "")]
    [InlineData("""{"image":"ignored-as-before"}""", "")]
    [InlineData("  ", "")]
    [InlineData("", "?session=auto")]
    [InlineData("", "?image=fleet:agent-pr")]
    public async Task Rest_WithAuto_OrNoChoice_KeepsTheFile(string body, string query)
    {
        await SeedRunningAgentAsync();

        var response = await _client.PostAsync($"/api/agents/agent1/reprovision{query}", Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("new session", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(SessionFile()));
        Assert.Single(_harness.CreateBodies);
    }

    [Theory]
    [InlineData("""{"session":"fresh"}""", "")]
    [InlineData("", "?session=fresh")]
    [InlineData("""{"session":"fresh"}""", "?session=fresh&image=fleet:agent-pr")]
    public async Task Rest_WithFresh_RemovesTheFile_AfterTheOldContainerIsGone_AndBeforeTheNewOneIsCreated(string body, string query)
    {
        await SeedRunningAgentAsync();
        var fileAt = new Dictionary<string, bool>();
        _harness.OnDockerRequest = call =>
        {
            var step = call.StartsWith("DELETE ", StringComparison.Ordinal) ? "remove"
                : call.EndsWith("/containers/create", StringComparison.Ordinal) ? "create"
                : call.EndsWith("/start", StringComparison.Ordinal) ? "start"
                : null;
            if (step is not null) fileAt[step] = File.Exists(SessionFile());
            return Task.CompletedTask;
        };

        var response = await _client.PostAsync($"/api/agents/agent1/reprovision{query}", Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.StartsWith("reprovision complete (new session) — container 'fleet-agent1' created and started", message);
        // Still there while the old container was being removed; gone before the new one existed.
        Assert.Equal(
            new Dictionary<string, bool> { ["remove"] = true, ["create"] = false, ["start"] = false },
            fileAt);
        Assert.False(File.Exists(SessionFile()));
        // Only that file: the rest of the workspace is the agent's.
        Assert.True(Directory.Exists(Path.GetDirectoryName(SessionFile())));
    }

    [Fact]
    public async Task Rest_WithFresh_AndNoFile_Succeeds()
    {
        await SeedRunningAgentAsync(withSessionFile: false);
        Assert.False(Directory.Exists(Path.GetDirectoryName(SessionFile())));

        var response = await _client.PostAsync("/api/agents/agent1/reprovision", Json("""{"session":"fresh"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("reprovision complete (new session)", await response.Content.ReadAsStringAsync());
        Assert.Single(_harness.CreateBodies);
    }

    [Fact]
    public async Task Rest_WithFresh_AndNoContainerLeft_StillRemovesTheFile()
    {
        await SeedRunningAgentAsync();
        _harness.ContainerExists = false;

        var response = await _client.PostAsync("/api/agents/agent1/reprovision", Json("""{"session":"fresh"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(SessionFile()));
        Assert.Single(_harness.CreateBodies);
    }

    [Theory]
    [InlineData("""{"session":"new"}""", "", "session_choice_invalid: session must be 'auto' or 'fresh'")]
    [InlineData("""{"session":"Fresh"}""", "", "session_choice_invalid: session must be 'auto' or 'fresh'")]
    [InlineData("""{"session":" fresh"}""", "", "session_choice_invalid: session must be 'auto' or 'fresh'")]
    [InlineData("", "?session=reset", "session_choice_invalid: session must be 'auto' or 'fresh'")]
    [InlineData("", "?session=fresh&session=auto", "session_choice_invalid: session must be 'auto' or 'fresh'")]
    [InlineData("""{"session":true}""", "", "the request body must be a JSON object, and session a string")]
    [InlineData("""["fresh"]""", "", "the request body must be a JSON object, and session a string")]
    [InlineData("session=fresh", "", "the request body must be a JSON object, and session a string")]
    [InlineData("""{"session":"fresh"}""", "?session=auto", "session is given in the query and in the body with different values")]
    public async Task Rest_WithAnythingElse_Is400_AndNothingIsTouched(string body, string query, string error)
    {
        await SeedRunningAgentAsync();

        var response = await _client.PostAsync($"/api/agents/agent1/reprovision{query}", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await ErrorAsync(response));
        AssertNothingWasTouched();
    }

    [Fact]
    public async Task Rest_WithFresh_WhenTheFileCannotBeRemoved_IsAFault_AndNoNewContainerStarts()
    {
        await SeedRunningAgentAsync();
        MakeTheSessionFileUnremovable();

        var response = await _client.PostAsync("/api/agents/agent1/reprovision", Json("""{"session":"fresh"}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.StartsWith("session_file_not_removed: workspaces/fleet-agent1/.fleet/session.json could not be removed (", detail);
        Assert.Contains("The old container is gone and no new one was started, so 'agent1' is down.", detail);
        Assert.DoesNotContain("new session", detail);
        // The old container was removed first, as for any reprovision; nothing was created after the fault.
        Assert.Contains(_harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.Empty(_harness.CreateBodies);
        Assert.DoesNotContain(_harness.DockerRequests, r => r.EndsWith("/start", StringComparison.Ordinal));
        Assert.True(Directory.Exists(SessionFile()));

        // The way back the message names: without the word the agent comes up and keeps its session.
        var retry = await _client.PostAsync("/api/agents/agent1/reprovision", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(_harness.CreateBodies);
    }

    [Fact]
    public async Task Rest_WithFresh_ThroughALinkedDirectory_DeletesNothingOutsideTheWorkspace()
    {
        await SeedRunningAgentAsync(withSessionFile: false);
        var elsewhere = Path.Combine(_harness.BaseDir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "session.json"), "{}");
        Directory.CreateDirectory(WorkspaceDir());
        Directory.CreateSymbolicLink(Path.Combine(WorkspaceDir(), ".fleet"), elsewhere);

        var response = await _client.PostAsync("/api/agents/agent1/reprovision", Json("""{"session":"fresh"}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("workspaces/fleet-agent1/.fleet is a symbolic link", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(Path.Combine(elsewhere, "session.json")));
        Assert.Empty(_harness.CreateBodies);
    }

    // ── REST: every running agent ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestRunning_WithFresh_RemovesTheFileOfEveryAgentItReprovisions_AndOfNoOther()
    {
        await SeedRunningAgentAsync("agent1");
        await SeedRunningAgentAsync("agent2");
        await SeedRunningAgentAsync("stopped");
        _harness.RunningContainers = ["fleet-agent1", "fleet-agent2"];

        var response = await _client.PostAsync("/api/agents/reprovision-running", Json("""{"session":"fresh"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reprovisioned 2 running agent(s)", body.GetProperty("message").GetString());
        Assert.All(body.GetProperty("results").EnumerateArray(),
            r => Assert.StartsWith("reprovision complete (new session)", r.GetProperty("message").GetString()));
        Assert.False(File.Exists(SessionFile("fleet-agent1")));
        Assert.False(File.Exists(SessionFile("fleet-agent2")));
        Assert.True(File.Exists(SessionFile("fleet-stopped")));
        Assert.Equal(2, _harness.CreateBodies.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"session":"auto"}""")]
    public async Task RestRunning_WithNoChoice_OrAuto_KeepsEveryFile(string? body)
    {
        await SeedRunningAgentAsync("agent1");
        await SeedRunningAgentAsync("agent2");
        _harness.RunningContainers = ["fleet-agent1", "fleet-agent2"];

        var response = await _client.PostAsync("/api/agents/reprovision-running", body is null ? null : Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(SessionFile("fleet-agent1")));
        Assert.True(File.Exists(SessionFile("fleet-agent2")));
        Assert.Equal(2, _harness.CreateBodies.Count);
    }

    [Theory]
    [InlineData("""{"session":"new"}""", "")]
    [InlineData("", "?session=new")]
    [InlineData("not json", "")]
    public async Task RestRunning_WithAnythingElse_Is400_AndReprovisionsNoOne(string body, string query)
    {
        await SeedRunningAgentAsync();
        _harness.RunningContainers = ["fleet-agent1"];

        var response = await _client.PostAsync($"/api/agents/reprovision-running{query}", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(await ErrorAsync(response));
        AssertNothingWasTouched();
    }

    [Fact]
    public async Task RestRunning_WithFresh_WhenOneFileCannotBeRemoved_FailsThatAgent_AndSaysSo()
    {
        await SeedRunningAgentAsync("agent1");
        await SeedRunningAgentAsync("agent2");
        _harness.RunningContainers = ["fleet-agent1", "fleet-agent2"];
        MakeTheSessionFileUnremovable("fleet-agent2");

        var response = await _client.PostAsync("/api/agents/reprovision-running", Json("""{"session":"fresh"}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1/2 agent(s) failed to reprovision", body.GetProperty("message").GetString());
        var results = body.GetProperty("results").EnumerateArray()
            .ToDictionary(r => r.GetProperty("agentName").GetString()!, r => r);
        Assert.True(results["agent1"].GetProperty("success").GetBoolean());
        Assert.False(results["agent2"].GetProperty("success").GetBoolean());
        Assert.StartsWith("session_file_not_removed:", results["agent2"].GetProperty("message").GetString());
        Assert.False(File.Exists(SessionFile("fleet-agent1")));
        Assert.Single(_harness.CreateBodies);
    }

    // ── MCP ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public async Task Mcp_WithNoChoice_OrAuto_KeepsTheFile_AndAnswersAsBefore(string? session)
    {
        await SeedRunningAgentAsync();

        var answer = session is null
            ? await Tool().ReprovisionAsync("agent1")
            : await Tool().ReprovisionAsync("agent1", session);

        Assert.StartsWith("✓ reprovision complete — container 'fleet-agent1' created and started", answer);
        Assert.True(File.Exists(SessionFile()));
    }

    [Fact]
    public async Task Mcp_WithFresh_RemovesTheFile_BetweenTheTwoContainers()
    {
        await SeedRunningAgentAsync();
        var fileAt = new Dictionary<string, bool>();
        _harness.OnDockerRequest = call =>
        {
            if (call.StartsWith("DELETE ", StringComparison.Ordinal)) fileAt["remove"] = File.Exists(SessionFile());
            if (call.EndsWith("/containers/create", StringComparison.Ordinal)) fileAt["create"] = File.Exists(SessionFile());
            return Task.CompletedTask;
        };

        var answer = await Tool().ReprovisionAsync("agent1", "fresh");

        Assert.StartsWith("✓ reprovision complete (new session) — container 'fleet-agent1' created and started", answer);
        Assert.Equal(new Dictionary<string, bool> { ["remove"] = true, ["create"] = false }, fileAt);
    }

    [Fact]
    public async Task Mcp_WithFresh_AndNoFile_Succeeds()
    {
        await SeedRunningAgentAsync(withSessionFile: false);

        var answer = await Tool().ReprovisionAsync("agent1", "fresh");

        Assert.StartsWith("✓ reprovision complete (new session)", answer);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("FRESH")]
    [InlineData("true")]
    public async Task Mcp_WithAnythingElse_IsRefused_AndNothingIsTouched(string session)
    {
        await SeedRunningAgentAsync();

        var answer = await Tool().ReprovisionAsync("agent1", session);

        Assert.Equal("✗ reprovision_agent failed: session_choice_invalid: session must be 'auto' or 'fresh'", answer);
        AssertNothingWasTouched();
    }

    [Fact]
    public async Task Mcp_WithFresh_WhenTheFileCannotBeRemoved_IsAFault_AndNoNewContainerStarts()
    {
        await SeedRunningAgentAsync();
        MakeTheSessionFileUnremovable();

        var answer = await Tool().ReprovisionAsync("agent1", "fresh");

        Assert.StartsWith("✗ reprovision_agent failed: session_file_not_removed: workspaces/fleet-agent1/.fleet/session.json could not be removed (", answer);
        Assert.Contains("'agent1' is down", answer);
        Assert.DoesNotContain("new session", answer);
        Assert.Empty(_harness.CreateBodies);
    }

    [Fact]
    public void Mcp_TheToolDescribesTheParameter()
    {
        var method = typeof(ReprovisionAgentTool).GetMethod(nameof(ReprovisionAgentTool.ReprovisionAsync))!;
        var session = method.GetParameters().Single(p => p.Name == "session");
        var description = ((System.ComponentModel.DescriptionAttribute)session
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Single()).Description;

        Assert.True(session.HasDefaultValue);
        Assert.Null(session.DefaultValue);
        Assert.Contains($"'{AgentSessionFile.Auto}'", description);
        Assert.Contains($"'{AgentSessionFile.Fresh}'", description);
    }

    /// <summary>
    /// Puts a directory with something in it where the file should be: removing it as a file fails
    /// for any user, root included, which a permission bit would not.
    /// </summary>
    private void MakeTheSessionFileUnremovable(string container = "fleet-agent1")
    {
        var path = SessionFile(container);
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "held"), "");
    }
}
