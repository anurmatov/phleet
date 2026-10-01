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

/// <summary>Real HTTP + SQLite: a missing or invalid key blocks only false-to-true.</summary>
public sealed class AgentConfigEndpointsJournalTests : IAsyncLifetime
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
        var journalConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();
        builder.Services.AddSingleton(new JournalTokenService(journalConfig));
        builder.Services.AddSingleton(new JournalTokenServiceConfig(journalConfig));
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
    [InlineData(false, true, false, "journal_key_missing")]
    [InlineData(false, true, true, "journal_key_invalid")]
    [InlineData(true, true, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(false, false, false, null)]
    public async Task Put_journal_transition_enforces_key_only_when_enabling(
        bool before, bool requested, bool invalidKey, string? fault)
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = await db.Agents.SingleAsync();
            agent.JournalEnabled = before;
            agent.ShowStats = true;
            await db.SaveChangesAsync();
        }
        var config = (IConfigurationRoot)_app.Services.GetRequiredService<JournalTokenServiceConfig>().Config;
        config["Journal:TokenKey"] = invalidKey ? "invalid" : null;
        var response = await _client.PutAsJsonAsync("/api/agents/agent1/config", new { journalEnabled = requested, showStats = false });
        Assert.Equal(fault is null ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        if (fault is not null)
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("journal_not_configured", error.GetProperty("error").GetString());
            Assert.Equal(fault, error.GetProperty("detail").GetString());
        }
        await using var readScope = _app.Services.CreateAsyncScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Agents.AsNoTracking().SingleAsync();
        Assert.Equal(fault is null ? requested : before, stored.JournalEnabled);
        Assert.Equal(fault is not null, stored.ShowStats);
    }

    private sealed record JournalTokenServiceConfig(IConfiguration Config);

    private sealed class NoopAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
