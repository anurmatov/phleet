using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Fleet.Orchestrator.Tests.Support;
using Fleet.Orchestrator.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Application;

/// <summary>
/// The same write, sent once through the REST route and once through the MCP tool, stores the same
/// thing and is refused for the same reasons. Both adapters run over real HTTP and a real container:
/// the routes are the production ones and each tool is built by the container, as MCP builds it.
/// </summary>
/// <remarks>
/// One class per write path would be seven hosts. This is one host with every route the writers sit
/// behind, an in-memory SQLite database, a Docker API that accepts stop, start and restart and
/// knows no container, and no broker: what would have been broadcast is recorded.
/// </remarks>
public sealed class WriteParityTests : IAsyncLifetime
{
    private const long Owner = 900;

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly MutableTelegramSetup _telegram = new() { IsConfigured = true, OwnerUserId = Owner };
    private readonly Writers.RecordingConfigWriter _env = new();
    private readonly List<string> _allowlistBroadcasts = [];
    private readonly List<string> _dockerCalls = [];
    private int _aclBroadcasts;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.env"),
            ["Provisioning:BaseDir"] = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}"),
        });
        IConfiguration config = builder.Configuration;
        var services = builder.Services;
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ITelegramSetup>(_telegram);
        services.AddSingleton<IConfigWriter>(_env);
        services.AddSingleton<IAclChangeNotifier>(new CountingAclNotifier(() => _aclBroadcasts++));
        services.AddSingleton(new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance)
        {
            Send = (routingKey, body, _) =>
            {
                lock (_allowlistBroadcasts) _allowlistBroadcasts.Add($"{routingKey} {Encoding.UTF8.GetString(body)}");
                return Task.CompletedTask;
            },
        });
        services.AddSingleton(sp => new ConfigService(
            config, Options.Create(new RabbitMqOptions { Host = "", Exchange = "" }),
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConfigService>.Instance)
        {
            Publish = (_, _) => { _aclBroadcasts++; return Task.CompletedTask; },
        });
        services.AddSingleton(new DockerService(
            NullLogger<DockerService>.Instance,
            new HttpClient(new AcceptingDockerApi(_dockerCalls)) { BaseAddress = new Uri("http://localhost") }));
        services.AddSingleton(new JournalTokenService(config));
        services.AddSingleton<ContainerProvisioningService>();
        services.AddSingleton(new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance));
        services.AddSingleton(new TemporalClientRegistry(
            Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance));
        services.AddSingleton(new PromptSizePolicy(10_000, 10_000, NullLogger.Instance));
        services.AddAgentWrites();

        _app = builder.Build();
        _app.MapAgentConfigEndpoints();
        _app.MapAgentCreateEndpoints();
        _app.MapAgentLifecycleEndpoints();
        _app.MapAgentAccessEndpoints();
        _app.MapInstructionEndpoints();
        _app.MapWorkflowDefinitionEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ── The container builds every writer and every tool that adapts one ─────────────────────────

    [Fact]
    public void EveryWriter_AndEveryToolOverOne_IsBuiltByTheContainer()
    {
        Type[] writers =
        [
            typeof(AgentConfigWriter), typeof(AgentCreator), typeof(AgentLifecycle), typeof(AgentAllowlist),
            typeof(AgentProjectAccessWriter), typeof(InstructionWriter), typeof(WorkflowDefinitionWriter),
        ];
        foreach (var writer in writers)
            Assert.NotNull(_app.Services.GetRequiredService(writer));

        // Every MCP tool whose constructor takes a writer: found by reflection, so a new adapter
        // is covered without being listed.
        var tools = typeof(UpdateAgentConfigTool).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Length > 0)
            .Where(t => t.GetConstructors().Single().GetParameters().Any(p => writers.Contains(p.ParameterType)))
            .ToList();
        Assert.True(tools.Count >= 14, $"expected at least 14 tools over a writer, found {tools.Count}");
        foreach (var tool in tools)
            Assert.NotNull(ActivatorUtilities.CreateInstance(_app.Services, tool));
    }

    /// <summary>
    /// No tool that has a writer for its aggregate reaches the database to change it: the tools the
    /// writers replaced no longer take a scope factory at all.
    /// </summary>
    [Theory]
    [InlineData(typeof(UpdateAgentConfigTool))]
    [InlineData(typeof(CreateAgentTool))]
    [InlineData(typeof(ManageAgentEnvRefsTool))]
    [InlineData(typeof(ManageAgentTelegramUsersTool))]
    [InlineData(typeof(ManageAgentTelegramGroupsTool))]
    [InlineData(typeof(ManageAgentProjectAccessTool))]
    [InlineData(typeof(StopAgentTool))]
    [InlineData(typeof(StartAgentTool))]
    [InlineData(typeof(RestartAgentTool))]
    [InlineData(typeof(RestartAgentWithVersionTool))]
    [InlineData(typeof(CreateInstructionTool))]
    [InlineData(typeof(UpdateInstructionTool))]
    [InlineData(typeof(RollbackInstructionTool))]
    public void AnAdapterTool_TakesItsWriter_AndNothingThatWrites(Type tool)
    {
        var parameters = tool.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Single(parameters);
        Assert.Equal("Fleet.Orchestrator.Application", parameters[0].Namespace);
    }

    // ── Agent config ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigUpdate_TheSamePatch_StoresTheSameAgent()
    {
        await SeedAgentAsync("rest-a", a => a.Tools = [Tool("Read", true), Tool("Bash", false)]);
        await SeedAgentAsync("mcp-a", a => a.Tools = [Tool("Read", true), Tool("Bash", false)]);

        var rest = await _client.PutAsJsonAsync("/api/agents/rest-a/config", new
        {
            model = "claude-opus-5-5", effort = "high", shortName = "  Spaced  ", permissionMode = "default",
            formattingMode = 2, maxTurns = 80, tools = new[] { "Read", "Bash", "Grep" }, projects = new[] { "alpha", "Beta" },
        });
        var mcp = await McpTool<UpdateAgentConfigTool>().UpdateAgentConfigAsync("mcp-a",
            model: "claude-opus-5-5", effort: "high", short_name: "  Spaced  ", permission_mode: "default",
            formatting_mode: 2, max_turns: 80, tools: "Read, Bash, Grep", projects: "alpha, Beta");

        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        Assert.StartsWith("Agent 'mcp-a' updated:", mcp);

        var viaRest = await StoredAgentAsync("rest-a");
        var viaMcp = await StoredAgentAsync("mcp-a");
        Assert.Equal(viaRest, viaMcp);
        // And it is the patch: the blank-padded short name is trimmed, a tool that was switched off
        // stays off, and the new tool is on.
        Assert.Contains("\"ShortName\":\"Spaced\"", viaRest);
        Assert.Contains("Bash=False", viaRest);
        Assert.Contains("Grep=True", viaRest);

        // Project assignment is the memory grant, whichever surface assigned it.
        Assert.Equal(await AccessRowsAsync("rest-a"), await AccessRowsAsync("mcp-a"));
        Assert.Equal(["alpha:assignment", "beta:assignment"], await AccessRowsAsync("mcp-a"));
    }

    public static TheoryData<string, object, Func<UpdateAgentConfigTool, string, Task<string>>> RefusedConfigUpdates() => new()
    {
        { "bypassPermissions", new { permissionMode = "bypassPermissions" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, permission_mode: "bypassPermissions") },
        { "effort outside the provider's list", new { effort = "ultra" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, effort: "ultra") },
        { "formatting mode past the last one", new { formattingMode = 3 }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, formatting_mode: 3) },
        { "unknown codex sandbox mode", new { codexSandboxMode = "wide-open" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, codex_sandbox_mode: "wide-open") },
        { "warmup timeout out of range", new { warmupTimeoutSeconds = 5 }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, warmup_timeout_seconds: 5) },
        { "unknown provider", new { provider = "pi" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, provider: "pi") },
        { "effort on gemini", new { provider = "gemini", effort = "high" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, provider: "gemini", effort: "high") },
        { "local server URL with a path", new { localBaseUrl = "http://inference.test:11434/v1" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, local_base_url: "http://inference.test:11434/v1") },
        { "harness setting off the allowlist", new { harnessExtra = "--mcp-config /tmp/other.json" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, harness_extra: "--mcp-config /tmp/other.json") },
        { "output style that does not exist", new { outputStyle = "nope" }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, output_style: "nope") },
        { "context window out of range", new { contextWindow = 12 }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, context_window: 12) },
        { "request message past 500 characters", new { requestReceivedMessage = new string('x', 501) }, (tool, agent) => tool.UpdateAgentConfigAsync(agent, request_received_message: new string('x', 501)) },
    };

    [Theory]
    [MemberData(nameof(RefusedConfigUpdates))]
    public async Task ConfigUpdate_WhatOneSurfaceRefuses_TheOtherRefuses_AndNeitherSaves(
        string rule, object restBody, Func<UpdateAgentConfigTool, string, Task<string>> viaMcp)
    {
        await SeedAgentAsync("rest-a");
        await SeedAgentAsync("mcp-a");
        var before = await StoredAgentAsync("rest-a");
        Assert.Equal(before, await StoredAgentAsync("mcp-a"));

        var rest = await _client.PutAsJsonAsync("/api/agents/rest-a/config", restBody);
        var mcp = await viaMcp(McpTool<UpdateAgentConfigTool>(), "mcp-a");

        Assert.True(rest.StatusCode == HttpStatusCode.BadRequest, $"REST accepted: {rule}");
        Assert.False(mcp.StartsWith("Agent 'mcp-a' updated", StringComparison.Ordinal), $"MCP accepted: {rule} ({mcp})");
        Assert.Equal(before, await StoredAgentAsync("rest-a"));
        Assert.Equal(before, await StoredAgentAsync("mcp-a"));
    }

    [Fact]
    public async Task ConfigUpdate_TheSameSentence_ForTheSameRefusal()
    {
        await SeedAgentAsync("rest-a");
        await SeedAgentAsync("mcp-a");

        var rest = await _client.PutAsJsonAsync("/api/agents/rest-a/config", new { permissionMode = "bypassPermissions" });
        var mcp = await McpTool<UpdateAgentConfigTool>().UpdateAgentConfigAsync("mcp-a", permission_mode: "bypassPermissions");

        Assert.Equal((await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), mcp);
        Assert.StartsWith("PermissionMode 'bypassPermissions' is not supported", mcp);
    }

    [Fact]
    public async Task ConfigUpdate_ReSendingAStoredEffort_IsNotARefusal_OnEitherSurface()
    {
        // A value stored before the rule existed must not block every later save of the agent.
        await SeedAgentAsync("rest-a", a => a.Effort = "ultra");
        await SeedAgentAsync("mcp-a", a => a.Effort = "ultra");

        var rest = await _client.PutAsJsonAsync("/api/agents/rest-a/config", new { effort = "ultra", maxTurns = 70 });
        var mcp = await McpTool<UpdateAgentConfigTool>().UpdateAgentConfigAsync("mcp-a", effort: "ultra", max_turns: 70);

        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        Assert.StartsWith("Agent 'mcp-a' updated:", mcp);
        Assert.Equal(await StoredAgentAsync("rest-a"), await StoredAgentAsync("mcp-a"));
    }

    /// <summary>
    /// The two rules that differ by caller are a policy on the call, not a second implementation:
    /// the same writer, asked by the operator and by an agent.
    /// </summary>
    [Fact]
    public async Task OperatorOnlyChanges_AreRefusedForAnAgent_ByTheSameWriter()
    {
        await SeedAgentAsync("agent1");
        await DbAsync(async db =>
        {
            db.SecretBindings.Add(new SecretBinding { EnvKeyName = "EXAMPLE_API_KEY", Hosts = "api.example.test" });
            await db.SaveChangesAsync();
            return 0;
        });
        var writer = _app.Services.GetRequiredService<AgentConfigWriter>();

        var crossChatByAgent = await writer.UpdateAsync("agent1", new AgentConfigPatch { JournalCrossChatEnabled = true }, WriteCaller.Mcp);
        var boundKeyByAgent = await writer.UpdateAsync("agent1", new AgentConfigPatch { EnvRefs = ["EXAMPLE_API_KEY"] }, WriteCaller.Mcp);
        var boundKeyByTool = await McpTool<ManageAgentEnvRefsTool>().ManageAgentEnvRefsAsync("agent1", "add", "EXAMPLE_API_KEY");

        Assert.Equal("operator_only", crossChatByAgent.Fault?.Code);
        Assert.Equal("bound_secret_operator_only", boundKeyByAgent.Fault?.Code);
        Assert.Contains("only the operator can attach", boundKeyByTool);
        Assert.False(await DbAsync(db => db.Agents.Select(a => a.JournalCrossChatEnabled).SingleAsync()));
        Assert.Empty(await DbAsync(db => db.AgentEnvRefs.ToListAsync()));

        var byOperator = await _client.PutAsJsonAsync("/api/agents/agent1/config",
            new { journalCrossChatEnabled = true, envRefs = new[] { "EXAMPLE_API_KEY" } });

        Assert.Equal(HttpStatusCode.OK, byOperator.StatusCode);
        Assert.True(await DbAsync(db => db.Agents.Select(a => a.JournalCrossChatEnabled).SingleAsync()));
        Assert.Equal(["EXAMPLE_API_KEY"], await DbAsync(db => db.AgentEnvRefs.Select(r => r.EnvKeyName).ToListAsync()));
    }

    // ── Agent create ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithoutTelegram_IsRefusedOnBothSurfaces_AndNothingIsWritten()
    {
        _telegram.IsConfigured = false;

        var rest = await _client.PostAsJsonAsync("/api/agents", new { name = "cto-rest", role = "co-cto", model = "sonnet", provision = false });
        var mcp = await McpTool<CreateAgentTool>().CreateAgentAsync("cto-mcp", "co-cto", "sonnet");

        Assert.Equal(HttpStatusCode.Conflict, rest.StatusCode);
        Assert.Equal("telegram_not_configured", (await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.StartsWith("Cannot provision agent: Telegram bot token is not configured.", mcp);
        Assert.Empty(await DbAsync(db => db.Agents.ToListAsync()));
    }

    [Fact]
    public async Task Create_TheFirstAgent_MustBeACoCto_OnBothSurfaces()
    {
        var rest = await _client.PostAsJsonAsync("/api/agents", new { name = "dev-rest", role = "developer", model = "sonnet", provision = false });
        var mcp = await McpTool<CreateAgentTool>().CreateAgentAsync("dev-mcp", "developer", "sonnet");

        Assert.Equal(HttpStatusCode.Conflict, rest.StatusCode);
        Assert.Equal("cto_required_first", (await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("Your first agent must be a co-cto.", mcp);
        Assert.Empty(await DbAsync(db => db.Agents.ToListAsync()));
    }

    [Fact]
    public async Task Create_TheSameRequest_StoresTheSameAgent_WithItsNetworkTokenAndOwner()
    {
        Assert.Contains("created in DB", await McpTool<CreateAgentTool>().CreateAgentAsync("cto", "co-cto", "sonnet"));

        var rest = await _client.PostAsJsonAsync("/api/agents", new { name = "Dev-Rest ", role = "developer", model = "sonnet", provision = false });
        var mcp = await McpTool<CreateAgentTool>().CreateAgentAsync("Dev-Mcp ", "developer", "sonnet");

        Assert.Equal(HttpStatusCode.Created, rest.StatusCode);
        Assert.StartsWith("Agent 'dev-mcp' created in DB", mcp);

        var viaRest = await StoredAgentAsync("dev-rest");
        var viaMcp = await StoredAgentAsync("dev-mcp");
        Assert.Equal(viaRest, viaMcp);
        // Neither surface leaves an agent that cannot reach the fleet, cannot start its transport,
        // or that its owner cannot write to.
        Assert.Contains("\"Networks\":[\"fleet-net\"]", viaRest);
        Assert.Contains($"\"EnvRefs\":[\"{AgentCreator.DefaultBotTokenRef}\"]", viaRest);
        Assert.Contains($"\"TelegramUsers\":[{Owner}]", viaRest);

        // Three agents, three stored ports, none shared.
        var ports = await DbAsync(db => db.Agents.Select(a => a.HostPort).ToListAsync());
        Assert.All(ports, port => Assert.NotNull(port));
        Assert.Equal(3, ports.Distinct().Count());

        // Both are listed at once, before any container or heartbeat.
        var registry = _app.Services.GetRequiredService<AgentRegistry>();
        Assert.NotNull(registry.Get("dev-rest"));
        Assert.NotNull(registry.Get("dev-mcp"));
    }

    [Fact]
    public async Task Create_TheFirstCoCto_BecomesTheFleetCto_WhicheverSurfaceCreatedIt()
    {
        await McpTool<CreateAgentTool>().CreateAgentAsync("cto", "co-cto", "sonnet");
        Assert.Equal("cto", _env.Written["FLEET_CTO_AGENT"]);

        // A second co-cto does not take the role over.
        var second = await _client.PostAsJsonAsync("/api/agents", new { name = "cto2", role = "co-cto", model = "sonnet", provision = false });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("cto", _env.Written["FLEET_CTO_AGENT"]);
    }

    [Fact]
    public async Task Create_ANameThatExists_IsRefusedOnBothSurfaces()
    {
        await SeedAgentAsync("agent1");

        var rest = await _client.PostAsJsonAsync("/api/agents", new { name = "agent1", role = "developer", model = "sonnet", provision = false });
        var mcp = await McpTool<CreateAgentTool>().CreateAgentAsync("agent1", "developer", "sonnet");

        Assert.Equal(HttpStatusCode.Conflict, rest.StatusCode);
        Assert.Equal("Agent 'agent1' already exists in DB.", mcp);
        Assert.Single(await DbAsync(db => db.Agents.ToListAsync()));
    }

    [Fact]
    public async Task Create_NeverTakesAPortAnotherAgentAnswersOn()
    {
        // Agents without a stored port answer on 8080 + id; one with a stored port uses it.
        await SeedAgentAsync("no-port-1");
        await SeedAgentAsync("no-port-2");
        await SeedAgentAsync("stored-port", a => a.HostPort = 8082);
        var inUse = await DbAsync(db => db.Agents.Select(a => new { a.Id, a.HostPort }).ToListAsync());
        var effective = inUse.Select(a => a.HostPort ?? 8080 + a.Id).ToList();

        await McpTool<CreateAgentTool>().CreateAgentAsync("new-mcp", "developer", "sonnet");
        await _client.PostAsJsonAsync("/api/agents", new { name = "new-rest", role = "developer", model = "sonnet", provision = false });

        var mcpPort = await DbAsync(db => db.Agents.Where(a => a.Name == "new-mcp").Select(a => a.HostPort).SingleAsync());
        var restPort = await DbAsync(db => db.Agents.Where(a => a.Name == "new-rest").Select(a => a.HostPort).SingleAsync());
        Assert.DoesNotContain(mcpPort!.Value, effective);
        Assert.DoesNotContain(restPort!.Value, effective);
        Assert.NotEqual(mcpPort, restPort);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_IsARequestedStop_WhicheverSurfaceAsked()
    {
        await SeedAgentAsync("rest-a", register: true);
        await SeedAgentAsync("mcp-a", register: true);
        var stops = _app.Services.GetRequiredService<OperatorStops>();

        var rest = await _client.PostAsync("/api/agents/rest-a/stop", null);
        var mcp = await McpTool<StopAgentTool>().StopAgentAsync("mcp-a");

        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        Assert.Equal("Container 'fleet-mcp-a' stopped successfully.", mcp);
        // Neither is "an agent went down" to the push notifier.
        Assert.True(stops.Contains("rest-a"));
        Assert.True(stops.Contains("mcp-a"));
        Assert.Contains(_dockerCalls, call => call.EndsWith("/containers/fleet-rest-a/stop?t=10", StringComparison.Ordinal));
        Assert.Contains(_dockerCalls, call => call.EndsWith("/containers/fleet-mcp-a/stop?t=10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopStartRestart_OfAnUnknownAgent_AreRefusedOnBothSurfaces_WithoutADockerCall()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/agents/ghost/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/agents/ghost/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/agents/ghost/restart", null)).StatusCode);
        Assert.Contains("not found in registry", await McpTool<StopAgentTool>().StopAgentAsync("ghost"));
        Assert.Contains("not found in registry", await McpTool<StartAgentTool>().StartAgentAsync("ghost"));
        Assert.Contains("not found in registry or DB", await McpTool<RestartAgentTool>().RestartAgentAsync("ghost"));

        Assert.Empty(_dockerCalls);
        Assert.False(_app.Services.GetRequiredService<OperatorStops>().Contains("ghost"));
    }

    [Fact]
    public async Task RestartWithVersion_AVersionThatDoesNotExist_IsRefusedOnBothSurfaces_AndNothingIsPinned()
    {
        await SeedAgentAsync("agent1");
        await SeedInstructionAsync("base", versions: 2);

        var rest = await _client.PostAsJsonAsync("/api/agents/agent1/restart-with-version",
            new { instructionName = "base", versionNumber = 9, pin = true });
        var mcp = await McpTool<RestartAgentWithVersionTool>().RestartAgentWithVersionAsync("agent1", "base", 9, pin: true);

        Assert.Equal(HttpStatusCode.NotFound, rest.StatusCode);
        Assert.Equal("Version 9 does not exist for instruction 'base'. Available: v2, v1", mcp);
        Assert.Equal(2, await DbAsync(db => db.Instructions.Select(i => i.CurrentVersion).SingleAsync()));
        Assert.Empty(_dockerCalls);
    }

    // ── Telegram allowlist ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Allowlist_TheSameChanges_StoreTheSameList_AndTellTheAgentOnce()
    {
        await SeedAgentAsync("rest-a");
        await SeedAgentAsync("mcp-a");

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/agents/rest-a/telegram-users", new { userId = 111 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/agents/rest-a/telegram-groups", new { groupId = -100 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/agents/rest-a/telegram-users", new { userId = 222 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync("/api/agents/rest-a/telegram-users/222")).StatusCode);

        Assert.Equal("Added Telegram user 111 to agent 'mcp-a'.", await McpTool<ManageAgentTelegramUsersTool>().ManageAgentTelegramUsersAsync("mcp-a", "add", 111));
        Assert.Equal("Added Telegram group -100 to agent 'mcp-a'.", await McpTool<ManageAgentTelegramGroupsTool>().ManageAgentTelegramGroupsAsync("mcp-a", "add", -100));
        Assert.Equal("Added Telegram user 222 to agent 'mcp-a'.", await McpTool<ManageAgentTelegramUsersTool>().ManageAgentTelegramUsersAsync("mcp-a", "add", 222));
        Assert.Equal("Removed Telegram user 222 from agent 'mcp-a'.", await McpTool<ManageAgentTelegramUsersTool>().ManageAgentTelegramUsersAsync("mcp-a", "remove", 222));

        Assert.Equal(await StoredAgentAsync("rest-a"), await StoredAgentAsync("mcp-a"));
        Assert.Contains("\"TelegramUsers\":[111]", await StoredAgentAsync("mcp-a"));
        // Four changes each, one broadcast per change.
        Assert.Equal(4, _allowlistBroadcasts.Count(b => b.StartsWith("rest-a ", StringComparison.Ordinal)));
        Assert.Equal(4, _allowlistBroadcasts.Count(b => b.StartsWith("mcp-a ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Allowlist_TheOwner_IsNeverRemoved_OnEitherSurface()
    {
        await SeedAgentAsync("rest-a", a => a.TelegramUsers = [new AgentTelegramUser { UserId = Owner }]);
        await SeedAgentAsync("mcp-a", a => a.TelegramUsers = [new AgentTelegramUser { UserId = Owner }]);

        var rest = await _client.DeleteAsync($"/api/agents/rest-a/telegram-users/{Owner}");
        var mcp = await McpTool<ManageAgentTelegramUsersTool>().ManageAgentTelegramUsersAsync("mcp-a", "remove", Owner);

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal($"Cannot remove the owner's Telegram user ID ({Owner}) from the allowlist.", mcp);
        Assert.Equal(mcp, (await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(2, await DbAsync(db => db.AgentTelegramUsers.CountAsync(u => u.UserId == Owner)));
        Assert.Empty(_allowlistBroadcasts);
    }

    // ── Memory project access ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectAccess_TheSameGrantsAndRevocations_StoreTheSameRows()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/agents/Rest-A/project-access", new { project = " Alpha " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/agents/rest-a/project-access", new { project = "beta" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/agents/rest-a/project-access", new { project = "*" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/agents/rest-a/project-access/beta")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/agents/rest-a/project-access/gamma")).StatusCode);

        var tool = McpTool<ManageAgentProjectAccessTool>();
        Assert.StartsWith("Added project 'alpha'", await tool.ManageAgentProjectAccessAsync("Mcp-A", "add", " Alpha "));
        Assert.StartsWith("Added project 'beta'", await tool.ManageAgentProjectAccessAsync("mcp-a", "add", "beta"));
        Assert.Contains("now has wildcard access", await tool.ManageAgentProjectAccessAsync("mcp-a", "set_wildcard"));
        Assert.StartsWith("Removed project 'beta'", await tool.ManageAgentProjectAccessAsync("mcp-a", "remove", "beta"));
        Assert.Contains("does not have access to project 'gamma'", await tool.ManageAgentProjectAccessAsync("mcp-a", "remove", "gamma"));

        Assert.Equal(await AccessRowsAsync("rest-a"), await AccessRowsAsync("mcp-a"));
        Assert.Equal(["*:manual", "alpha:manual"], await AccessRowsAsync("mcp-a"));
        // Four stored changes on each surface, fleet-memory told after each; the two refusals tell no one.
        Assert.Equal(8, _aclBroadcasts);
    }

    // ── Instructions ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Instructions_CreateVersionAndRollback_StoreTheSameHistory()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/instructions", new { name = "via-rest", content = "one", createdBy = "tester" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/instructions/via-rest/versions", new { content = "two", reason = "edit", createdBy = "tester" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/instructions/via-rest/rollback/1", null)).StatusCode);

        Assert.StartsWith("Instruction 'via-mcp' created at v1.", await McpTool<CreateInstructionTool>().CreateInstructionAsync("via-mcp", "one", "tester"));
        Assert.StartsWith("Instruction 'via-mcp' updated to v2.", await McpTool<UpdateInstructionTool>().UpdateInstructionAsync("via-mcp", "two", "edit", "tester"));
        Assert.StartsWith("Instruction 'via-mcp' rolled back to v1 content — saved as v3.", await McpTool<RollbackInstructionTool>().RollbackInstructionAsync("via-mcp", 1));

        var viaRest = await InstructionHistoryAsync("via-rest");
        Assert.Equal(viaRest, await InstructionHistoryAsync("via-mcp"));
        Assert.Equal(
            ["current=3", "v1 one by tester (Initial creation)", "v2 two by tester (edit)", "v3 one by rollback (rollback to v1)"],
            viaRest);
    }

    [Theory]
    [InlineData("bad name!", "content")]
    [InlineData("good-name", "   ")]
    public async Task Instructions_ABadNameOrEmptyContent_IsRefusedOnBothSurfaces(string name, string content)
    {
        var rest = await _client.PostAsJsonAsync("/api/instructions", new { name, content });
        var mcp = await McpTool<CreateInstructionTool>().CreateInstructionAsync(name, content, "tester");

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal((await rest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), mcp);
        Assert.Empty(await DbAsync(db => db.Instructions.ToListAsync()));
    }

    [Fact]
    public async Task Instructions_AnEmptyNewVersion_IsRefusedOnBothSurfaces()
    {
        await SeedInstructionAsync("base", versions: 1);

        var rest = await _client.PostAsJsonAsync("/api/instructions/base/versions", new { content = " " });
        var mcp = await McpTool<UpdateInstructionTool>().UpdateInstructionAsync("base", " ", "edit", "tester");

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal("content is required", mcp);
        Assert.Equal(1, await DbAsync(db => db.InstructionVersions.CountAsync()));
    }

    // ── Workflow definitions ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowDefinitions_OnlyANewStepTree_IsANewVersion_OnBothSurfaces()
    {
        const string first = """{"type":"sequence","steps":[]}""";
        const string second = """{"type":"sequence","steps":[{"type":"sleep","seconds":1}]}""";
        var tool = McpTool<WorkflowDefinitionTools>();

        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/workflow-definitions",
            new { name = "ViaRest", @namespace = "fleet", taskQueue = "fleet", definition = first })).StatusCode);
        Assert.Equal("Created workflow definition 'ViaMcp' v1.", await tool.CreateWorkflowDefinitionAsync("ViaMcp", "fleet", "fleet", first));

        // A description alone is not a new version of the workflow.
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/workflow-definitions/ViaRest", new { description = "what it does" })).StatusCode);
        Assert.Equal("Updated workflow definition 'ViaMcp' (description only; still v1).", await tool.UpdateWorkflowDefinitionAsync("ViaMcp", description: "what it does"));
        Assert.Equal(await DefinitionAsync("ViaRest"), await DefinitionAsync("ViaMcp"));
        Assert.Equal("v1 active desc=what it does history=[] tree=" + first, await DefinitionAsync("ViaMcp"));

        // A new step tree is, and the one it replaces is kept under the version it had.
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync("/api/workflow-definitions/ViaRest", new { definition = second })).StatusCode);
        Assert.Equal("Updated workflow definition 'ViaMcp' to v2.", await tool.UpdateWorkflowDefinitionAsync("ViaMcp", definition: second));
        Assert.Equal(await DefinitionAsync("ViaRest"), await DefinitionAsync("ViaMcp"));
        Assert.Equal("v2 active desc=what it does history=[v1] tree=" + second, await DefinitionAsync("ViaMcp"));
    }

    [Fact]
    public async Task WorkflowDefinitions_AnIncompleteOrDuplicateCreate_IsRefusedOnBothSurfaces()
    {
        var tool = McpTool<WorkflowDefinitionTools>();

        var incomplete = await _client.PostAsJsonAsync("/api/workflow-definitions", new { name = "NoQueue", @namespace = "fleet", definition = "{}" });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Equal("Failed to create workflow definition 'NoQueue': TaskQueue is required", await tool.CreateWorkflowDefinitionAsync("NoQueue", "fleet", " ", "{}"));

        Assert.Equal("Created workflow definition 'Twice' v1.", await tool.CreateWorkflowDefinitionAsync("Twice", "fleet", "fleet", "{}"));
        var duplicate = await _client.PostAsJsonAsync("/api/workflow-definitions", new { name = "Twice", @namespace = "fleet", taskQueue = "fleet", definition = "{}" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Failed to create workflow definition 'Twice': Workflow definition 'Twice' already exists", await tool.CreateWorkflowDefinitionAsync("Twice", "fleet", "fleet", "{}"));

        Assert.Equal(["Twice"], await DbAsync(db => db.WorkflowDefinitions.Select(d => d.Name).ToListAsync()));
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tool as MCP builds it: its constructor's arguments come from the container.</summary>
    private T McpTool<T>() where T : class => ActivatorUtilities.CreateInstance<T>(_app.Services);

    private static AgentTool Tool(string name, bool enabled) => new() { ToolName = name, IsEnabled = enabled };

    private async Task<T> DbAsync<T>(Func<OrchestratorDbContext, Task<T>> read)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    private async Task SeedAgentAsync(string name, Action<Agent>? configure = null, bool register = false)
    {
        var agent = new Agent
        {
            Name = name, DisplayName = name, Role = "developer", Model = "claude-sonnet-5", Provider = "claude",
            MemoryLimitMb = 1024, ContainerName = $"fleet-{name}", ShortName = name,
        };
        configure?.Invoke(agent);
        await DbAsync(async db => { db.Agents.Add(agent); return await db.SaveChangesAsync(); });
        if (register) _app.Services.GetRequiredService<AgentRegistry>().PreloadFromDbConfig(agent);
    }

    private Task SeedInstructionAsync(string name, int versions) => DbAsync(async db =>
    {
        var instruction = new Instruction { Name = name, CurrentVersion = versions };
        for (var v = 1; v <= versions; v++)
            instruction.Versions.Add(new InstructionVersion { VersionNumber = v, Content = $"content {v}", CreatedBy = "seed" });
        db.Instructions.Add(instruction);
        return await db.SaveChangesAsync();
    });

    /// <summary>
    /// Everything an agent write can change, as one comparable string, without what names the agent
    /// (so two agents written the same way compare equal) and without the port, which is unique.
    /// </summary>
    private Task<string> StoredAgentAsync(string name) => DbAsync(async db =>
    {
        var a = await db.Agents
            .Include(x => x.Tools).Include(x => x.Projects).Include(x => x.McpEndpoints).Include(x => x.Networks)
            .Include(x => x.EnvRefs).Include(x => x.TelegramUsers).Include(x => x.TelegramGroups).Include(x => x.Instructions)
            .AsSplitQuery().AsNoTracking().SingleAsync(x => x.Name == name);
        return JsonSerializer.Serialize(new
        {
            ShortName = a.ShortName == a.Name ? "(name)" : a.ShortName,
            DisplayName = a.DisplayName == a.Name ? "(name)" : a.DisplayName,
            ContainerName = a.ContainerName == $"fleet-{a.Name}" ? "(default)" : a.ContainerName,
            a.Role, a.Model, a.Provider, a.MemoryLimitMb, a.IsEnabled, a.PermissionMode, a.MaxTurns, a.WorkDir,
            a.ProactiveIntervalMinutes, a.GroupListenMode, a.GroupDebounceSeconds, a.WarmupTimeoutSeconds, a.ShowStats,
            a.PrefixMessages, FormattingMode = (int)a.FormattingMode, a.SuppressToolMessages, a.TelegramSendOnly, a.Image,
            a.Effort, a.JsonSchema, a.AgentsJson, a.AutoMemoryEnabled, a.JournalEnabled, a.JournalCrossChatEnabled,
            a.ChatEnabled, a.CodexSandboxMode, a.OutputStyle, a.LocalBaseUrl, a.ContextWindow, a.HarnessExtra,
            a.MountDockerSock, a.CanReceiveChatRequests, a.RequestReceivedMessage,
            Tools = a.Tools.OrderBy(t => t.ToolName, StringComparer.Ordinal).Select(t => $"{t.ToolName}={t.IsEnabled}"),
            Projects = a.Projects.Select(p => p.ProjectName).Order(StringComparer.Ordinal),
            Networks = a.Networks.Select(n => n.NetworkName).Order(StringComparer.Ordinal),
            EnvRefs = a.EnvRefs.Select(r => r.EnvKeyName).Order(StringComparer.Ordinal),
            TelegramUsers = a.TelegramUsers.Select(u => u.UserId).Order(),
            TelegramGroups = a.TelegramGroups.Select(g => g.GroupId).Order(),
            McpEndpoints = a.McpEndpoints.Select(e => e.McpName).Order(StringComparer.Ordinal),
            Instructions = a.Instructions.Count,
        });
    });

    private Task<List<string>> AccessRowsAsync(string agent) => DbAsync(async db =>
        (await db.AgentProjectAccess.Where(x => x.AgentName == agent).ToListAsync())
            .Select(x => $"{x.Project}:{x.Source}").Order(StringComparer.Ordinal).ToList());

    private Task<List<string>> InstructionHistoryAsync(string name) => DbAsync(async db =>
    {
        var instruction = await db.Instructions.Include(i => i.Versions).AsNoTracking().SingleAsync(i => i.Name == name);
        return instruction.Versions.OrderBy(v => v.VersionNumber)
            .Select(v => $"v{v.VersionNumber} {v.Content} by {v.CreatedBy} ({v.Reason})")
            .Prepend($"current={instruction.CurrentVersion}")
            .ToList();
    });

    private Task<string> DefinitionAsync(string name) => DbAsync(async db =>
    {
        var d = await db.WorkflowDefinitions.Include(x => x.Versions).AsNoTracking().SingleAsync(x => x.Name == name);
        var history = string.Join(",", d.Versions.OrderBy(v => v.Version).Select(v => $"v{v.Version}"));
        return $"v{d.Version} {(d.IsActive ? "active" : "inactive")} desc={d.Description} history=[{history}] tree={d.Definition}";
    });

    private sealed class MutableTelegramSetup : ITelegramSetup
    {
        public bool IsConfigured { get; set; }
        public long? OwnerUserId { get; set; }
    }

    private sealed class CountingAclNotifier(Action told) : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) { told(); return Task.CompletedTask; }
    }

    /// <summary>Accepts a stop, start or restart of any container, and has no container to inspect or remove.</summary>
    private sealed class AcceptingDockerApi(List<string> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (calls) calls.Add($"{request.Method} {path}");
            var accepted = request.Method == HttpMethod.Post
                && (path.Contains("/stop", StringComparison.Ordinal) || path.Contains("/start", StringComparison.Ordinal) || path.Contains("/restart", StringComparison.Ordinal));
            return Task.FromResult(new HttpResponseMessage(accepted ? HttpStatusCode.NoContent : HttpStatusCode.NotFound));
        }
    }
}
