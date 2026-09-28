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
/// Every rejected request is checked against the stored row, not the response alone.
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

    private async Task SeedAsync(string provider, string model, string? url = null, int? window = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = provider,
            LocalBaseUrl = url, ContextWindow = window, MemoryLimitMb = 1024, ContainerName = "agent1",
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
    [InlineData("gemini", "gemini-2.5-pro", Url, "Local model runs on claude or codex")]
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

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
