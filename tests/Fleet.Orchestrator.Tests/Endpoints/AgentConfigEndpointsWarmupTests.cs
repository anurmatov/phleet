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
/// #357 REST patch path over real HTTP: <c>PUT /api/agents/{name}/config</c> rejects 9 and 601
/// without saving, accepts the 10..600 boundaries, preserves the stored value when the field is
/// omitted, and <c>GET</c> exposes <c>warmupTimeoutSeconds</c>.
/// </summary>
/// <remarks>
/// The handlers are the production <see cref="AgentConfigEndpoints"/> lifted from Program.cs, on
/// SQLite — same harness shape as the other endpoint test classes. No allowlist or project payload
/// is sent, so the publisher (disabled: empty RabbitMQ options) and the setup service's
/// credential reader are never reached.
/// </remarks>
public sealed class AgentConfigEndpointsWarmupTests : IAsyncLifetime
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
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"warmup-{Guid.NewGuid():N}.env"),
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
                Model = "claude-sonnet-4-6",
                Provider = "claude",
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
    [InlineData(9)]
    [InlineData(601)]
    public async Task Put_rejects_out_of_range_values_without_saving(int seconds)
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config",
            new { warmupTimeoutSeconds = seconds });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("10..600", body.GetProperty("error").GetString());
        Assert.Equal(60, await StoredSecondsAsync());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(180)]
    [InlineData(600)]
    public async Task Put_accepts_the_valid_range_and_persists(int seconds)
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config",
            new { warmupTimeoutSeconds = seconds });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(seconds, await StoredSecondsAsync());
    }

    [Fact]
    public async Task Put_omitting_the_field_keeps_the_stored_value()
    {
        await _client.PutAsJsonAsync("/api/agents/agent1/config", new { warmupTimeoutSeconds = 180 });

        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config",
            new { model = "claude-opus-4-8" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(180, await StoredSecondsAsync());
        Assert.Equal("claude-opus-4-8", await StoredModelAsync());
    }

    [Fact]
    public async Task Get_exposes_warmupTimeoutSeconds()
    {
        await _client.PutAsJsonAsync("/api/agents/agent1/config", new { warmupTimeoutSeconds = 180 });

        var config = await _client.GetFromJsonAsync<JsonElement>("/api/agents/agent1/config");

        Assert.Equal(180, config.GetProperty("warmupTimeoutSeconds").GetInt32());
    }

    private async Task<int> StoredSecondsAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return (await db.Agents.AsNoTracking().SingleAsync(a => a.Name == "agent1")).WarmupTimeoutSeconds;
    }

    private async Task<string> StoredModelAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return (await db.Agents.AsNoTracking().SingleAsync(a => a.Name == "agent1")).Model;
    }

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
