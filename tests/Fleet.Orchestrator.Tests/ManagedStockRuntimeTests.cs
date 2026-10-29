using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests;

public class ManagedStockRuntimeTests
{
    internal const string PolicyYaml = "version: 1\nfilesystem_policy:\n  include_workdir: true\n  read_only: [/usr]\n  read_write: [/workspace]\nprocess:\n  run_as_user: sandbox\n  run_as_group: sandbox\nlandlock:\n  compatibility: hard_requirement\nnetwork_policies: {}\n";

    internal static Agent Agent() => new() { Name = "agent1", DisplayName = "Agent", Role = "test", Model = "sol", Provider = "codex", ContainerName = "agent1" };
    internal static OrchestratorDbContext Db() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void Valid_stock_subset_has_exact_content_hash()
    {
        Assert.Null(SandboxPolicyValidation.DescribeFault(PolicyYaml, new()));
        Assert.Equal(64, SandboxPolicyValidation.Hash(PolicyYaml).Length);
        Assert.NotEqual(SandboxPolicyValidation.Hash(PolicyYaml), SandboxPolicyValidation.Hash(PolicyYaml + "\n"));
    }

    [Fact]
    public void Plain_stock_integer_and_boolean_scalars_are_accepted()
    {
        Assert.Null(SandboxPolicyValidation.DescribeFault(
            "version: 1\nfilesystem_policy: {include_workdir: false}\nnetwork_policies: {api: {endpoints: [{host: example.com, port: 443}]}}", new()));
    }

    [Theory]
    [InlineData("version: 1\nversion: 2")]
    [InlineData("version: 1\nunknown: true")]
    [InlineData("version: 1\nfilesystem_policy: {read_only: x}")]
    [InlineData("version: 999")]
    [InlineData("- not-a-policy")]
    [InlineData("version: 1\nnetwork_policies: {api: {endpoints: [{host: example.com, port: 443, tls: auto}]}}")]
    [InlineData("version: 1\nnetwork_policies: {api: {endpoints: [{host: example.com, port: 443, protocol: tcp, access: full}]}}")]
    [InlineData("version: 1\nfilesystem_policy: &x {read_only: [/usr]}\nprocess: *x") ]
    public void Invalid_or_unsupported_yaml_refused(string yaml) => Assert.NotNull(SandboxPolicyValidation.DescribeFault(yaml, new()));

    [Fact]
    public async Task Selection_is_pending_and_does_not_touch_applied_binding_or_grants()
    {
        using var db = Db();
        db.SandboxPolicies.Add(new() { Name = "test", Versions = [new() { Version = 1, Yaml = PolicyYaml, Sha256 = SandboxPolicyValidation.Hash(PolicyYaml) }] });
        await db.SaveChangesAsync();
        var a = Agent();
        a.AppliedRuntimeBinding = "previous-binding";
        a.Tools.Add(new() { ToolName = "existing", IsEnabled = true });
        Assert.Null(await AgentPatchHelpers.ApplyRuntimeSelectionAsync(db, a, "openshell", 1, "api_key", "TEST_API_KEY"));
        Assert.Equal("docker", a.AppliedRuntimeKind);
        Assert.Equal("previous-binding", a.AppliedRuntimeBinding);
        Assert.Single(a.Tools);
        Assert.True(AgentPatchHelpers.RuntimePending(a));
        Assert.Equal("managed_runtime_unavailable", AgentPatchHelpers.RuntimeApplyFault(a));
    }

    [Fact]
    public async Task Missing_version_refuses_and_docker_defaults_are_unchanged()
    {
        using var db = Db();
        var a = Agent();
        Assert.Null(await AgentPatchHelpers.ApplyRuntimeSelectionAsync(db, a, null, null, null, null));
        Assert.Equal("docker", a.RuntimeKind);
        var fault = await AgentPatchHelpers.ApplyRuntimeSelectionAsync(db, a, "openshell", 999, "api_key", "TEST_API_KEY");
        Assert.Equal(404, fault!.Status);
    }

