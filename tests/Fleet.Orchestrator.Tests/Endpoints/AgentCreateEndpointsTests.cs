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
        _client.PostAsJsonAsync("/api/agents", new { name = "agent1", role = "co-cto", model = "model-x", provider });

    private Task<HttpResponseMessage> PostLocalAsync(
        string provider, string model, string? localBaseUrl, int? contextWindow, string? effort = null) =>
        _client.PostAsJsonAsync("/api/agents",
            new { name = "agent1", role = "co-cto", model, provider, localBaseUrl, contextWindow, effort });

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    // ── #386 H1: create checks the local server URL and context like the PUT, before any write ──

    [Theory]
    [InlineData("gemini", "gemini-2.5-pro", "http://inference-host:8000", null, null, "Local model runs on claude or codex; clear the local server URL first.")]
    public async Task H1_ALocalModelFault_Is400WithTheRuleText_AndNoRowIsWritten(
        string provider, string model, string? url, int? context, string? effort, string expected)
    {
        var response = await PostLocalAsync(provider, model, url, context, effort);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await ErrorAsync(response));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Fact]
    public async Task H1_AUrlWithAPath_IsRefusedByL2_AndNoRowIsWritten()
    {
        var response = await PostLocalAsync("claude", "qwen3.8-flash-next", "http://inference-host:8000/v1", 131072);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("has a path", await ErrorAsync(response));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Theory]
    [InlineData("pi")]
    [InlineData("foo")]
    [InlineData("Claude")]
    [InlineData("pi ")]
    public async Task A30_AnUnknownProvider_Is400PV1_AndNoRowIsWritten(string provider)
    {
        var response = await PostAsync(provider);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Equal($"Unknown provider '{provider}'; use claude, codex or gemini.", error);
        Assert.Equal(0, await AgentCountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task AKnownOrEmptyProvider_PassesPV1(string? provider)
    {
        var response = await PostAsync(provider);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("telegram_not_configured",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(0, await AgentCountAsync());
    }


    // ── #460: harnessExtra on create, the same allowlist as the PUT, before any write ─────────────

    [Theory]
    [InlineData("codex", "gpt-5.5", "notify = [\"sh\"]", "invalid_line")]
    [InlineData("claude", "claude-opus-5-5", "--dangerously-skip-permissions", "invalid_line")]
    [InlineData("gemini", "gemini-2.5-pro", "# anything", "unsupported_provider")]
    public async Task HarnessExtra_ARefusedValue_Is400_AndNoRowIsWritten(string provider, string model, string extra, string code)
    {
        var response = await _client.PostAsJsonAsync("/api/agents",
            new { name = "agent1", role = "co-cto", model, provider, harnessExtra = extra });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((code, "harnessExtra"), (body.GetProperty("code").GetString(), body.GetProperty("field").GetString()));
        Assert.Equal(0, await AgentCountAsync());
    }

    [Fact]
    public async Task HarnessExtra_IsStoredOnCreate_Verbatim()
    {
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

        const string extra = "# cloud agent without web search\n--disallowedTools WebSearch\n";
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "agent1", role = "developer", model = "claude-opus-5-5", harnessExtra = extra, provision = false,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var check = _app.Services.CreateAsyncScope();
        Assert.Equal(extra, (await check.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents
            .AsNoTracking().SingleAsync(a => a.Name == "agent1")).HarnessExtra);
    }

    /// <summary>Fails the test if anything reaches Docker.</summary>
    private sealed class RefusingDockerApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"unexpected Docker call {request.Method} {request.RequestUri}");
    }
}
