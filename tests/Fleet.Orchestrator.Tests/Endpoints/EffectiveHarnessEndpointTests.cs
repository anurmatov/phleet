extern alias FleetAgent;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FleetAgent::Fleet.Agent.Configuration;
using FleetAgent::Fleet.Agent.Services;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// #460 Part 3a: <c>GET /api/agents/{name}/effective-harness</c>. AC-X3 parity — the served Codex
/// root TOML equals the generated <c>codex-root.toml</c> bytes, and the served plan equals the plan
/// computed from exactly what provisioning hands the agent (its generated appsettings and container
/// env). AC-X5 — credential values masked, overrides and state shown. AC-X6 — the started-plan
/// report from registration/heartbeats drives <c>pendingReprovision</c>. Plus the 404s and the
/// "ignored" path for a stored value the allowlist refuses.
/// </summary>
public sealed class EffectiveHarnessEndpointTests : IAsyncLifetime
{
    private const string Url = "http://inference-host:8000";
    private const string Get = "/api/agents/agent1/effective-harness";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private AgentRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        _registry = new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance);
        builder.Services.AddSingleton(_registry);

        _app = builder.Build();
        _app.MapAgentHarnessEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    public static TheoryData<string> Configs => ["codex-cloud", "codex-local", "codex-hosted", "claude-cloud", "claude-local"];

    private static Agent NewAgent(string config, string? extra = null)
    {
        var (provider, model, url, effort) = config switch
        {
            "codex-cloud" => ("codex", "gpt-5.5", (string?)null, "high"),
            "codex-local" => ("codex", "ollama/qwen3.8-flash-next", Url, "low"),
            "codex-hosted" => ("codex", "zai/glm-5.3", null, "high"),
            "claude-cloud" => ("claude", "claude-opus-5-5", null, "high"),
            "claude-local" => ("claude", "qwen3.8-flash-next", Url, "off"),
            "pi" => ("pi", "qwen3.8-flash-next", Url, "off"),
            "gemini" => ("gemini", "gemini-2.5-pro", null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config, null),
        };
        var agent = new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "developer", Model = model, Provider = provider,
            ContainerName = "fleet-agent1", MemoryLimitMb = 1024, Effort = effort, LocalBaseUrl = url,
            ContextWindow = 131_072, HarnessExtra = extra,
        };
        agent.Tools.Add(new AgentTool { ToolName = "Read", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "Bash(git *)", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-memory__memory_search", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "Write", IsEnabled = false });
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "ZAI_CODING_PLAN_API_KEY" });
        return agent;
    }

    private static string ExtraFor(string config) => config.StartsWith("codex", StringComparison.Ordinal)
        ? "tool_output_token_limit = 12000\nhistory.persistence = \"save-all\"\n"
        : "MAX_MCP_OUTPUT_TOKENS=12000\n--tools NotebookEdit\n";

    private async Task SeedAsync(Agent agent)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
    }

    private async Task SetExtraAsync(string? extra)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Agents.SingleAsync(a => a.Name == "agent1")).HarnessExtra = extra;
        await db.SaveChangesAsync();
    }

    private Task<JsonElement> GetAsync() => _client.GetFromJsonAsync<JsonElement>(Get);

    // ── AC-X3: parity with what provisioning generates ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Configs))]
    public async Task ServedPlan_EqualsThePlanFromWhatTheAgentIsGiven(string config)
    {
        var extra = ExtraFor(config);
        await SeedAsync(NewAgent(config, extra));
        var served = await GetAsync();

        await using var h = ProvisioningHarness.Create();
        File.AppendAllText(Path.Combine(h.BaseDir, ".env"), "ZAI_CODING_PLAN_API_KEY=zai-key-placeholder\n");
        await h.SeedAsync(db => db.Agents.Add(NewAgent(config, extra)));
        var preview = await h.Service.PreviewAsync("agent1");
        var result = await h.Service.ProvisionAsync("agent1");
        Assert.True(result.Success, result.Message);
        var dir = h.GeneratedDir("fleet-agent1");

        // The agent's own inputs, read the way the agent reads them: its generated appsettings bound
        // by the real configuration binder (which appends AllowedTools to the six-tool default), and
        // the container env, where a Claude local agent's window arrives.
        var appsettingsJson = await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json"));
        var options = new AgentOptions { Name = "", Role = "", WorkDir = "" };
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(appsettingsJson))).Build()
            .GetSection(AgentOptions.Section).Bind(options);
        var containerEnv = preview.Desired.Env.Select(e => e.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var agentSide = new AgentHarness(options, name => containerEnv.GetValueOrDefault(name), NullLogger.Instance).Plan!;
        string? Str(string key) => typeof(AgentOptions).GetProperty(key)!.GetValue(options) as string;
        var provider = options.Provider;

        Assert.Equal(agentSide.PlanSha256, served.GetProperty("planSha256").GetString());
        Assert.Equal(agentSide.PlanSha256, served.GetProperty("computedPlanSha256").GetString());
        Assert.Equal(extra, Str("HarnessExtra"));
        // What the executors apply is that same plan, entry for entry.
        if (agentSide.ClaudeArgs is { } args)
        {
            Assert.Equal(args, served.GetProperty("claude").GetProperty("args").EnumerateArray().Select(a => a.GetString()!));
            Assert.Equal(agentSide.ClaudeEnv!.Select(e => $"{e.Key}={HarnessDefaults.DisplayValue(e.Key, e.Value)}"),
                served.GetProperty("claude").GetProperty("env").EnumerateArray()
                    .Select(e => $"{e.GetProperty("name").GetString()}={e.GetProperty("value").GetString()}"));
        }
        else
        {
            var startParams = new System.Text.Json.Nodes.JsonObject();
            CodexExecutor.MergeHarnessThreadConfig(startParams, agentSide.CodexThreadConfig!);
            Assert.Equal(startParams["config"]!.ToJsonString(), served.GetProperty("codex").GetProperty("threadConfig").GetRawText());
        }

        var rootFile = Path.Combine(dir, HarnessDefaults.CodexRootTomlFileName);
        if (provider == "codex")
        {
            Assert.Equal(await File.ReadAllTextAsync(rootFile), served.GetProperty("codex").GetProperty("configToml").GetString());
            Assert.Contains($"{HarnessDefaults.CodexRootTomlFileName}:/app/codex-root.toml:ro", string.Join('\n', preview.Desired.Binds));
            Assert.Equal(JsonValueKind.Null, served.GetProperty("claude").ValueKind);
        }
        else
        {
            Assert.False(File.Exists(rootFile));
            Assert.DoesNotContain(preview.Desired.Binds, b => b.Contains("codex-root.toml", StringComparison.Ordinal));
            Assert.Equal(JsonValueKind.Null, served.GetProperty("codex").ValueKind);
        }
    }

    // ── AC-X5: the served shape ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaudeLocal_MasksCredentials_MarksOverrides_AndShowsState()
    {
        await SeedAsync(NewAgent("claude-local", "MAX_MCP_OUTPUT_TOKENS=12000\n!--exclude-dynamic-system-prompt-sections"));

        var served = await GetAsync();

        Assert.Equal(("claude", "local"), (served.GetProperty("provider").GetString(), served.GetProperty("mode").GetString()));
        var env = served.GetProperty("claude").GetProperty("env").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("value").GetString());
        Assert.Equal(("***", "***"), (env["ANTHROPIC_API_KEY"], env["ANTHROPIC_AUTH_TOKEN"]));
        Assert.Equal((Url, "12000"), (env["ANTHROPIC_BASE_URL"], env["MAX_MCP_OUTPUT_TOKENS"]));
        Assert.DoesNotContain(ClaudeLocalModel.PlaceholderAuthToken, served.GetRawText());
        Assert.Equal(
            // No --tools: only an explicit harnessExtra --tools line narrows built-ins.
            ["--disallowedTools", "WebSearch", "--strict-mcp-config"],
            served.GetProperty("claude").GetProperty("args").EnumerateArray().Select(x => x.GetString()).ToArray());
        var extra = served.GetProperty("extra");
        Assert.Equal("applied", extra.GetProperty("state").GetString());
        Assert.Equal(["MAX_MCP_OUTPUT_TOKENS", "--exclude-dynamic-system-prompt-sections"],
            extra.GetProperty("replaced").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task CodexLocal_ServesTheThreadConfigWithTypedValues()
    {
        await SeedAsync(NewAgent("codex-local"));

        var thread = (await GetAsync()).GetProperty("codex").GetProperty("threadConfig");

        Assert.Equal("disabled", thread.GetProperty("web_search").GetString());
        Assert.False(thread.GetProperty("features.view_image").GetBoolean());
        Assert.Equal(8192, thread.GetProperty("tool_output_token_limit").GetInt32());
    }

    [Fact]
    public async Task AStoredValueTheAllowlistRefuses_IsServedAsIgnored_OnTheModeDefaults()
    {
        await SeedAsync(NewAgent("codex-local", "web_search = \"live\""));

        var served = await GetAsync();

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Get)).StatusCode);
        Assert.Equal("ignored", served.GetProperty("extra").GetProperty("state").GetString());
        Assert.Contains("cannot enable web search in local mode", served.GetProperty("extra").GetProperty("reason").GetString());
        Assert.Equal("disabled", served.GetProperty("codex").GetProperty("threadConfig").GetProperty("web_search").GetString());
    }

    [Fact]
    public async Task UnknownAgent_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(Get)).StatusCode);
    }

    [Theory]
    [InlineData("pi")]
    [InlineData("gemini")]
    public async Task PiAndGemini_Are404UnsupportedProvider(string config)
    {
        await SeedAsync(NewAgent(config));

        var response = await _client.GetAsync(Get);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unsupported_provider", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }

    // ── AC-X6: drift ───────────────────────────────────────────────────────────────────────────────

    private void Report(string? sha, string routingKey = "heartbeat") =>
        _registry.UpdateAgent(HeartbeatConsumerService.DeserializeHeartbeat(JsonSerializer.Serialize(new
        {
            agentName = "agent1", status = routingKey == "registration" ? "starting" : "idle",
            timestamp = DateTimeOffset.UtcNow, planSha256 = sha,
        }))!);

    [Fact]
    public async Task Drift_NotReported_ThenMatching_ThenPendingAfterASave_ThenClearedByTheNewReport()
    {
        await SeedAsync(NewAgent("claude-cloud"));

        var first = await GetAsync();
        Assert.Equal(JsonValueKind.Null, first.GetProperty("startedPlanSha256").ValueKind);
        Assert.False(first.GetProperty("pendingReprovision").GetBoolean());

        var started = first.GetProperty("computedPlanSha256").GetString()!;
        Report(started, "registration");
        var running = await GetAsync();
        Assert.Equal(started, running.GetProperty("startedPlanSha256").GetString());
        Assert.False(running.GetProperty("pendingReprovision").GetBoolean());

        // A save without reprovision: the computed plan moves, the running one does not.
        await SetExtraAsync("--disallowedTools WebSearch");
        var pending = await GetAsync();
        Assert.NotEqual(started, pending.GetProperty("computedPlanSha256").GetString());
        Assert.Equal(started, pending.GetProperty("startedPlanSha256").GetString());
        Assert.True(pending.GetProperty("pendingReprovision").GetBoolean());

        // After reprovision the agent reports the new plan and the flag clears.
        Report(pending.GetProperty("computedPlanSha256").GetString());
        Assert.False((await GetAsync()).GetProperty("pendingReprovision").GetBoolean());

        // An older image reports no sha: "not reported" again, never a stale value.
        Report(null);
        var older = await GetAsync();
        Assert.Equal(JsonValueKind.Null, older.GetProperty("startedPlanSha256").ValueKind);
        Assert.False(older.GetProperty("pendingReprovision").GetBoolean());
    }

    [Fact]
    public void Heartbeat_WithoutTheField_DeserializesAsNull()
    {
        var old = HeartbeatConsumerService.DeserializeHeartbeat(
            """{"agentName":"agent1","status":"idle","timestamp":"2026-10-14T00:00:00Z"}""")!;
        var current = HeartbeatConsumerService.DeserializeHeartbeat(
            """{"AgentName":"agent1","Status":"idle","Timestamp":"2026-10-14T00:00:00Z","PlanSha256":"abc"}""")!;

        Assert.Null(old.PlanSha256);
        Assert.Equal("abc", current.PlanSha256);
    }
}
