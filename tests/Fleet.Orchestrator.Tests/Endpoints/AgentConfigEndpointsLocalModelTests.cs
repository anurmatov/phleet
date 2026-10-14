using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
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
/// #382 REST path over real HTTP: <c>localBaseUrl</c> and its alias <c>anthropicBaseUrl</c> (A1),
/// codex local mode, the L1/L2/C1 rules, and a provider switch while local, one case per direction.
/// #386: pi's rules (A5), switches into pi (A6) and PV1 on the PUT (A30). A field of the wrong JSON
/// type is a 400. Every rejected request is checked against the stored row, not the response alone.
/// </summary>
/// <remarks>Same harness as <see cref="AgentConfigEndpointsContextWindowTests"/>.</remarks>
public sealed class AgentConfigEndpointsLocalModelTests : IAsyncLifetime
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
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"localmodel-{Guid.NewGuid():N}.env"),
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

    private async Task SeedAsync(string provider, string model, string? url = null, int? window = null,
        string? effort = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = provider,
            LocalBaseUrl = url, ContextWindow = window, Effort = effort, MemoryLimitMb = 1024, ContainerName = "agent1",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Agent> StoredAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Agents.AsNoTracking().SingleAsync(a => a.Name == "agent1");
    }

    private async Task<string> BadRequestErrorAsync(object body)
    {
        var response = await _client.PutAsJsonAsync(Put, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
    }

    // ── Acceptance 8 ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codex_PutLocalBaseUrl_GetReturnsTheCanonicalValueUnderBothNames()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b");

        var response = await _client.PutAsJsonAsync(Put, new { localBaseUrl = "HTTP://Inference-Host:11434/" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var get = await _client.GetFromJsonAsync<JsonElement>(Put);
        Assert.Equal(Url, get.GetProperty("localBaseUrl").GetString());
        Assert.Equal(Url, get.GetProperty("anthropicBaseUrl").GetString());
    }

    [Fact]
    public async Task Claude_PutAnthropicBaseUrl_BehavesAsBefore()
    {
        await SeedAsync("claude", "qwen3.8:27b");

        var response = await _client.PutAsJsonAsync(Put, new { anthropicBaseUrl = Url + "/" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Url, (await StoredAsync()).LocalBaseUrl);
    }

    // ── Acceptance 9: A1 ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A1a_OnlyLocalBaseUrl_IsStored()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b");

        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync(Put, new { localBaseUrl = Url })).StatusCode);
        Assert.Equal(Url, (await StoredAsync()).LocalBaseUrl);
    }

    [Fact]
    public async Task A1b_BothEmpty_Clears()
    {
        await SeedAsync("claude", "qwen3.8:27b", Url);

        var response = await _client.PutAsJsonAsync(Put,
            new { localBaseUrl = "", anthropicBaseUrl = "", model = "claude-sonnet-5" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await StoredAsync()).LocalBaseUrl);
    }

    [Theory]
    [InlineData("", Url)]
    [InlineData(Url, "")]
    [InlineData(Url, "http://other-host:11434")]
    public async Task A1ce_EmptyAgainstAValue_OrTwoHosts_IsAConflictNamingBoth(string local, string alias)
    {
        await SeedAsync("claude", "qwen3.8:27b", "http://seeded-host:11434");

        var error = await BadRequestErrorAsync(new { localBaseUrl = local, anthropicBaseUrl = alias });

        Assert.Contains("localBaseUrl", error);
        Assert.Contains("anthropicBaseUrl", error);
        Assert.Equal("http://seeded-host:11434", (await StoredAsync()).LocalBaseUrl);
    }

    [Fact]
    public async Task A1d_TwoSpellingsOfOneOrigin_AreStoredCanonical()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b");

        var response = await _client.PutAsJsonAsync(Put,
            new { localBaseUrl = "http://Inference-Host:11434/", anthropicBaseUrl = Url });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Url, (await StoredAsync()).LocalBaseUrl);
    }

    [Fact]
    public async Task A1f_AFaultInTheAlias_NamesTheAlias()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b");

        var error = await BadRequestErrorAsync(new { localBaseUrl = Url, anthropicBaseUrl = Url + "/v1" });

        Assert.StartsWith("anthropicBaseUrl:", error);
        Assert.Null((await StoredAsync()).LocalBaseUrl);
    }

    // ── Acceptance 10: validation ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("codex", "qwen3.8:27b", Url, "ollama/<tag>")]
    [InlineData("codex", "zai/glm-5.3", Url, "ollama/<tag>")]
    [InlineData("gemini", "gemini-2.5-pro", Url, "Local model runs on claude, codex or pi")]
    [InlineData("codex", "ollama/qwen3.8:27b", Url + "/v1", "Fleet adds")]
    public async Task Validation_RejectsWithTheRuleText_AndSavesNothing(
        string provider, string model, string url, string expected)
    {
        await SeedAsync(provider, model);

        var error = await BadRequestErrorAsync(new { localBaseUrl = url });

        Assert.Contains(expected, error);
        Assert.Null((await StoredAsync()).LocalBaseUrl);
    }

    [Fact]
    public async Task Validation_ContextWindowRangeIsUnchanged()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b", Url);

        var error = await BadRequestErrorAsync(new { contextWindow = 4_095 });

        Assert.Contains("4096..1048576", error);
    }

    // ── Acceptance 11: provider switch while local ───────────────────────────────────────────────

    [Fact]
    public async Task CodexToClaude_WithTheBareTag_KeepsUrlAndWindow()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b", Url, 131_072);

        var response = await _client.PutAsJsonAsync(Put, new { provider = "claude", model = "qwen3.8:27b" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync();
        Assert.Equal(("claude", "qwen3.8:27b", Url, (int?)131_072),
            (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow));
    }

    [Fact]
    public async Task CodexToClaude_KeepingThePrefixedModel_IsRejectedByV6()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b", Url, 131_072);

        var error = await BadRequestErrorAsync(new { provider = "claude" });

        Assert.Contains("selects the codex path", error);
        var stored = await StoredAsync();
        Assert.Equal(("codex", "ollama/qwen3.8:27b"), (stored.Provider, stored.Model));
    }

    [Fact]
    public async Task ClaudeToCodex_WithTheComposedModel_KeepsUrlAndWindow()
    {
        await SeedAsync("claude", "qwen3.8:27b", Url, 131_072);

        var response = await _client.PutAsJsonAsync(Put, new { provider = "codex", model = "ollama/qwen3.8:27b" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync();
        Assert.Equal(("codex", "ollama/qwen3.8:27b", Url, (int?)131_072),
            (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow));
    }

    [Fact]
    public async Task ClaudeToCodex_KeepingTheBareTag_IsRejectedByC1()
    {
        await SeedAsync("claude", "qwen3.8:27b", Url, 131_072);

        var error = await BadRequestErrorAsync(new { provider = "codex" });

        Assert.Contains("ollama/<tag>", error);
        var stored = await StoredAsync();
        Assert.Equal(("claude", "qwen3.8:27b"), (stored.Provider, stored.Model));
    }

    // ── #386 provider pi ─────────────────────────────────────────────────────────────────────────

    private const string PiUrl = "http://inference-host:8000";
    private const string PiTag = "qwen3.8-flash-next";

    private async Task<(string Provider, string Model, string? Url, int? Window, string? Effort, int Memory)> RowAsync()
    {
        var stored = await StoredAsync();
        return (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow, stored.Effort, stored.MemoryLimitMb);
    }

    [Fact]
    public async Task Pi_SetUpByASwitchFromClaudeCloud_IsStoredCanonical()
    {
        await SeedAsync("claude", "claude-opus-5-5");

        var response = await _client.PutAsJsonAsync(Put, new
        {
            provider = "pi", model = PiTag, localBaseUrl = "HTTP://Inference-Host:8000/", contextWindow = 131_072, effort = "on",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("pi", PiTag, (string?)PiUrl, (int?)131_072, (string?)"on", 1024), await RowAsync());
    }

    // ── A5 over REST: each rule's text, and nothing saved ────────────────────────────────────────

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
    public async Task A5_PiRules_Return400WithTheRuleText_AndLeaveTheRowUnchanged(string @case, string expected)
    {
        if (@case == "PI1 no url") await SeedAsync("claude", "claude-opus-5-5");
        else if (@case == "L1 gemini url") await SeedAsync("gemini", "gemini-2.5-pro");
        else await SeedAsync("pi", PiTag, PiUrl, 131_072, effort: "on");
        var before = await RowAsync();

        object body = @case switch
        {
            "PI1 no url" => new { provider = "pi", model = PiTag, contextWindow = 131_072, memoryLimitMb = 2048 },
            "PI1 url cleared" => new { localBaseUrl = "", memoryLimitMb = 2048 },
            "PI2 charset" => new { model = "qwen 3.8", memoryLimitMb = 2048 },
            "PI2 ollama/" => new { model = "ollama/" + PiTag, memoryLimitMb = 2048 },
            "PI2 lmstudio/" => new { model = "lmstudio/" + PiTag, memoryLimitMb = 2048 },
            "PI2 zai/" => new { model = "zai/glm-5.3", memoryLimitMb = 2048 },
            "PI3 unset" => new { contextWindow = 0, memoryLimitMb = 2048 },
            "PI3 65535" => new { contextWindow = 65_535, memoryLimitMb = 2048 },
            "PI4 xhigh" => new { effort = "xhigh", memoryLimitMb = 2048 },
            "L1 gemini url" => new { localBaseUrl = PiUrl, memoryLimitMb = 2048 },
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        Assert.Equal(expected, await BadRequestErrorAsync(body));
        Assert.Equal(before, await RowAsync());
    }

    // ── A6 over REST ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A6_CodexLocalToPi_WithTagAndEffort_Returns200_UrlAndWindowUnchanged()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b", Url, 131_072, effort: "none");

        var response = await _client.PutAsJsonAsync(Put, new { provider = "pi", model = "qwen3.8:27b", effort = "off" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("pi", "qwen3.8:27b", (string?)Url, (int?)131_072, (string?)"off", 1024), await RowAsync());
    }

    [Fact]
    public async Task A6_CodexLocalToPi_ProviderAlone_Is400PI2()
    {
        await SeedAsync("codex", "ollama/qwen3.8:27b", Url, 131_072, effort: "none");
        var before = await RowAsync();

        Assert.Equal(Fleet.Shared.PiLocalModel.Pi2Text, await BadRequestErrorAsync(new { provider = "pi" }));
        Assert.Equal(before, await RowAsync());
    }

    [Fact]
    public async Task A6_ClaudeLocalWithXhighToPi_ProviderAlone_Is400PI4()
    {
        await SeedAsync("claude", "qwen3.8:27b", Url, 131_072, effort: "xhigh");
        var before = await RowAsync();

        Assert.Equal(Fleet.Shared.PiLocalModel.Pi4Text, await BadRequestErrorAsync(new { provider = "pi" }));
        Assert.Equal(before, await RowAsync());
    }

    // ── Leaving pi over REST: its on/off effort never reaches a provider that cannot take it ──────

    public static TheoryData<string, object, string> LeavingPiWithItsEffort => new()
    {
        { "on", new { provider = "claude", model = "claude-opus-5-5", localBaseUrl = "" },
            "Effort 'on' applies only to provider pi; clear it or choose an effort for claude." },
        { "on", new { provider = "codex", model = "ollama/qwen3.8:27b" },
            "Effort 'on' applies only to provider pi; clear it or choose an effort for codex." },
        { "off", new { provider = "codex", model = "ollama/qwen3.8:27b" },
            "Effort 'off' applies only to pi and local claude models; clear it or choose an effort for codex." },
        { "on", new { provider = "claude", model = PiTag },
            "Effort on a local Claude model must be empty (model default, sent as xhigh), off, low, medium or xhigh." },
    };

    [Theory]
    [MemberData(nameof(LeavingPiWithItsEffort))]
    public async Task LeavingPi_WithItsStoredEffort_Is400_AndTheRowIsUnchanged(string effort, object body, string expected)
    {
        await SeedAsync("pi", PiTag, PiUrl, 131_072, effort: effort);
        var before = await RowAsync();

        Assert.Equal(expected, await BadRequestErrorAsync(body));
        Assert.Equal(before, await RowAsync());
    }

    [Fact]
    public async Task LeavingPi_ForClaudeLocalWithOff_Returns200()
    {
        await SeedAsync("pi", PiTag, PiUrl, 131_072, effort: "off");

        var response = await _client.PutAsJsonAsync(Put, new { provider = "claude" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("claude", PiTag, (string?)PiUrl, (int?)131_072, (string?)"off", 1024), await RowAsync());
    }

    [Fact]
    public async Task LeavingPi_WithTheEffortCleared_Returns200()
    {
        await SeedAsync("pi", PiTag, PiUrl, 131_072, effort: "on");

        var response = await _client.PutAsJsonAsync(Put, new { provider = "codex", model = $"ollama/{PiTag}", effort = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("codex", $"ollama/{PiTag}", (string?)PiUrl, (int?)131_072, (string?)null, 1024), await RowAsync());
    }

    // ── A30: PV1 over REST PUT ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("Pi")]
    public async Task A30_AnUnknownProvider_Is400PV1_AndTheRowIsUnchanged(string provider)
    {
        await SeedAsync("claude", "claude-opus-5-5");
        var before = await RowAsync();

        var error = await BadRequestErrorAsync(new { provider, memoryLimitMb = 2048 });

        Assert.Equal($"Unknown provider '{provider}'; use claude, codex, gemini or pi.", error);
        Assert.Equal(before, await RowAsync());
    }

    [Fact]
    public async Task A30_ARowAlreadyHoldingAnUnknownProvider_Is400PV1_UntilTheProviderIsFixed()
    {
        await SeedAsync("foo", "claude-opus-5-5");

        Assert.Equal("Unknown provider 'foo'; use claude, codex, gemini or pi.",
            await BadRequestErrorAsync(new { memoryLimitMb = 2048 }));
        Assert.Equal(1024, (await StoredAsync()).MemoryLimitMb);

        var response = await _client.PutAsJsonAsync(Put, new { provider = "", memoryLimitMb = 2048 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("", 2048), ((await StoredAsync()).Provider, (await StoredAsync()).MemoryLimitMb));
    }

    // ── A field of the wrong JSON type ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("{}")]
    public async Task ANonBoolean_Is400_AndTheRowIsUnchanged(string value)
    {
        await SeedAsync("pi", PiTag, PiUrl, 131_072);
        var before = await RowAsync();

        var response = await _client.PutAsync(Put, new StringContent(
            $$"""{"memoryLimitMb":2048,"mountDockerSock":{{value}}}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.StartsWith("Invalid request body at '$.mountDockerSock'", error);
        Assert.Equal(before, await RowAsync());
    }

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
