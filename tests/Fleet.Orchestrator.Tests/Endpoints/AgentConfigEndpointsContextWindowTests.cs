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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// #367 REST patch path over real HTTP: <c>PUT /api/agents/{name}/config</c> rejects values outside
/// 4096..1048576 with the range and without saving, accepts the boundaries, clears on 0, keeps the
/// stored value when the field is omitted, and <c>GET</c> exposes <c>contextWindow</c>.
/// </summary>
/// <remarks>
/// The handlers are the production <see cref="AgentConfigEndpoints"/> lifted from Program.cs, on
/// SQLite — same harness shape as the other endpoint test classes. No allowlist or project payload
/// is sent, so the publisher (disabled: empty RabbitMQ options) and the setup service's
/// credential reader are never reached.
/// </remarks>
public sealed class AgentConfigEndpointsContextWindowTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"ctxwin-{Guid.NewGuid():N}.env"),
        }).Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<IAclChangeNotifier>(new NoopAclChangeNotifier());
        builder.Services.AddSingleton(new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance));
        // Only GetTelegramUserId is reachable from these payloads, and none of them send
        // telegramUsers — the heavy collaborators are never dereferenced.
        builder.Services.AddSingleton(sp => new SetupService(
            config, null!, null!, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SetupService>.Instance, null!));

        _app = builder.Build();
        _app.MapAgentConfigEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(new Agent
            {
                Name = "agent1",
                DisplayName = "agent1",
                Role = "test",
                Model = "qwen3.8:27b-agent",
                Provider = "claude",
                AnthropicBaseUrl = "http://inference-host:11434",
                MemoryLimitMb = 1024,
                ContainerName = "agent1",
            });
            await db.SaveChangesAsync();
        }

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData(4_095)]
    [InlineData(1_048_577)]
    [InlineData(-1)]
    public async Task Put_rejects_out_of_range_values_with_the_range_and_saves_nothing(int tokens)
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { contextWindow = tokens });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("4096..1048576", body.GetProperty("error").GetString());
        Assert.Null(await StoredWindowAsync());
    }

    [Theory]
    [InlineData(4_096)]
    [InlineData(131_072)]
    [InlineData(1_048_576)]
    public async Task Put_accepts_the_valid_range_and_persists(int tokens)
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { contextWindow = tokens });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(tokens, await StoredWindowAsync());
    }

    [Fact]
    public async Task Put_zero_clears_the_value()
    {
        await _client.PutAsJsonAsync("/api/agents/agent1/config", new { contextWindow = 65_536 });

        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { contextWindow = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StoredWindowAsync());
    }

    [Fact]
    public async Task Put_omitting_the_field_keeps_the_stored_value()
    {
        await _client.PutAsJsonAsync("/api/agents/agent1/config", new { contextWindow = 65_536 });

        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { memoryLimitMb = 2048 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(65_536, await StoredWindowAsync());
    }

    [Fact]
    public async Task Get_exposes_contextWindow_null_until_set()
    {
        var before = await _client.GetFromJsonAsync<JsonElement>("/api/agents/agent1/config");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("contextWindow").ValueKind);

        await _client.PutAsJsonAsync("/api/agents/agent1/config", new { contextWindow = 131_072 });

        var after = await _client.GetFromJsonAsync<JsonElement>("/api/agents/agent1/config");
        Assert.Equal(131_072, after.GetProperty("contextWindow").GetInt32());
    }

    private async Task<int?> StoredWindowAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return (await db.Agents.AsNoTracking().SingleAsync(a => a.Name == "agent1")).ContextWindow;
    }

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
