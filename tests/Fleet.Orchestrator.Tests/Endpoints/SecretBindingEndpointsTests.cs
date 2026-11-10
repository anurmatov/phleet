using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The real <c>/api/secret-bindings</c> handlers behind the real bearer rule, with the same save
/// interceptor and state writer <c>Program.cs</c> registers. All values are synthetic.
/// </summary>
public class SecretBindingEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-bearer-token";
    private const string Key = "EXAMPLE_API_KEY";
    private const string Value = "synthetic-value-c41e9d";

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"secret-bindings-{Guid.NewGuid():N}");
    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private string EnvFile => Path.Combine(_baseDir, ".env");
    private string StateFile => Path.Combine(_baseDir, SecretProxyState.StateDirectory, SecretProxyState.FileName);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_baseDir);
        File.WriteAllText(EnvFile, $"{Key}={Value}\nGITHUB_APP_ID=424242\n");
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:BaseDir"] = _baseDir,
            ["Provisioning:EnvFilePath"] = EnvFile,
            ["SecretProxy:Enabled"] = "true",
        });
        builder.Services.AddSingleton(sp => SecretProxySettings.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<SecretProxyStateWriter>();
        builder.Services.AddSingleton<SecretStateSaveInterceptor>();
        builder.Services.AddDbContext<OrchestratorDbContext>((sp, o) =>
            o.UseSqlite(_connection).AddInterceptors(sp.GetRequiredService<SecretStateSaveInterceptor>()));

        _app = builder.Build();

        // The production gate, called rather than copied.
        _app.Use(async (context, next) =>
        {
            if (!OrchestratorAuth.RequiresBearerToken(context.Request.Method, context.Request.Path.Value ?? ""))
            {
                await next(context);
                return;
            }

            var header = context.Request.Headers.Authorization.FirstOrDefault();
            var token = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? header["Bearer ".Length..].Trim()
                : null;
            if (token != Token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }

            await next(context);
        });

        _app.MapSecretBindingEndpoints();

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
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, object? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    private Task<HttpResponseMessage> PutAsync(string key, object payload) =>
        _client.SendAsync(Authed(HttpMethod.Put, $"/api/secret-bindings/{key}", payload));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<T> DbAsync<T>(Func<OrchestratorDbContext, Task<T>> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    private Task SeedAgentAsync(string name, string? placeholderHash = null, params string[] envRefs) => DbAsync(async db =>
    {
        db.Agents.Add(new Agent
        {
            Name = name, DisplayName = name, Role = "developer", Provider = "claude", Model = "opus",
            ContainerName = $"fleet-{name}",
            EnvRefs = [.. envRefs.Select(k => new AgentEnvRef { EnvKeyName = k, PlaceholderSha256 = placeholderHash })],
        });
        return await db.SaveChangesAsync();
    });

    private SecretProxyState State() => SecretProxyState.TryParse(File.ReadAllText(StateFile), out _)!;

    // ── Who may write ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_NeedTheOperatorToken_ReadsDoNot()
    {
        using var put = await _client.PutAsJsonAsync($"/api/secret-bindings/{Key}", new { hosts = new[] { "api.example.test" } });
        using var delete = await _client.DeleteAsync($"/api/secret-bindings/{Key}");
        using var get = await _client.GetAsync("/api/secret-bindings");

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(0, await DbAsync(db => db.SecretBindings.CountAsync()));
    }

    // ── Create, read, update ──────────────────────────────────────────────────

    [Fact]
    public async Task Put_CreatesABinding_WithDefaults_AndGetNeverReturnsTheValue()
    {
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        using var get = await _client.GetAsync("/api/secret-bindings");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await ReadJsonAsync(put)).GetProperty("created").GetBoolean());
        var body = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Value, body);
        Assert.DoesNotContain(Value, await put.Content.ReadAsStringAsync());

        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(SecretProxyNetwork.DefaultSubnet, root.GetProperty("subnet").GetString());
        Assert.False(root.GetProperty("caPresent").GetBoolean());
        var binding = root.GetProperty("bindings")[0];
        Assert.Equal(Key, binding.GetProperty("envKeyName").GetString());
        Assert.Equal("static", binding.GetProperty("kind").GetString());
        Assert.Equal("Authorization", binding.GetProperty("headerName").GetString());
        Assert.Equal(Key, binding.GetProperty("exposeAs")[0].GetString());
        Assert.False(binding.GetProperty("agentAttachable").GetBoolean());
        Assert.True(binding.GetProperty("valuePresent").GetBoolean());
    }

    [Fact]
    public async Task Put_Again_ReplacesTheBinding_AndListsTheAgentsToReprovision()
    {
        await SeedAgentAsync("agent1", null, Key);
        using var first = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        using var second = await PutAsync(Key, new
        {
            hosts = new[] { "api.example.test", "upload.example.test" }, headerName = "X-Api-Key", agentAttachable = true,
        });

        var json = await ReadJsonAsync(second);
        Assert.False(json.GetProperty("created").GetBoolean());
        Assert.Equal(["agent1"], json.GetProperty("reprovision").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(["agent1"], json.GetProperty("binding").GetProperty("pendingReprovision").EnumerateArray().Select(a => a.GetString()));
        var stored = await DbAsync(db => db.SecretBindings.SingleAsync());
        Assert.Equal("api.example.test,upload.example.test", stored.Hosts);
        Assert.Equal("X-Api-Key", stored.HeaderName);
        Assert.True(stored.AgentAttachable);
    }

    [Fact]
    public async Task ValuePresent_IsFalse_WhenEnvHasNoUsableValue()
    {
        using var put = await PutAsync("ABSENT_KEY", new { hosts = new[] { "api.example.test" } });

        Assert.False((await ReadJsonAsync(put)).GetProperty("binding").GetProperty("valuePresent").GetBoolean());
    }

    // ── Validation: nothing is saved ──────────────────────────────────────────

    public static TheoryData<string, object, string> InvalidBindings => new()
    {
        { Key, new { hosts = Array.Empty<string>() }, "secret_binding_hosts_missing" },
        { Key, new { hosts = new[] { "*.example.test" } }, "secret_binding_hosts_invalid" },
        { Key, new { hosts = new[] { "https://api.example.test" } }, "secret_binding_hosts_invalid" },
        { Key, new { hosts = new[] { "API.EXAMPLE.TEST" } }, "secret_binding_hosts_invalid" },
        { Key, new { hosts = new[] { "10.0.0.5" } }, "secret_binding_hosts_invalid" },
        { Key, new { hosts = new[] { "api.example.test" }, headerName = "Host" }, "secret_binding_header_invalid" },
        { Key, new { hosts = new[] { "api.example.test" }, kind = "dynamic" }, "secret_binding_kind_invalid" },
        { Key, new { hosts = new[] { "api.example.test" }, exposeAs = new[] { "not valid" } }, "secret_binding_expose_invalid" },
        { Key, new { hosts = new[] { "api.example.test" }, exposeAs = new[] { "Telegram__BotToken" } }, "secret_binding_expose_invalid" },
        { Key, new { hosts = new[] { "api.example.test" }, appIdEnvKey = "GITHUB_APP_ID" }, "secret_binding_app_id_unexpected" },
        { "TELEGRAM_EXAMPLE_BOT_TOKEN", new { hosts = new[] { "api.example.test" } }, "secret_binding_key_unsupported" },
        { "ZAI_CODING_PLAN_API_KEY", new { hosts = new[] { "api.example.test" } }, "secret_binding_key_unsupported" },
        { "Section__Token", new { hosts = new[] { "api.example.test" } }, "secret_binding_expose_invalid" },
        { "GITHUB_APP_PEM", new { kind = "github_app", hosts = new[] { "api.github.com" }, exposeAs = new[] { "GH_TOKEN" } }, "secret_binding_app_id_missing" },
        { "GITHUB_APP_PEM", new { kind = "github_app", hosts = new[] { "api.github.com" }, appIdEnvKey = "GITHUB_APP_ID" }, "secret_binding_expose_invalid" },
        { "GITHUB_APP_PEM", new { kind = "github_app", hosts = new[] { "api.github.com" }, appIdEnvKey = "GITHUB_APP_ID", exposeAs = new[] { "GITHUB_APP_PEM" } }, "secret_binding_expose_invalid" },
        { "GITHUB_APP_PEM", new { kind = "github_app", hosts = new[] { "api.github.com" }, appIdEnvKey = "GITHUB_APP_ID", exposeAs = new[] { "GH_TOKEN" }, headerName = "X-Token" }, "secret_binding_header_invalid" },
    };

    [Theory]
    [MemberData(nameof(InvalidBindings))]
    public async Task InvalidBinding_Is400_WithANamedFault_AndNothingIsSaved(string key, object payload, string fault)
    {
        using var response = await PutAsync(key, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(fault, (await ReadJsonAsync(response)).GetProperty("error").GetString());
        Assert.Equal(0, await DbAsync(db => db.SecretBindings.CountAsync()));
    }

    [Fact]
    public async Task GitHubAppBinding_IsAccepted_WithItsAppIdKeyAndTokenNames()
    {
        using var response = await PutAsync("GITHUB_APP_PEM", new
        {
            kind = "github_app",
            hosts = new[] { "api.github.com", "github.com", "uploads.github.com" },
            appIdEnvKey = "GITHUB_APP_ID",
            exposeAs = new[] { "GH_TOKEN", "GITHUB_TOKEN" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await DbAsync(db => db.SecretBindings.SingleAsync());
        Assert.Equal(("github_app", "GITHUB_APP_ID", "GH_TOKEN,GITHUB_TOKEN"), (stored.Kind, stored.AppIdEnvKey, stored.ExposeAs));
        // GITHUB_APP_PEM is not in this test's .env, so the binding has no usable value yet.
        Assert.False((await ReadJsonAsync(response)).GetProperty("binding").GetProperty("valuePresent").GetBoolean());
    }

    [Fact]
    public async Task SameNameInAnotherLetterCase_Is409()
    {
        using var first = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        using var second = await PutAsync(Key.ToLowerInvariant(), new { hosts = new[] { "evil.example.test" } });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("api.example.test", (await DbAsync(db => db.SecretBindings.SingleAsync())).Hosts);
    }

    // ── Delete: never silently back to the value ──────────────────────────────

    [Fact]
    public async Task Delete_WhileAnAgentReferencesTheKey_Is409_AndNamesTheAgents()
    {
        await SeedAgentAsync("agent1", null, Key);
        await SeedAgentAsync("agent2", null, Key);
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });

        using var blocked = await _client.SendAsync(Authed(HttpMethod.Delete, $"/api/secret-bindings/{Key}"));

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var json = await ReadJsonAsync(blocked);
        Assert.StartsWith("secret_binding_in_use", json.GetProperty("error").GetString());
        Assert.Equal(["agent1", "agent2"], json.GetProperty("agents").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(1, await DbAsync(db => db.SecretBindings.CountAsync()));

        await DbAsync(async db =>
        {
            db.AgentEnvRefs.RemoveRange(await db.AgentEnvRefs.ToListAsync());
            return await db.SaveChangesAsync();
        });
        using var deleted = await _client.SendAsync(Authed(HttpMethod.Delete, $"/api/secret-bindings/{Key}"));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0, await DbAsync(db => db.SecretBindings.CountAsync()));
    }

    [Fact]
    public async Task Delete_WithAllowPlainDelivery_DeletesAnInUseBinding_KillsItsGrantsAtOnce_AndNamesTheAgents()
    {
        var hash = SecretPlaceholders.Hash(SecretPlaceholders.Create());
        await SeedAgentAsync("agent1", hash, Key);
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        Assert.Single(State().Grants);

        using var unauthenticated = await _client.DeleteAsync($"/api/secret-bindings/{Key}?allowPlainDelivery=true");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var explicitFalse = await _client.SendAsync(Authed(HttpMethod.Delete, $"/api/secret-bindings/{Key}?allowPlainDelivery=false"));
        Assert.Equal(HttpStatusCode.Conflict, explicitFalse.StatusCode);
        Assert.Single(State().Grants);

        using var deleted = await _client.SendAsync(Authed(HttpMethod.Delete, $"/api/secret-bindings/{Key}?allowPlainDelivery=true"));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(["agent1"], (await ReadJsonAsync(deleted)).GetProperty("reprovision").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(0, await DbAsync(db => db.SecretBindings.CountAsync()));
        Assert.Empty(State().Grants);
        Assert.Empty(State().Bindings);
        // The Env Ref stays (it is a plain reference again) and no longer carries a placeholder hash.
        var reference = await DbAsync(db => db.AgentEnvRefs.SingleAsync());
        Assert.Equal(Key, reference.EnvKeyName);
        Assert.Null(reference.PlaceholderSha256);
    }

    [Fact]
    public async Task Delete_OfAnUnknownKey_Is404()
    {
        using var response = await _client.SendAsync(Authed(HttpMethod.Delete, "/api/secret-bindings/NO_SUCH_KEY"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Revocation reaches the forwarder state when the save returns ──────────

    [Fact]
    public async Task SavingABinding_AndRemovingAnEnvRef_RewriteTheForwarderStateAtOnce()
    {
        var hash = SecretPlaceholders.Hash(SecretPlaceholders.Create());
        await SeedAgentAsync("agent1", hash, Key);

        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var state = State();
        Assert.Equal(Value, state.Bindings.Single().Value);
        Assert.Equal(("agent1", hash), (state.Grants.Single().Agent, state.Grants.Single().PlaceholderSha256));

        // No explicit write here: the save itself brings the file up to date.
        await DbAsync(async db =>
        {
            db.AgentEnvRefs.Remove(await db.AgentEnvRefs.SingleAsync());
            return await db.SaveChangesAsync();
        });
        Assert.Empty(State().Grants);
        Assert.Single(State().Bindings);
    }

    [Fact]
    public async Task DeletingAnAgent_RewritesTheForwarderStateAtOnce()
    {
        var hash = SecretPlaceholders.Hash(SecretPlaceholders.Create());
        await SeedAgentAsync("agent1", hash, Key);
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        Assert.Single(State().Grants);

        await DbAsync(async db =>
        {
            db.Agents.Remove(await db.Agents.SingleAsync());
            return await db.SaveChangesAsync();
        });

        Assert.Empty(State().Grants);
    }

    [Fact]
    public async Task ChangingABindingsHosts_ReachesTheForwarderStateAtOnce()
    {
        using var first = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        using var second = await PutAsync(Key, new { hosts = new[] { "moved.example.test" } });

        Assert.Equal(["moved.example.test"], State().Bindings.Single().Hosts);
    }

    // ── The MCP tool cannot grant a bound secret ──────────────────────────────

    private ManageAgentEnvRefsTool Tool() => new(_app.Services.GetRequiredService<IServiceScopeFactory>());

    [Theory]
    [InlineData(Key)]
    [InlineData("example_api_key")]
    public async Task McpTool_CannotAttachABoundSecret(string requested)
    {
        await SeedAgentAsync("agent1");
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });

        var answer = await Tool().ManageAgentEnvRefsAsync("agent1", "add", requested);

        Assert.Contains("only the operator can attach", answer);
        Assert.Equal(0, await DbAsync(db => db.AgentEnvRefs.CountAsync()));
    }

    [Fact]
    public async Task McpTool_MayAttachABoundSecretMarkedAgentAttachable_AndAnyUnboundKey()
    {
        await SeedAgentAsync("agent1");
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" }, agentAttachable = true });

        var bound = await Tool().ManageAgentEnvRefsAsync("agent1", "add", Key);
        var plain = await Tool().ManageAgentEnvRefsAsync("agent1", "add", "PLAIN_SETTING");

        Assert.StartsWith("Added env ref", bound);
        Assert.StartsWith("Added env ref", plain);
        Assert.Equal(2, await DbAsync(db => db.AgentEnvRefs.CountAsync()));
    }

    [Fact]
    public async Task McpTool_MayAlwaysRemoveABoundSecret_AndThatRevokesItAtOnce()
    {
        var hash = SecretPlaceholders.Hash(SecretPlaceholders.Create());
        await SeedAgentAsync("agent1", hash, Key);
        using var put = await PutAsync(Key, new { hosts = new[] { "api.example.test" } });
        Assert.Single(State().Grants);

        var answer = await Tool().ManageAgentEnvRefsAsync("agent1", "remove", Key);

        Assert.StartsWith("Removed env ref", answer);
        Assert.Empty(State().Grants);
    }
}
