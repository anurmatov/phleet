using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #382 through <c>update_agent_config</c> and <c>get_agent_config</c>: <c>local_base_url</c> and
/// its alias <c>anthropic_base_url</c> (A1), codex local mode, the gemini refusal, and a provider
/// switch while local, one case per direction — the MCP mirror of
/// <c>AgentConfigEndpointsLocalModelTests</c>. #386: pi's rules (A5), switches into pi (A6) and the
/// <c>get_agent_config</c> lines (A25). #455: <c>pi_image_input</c> through <c>create_agent</c>,
/// <c>update_agent_config</c> (directly and through the MCP SDK's argument binding, where a
/// non-boolean is a tool error), <c>get_agent_config</c> and <c>list_agent_configs</c>.
/// </summary>
public class UpdateAgentConfigLocalModelTests
{
    private const string Url = "http://inference-host:11434";

    private readonly DbContextOptions<OrchestratorDbContext> _options =
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"local-model-{Guid.NewGuid():N}")
            .Options;

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new OrchestratorDbContext(_options));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private UpdateAgentConfigTool Tool() => new(Scopes(), new NoOpAclChangeNotifier());

    private void Seed(string provider, string model, string? url = null, int? window = null, string? envRef = null,
        string? effort = null, bool piImageInput = false, string name = "agent1")
    {
        using var db = new OrchestratorDbContext(_options);
        db.Agents.Add(new Agent
        {
            Name = name, DisplayName = name, Role = "test", Model = model, Provider = provider,
            MemoryLimitMb = 1024, ContainerName = name, LocalBaseUrl = url, ContextWindow = window,
            Effort = effort, PiImageInput = piImageInput,
            EnvRefs = envRef is null ? [] : [new AgentEnvRef { EnvKeyName = envRef }],
        });
        db.SaveChanges();
    }

    private Agent Stored()
    {
        using var db = new OrchestratorDbContext(_options);
        return db.Agents.AsNoTracking().Single(a => a.Name == "agent1");
    }

    // ── codex local set / clear ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codex_SetThenClear()
    {
        Seed("codex", "ollama/qwen3.8:27b");

        var set = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "HTTP://Inference-Host:11434/");

        Assert.Contains($"local_base_url: (none) → {Url}", set);
        Assert.Equal(Url, Stored().LocalBaseUrl);

        var clear = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "");

        Assert.Contains($"local_base_url: {Url} → (none)", clear);
        Assert.Null(Stored().LocalBaseUrl);
    }

    [Fact]
    public async Task Gemini_WithAUrl_IsRefusedByL1()
    {
        Seed("gemini", "gemini-2.5-pro");

        var result = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: Url);

        Assert.Contains("Local model runs on claude, codex or pi", result);
        Assert.Null(Stored().LocalBaseUrl);
    }

    // ── A1 (acceptance 9, MCP) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A1a_OnlyTheAlias_IsStored()
    {
        Seed("claude", "qwen3.8:27b");

        await Tool().UpdateAgentConfigAsync("agent1", anthropic_base_url: Url);

        Assert.Equal(Url, Stored().LocalBaseUrl);
    }

    [Fact]
    public async Task A1b_BothEmpty_Clears()
    {
        Seed("claude", "qwen3.8:27b", Url);

        await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "", anthropic_base_url: "", model: "claude-sonnet-5");

        Assert.Null(Stored().LocalBaseUrl);
    }

    [Theory]
    [InlineData("", Url)]
    [InlineData(Url, "http://other-host:11434")]
    public async Task A1ce_Conflict_NamesBothAndSavesNothing(string local, string alias)
    {
        Seed("claude", "qwen3.8:27b", "http://seeded-host:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            local_base_url: local, anthropic_base_url: alias, memory_limit_mb: 2048);

        Assert.Contains("local_base_url", result);
        Assert.Contains("anthropic_base_url", result);
        var stored = Stored();
        Assert.Equal(("http://seeded-host:11434", 1024), (stored.LocalBaseUrl, stored.MemoryLimitMb));
    }

    [Fact]
    public async Task A1d_TwoSpellingsOfOneOrigin_AreStoredCanonical()
    {
        Seed("codex", "ollama/qwen3.8:27b");

        await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "http://Inference-Host:11434/", anthropic_base_url: Url);

        Assert.Equal(Url, Stored().LocalBaseUrl);
    }

    [Fact]
    public async Task A1f_AFaultInTheAlias_NamesTheAlias()
    {
        Seed("codex", "ollama/qwen3.8:27b");

        var result = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: Url, anthropic_base_url: Url + "/v1");

        Assert.StartsWith("anthropic_base_url:", result);
        Assert.Null(Stored().LocalBaseUrl);
    }

    // ── provider switch while local (acceptance 11, MCP) ─────────────────────────────────────────

    [Fact]
    public async Task CodexToClaude_WithTheBareTag_Accepted_AlonePrefixedModelRejectedByV6()
    {
        Seed("codex", "ollama/qwen3.8:27b", Url, 131_072);

        var rejected = await Tool().UpdateAgentConfigAsync("agent1", provider: "claude");
        Assert.Contains("selects the codex path", rejected);
        Assert.Equal("codex", Stored().Provider);

        var accepted = await Tool().UpdateAgentConfigAsync("agent1", provider: "claude", model: "qwen3.8:27b");
        Assert.DoesNotContain("Invalid", accepted);
        var stored = Stored();
        Assert.Equal(("claude", "qwen3.8:27b", Url, (int?)131_072),
            (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow));
    }

    [Fact]
    public async Task ClaudeToCodex_WithTheComposedModel_Accepted_AloneBareTagRejectedByC1()
    {
        Seed("claude", "qwen3.8:27b", Url, 131_072);

        var rejected = await Tool().UpdateAgentConfigAsync("agent1", provider: "codex");
        Assert.Contains("ollama/<tag>", rejected);
        Assert.Equal("claude", Stored().Provider);

        var accepted = await Tool().UpdateAgentConfigAsync("agent1", provider: "codex", model: "ollama/qwen3.8:27b");
        Assert.DoesNotContain("Invalid", accepted);
        var stored = Stored();
        Assert.Equal(("codex", "ollama/qwen3.8:27b", Url, (int?)131_072),
            (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow));
    }

    // ── get_agent_config ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("codex", "ollama/qwen3.8:27b", Url, null, "- Local model: on — server " + Url, "- Context window: 131072 tokens\n")]
    [InlineData("codex", "ollama/qwen3.8:27b", null, "CODEX_OSS_BASE_URL", "- Local model: on — server from CODEX_OSS_BASE_URL Env Ref (legacy)", "(ignored: legacy Env Ref; set the local server URL)")]
    [InlineData("codex", "gpt-5", null, null, "- Local model: off", "(ignored: local model off)")]
    [InlineData("claude", "qwen3.8:27b", Url, null, "- Local model: on — server " + Url, "- Context window: 131072 tokens\n")]
    [InlineData("pi", PiTag, PiUrl, null, "- Local model: on — server " + PiUrl + " (Pi)", "- Context window: 131072 tokens\n")]
    public async Task GetAgentConfig_ReportsTheLocalModel(
        string provider, string model, string? url, string? envRef, string localLine, string windowText)
    {
        Seed(provider, model, url, 131_072, envRef);

        var result = (await new GetAgentConfigTool(Scopes()).GetAgentConfigAsync("agent1")).ReplaceLineEndings("\n");

        Assert.Contains(localLine + "\n", result);
        Assert.Contains(windowText, result);
    }

    // ── #386 provider pi ─────────────────────────────────────────────────────────────────────────

    private const string PiUrl = "http://inference-host:8000";
    private const string PiTag = "qwen3.8-flash-next";

    private (string Provider, string Model, string? Url, int? Window, string? Effort) Row()
    {
        var stored = Stored();
        return (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow, stored.Effort);
    }

    [Theory]
    [InlineData("pi", "- Provider: pi\n")]
    [InlineData("codex", "- Provider: codex\n")]
    [InlineData("", "- Provider: (empty: claude)\n")]
    public async Task A25_GetAgentConfig_PrintsTheProvider(string provider, string line)
    {
        Seed(provider, PiTag, PiUrl, 131_072);

        var result = (await new GetAgentConfigTool(Scopes()).GetAgentConfigAsync("agent1")).ReplaceLineEndings("\n");

        Assert.Contains(line, result);
    }

    [Fact]
    public async Task Pi_SetUpByASwitchFromClaudeCloud_IsStoredCanonical()
    {
        Seed("claude", "claude-opus-5-5");

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            provider: "pi", model: PiTag, local_base_url: "HTTP://Inference-Host:8000/", context_window: 131_072, effort: "off");

        Assert.DoesNotContain("Invalid", result);
        Assert.Equal(("pi", PiTag, (string?)PiUrl, (int?)131_072, (string?)"off"), Row());
    }

    // ── A5 over MCP: each rule's text, and nothing saved ─────────────────────────────────────────

    public static TheoryData<string, string> A5Cases => new()
    {
        { "PI1 no url", Fleet.Shared.PiLocalModel.Pi1Text },
        { "PI1 url cleared", Fleet.Shared.PiLocalModel.Pi1Text },
        { "PI2 charset", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI2 ollama/", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI2 lmstudio/", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI2 zai/", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI3 unset", Fleet.Shared.PiLocalModel.Pi3Text },
        { "PI3 65535", Fleet.Shared.PiLocalModel.Pi3Text },
        { "PI4 xhigh", Fleet.Shared.PiLocalModel.Pi4Text },
        { "L1 gemini url", Fleet.Shared.LocalModel.L1Text },
    };

    [Theory]
    [MemberData(nameof(A5Cases))]
    public async Task A5_PiRules_ReturnTheRuleText_AndLeaveTheRowUnchanged(string @case, string expected)
    {
        if (@case == "PI1 no url") Seed("claude", "claude-opus-5-5");
        else if (@case == "L1 gemini url") Seed("gemini", "gemini-2.5-pro");
        else Seed("pi", PiTag, PiUrl, 131_072, effort: "on");
        var before = Row();

        var tool = Tool();
        var result = @case switch
        {
            "PI1 no url" => await tool.UpdateAgentConfigAsync("agent1", provider: "pi", model: PiTag, context_window: 131_072),
            "PI1 url cleared" => await tool.UpdateAgentConfigAsync("agent1", local_base_url: ""),
            "PI2 charset" => await tool.UpdateAgentConfigAsync("agent1", model: "qwen 3.8"),
            "PI2 ollama/" => await tool.UpdateAgentConfigAsync("agent1", model: "ollama/" + PiTag),
            "PI2 lmstudio/" => await tool.UpdateAgentConfigAsync("agent1", model: "lmstudio/" + PiTag),
            "PI2 zai/" => await tool.UpdateAgentConfigAsync("agent1", model: "zai/glm-5.3"),
            "PI3 unset" => await tool.UpdateAgentConfigAsync("agent1", context_window: 0),
            "PI3 65535" => await tool.UpdateAgentConfigAsync("agent1", context_window: 65_535),
            "PI4 xhigh" => await tool.UpdateAgentConfigAsync("agent1", effort: "xhigh"),
            "L1 gemini url" => await tool.UpdateAgentConfigAsync("agent1", local_base_url: PiUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        Assert.Contains(expected, result);
        Assert.Equal(before, Row());
    }

    // ── A6 over MCP: a switch sends the matching values; nothing is rewritten ────────────────────

    [Fact]
    public async Task A6_CodexLocalToPi_WithTagAndEffort_KeepsUrlAndWindow()
    {
        Seed("codex", "ollama/qwen3.8:27b", Url, 131_072, effort: "none");

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "pi", model: "qwen3.8:27b", effort: "off");

        Assert.DoesNotContain("Invalid", result);
        Assert.Equal(("pi", "qwen3.8:27b", (string?)Url, (int?)131_072, (string?)"off"), Row());
    }

    [Fact]
    public async Task A6_CodexLocalToPi_ProviderAlone_IsRefusedByPI2()
    {
        Seed("codex", "ollama/qwen3.8:27b", Url, 131_072, effort: "none");
        var before = Row();

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "pi");

        Assert.Contains(Fleet.Shared.PiLocalModel.Pi2Text, result);
        Assert.Equal(before, Row());
    }

    [Fact]
    public async Task A6_ClaudeLocalWithXhighToPi_ProviderAlone_IsRefusedByPI4()
    {
        Seed("claude", "qwen3.8:27b", Url, 131_072, effort: "xhigh");
        var before = Row();

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "pi");

        Assert.Contains(Fleet.Shared.PiLocalModel.Pi4Text, result);
        Assert.Equal(before, Row());
    }

    // ── Leaving pi over MCP: its on/off effort never reaches a provider that cannot take it ───────

    [Theory]
    [InlineData("on", "claude", "claude-opus-5-5", "", "Effort 'on' applies only to provider pi; clear it or choose an effort for claude.")]
    [InlineData("on", "codex", "ollama/qwen3.8:27b", null, "Effort 'on' applies only to provider pi; clear it or choose an effort for codex.")]
    [InlineData("off", "codex", "ollama/qwen3.8:27b", null, "Effort 'off' applies only to pi and local claude models; clear it or choose an effort for codex.")]
    public async Task LeavingPi_WithItsStoredEffort_IsRefused_AndTheRowIsUnchanged(
        string effort, string provider, string model, string? url, string expected)
    {
        Seed("pi", PiTag, PiUrl, 131_072, effort: effort);
        var before = Row();

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: provider, model: model, local_base_url: url);

        Assert.Contains(expected, result);
        Assert.Equal(before, Row());
    }

    [Fact]
    public async Task LeavingPi_ForClaudeLocalWithOff_IsAccepted()
    {
        Seed("pi", PiTag, PiUrl, 131_072, effort: "off");

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "claude");

        Assert.DoesNotContain("Effort", result);
        Assert.Equal(("claude", PiTag, (string?)PiUrl, (int?)131_072, (string?)"off"), Row());
    }

    // ── #455 A1: pi_image_input over MCP ─────────────────────────────────────────────────────────

    private async Task<string> GetConfigAsync() =>
        (await new GetAgentConfigTool(Scopes()).GetAgentConfigAsync("agent1")).ReplaceLineEndings("\n");

    [Fact]
    public async Task PiImageInput_SetOmitClear_RoundTrips_AndGetShowsIt()
    {
        Seed("pi", PiTag, PiUrl, 131_072);
        Assert.Contains("- Pi image input: False\n", await GetConfigAsync());

        var set = await Tool().UpdateAgentConfigAsync("agent1", pi_image_input: true);

        Assert.Contains("- pi_image_input: False → True", set);
        Assert.True(Stored().PiImageInput);
        Assert.Contains("- Pi image input: True\n", await GetConfigAsync());

        // Omitted: kept. The same value again: no change reported.
        await Tool().UpdateAgentConfigAsync("agent1", memory_limit_mb: 2048);
        Assert.Equal((true, 2048), (Stored().PiImageInput, Stored().MemoryLimitMb));
        Assert.Equal("No changes specified for agent 'agent1'.", await Tool().UpdateAgentConfigAsync("agent1", pi_image_input: true));

        var clear = await Tool().UpdateAgentConfigAsync("agent1", pi_image_input: false);

        Assert.Contains("- pi_image_input: True → False", clear);
        Assert.False(Stored().PiImageInput);
        Assert.Contains("- Pi image input: False\n", await GetConfigAsync());
    }

    [Fact]
    public async Task PiImageInput_SurvivesAProviderSwitch_AndGetMarksItIgnoredOffPi()
    {
        Seed("pi", PiTag, PiUrl, 131_072, piImageInput: true);

        var away = await Tool().UpdateAgentConfigAsync("agent1", provider: "codex", model: $"ollama/{PiTag}");

        Assert.DoesNotContain("pi_image_input", away);
        Assert.Equal(("codex", true), (Stored().Provider, Stored().PiImageInput));
        Assert.Contains("- Pi image input: True (ignored: pi only)\n", await GetConfigAsync());

        await Tool().UpdateAgentConfigAsync("agent1", provider: "pi", model: PiTag);

        Assert.Equal(("pi", true), (Stored().Provider, Stored().PiImageInput));
        Assert.Contains("- Pi image input: True\n", await GetConfigAsync());
    }

    [Fact]
    public async Task PiImageInput_OnANonPiAgent_IsStored()
    {
        Seed("claude", "claude-opus-5-5");

        var result = await Tool().UpdateAgentConfigAsync("agent1", pi_image_input: true);

        Assert.Contains("- pi_image_input: False → True", result);
        Assert.True(Stored().PiImageInput);
    }

    /// <summary>
    /// Calls <c>update_agent_config</c> through the MCP SDK's argument binding, as a client does. An
    /// argument the binding cannot convert throws before the method runs; the SDK's <c>tools/call</c>
    /// handler (0.8.0-preview.1 <c>McpServerImpl</c>) returns that to the client as an <c>isError</c>
    /// result, "An error occurred invoking 'update_agent_config'."
    /// </summary>
    private async Task<CallToolResult> CallThroughMcpAsync(Dictionary<string, string> arguments)
    {
        var method = typeof(UpdateAgentConfigTool).GetMethod(nameof(UpdateAgentConfigTool.UpdateAgentConfigAsync))!;
        var tool = McpServerTool.Create(method, Tool());
        var request = new RequestContext<CallToolRequestParams>(
            Substitute.For<McpServer>(), new JsonRpcRequest { Method = RequestMethods.ToolsCall })
        {
            Params = new CallToolRequestParams
            {
                Name = "update_agent_config",
                Arguments = arguments.ToDictionary(a => a.Key, a => JsonDocument.Parse(a.Value).RootElement.Clone()),
            },
        };
        return await tool.InvokeAsync(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("1")]
    [InlineData("0")]
    public async Task PiImageInput_ANonBoolean_FailsTheBinding_AndTheRowIsUnchanged(string value)
    {
        Seed("pi", PiTag, PiUrl, 131_072, piImageInput: true);
        var before = Row();

        var error = await Assert.ThrowsAsync<JsonException>(() => CallThroughMcpAsync(new()
        {
            ["agent_name"] = "\"agent1\"", ["memory_limit_mb"] = "2048", ["pi_image_input"] = value,
        }));

        Assert.Contains("System.Nullable`1[System.Boolean]", error.Message);
        Assert.Equal(before, Row());
        Assert.Equal((true, 1024), (Stored().PiImageInput, Stored().MemoryLimitMb));
    }

    [Fact]
    public async Task PiImageInput_ABooleanThroughTheMcpBinding_IsStored()
    {
        Seed("pi", PiTag, PiUrl, 131_072);

        var result = await CallThroughMcpAsync(new() { ["agent_name"] = "\"agent1\"", ["pi_image_input"] = "true" });

        Assert.NotEqual(true, result.IsError);
        Assert.True(Stored().PiImageInput);
    }

    [Fact]
    public async Task ListAgentConfigs_ShowsPiImageInput()
    {
        Seed("pi", PiTag, PiUrl, 131_072, piImageInput: true, name: "agent-a");
        Seed("pi", PiTag, PiUrl, 131_072, name: "agent-b");
        Seed("claude", "claude-opus-5-5", piImageInput: true, name: "agent-c");

        var result = (await new ListAgentConfigsTool(Scopes()).ListAgentConfigsAsync()).ReplaceLineEndings("\n");

        Assert.Contains("- **agent-a** (agent-a) — role: test, model: qwen3.8-flash-next, memory: 1024MB, warmup: 60s, pi image input: on, enabled\n", result);
        Assert.Contains("- **agent-b** (agent-b) — role: test, model: qwen3.8-flash-next, memory: 1024MB, warmup: 60s, pi image input: off, enabled\n", result);
        Assert.Contains("- **agent-c** (agent-c) — role: test, model: claude-opus-5-5, memory: 1024MB, warmup: 60s, pi image input: on (ignored: pi only), enabled\n", result);
    }

    [Fact]
    public async Task CreateAgent_PiImageInput_IsStored_AndOmittedIsFalse()
    {
        var create = new CreateAgentTool(Scopes(), new ConfigurationBuilder().Build());

        await create.CreateAgentAsync("agent-a", "developer", PiTag, provider: "pi",
            local_base_url: PiUrl, context_window: 131_072, pi_image_input: true);
        await create.CreateAgentAsync("agent-b", "developer", PiTag, provider: "pi",
            local_base_url: PiUrl, context_window: 131_072);
        await create.CreateAgentAsync("agent-c", "developer", "claude-opus-5-5", pi_image_input: true);

        using var db = new OrchestratorDbContext(_options);
        Assert.Equal(
            [("agent-a", true), ("agent-b", false), ("agent-c", true)],
            db.Agents.AsNoTracking().OrderBy(a => a.Name).Select(a => new { a.Name, a.PiImageInput })
                .AsEnumerable().Select(a => (a.Name, a.PiImageInput)).ToList());
    }
}