    [Fact]
    public async Task Old_versions_cannot_be_modified_or_removed()
    {
        using var db = Db();
        var p = new SandboxPolicy { Name = "test", Versions = [new() { Version = 1, Yaml = PolicyYaml, Sha256 = SandboxPolicyValidation.Hash(PolicyYaml) }] };
        db.SandboxPolicies.Add(p);
        await db.SaveChangesAsync();
        p.Versions[0].Yaml = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.SandboxPolicyVersions.Remove(await db.SandboxPolicyVersions.FirstAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}

/// <summary>Real handlers over HTTP; SQLite is only the local API store, never migration evidence.</summary>
public class ManagedStockRuntimeApiTests : IAsyncLifetime
{
    protected Microsoft.AspNetCore.Builder.WebApplication App = null!;
    protected HttpClient Client = null!;
    private Microsoft.Data.Sqlite.SqliteConnection? _sqlite;
    protected virtual async Task ConfigureDatabaseAsync(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        _sqlite = new("Data Source=:memory:");
        await _sqlite.OpenAsync();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_sqlite));
    }
    public async Task InitializeAsync()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Orchestrator:AuthToken"] = "synthetic-test-token";
        builder.Logging.ClearProviders();
        await ConfigureDatabaseAsync(builder.Services);
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.IAclChangeNotifier>(new NoopAcl());
        builder.Services.AddSingleton(new Fleet.Orchestrator.Services.AgentConfigPublisherService(
            Microsoft.Extensions.Options.Options.Create(new Fleet.Orchestrator.Configuration.RabbitMqOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Fleet.Orchestrator.Services.AgentConfigPublisherService>.Instance));
        builder.Services.AddSingleton(sp => new Fleet.Orchestrator.Services.SetupService(builder.Configuration, null!, null!,
            sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Fleet.Orchestrator.Services.SetupService>.Instance, null!));
        // The real managed adapter, disabled (no ManagedEnabled): routes exercise auth and the real DB store.
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.IStockCli, Fleet.Orchestrator.Services.ProcessStockCli>();
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.ICredentialsReader>(new NoCredentials());
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.StockBrokerProvisioner>();
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.IManagedBindingStore, Fleet.Orchestrator.Services.EfManagedBindingStore>();
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.IStockHostView>(new NoHost());
        builder.Services.AddSingleton<Fleet.Orchestrator.Services.StockOpenShellRuntime>();
        App = builder.Build();
        App.MapSandboxPolicyEndpoints();
        App.MapAgentConfigEndpoints();
        using (var scope = App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();
        await App.StartAsync();
        Client = App.GetTestClient();
        Client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-test-token");
    }
    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null) await App.DisposeAsync();
        if (_sqlite is not null) await _sqlite.DisposeAsync();
        await DropDatabaseAsync();
    }
    protected virtual Task DropDatabaseAsync() => Task.CompletedTask;
    [Fact]
    public async Task Pending_selection_uses_applied_runtime_and_managed_apply_cannot_touch_Docker()
    {
        var versionPolicy = await CreatePolicy();
        var version = await WithDb(db => db.SandboxPolicyVersions.SingleAsync(v => v.PolicyId == versionPolicy));
        await WithDb(async db =>
        {
            var a = ManagedStockRuntimeTests.Agent();
            a.RuntimeKind = "openshell"; a.SandboxPolicyVersionId = version.Id;
            a.NativeAuthMode = "api_key"; a.ProviderApiKeyEnvRef = "TEST_API_KEY";
            db.Agents.Add(a); return await db.SaveChangesAsync();
        });
        var factory = App.Services.GetRequiredService<IServiceScopeFactory>();
        var hold = new Fleet.Orchestrator.Services.StockRuntimeMutationHold(App.Configuration, factory);
        Assert.Null(await hold.DescribeAppliedFaultAsync("agent1"));
        var provisioning = new Fleet.Orchestrator.Services.ContainerProvisioningService(factory, null!, App.Configuration, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Fleet.Orchestrator.Services.ContainerProvisioningService>.Instance, hold);
        var pending = await WithDb(db => db.Agents.AsNoTracking().SingleAsync());
        Assert.Equal("http://agent1:8080", provisioning.GetAgentBaseUrl(pending)); // cancel still uses applied Docker
        Assert.Equal("managed_runtime_unavailable", (await provisioning.ProvisionAsync("agent1")).Message);
        Assert.Equal("managed_runtime_unavailable", (await provisioning.ReprovisionAsync("agent1")).Message);
        Assert.Equal("managed_runtime_unavailable", await new Fleet.Orchestrator.Tools.RestartAgentWithVersionTool(provisioning, factory).RestartAgentWithVersionAsync("agent1", "missing", 1, pin: true));
        Assert.Contains("managed_runtime_unavailable", string.Join(" ", (await provisioning.PreviewAsync("agent1")).Diffs));
        await WithDb(async db => { var a = await db.Agents.SingleAsync(); a.RuntimeKind = "docker"; a.AppliedRuntimeKind = "openshell"; a.AppliedRuntimeBinding = "synthetic-binding"; return await db.SaveChangesAsync(); });
        Assert.Equal("managed_runtime_unavailable", await hold.DescribeAppliedFaultAsync("agent1"));
        // No registry or Docker service is supplied: any legacy dispatch/fallback makes this fail.
        Assert.Equal("managed_runtime_unavailable", await new Fleet.Orchestrator.Tools.StartAgentTool(null!, null!, hold).StartAgentAsync("agent1"));
        Assert.Equal("managed_runtime_unavailable", await new Fleet.Orchestrator.Tools.StopAgentTool(null!, null!, hold).StopAgentAsync("agent1"));
        Assert.Throws<InvalidOperationException>(() => provisioning.GetAgentBaseUrl(new Agent { Name = "agent1", DisplayName = "Agent", Role = "test", Model = "sol", ContainerName = "agent1", AppliedRuntimeKind = "openshell" }));
    }

    [Fact]
    public async Task Delete_ownership_keeps_a_concurrent_claim_out_until_the_row_is_gone()
    {
        // Review A2/D1: an apply/Start/revoke claim CAS issued while the delete guard owns the inspected binding never
        // wins. On MySQL it waits on the row lock and then matches no row; the one shared SQLite test connection
        // refuses it outright. Either way the deprovision ran under the inspected binding alone.
        await WithDb(async db => { db.Agents.Add(ManagedStockRuntimeTests.Agent()); return await db.SaveChangesAsync(); });
        var inspected = await WithDb(db => db.Agents.AsNoTracking().SingleAsync());
        var store = App.Services.GetRequiredService<Fleet.Orchestrator.Services.IManagedBindingStore>();
        var claimJson = new Fleet.Orchestrator.Services.ManagedRecord { State = "applying", Operation = "apply:other-process:synthetic" }.Serialize();
        Task<bool>? claim = null;
        var guard = new Fleet.Orchestrator.Services.ManagedDeleteGuard(inspected.Id, inspected.AppliedRuntimeBinding, inspected.AppliedRuntimeKind, async _ =>
        {
            claim = Task.Run(() => store.SwapAsync(inspected.Id, inspected.AppliedRuntimeBinding, claimJson, null, null, CancellationToken.None));
            await Task.WhenAny(claim, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.False(claim.IsCompletedSuccessfully && claim.Result, "a concurrent claim won while the delete owned the row");
            return Fleet.Orchestrator.Services.ProvisionResult.Ok("agent1", "synthetic deprovision");
        });

        var (fault, deprovision) = await WithDb(async db => await guard.DeleteAsync(db, await db.Agents.SingleAsync()));

        Assert.Null(fault);
        Assert.True(deprovision!.Success);
        var won = false;
        try { won = await claim!; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { }
        Assert.False(won);
        Assert.Equal(0, await WithDb(db => db.Agents.CountAsync()));
    }

    private sealed class NoopAcl : Fleet.Orchestrator.Services.IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    protected async Task<T> WithDb<T>(Func<OrchestratorDbContext, Task<T>> action)
    {
        using var scope = App.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }
    protected async Task<int> CreatePolicy()
    {
        var r = await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Example policy", yaml = ManagedStockRuntimeTests.PolicyYaml });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetInt32();
    }
    [Fact]
    public async Task Every_policy_route_requires_operator_token_including_reads()
    {
        Client.DefaultRequestHeaders.Authorization = null;
        foreach (var path in new[] { "/api/sandbox-policies", "/api/sandbox-policies/1/versions", "/api/sandbox-policies/1/versions/1" })
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Client.GetAsync(path)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "x", yaml = "version: 1" })).StatusCode);
    }
    [Fact]
    public async Task Create_append_list_and_conflicts_preserve_old_bytes()
    {
        var id = await CreatePolicy();
        var url = $"/api/sandbox-policies/{id}/versions";
        var original = await Client.GetStringAsync(url + "/1");
        Assert.Equal(System.Net.HttpStatusCode.Created, (await Client.PostAsJsonAsync(url, new { version = 2, yaml = "version: 1\n" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await Client.PostAsJsonAsync(url, new { version = 2, yaml = "version: 1\n" })).StatusCode);
        Assert.Equal(original, await Client.GetStringAsync(url + "/1"));
        Assert.Equal(2, (await Client.GetFromJsonAsync<System.Text.Json.JsonElement>(url)).GetArrayLength());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Client.GetAsync(url + "/999")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Example policy", yaml = "version: 1" })).StatusCode);
    }
    [Theory]
    [InlineData("version: \"1\"")]
    [InlineData("version: '1'")]
    [InlineData("version: 1\nfilesystem_policy: {include_workdir: \"true\"}")]
    [InlineData("version: 1\nfilesystem_policy: {include_workdir: 'false'}")]
    [InlineData("version: 1\nnetwork_policies: {api: {endpoints: [{host: example.com, port: \"443\"}]}}")]
    [InlineData("version: 1\nnetwork_policies: {api: {endpoints: [{host: example.com, port: '443'}]}}")]
    public async Task Wrong_scalar_types_return_400_without_create_or_append_writes(string yaml)
    {
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Wrong scalar", yaml })).StatusCode);
        Assert.Equal(0, await WithDb(db => db.SandboxPolicies.CountAsync()));
        Assert.Equal(0, await WithDb(db => db.SandboxPolicyVersions.CountAsync()));

        var id = await CreatePolicy();
        var url = $"/api/sandbox-policies/{id}/versions";
        var original = await Client.GetStringAsync(url + "/1");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync(url, new { version = 2, yaml })).StatusCode);
        Assert.Equal(1, await WithDb(db => db.SandboxPolicies.CountAsync()));
        Assert.Equal(1, await WithDb(db => db.SandboxPolicyVersions.CountAsync()));
        Assert.Equal(original, await Client.GetStringAsync(url + "/1"));
    }

    [Fact]
    public async Task Invalid_policy_leaves_no_partial_policy_or_version()
    {
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Bad", yaml = "version: 1\nunknown: true" })).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Bad", yaml = "version: 1", providerProfileHashes = new Dictionary<string, string?> { ["example"] = null } })).StatusCode);
        Assert.Equal(0, await WithDb(db => db.SandboxPolicies.CountAsync()));
        Assert.Equal(0, await WithDb(db => db.SandboxPolicyVersions.CountAsync()));
    }
    [Fact]
    public async Task Rest_and_mcp_selection_pin_exact_version_and_keep_applied_binding()
    {
        var id = await CreatePolicy();
        var version = await WithDb(db => db.SandboxPolicyVersions.SingleAsync(v => v.PolicyId == id));
        await WithDb(async db => { var a = ManagedStockRuntimeTests.Agent(); a.AppliedRuntimeBinding = "existing-docker-binding"; a.Tools = [new() { ToolName = "existing", IsEnabled = true }]; db.Agents.Add(a); return await db.SaveChangesAsync(); });
        var response = await Client.PutAsJsonAsync("/api/agents/agent1/config", new { runtimeKind = "openshell", sandboxPolicyVersionId = version.Id, nativeAuthMode = "api_key", providerApiKeyEnvRef = "TEST_API_KEY", appliedRuntimeKind = "openshell", appliedRuntimeBinding = "forged" });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var get = await Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/agents/agent1/config");
        Assert.Equal("docker", get.GetProperty("appliedRuntimeKind").GetString());
        Assert.Equal("existing-docker-binding", get.GetProperty("appliedRuntimeBinding").GetString());
        Assert.True(get.GetProperty("runtimePending").GetBoolean());
        Assert.Equal(version.Id, get.GetProperty("sandboxPolicyVersionId").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Client.PutAsJsonAsync("/api/agents/agent1/config", new { sandboxPolicyVersionId = 999999 })).StatusCode);
        var update = new Fleet.Orchestrator.Tools.UpdateAgentConfigTool(App.Services.GetRequiredService<IServiceScopeFactory>(), new NoopAcl());
        Assert.Contains("saved", await update.UpdateAgentConfigAsync("agent1", runtime_kind: "docker"));
        Assert.Equal("existing-docker-binding", await WithDb(db => db.Agents.Select(a => a.AppliedRuntimeBinding).SingleAsync()));
        Assert.Equal(1, await WithDb(db => db.AgentTools.CountAsync()));
    }
    [Fact]
    public async Task Apply_is_named_409_and_does_not_change_state_without_runtime_services()
    {
        await WithDb(async db => { db.Agents.Add(ManagedStockRuntimeTests.Agent()); return await db.SaveChangesAsync(); });
        var response = await Client.PostAsync("/api/agents/agent1/runtime/apply", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("managed_runtime_unavailable", await response.Content.ReadAsStringAsync());
        Assert.Equal("docker", await WithDb(db => db.Agents.Select(a => a.AppliedRuntimeKind).SingleAsync()));
    }
    [Fact]
    public async Task Revoke_and_rollback_preflight_are_operator_only_and_read_the_real_store()
    {
        await WithDb(async db =>
        {
            var desired = ManagedStockRuntimeTests.Agent();
            desired.RuntimeKind = "openshell";
            db.Agents.Add(desired);
            return await db.SaveChangesAsync();
        });
        using var anonymous = App.GetTestClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/agents/agent1/revoke", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/managed-runtime/rollback-preflight")).StatusCode);

        var revoke = await Client.PostAsync("/api/agents/agent1/revoke", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, revoke.StatusCode);
        Assert.Contains("managed_runtime_unavailable", await revoke.Content.ReadAsStringAsync());
        var preflight = await Client.GetAsync("/api/managed-runtime/rollback-preflight");
        Assert.Equal(System.Net.HttpStatusCode.OK, preflight.StatusCode);
        Assert.Equal("{\"desiredOpenShell\":1,\"appliedOpenShell\":0,\"unresolvedBindings\":0}", await preflight.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_operator_auth_configuration_fails_closed()
    {
        App.Configuration["Orchestrator:AuthToken"] = "";
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.GetAsync("/api/sandbox-policies")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.PostAsync("/api/agents/agent1/runtime/apply", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.PostAsync("/api/agents/agent1/revoke", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.GetAsync("/api/managed-runtime/rollback-preflight")).StatusCode);
    }

    private sealed class NoCredentials : Fleet.Orchestrator.Services.ICredentialsReader
    {
        public string? Get(string key) => null;
        public void InvalidateCache() { }
    }

    private sealed class NoHost : Fleet.Orchestrator.Services.IStockHostView
    {
        public Task<string?> GatewayGenerationAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> RunningSandboxContainersAsync(string sandboxId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<IReadOnlyList<string>?> DockerContainerStatesAsync(string containerName, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
    }
    [Fact]
    public async Task First_version_write_failure_rolls_back_policy_row()
    {
        await WithDb(db => db.Database.ExecuteSqlRawAsync(db.Database.IsSqlite()
            ? "CREATE TRIGGER refuse_policy BEFORE INSERT ON sandbox_policy_versions BEGIN SELECT RAISE(ABORT, 'synthetic refusal'); END"
            : "CREATE TRIGGER refuse_policy BEFORE INSERT ON sandbox_policy_versions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='synthetic refusal'"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Example", yaml = "version: 1" })).StatusCode);
        Assert.Equal(0, await WithDb(db => db.SandboxPolicies.CountAsync()));
        Assert.Equal(0, await WithDb(db => db.SandboxPolicyVersions.CountAsync()));
    }
    [Fact]
    public async Task Database_failure_returns_503_not_an_empty_list()
    {
        await WithDb(db => db.Database.ExecuteSqlRawAsync("ALTER TABLE sandbox_policies RENAME TO unavailable_policies"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.GetAsync("/api/sandbox-policies")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, (await Client.PostAsJsonAsync("/api/sandbox-policies", new { name = "Example", yaml = "version: 1" })).StatusCode);
    }
}
