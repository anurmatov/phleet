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
using NSubstitute;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// #386 A30 over real HTTP: <c>POST /api/agents</c> refuses an unknown provider with the PV1 text
/// and writes no row. A known or empty provider gets past PV1 to the next gate — Telegram is not
/// configured in this host, so the request then stops with 409 and still writes nothing.
/// #455: a non-boolean <c>piImageInput</c> is a 400 with no row, and a boolean is stored.
/// </summary>
/// <remarks>
/// The handler is the production <see cref="AgentCreateEndpoints"/> lifted from <c>Program.cs</c>.
/// Its services are real instances over SQLite; none of them is reached before PV1 or the Telegram
/// gate, so no Docker, RabbitMQ or Temporal call is ever made. The one test that configures
/// Telegram creates a non-first, non-co-cto agent with <c>provision: false</c>, which reaches none
/// of them either.
/// </remarks>
public sealed class AgentCreateEndpointsTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        // A .env that does not exist: Telegram reads as not configured.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"create-{Guid.NewGuid():N}.env"),
            ["Provisioning:BaseDir"] = Path.Combine(Path.GetTempPath(), $"create-{Guid.NewGuid():N}"),
        });
        IConfiguration config = builder.Configuration;
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton(new DockerService(
            NullLogger<DockerService>.Instance, new HttpClient(new RefusingDockerApi()) { BaseAddress = new Uri("http://localhost") }));
        builder.Services.AddSingleton(new JournalTokenService(config));
        builder.Services.AddSingleton<ContainerProvisioningService>();
        builder.Services.AddSingleton(new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance));
        builder.Services.AddSingleton(sp => new SetupService(
            config, null!, null!, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SetupService>.Instance, Substitute.For<ICredentialsReader>()));
        builder.Services.AddSingleton(sp => new ConfigService(
            config, Options.Create(new RabbitMqOptions()), sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConfigService>.Instance));
        builder.Services.AddSingleton(new TemporalClientRegistry(
            Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance));

        _app = builder.Build();
        _app.MapAgentCreateEndpoints();

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

    private async Task<int> AgentCountAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.CountAsync();
    }

    private Task<HttpResponseMessage> PostAsync(string? provider) =>
        provider == "pi"
            ? PostLocalAsync("pi", "qwen3.8-flash-next", "http://inference-host:8000", 131072)
            : _client.PostAsJsonAsync("/api/agents", new { name = "agent1", role = "co-cto", model = "model-x", provider });

    private Task<HttpResponseMessage> PostLocalAsync(
        string provider, string model, string? localBaseUrl, int? contextWindow, string? effort = null) =>
        _client.PostAsJsonAsync("/api/agents",
            new { name = "agent1", role = "co-cto", model, provider, localBaseUrl, contextWindow, effort });

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    // ── #386 H1: create checks the local server URL and context like the PUT, before any write ──

    [Theory]
    [InlineData("pi", "qwen3.8-flash-next", null, 131072, null, "Pi runs only on a local server; set the local server URL.")]
    [InlineData("pi", "qwen3.8-flash-next", "http://inference-host:8000", null, null, "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.")]
    [InlineData("pi", "qwen3.8-flash-next", "http://inference-host:8000", 65535, null, "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.")]
    [InlineData("pi", "ollama/qwen3.8-flash-next", "http://inference-host:8000", 131072, null, "Pi takes the bare model tag the server serves (no ollama/ or lmstudio/ prefix).")]
    [InlineData("pi", "qwen3.8-flash-next", "http://inference-host:8000", 131072, "xhigh", "Pi effort is off or on.")]
    [InlineData("gemini", "gemini-2.5-pro", "http://inference-host:8000", null, null, "Local model runs on claude, codex or pi; clear the local server URL first.")]
    public async Task H1_ALocalModelFault_Is400WithTheRuleText_AndNoRowIsWritten(
        string provider, string model, string? url, int? context, string? effort, string expected)
    {
        var response = await PostLocalAsync(provider, model, url, context, effort);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await ErrorAsync(response));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Theory]
    [InlineData("claude", "claude-opus-5-5", "off", "Effort 'off' applies only to local Claude models; clear it or choose low, medium, high, xhigh or max.")]
    [InlineData("claude", "claude-opus-5-5", "on", "Effort 'on' applies only to provider pi; clear it or choose an effort for claude.")]
    [InlineData("codex", "gpt-5", "off", "Effort 'off' applies only to pi and local claude models; clear it or choose an effort for codex.")]
    public async Task ACloudCreate_WithALocalOrPiOnlyEffort_Is400_LikeThePut(string provider, string model, string effort, string expected)
    {
        var response = await PostLocalAsync(provider, model, null, null, effort);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await ErrorAsync(response));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Fact]
    public async Task H1_AUrlWithAPath_IsRefusedByL2_AndNoRowIsWritten()
    {
        var response = await PostLocalAsync("pi", "qwen3.8-flash-next", "http://inference-host:8000/v1", 131072);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has a path", await ErrorAsync(response));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Theory]
    [InlineData("off")]
    [InlineData("on")]
    [InlineData(null)]
    public async Task H1_ACompletePiCreate_PassesTheLocalModelChecks(string? effort)
    {
        // Past every request check: the next refusal is the environment's (Telegram not configured).
        var response = await PostLocalAsync("pi", "qwen3.8-flash-next", "http://inference-host:8000", 131072, effort);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("telegram_not_configured", await ErrorAsync(response));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Claude")]
    [InlineData("pi ")]
    public async Task A30_AnUnknownProvider_Is400PV1_AndNoRowIsWritten(string provider)
    {
        var response = await PostAsync(provider);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Equal($"Unknown provider '{provider}'; use claude, codex, gemini or pi.", error);
        Assert.Equal(0, await AgentCountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("pi")]
    public async Task AKnownOrEmptyProvider_PassesPV1(string? provider)
    {
        var response = await PostAsync(provider);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("telegram_not_configured",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(0, await AgentCountAsync());
    }

    // ── #455 A1: piImageInput on create ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public async Task PiImageInput_ANonBoolean_Is400_AndNoRowIsWritten(string value)
    {
        var response = await _client.PostAsync("/api/agents", new StringContent(
            $$"""{"name":"agent1","role":"co-cto","model":"qwen3.8-flash-next","provider":"pi","localBaseUrl":"http://inference-host:8000","contextWindow":131072,"piImageInput":{{value}}}""",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Invalid request body at '$.piImageInput'", await ErrorAsync(response));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Fact]
    public async Task PiImageInput_IsStoredOnCreate_AndOmittedIsFalse()
    {
        // Telegram configured (placeholder tokens in the token format), and an existing co-cto so the
        // new rows skip the first-agent and FLEET_CTO_AGENT paths.
        var token = "123456:" + new string('A', 35);
        await File.WriteAllTextAsync(_app.Configuration["Provisioning:EnvFilePath"]!,
            $"TELEGRAM_CTO_BOT_TOKEN={token}\nTELEGRAM_NOTIFIER_BOT_TOKEN={token}\n");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Agent
            {
                Name = "agent0", DisplayName = "agent0", Role = "co-cto", Model = "claude-opus-5-5",
                MemoryLimitMb = 1024, ContainerName = "fleet-agent0",
            });
            await db.SaveChangesAsync();
        }

        var pi = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "agent1", role = "developer", model = "qwen3.8-flash-next", provider = "pi",
            localBaseUrl = "http://inference-host:8000", contextWindow = 131072, piImageInput = true, provision = false,
        });
        var claude = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "agent2", role = "developer", model = "claude-opus-5-5", provision = false,
        });

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (pi.StatusCode, claude.StatusCode));
        await using var check = _app.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AsNoTracking()
            .Where(a => a.Name != "agent0").OrderBy(a => a.Name).Select(a => new { a.Name, a.PiImageInput }).ToListAsync();
        Assert.Equal([("agent1", true), ("agent2", false)], stored.Select(a => (a.Name, a.PiImageInput)).ToList());
    }

    /// <summary>Fails the test if anything reaches Docker.</summary>
    private sealed class RefusingDockerApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"unexpected Docker call {request.Method} {request.RequestUri}");
    }
}
