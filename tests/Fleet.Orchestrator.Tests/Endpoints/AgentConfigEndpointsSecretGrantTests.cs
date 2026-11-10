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
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// Saving an agent's configuration replaces its Env Ref list wholesale, and the dashboard sends the
/// list on every save. A key that stays must keep its placeholder hash, or an unrelated edit would
/// cut a running agent off from its bound secrets; a key that goes must lose it at once.
/// </summary>
public sealed class AgentConfigEndpointsSecretGrantTests : IAsyncLifetime
{
    private static readonly string HashA = SecretPlaceholders.Hash(SecretPlaceholders.Create());
    private static readonly string HashB = SecretPlaceholders.Hash(SecretPlaceholders.Create());

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"secret-grants-{Guid.NewGuid():N}.env"),
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
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Agents.Add(new Agent
            {
                Name = "agent1", DisplayName = "agent1", Role = "test", Model = "opus", Provider = "claude",
                MemoryLimitMb = 1024, ContainerName = "agent1",
                EnvRefs =
                [
                    new AgentEnvRef { EnvKeyName = "EXAMPLE_API_KEY", PlaceholderSha256 = HashA },
                    new AgentEnvRef { EnvKeyName = "OTHER_API_KEY", PlaceholderSha256 = HashB },
                    new AgentEnvRef { EnvKeyName = "PLAIN_SETTING" },
                ],
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

    private async Task<Dictionary<string, string?>> StoredAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.AgentEnvRefs.AsNoTracking().ToDictionaryAsync(r => r.EnvKeyName, r => r.PlaceholderSha256);
    }

    [Fact]
    public async Task Put_repeating_the_same_env_refs_keeps_every_placeholder_hash()
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            memoryLimitMb = 2048,
            envRefs = new[] { "EXAMPLE_API_KEY", "OTHER_API_KEY", "PLAIN_SETTING" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync();
        Assert.Equal(HashA, stored["EXAMPLE_API_KEY"]);
        Assert.Equal(HashB, stored["OTHER_API_KEY"]);
        Assert.Null(stored["PLAIN_SETTING"]);
    }

    [Fact]
    public async Task Put_dropping_a_key_removes_its_hash_and_keeps_the_others()
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new
        {
            envRefs = new[] { "OTHER_API_KEY", "NEW_API_KEY" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync();
        Assert.Equal(["NEW_API_KEY", "OTHER_API_KEY"], stored.Keys.Order());
        Assert.Equal(HashB, stored["OTHER_API_KEY"]);
        // A key attached now has no placeholder until the agent is provisioned.
        Assert.Null(stored["NEW_API_KEY"]);
    }

    [Fact]
    public async Task Put_without_env_refs_leaves_them_untouched()
    {
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { memoryLimitMb = 2048 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HashA, (await StoredAsync())["EXAMPLE_API_KEY"]);
    }

    [Fact]
    public async Task Get_never_returns_a_placeholder_hash()
    {
        var body = await _client.GetStringAsync("/api/agents/agent1/config");

        Assert.Contains("EXAMPLE_API_KEY", body);
        Assert.DoesNotContain(HashA, body);
        Assert.DoesNotContain("placeholder", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
