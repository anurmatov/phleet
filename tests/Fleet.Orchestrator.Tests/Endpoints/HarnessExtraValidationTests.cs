using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
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
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// #460 AC-X1 and AC-X5 (storage): <c>harnessExtra</c> on <c>PUT /api/agents/{name}/config</c> and
/// <c>harness_extra</c> on <c>update_agent_config</c> / <c>create_agent</c>. Every allowlisted key,
/// flag and env name is accepted with an in-range value; everything else is a 400 or tool error naming
/// the field, the line and the allowed names, and every refusal is checked against the stored row.
/// </summary>
public sealed class HarnessExtraValidationTests : IAsyncLifetime
{
    private const string Url = "http://inference-host:11434";
    private const string Put = "/api/agents/agent1/config";

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"harness-{Guid.NewGuid():N}.env"),
        }).Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<IAclChangeNotifier>(new NoopAclChangeNotifier());
        builder.Services.AddSingleton(new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance));
        builder.Services.AddSingleton(sp => new SetupService(
            config, null!, null!, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SetupService>.Instance, null!));

        _app = builder.Build();
        _app.MapAgentConfigEndpoints();

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

    private async Task SeedAsync(string provider, string model, string? url = null, string? extra = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = provider,
            LocalBaseUrl = url, ContextWindow = url is null ? null : 131_072, MemoryLimitMb = 1024,
            ContainerName = "agent1", HarnessExtra = extra,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Agent> StoredAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .Agents.AsNoTracking().SingleAsync(a => a.Name == "agent1");
    }

    private IServiceScopeFactory Scopes => _app.Services.GetRequiredService<IServiceScopeFactory>();

    /// <summary>
    /// #460 owner direction: a stored dated id still validates and is never rewritten; a family alias is
    /// accepted and stored as the alias (Codex maps it when the agent starts, Claude Code resolves it).
    /// </summary>
    [Theory]
    [InlineData("claude", "claude-opus-4-7", "opus")]
    [InlineData("claude", "claude-haiku-4-5-20251001", "haiku")]
    [InlineData("codex", "gpt-5.6-sol", "sol")]
    [InlineData("codex", "gpt-5.5", "terra")]
    public async Task Models_ADatedIdStillValidates_AndAnAliasIsStoredAsIs(string provider, string dated, string alias)
    {
        await SeedAsync(provider, dated);

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { memoryLimitMb = 2048 })).StatusCode);
        Assert.Equal(dated, (await StoredAsync()).Model);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { model = alias })).StatusCode);
        Assert.Equal(alias, (await StoredAsync()).Model);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { model = dated })).StatusCode);
        Assert.Equal(dated, (await StoredAsync()).Model);
    }

    private async Task<JsonElement> BadRequestAsync(object body)
    {
        var response = await _client.PutAsJsonAsync(Put, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── parser: every allowlisted name is accepted ─────────────────────────────────────────────────

    [Theory]
    [InlineData("codex", false, "web_search = \"live\"")]
    [InlineData("codex", false, "web_search = \"cached\"")]
    [InlineData("codex", true, "web_search = \"disabled\"")]
    [InlineData("codex", false, "features.view_image = false")]
    [InlineData("codex", true, "features.view_image = false")]
    [InlineData("codex", false, "model_reasoning_summary = \"detailed\"")]
    [InlineData("codex", true, "tool_output_token_limit = 1000")]
    [InlineData("codex", true, "tool_output_token_limit = 100000")]
    [InlineData("codex", false, "features.apps = true")]
    [InlineData("codex", false, "features.remote_plugin = false")]
    [InlineData("codex", false, "features.skill_mcp_dependency_install = false")]
    [InlineData("codex", false, "check_for_update_on_startup = true")]
    [InlineData("codex", false, "analytics.enabled = false")]
    [InlineData("codex", false, "history.persistence = \"save-all\"")]
    [InlineData("claude", false, "--tools Edit,Write")]
    [InlineData("claude", true, "--tools NotebookEdit")]
    [InlineData("claude", false, "--disallowedTools WebSearch")]
    [InlineData("claude", true, "--disallowedTools WebSearch")]
    [InlineData("claude", false, "!--exclude-dynamic-system-prompt-sections")]
    [InlineData("claude", false, "--disable-slash-commands")]
    [InlineData("claude", true, "--disable-slash-commands")]
    [InlineData("claude", true, "MAX_MCP_OUTPUT_TOKENS=12000")]
    [InlineData("claude", false, "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=0")]
    [InlineData("", false, "MAX_MCP_OUTPUT_TOKENS=1000")]
    [InlineData("claude", false, "# a comment\n\n  MAX_MCP_OUTPUT_TOKENS=100000  \r\n")]
    public void Parse_AcceptsEveryAllowlistedSetting(string provider, bool local, string text)
    {
        var parse = HarnessExtra.Parse(text, provider, local);

        Assert.Null(parse.Fault);
        Assert.Single(parse.Lines);
    }

    // ── parser: everything else is refused, naming the line and the reason ─────────────────────────

    [Theory]
    [InlineData("codex", false, "tool_output_token_limit = 999", "outside 1000-100000")]
    [InlineData("codex", false, "tool_output_token_limit = 100001", "outside 1000-100000")]
    [InlineData("codex", false, "tool_output_token_limit = \"12000\"", "must be an integer")]
    [InlineData("codex", false, "features.view_image = yes", "must be true or false")]
    [InlineData("codex", false, "web_search = live", "must be a quoted string")]
    [InlineData("codex", false, "web_search = \"on\"", "cannot be \"on\"")]
    [InlineData("codex", true, "web_search = \"live\"", "cannot enable web search in local mode")]
    [InlineData("codex", true, "web_search = \"cached\"", "cannot enable web search in local mode")]
    [InlineData("codex", true, "features.view_image = true", "cannot enable the image tool in local mode")]
    [InlineData("codex", false, "features.multi_agent = false", "'features.multi_agent' is not an allowed setting")]
    [InlineData("codex", false, "model_auto_compact_token_limit = 5000", "is not an allowed setting")]
    [InlineData("codex", false, "project_doc_max_bytes = 1", "is not an allowed setting")]
    [InlineData("codex", false, "notify = [\"sh\"]", "'notify' is not an allowed setting")]
    [InlineData("codex", false, "approval_policy = \"never\"", "is not an allowed setting")]
    [InlineData("codex", false, "model_context_window = 4096", "is not an allowed setting")]
    [InlineData("codex", false, "mcp_servers.x.url = \"http://x\"", "is not an allowed setting")]
    [InlineData("codex", false, "--tools Edit", "expected 'key = value'")]
    [InlineData("claude", false, "--allowedTools Bash", "'--allowedTools' is not an allowed setting")]
    [InlineData("claude", false, "--mcp-config /tmp/x.json", "'--mcp-config' is not an allowed setting")]
    [InlineData("claude", false, "--strict-mcp-config", "is not an allowed setting")]
    [InlineData("claude", false, "--dangerously-skip-permissions", "is not an allowed setting")]
    [InlineData("claude", false, "--disallowedTools Bash", "accepts only the value WebSearch")]
    [InlineData("claude", false, "--disallowedTools", "accepts only the value WebSearch")]
    [InlineData("claude", false, "--disable-slash-commands foo", "'--disable-slash-commands' takes no value")]
    [InlineData("claude", true, "--disable-slash-commands true", "'--disable-slash-commands' takes no value")]
    [InlineData("claude", false, "!--disable-slash-commands", "cannot be removed")]
    [InlineData("codex", false, "--disable-slash-commands", "expected 'key = value'")]
    [InlineData("claude", false, "!--strict-mcp-config", "cannot be removed")]
    [InlineData("claude", false, "!--tools", "cannot be removed")]
    [InlineData("claude", false, "--tools", "needs built-in tool names to keep")]
    [InlineData("claude", false, "--tools \"\"", "takes known built-in tool names only")]
    [InlineData("claude", false, "--tools default", "'default' is not one")]
    [InlineData("claude", false, "--tools Bash Read", "at most one value token")]
    [InlineData("claude", true, "--tools WebSearch", "cannot add it back")]
    [InlineData("claude", false, "MAX_MCP_OUTPUT_TOKENS=99", "outside 1000-100000")]
    [InlineData("claude", false, "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=true", "must be 0 or 1")]
    [InlineData("claude", false, "ANTHROPIC_BASE_URL=http://x", "'ANTHROPIC_BASE_URL' is not an allowed setting")]
    [InlineData("claude", false, "CLAUDE_CODE_OAUTH_TOKEN=x", "is not an allowed setting")]
    [InlineData("claude", false, "LD_PRELOAD=/x.so", "is not an allowed setting")]
    [InlineData("claude", false, "web_search = \"live\"", "'web_search' is not an allowed setting for claude")]
    [InlineData("claude", false, "features.view_image=false", "is not a valid environment variable name")]
    [InlineData("claude", false, "just text", "expected '--flag [value]'")]
    public void Parse_RefusesAnythingElse(string provider, bool local, string text, string reason)
    {
        var fault = HarnessExtra.Parse("# first\n" + text, provider, local).Fault;

        Assert.NotNull(fault);
        Assert.Equal((HarnessExtra.InvalidLineCode, (int?)2), (fault.Code, fault.Line));
        Assert.StartsWith("harnessExtra line 2: ", fault.Message);
        Assert.Contains(reason, fault.Message);
    }

    [Fact]
    public void Parse_AnUnknownName_ListsTheAllowedNames()
    {
        Assert.Contains("Allowed keys: web_search, features.view_image,", HarnessExtra.Parse("notify = 1", "codex", false).Fault!.Message);
        Assert.Contains("Allowed: --tools <names>, --disallowedTools WebSearch, --disable-slash-commands, !--exclude-dynamic-system-prompt-sections,",
            HarnessExtra.Parse("--x", "claude", false).Fault!.Message);
    }

    [Fact]
    public void Parse_ADuplicate_NamesBothLines()
    {
        var fault = HarnessExtra.Parse("MAX_MCP_OUTPUT_TOKENS=2000\nMAX_MCP_OUTPUT_TOKENS=3000", "claude", false).Fault!;

        Assert.Equal("harnessExtra line 2: 'MAX_MCP_OUTPUT_TOKENS' is already set on line 1.", fault.Message);

        var flag = HarnessExtra.Parse("--disable-slash-commands\n--tools WebFetch\n--disable-slash-commands", "claude", true).Fault!;
        Assert.Equal("harnessExtra line 3: '--disable-slash-commands' is already set on line 1.", flag.Message);
    }

    [Fact]
    public void Parse_SizeLimits()
    {
        var ok = string.Join('\n', Enumerable.Repeat("# x", HarnessExtra.MaxLines)) + "\n";
        Assert.Null(HarnessExtra.Parse(ok, "codex", false).Fault);
        Assert.Null(HarnessExtra.Parse(new string('#', HarnessExtra.MaxChars), "codex", false).Fault);

        var lines = HarnessExtra.Parse(ok + "# one more", "codex", false).Fault!;
        Assert.Equal((HarnessExtra.TooLargeCode, "harnessExtra has 51 lines; the limit is 50."), (lines.Code, lines.Message));
        var chars = HarnessExtra.Parse(new string('#', HarnessExtra.MaxChars + 1), "codex", false).Fault!;
        Assert.Equal((HarnessExtra.TooLargeCode, "harnessExtra is 4097 characters; the limit is 4096."), (chars.Code, chars.Message));
    }

    [Theory]
    [InlineData("gemini")]
    public void Parse_OtherProviders_RefuseAnyNonEmptyValue(string provider)
    {
        Assert.Null(HarnessExtra.Parse(null, provider, false).Fault);
        Assert.Null(HarnessExtra.Parse("", provider, false).Fault);
        Assert.Equal(HarnessExtra.UnsupportedProviderCode, HarnessExtra.Parse("# only a comment", provider, false).Fault!.Code);
    }

    // ── REST ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_StoresTheTextByteIdentical_GetReturnsIt_AndEmptyClears()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b", Url);
        const string text = "# keep tool output short\r\ntool_output_token_limit = 12000\nhistory.persistence = \"save-all\"\n";

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { harnessExtra = text })).StatusCode);
        Assert.Equal(text, (await StoredAsync()).HarnessExtra);
        Assert.Equal(text, (await _client.GetFromJsonAsync<JsonElement>(Put)).GetProperty("harnessExtra").GetString());

        // Omitted keeps it.
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { memoryLimitMb = 2048 })).StatusCode);
        Assert.Equal(text, (await StoredAsync()).HarnessExtra);

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { harnessExtra = "" })).StatusCode);
        Assert.Null((await StoredAsync()).HarnessExtra);
        Assert.Equal(JsonValueKind.Null, (await _client.GetFromJsonAsync<JsonElement>(Put)).GetProperty("harnessExtra").ValueKind);
    }

    [Fact]
    public async Task Put_ARefusedLine_Is400NamingFieldLineAndAllowed_AndNothingIsWritten()
    {
        await SeedAsync("codex", "gpt-5.5", extra: "web_search = \"live\"");

        var body = await BadRequestAsync(new { harnessExtra = "web_search = \"cached\"\nnotify = [\"sh\"]", memoryLimitMb = 4096 });

        Assert.Equal("harnessExtra", body.GetProperty("field").GetString());
        Assert.Equal(2, body.GetProperty("line").GetInt32());
        Assert.Equal(HarnessExtra.InvalidLineCode, body.GetProperty("code").GetString());
        Assert.StartsWith("harnessExtra line 2: 'notify' is not an allowed setting for codex. Allowed keys: web_search,",
            body.GetProperty("error").GetString());
        var stored = await StoredAsync();
        Assert.Equal(("web_search = \"live\"", 1024), (stored.HarnessExtra, stored.MemoryLimitMb));
    }

    [Theory]
    [InlineData("gemini", "gemini-2.5-pro", null)]
    public async Task Put_AnyValueOnGemini_Is400UnsupportedProvider(string provider, string model, string? url)
    {
        await SeedAsync(provider, model, url);

        var body = await BadRequestAsync(new { harnessExtra = "MAX_MCP_OUTPUT_TOKENS=2000" });

        Assert.Equal(HarnessExtra.UnsupportedProviderCode, body.GetProperty("code").GetString());
        Assert.StartsWith("unsupported_provider: harnessExtra applies only to claude and codex agents",
            body.GetProperty("error").GetString());
        Assert.Null((await StoredAsync()).HarnessExtra);
    }

    [Fact]
    public async Task Put_ASwitchThatWouldLeaveTheStoredValueOutsideTheAllowlist_IsRefused()
    {
        // Cloud codex may enable live web search; the same agent switched to local may not.
        await SeedAsync("codex", "ollama/qwen3.8:27b", extra: "web_search = \"live\"");

        var body = await BadRequestAsync(new { localBaseUrl = Url });
        Assert.Contains("cannot enable web search in local mode", body.GetProperty("error").GetString());
        Assert.Null((await StoredAsync()).LocalBaseUrl);

        // Changing the value in the same request lets the switch through.
        var both = await _client.PutAsJsonAsync(Put, new { localBaseUrl = Url, harnessExtra = "web_search = \"disabled\"" });
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
    }

    [Fact]
    public async Task Put_ALargerValue_Is400TooLarge()
    {
        await SeedAsync("claude", "claude-opus-5-5");

        var body = await BadRequestAsync(new { harnessExtra = string.Concat(Enumerable.Repeat("# x\n", 51)) });

        Assert.Equal(HarnessExtra.TooLargeCode, body.GetProperty("code").GetString());
        Assert.Null((await StoredAsync()).HarnessExtra);
    }

    // ── MCP ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgentConfig_SetsAndClears()
    {
        await SeedAsync("claude", "claude-opus-5-5");
        var tool = new UpdateAgentConfigTool(Scopes, new NoopAclChangeNotifier());

        var set = await tool.UpdateAgentConfigAsync("agent1", harness_extra: "--disallowedTools WebSearch\n");
        Assert.Contains("- harness_extra: (none) → (set)", set);
        Assert.Equal("--disallowedTools WebSearch\n", (await StoredAsync()).HarnessExtra);

        var shown = await new GetAgentConfigTool(Scopes).GetAgentConfigAsync("agent1");
        Assert.Contains("### Harness extra\n```\n--disallowedTools WebSearch\n```", shown.ReplaceLineEndings("\n"));

        var cleared = await tool.UpdateAgentConfigAsync("agent1", harness_extra: "");
        Assert.Contains("- harness_extra: (set) → (none)", cleared);
        Assert.Null((await StoredAsync()).HarnessExtra);
    }

    [Fact]
    public async Task UpdateAgentConfig_LocalClaude_AcceptsDisableSlashCommands_AndRefusesAValue()
    {
        await SeedAsync("claude", "qwen3.8:27b", Url);
        var tool = new UpdateAgentConfigTool(Scopes, new NoopAclChangeNotifier());

        var set = await tool.UpdateAgentConfigAsync("agent1", harness_extra: "--tools WebFetch\n--disable-slash-commands");
        Assert.Contains("- harness_extra: (none) → (set)", set);
        Assert.Equal("--tools WebFetch\n--disable-slash-commands", (await StoredAsync()).HarnessExtra);

        var refused = await tool.UpdateAgentConfigAsync("agent1", harness_extra: "--disable-slash-commands foo");
        Assert.Equal("Invalid harness_extra: harnessExtra line 1: '--disable-slash-commands' takes no value.", refused);
        Assert.Equal("--tools WebFetch\n--disable-slash-commands", (await StoredAsync()).HarnessExtra);
    }

    [Fact]
    public async Task UpdateAgentConfig_ARefusedLine_IsAToolError_AndNothingIsWritten()
    {
        await SeedAsync("claude", "claude-opus-5-5");
        var tool = new UpdateAgentConfigTool(Scopes, new NoopAclChangeNotifier());

        var result = await tool.UpdateAgentConfigAsync("agent1", memory_limit_mb: 4096,
            harness_extra: "MAX_MCP_OUTPUT_TOKENS=2000\n--mcp-config /tmp/other.json");

        Assert.StartsWith("Invalid harness_extra: harnessExtra line 2: '--mcp-config' is not an allowed setting for claude. Allowed:", result);
        var stored = await StoredAsync();
        Assert.Equal(((string?)null, 1024), (stored.HarnessExtra, stored.MemoryLimitMb));
    }

    [Fact]
    public async Task CreateAgent_ValidatesLikeTheUpdate_BeforeAnyWrite()
    {
        var config = new ConfigurationBuilder().Build();
        var tool = new CreateAgentTool(Scopes, config);

        var refused = await tool.CreateAgentAsync("agent2", "developer", "gpt-5.5", provider: "codex",
            harness_extra: "features.multi_agent = false");
        Assert.StartsWith("Invalid harness_extra: harnessExtra line 1: 'features.multi_agent' is not an allowed setting", refused);

        var created = await tool.CreateAgentAsync("agent3", "developer", "gpt-5.5", provider: "codex",
            harness_extra: "features.apps = true");
        Assert.Contains("created in DB", created);

        await using var scope = _app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AsNoTracking()
            .OrderBy(a => a.Name).Select(a => new { a.Name, a.HarnessExtra }).ToListAsync();
        Assert.Equal([("agent3", "features.apps = true")], stored.Select(a => (a.Name, a.HarnessExtra)).ToList());
    }

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
