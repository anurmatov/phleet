using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Tests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
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

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// A change that is committed but could not be broadcast to the service that enforces it is not
/// reported as a success: REST answers 503 with <c>saved: true, delivered: false</c>, an MCP tool
/// answers with a sentence that says what to do.
/// </summary>
public sealed class LiveDeliveryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private IServiceScopeFactory _scopes = null!;
    private IConfiguration _config = null!;
    private readonly List<(string RoutingKey, string Body)> _sent = [];
    private bool _brokerDown;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();
        _scopes = _services.GetRequiredService<IServiceScopeFactory>();
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"live-delivery-{Guid.NewGuid():N}.env"),
        }).Build();

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = "sonnet", Provider = "claude",
            MemoryLimitMb = 1024, ContainerName = "agent1", ShortName = "agent1",
            TelegramUsers = [new AgentTelegramUser { UserId = 111 }],
            TelegramGroups = [new AgentTelegramGroup { GroupId = -100 }],
        });
        db.AgentProjectAccess.Add(new AgentProjectAccess { AgentName = "agent1", Project = "alpha", Source = AgentProjectAccessSource.Manual });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private AgentConfigPublisherService Publisher() =>
        new(Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance)
        {
            Send = (routingKey, body, _) =>
            {
                if (_brokerDown) throw new InvalidOperationException("broker unreachable");
                _sent.Add((routingKey, Encoding.UTF8.GetString(body)));
                return Task.CompletedTask;
            },
        };

    private ConfigService Config() =>
        new(_config, Options.Create(new RabbitMqOptions { Host = "", Exchange = "" }), _scopes, NullLogger<ConfigService>.Instance)
        {
            Publish = (keys, _) =>
            {
                if (_brokerDown) throw new InvalidOperationException("broker unreachable");
                _sent.Add(("config.changed", string.Join(",", keys)));
                return Task.CompletedTask;
            },
        };

    private async Task<T> DbAsync<T>(Func<OrchestratorDbContext, Task<T>> read)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    // ── The publisher says whether it published ──────────────────────────────────────────────────

    [Fact]
    public async Task ThePublisher_ReportsADiffItCouldNotHandOver()
    {
        var publisher = Publisher();

        Assert.True(await publisher.PublishAllowlistUpdateAsync("Agent1", [], [111], [], []));
        var (routingKey, body) = Assert.Single(_sent);
        Assert.Equal("agent1", routingKey);
        Assert.Contains("config.update", body);

        _brokerDown = true;
        Assert.False(await publisher.PublishAllowlistUpdateAsync("Agent1", [], [111], [], []));
    }

    [Fact]
    public async Task NothingToSend_AndNoBrokerConfigured_AreNotFailures()
    {
        _brokerDown = true;
        Assert.True(await Publisher().PublishAllowlistUpdateAsync("agent1", [], [], [], []));

        var unconfigured = new AgentConfigPublisherService(
            Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance);
        Assert.False(unconfigured.IsEnabled);
        Assert.True(await unconfigured.PublishAllowlistUpdateAsync("agent1", [], [111], [], []));
    }

    // ── MCP: the allowlist tools ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("remove", 111)]
    [InlineData("add", 222)]
    public async Task TelegramUsersTool_WhenTheAgentCouldNotBeTold_SaysSaved_NotDelivered(string action, long userId)
    {
        _brokerDown = true;
        var tool = Writers.TelegramUsersTool(_scopes, Publisher());

        var answer = await tool.ManageAgentTelegramUsersAsync("agent1", action, userId);

        Assert.StartsWith("Saved, but not delivered:", answer);
        Assert.Contains("Reprovision the agent", answer);
        Assert.DoesNotContain("Removed Telegram user", answer);
        Assert.DoesNotContain("Added Telegram user", answer);
        // Saved is true: the row is as the caller asked.
        var stored = await DbAsync(db => db.AgentTelegramUsers.Select(u => u.UserId).ToListAsync());
        Assert.Equal(action == "remove" ? [] : [111L, 222L], stored.Order().ToList());
    }

    [Theory]
    [InlineData("remove", -100)]
    [InlineData("add", -200)]
    public async Task TelegramGroupsTool_WhenTheAgentCouldNotBeTold_SaysSaved_NotDelivered(string action, long groupId)
    {
        _brokerDown = true;
        var tool = Writers.TelegramGroupsTool(_scopes, Publisher());

        var answer = await tool.ManageAgentTelegramGroupsAsync("agent1", action, groupId);

        Assert.StartsWith("Saved, but not delivered:", answer);
        Assert.Contains("Reprovision the agent", answer);
        var stored = await DbAsync(db => db.AgentTelegramGroups.Select(g => g.GroupId).ToListAsync());
        Assert.Equal(action == "remove" ? [] : [-200L, -100L], stored.Order().ToList());
    }

    [Fact]
    public async Task TheAllowlistTools_WhenTheAgentWasTold_AnswerAsBefore()
    {
        var users = await Writers.TelegramUsersTool(_scopes, Publisher()).ManageAgentTelegramUsersAsync("agent1", "remove", 111);
        var groups = await Writers.TelegramGroupsTool(_scopes, Publisher()).ManageAgentTelegramGroupsAsync("agent1", "remove", -100);

        Assert.Equal("Removed Telegram user 111 from agent 'agent1'.", users);
        Assert.Equal("Removed Telegram group -100 from agent 'agent1'.", groups);
        Assert.Equal(2, _sent.Count);
    }

    // ── MCP: project access ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("remove", "alpha")]
    [InlineData("add", "beta")]
    [InlineData("set_wildcard", null)]
    public async Task ProjectAccessTool_WhenFleetMemoryCouldNotBeTold_SaysSaved_NotDelivered(string action, string? project)
    {
        _brokerDown = true;
        var tool = Writers.ProjectAccessTool(_scopes, Config());

        var answer = await tool.ManageAgentProjectAccessAsync("agent1", action, project);

        Assert.StartsWith("Saved, but not delivered:", answer);
        Assert.Contains("within five minutes", answer);
        var stored = await DbAsync(db => db.AgentProjectAccess.Select(a => a.Project).ToListAsync());
        Assert.Equal(action switch { "remove" => [], "add" => ["alpha", "beta"], _ => ["*", "alpha"] }, stored.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task ProjectAccessTool_WhenFleetMemoryWasTold_AnswersAsBefore()
    {
        var answer = await Writers.ProjectAccessTool(_scopes, Config()).ManageAgentProjectAccessAsync("agent1", "remove", "alpha");

        Assert.Equal("Removed project 'alpha' from agent 'agent1'. Remaining access: (none)", answer);
        Assert.Equal(("config.changed", ConfigService.AclChangedKey), Assert.Single(_sent));
    }

    // ── REST ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<(WebApplication App, HttpClient Client)> StartConfigEndpointsAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddSingleton<IAclChangeNotifier>(Config());
        builder.Services.AddSingleton(Publisher());
        builder.Services.AddSingleton(sp => new SetupService(
            _config, null!, null!, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SetupService>.Instance,
            new EnvFileCredentialsReader(_config, NullLogger<EnvFileCredentialsReader>.Instance)));
        builder.Services.AddAgentWrites();
        var app = builder.Build();
        app.MapAgentConfigEndpoints();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task PutConfig_RemovingAUser_WhenTheAgentCouldNotBeTold_Is503_SavedNotDelivered()
    {
        var (app, client) = await StartConfigEndpointsAsync();
        await using var _ = app;
        _brokerDown = true;

        var response = await client.PutAsJsonAsync("/api/agents/agent1/config", new { telegramUsers = Array.Empty<long>() });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("saved").GetBoolean());
        Assert.False(body.GetProperty("delivered").GetBoolean());
        Assert.Contains("Reprovision the agent", body.GetProperty("error").GetString());
        Assert.Empty(await DbAsync(db => db.AgentTelegramUsers.ToListAsync()));
    }

    [Fact]
    public async Task PutConfig_RemovingAUser_WhenTheAgentWasTold_Is200()
    {
        var (app, client) = await StartConfigEndpointsAsync();
        await using var _ = app;

        var response = await client.PutAsJsonAsync("/api/agents/agent1/config", new { telegramUsers = Array.Empty<long>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_sent, s => s.RoutingKey == "agent1" && s.Body.Contains("removed_user_ids"));
    }

    [Fact]
    public async Task TheRestAnswer_Is503_WithSavedTrue_AndDeliveredFalse()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await LiveDelivery.NotDelivered(LiveDelivery.AllowlistNotDelivered("the removal of user 111", "agent1")).ExecuteAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Response.Body);
        Assert.True(body.GetProperty("saved").GetBoolean());
        Assert.False(body.GetProperty("delivered").GetBoolean());
        Assert.Equal(
            "Saved, but not delivered: the removal of user 111 is stored, and the running agent 'agent1' could not be told, "
            + "so it still uses its previous allowlist. Reprovision the agent to apply it.",
            body.GetProperty("error").GetString());
    }

    /// <summary>
    /// The routes in <c>Program.cs</c> are not hosted by any test, so this reads the source: nowhere
    /// in the orchestrator is the outcome of an allowlist broadcast thrown away, and no route or
    /// tool that writes project access directly uses the broadcast that hides its outcome.
    /// </summary>
    [Fact]
    public void NoCaller_DiscardsTheOutcomeOfABroadcast()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var sources = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Fleet.Orchestrator"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(sources);

        var discarded = new Regex(@"^\s*await\s+\w+\.PublishAllowlistUpdateAsync\(", RegexOptions.Multiline);
        var hidden = new Regex(@"configService\.PublishAclChangedAsync\(");
        var allowlistCalls = 0;
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            allowlistCalls += Regex.Matches(text, @"\.PublishAllowlistUpdateAsync\(").Count;
            Assert.False(discarded.IsMatch(text), $"{Path.GetFileName(file)} awaits PublishAllowlistUpdateAsync and ignores whether it was delivered");
            Assert.False(hidden.IsMatch(text), $"{Path.GetFileName(file)} calls PublishAclChangedAsync on ConfigService; use TryPublishAclChangedAsync and report a false");
        }
        // One per write, now that routes and tools share a writer: the four allowlist changes in
        // AgentAllowlist and the config update in AgentConfigWriter. It was nine while the four
        // routes, the config PUT and the two tools each published for themselves.
        Assert.Equal(5, allowlistCalls);
    }
}
