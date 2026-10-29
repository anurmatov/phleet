using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #492 apply/revoke review regressions on the synthetic stock CLI, store, host and broker of
/// <see cref="ManagedStockRuntimeAdapterTests"/>. Source checks only, not runtime qualification.
/// </summary>
public sealed class ManagedStockRuntimeApplyReviewTests : IDisposable
{
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Hold_appends_to_earlier_errors_and_partial_names()
    {
        var binding = _t.Binding("fleet-agent1-a1a1a1a1", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Errors = ["earlier_hold", "partial:user-x"] }));
        _t._broker.FailDeletes = true;

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.Equal("revoke_unresolved", revoke.Code);
        var errors = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Errors;
        Assert.Equal(["earlier_hold", "partial:user-x", "revoke_unresolved"], errors.Take(3));
        Assert.Contains(errors, e => e.StartsWith("broker:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revoke_detaches_the_union_of_applied_and_candidate_providers()
    {
        var binding = _t.Binding("fleet-agent1-b2b2b2b2", Guid.NewGuid());
        var candidate = binding with { Epoch = Guid.NewGuid(), ServiceProviders = ["fleet-agent1-telegram"] };
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Candidate = candidate }));

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Message);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "provider", "detach", binding.SandboxName, "fleet-agent1-telegram"]));
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "provider", "detach", binding.SandboxName, "fleet-agent1-openai"]));
        Assert.Null(ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Candidate);
    }

    [Fact]
    public async Task Reuse_apply_keeps_live_control_credentials_until_the_cut_succeeds()
    {
        var binding = _t.Binding("fleet-agent1-c3c3c3c3", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = _t.Runtime();
        var control = Path.Combine(_t._dir, "managed", "authority", "agent1.control.password");
        Directory.CreateDirectory(Path.GetDirectoryName(control)!);
        File.WriteAllText(control, "live-control");
        _t.WriteAuthority("active");
        _t._broker.FailDeletes = true; // the cut of the live epoch fails

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.Equal("revoke_unresolved", result.Code);
        Assert.Equal("live-control", File.ReadAllText(control));
        Assert.Null(JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!["Epoch"]); // the candidate epoch was never published
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Empty(_t._broker.Puts);
    }

    [Fact]
    public async Task Every_selected_provider_profile_is_pinned_and_verified_before_the_old_runtime_stops()
    {
        var agent = _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
        _t._broker.Credentials.Extra["TELEGRAM_AGENT1_BOT_TOKEN"] = "424242:synthetic-bot-secret-never-in-argv";
        var pins = JsonNode.Parse(ManagedStockRuntimeAdapterTests.FakeStore.PinnedProfiles)!.AsObject();
        pins.Remove(StockOpenShellRuntime.TelegramProfile);
        _t._store.ProfileHashes = pins.ToJsonString();
        var stops = 0;
        _t._host.DockerStates = ["running"];

        var result = await _t.Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal("provider_profile_unpinned:" + StockOpenShellRuntime.TelegramProfile, result.Code);
        Assert.Equal(0, stops);
        Assert.Empty(_t._broker.Puts);
    }

    [Fact]
    public async Task Config_dir_with_a_linked_ancestor_or_foreign_owner_refuses_before_any_write()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = _t.Runtime(); // writes the fixture's private ConfigDir
        var link = Path.Combine(Path.GetDirectoryName(_t._dir)!, "linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(_t._dir)!);
        try
        {
            var linked = new StockOpenShellRuntime(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["StockOpenShell:ManagedEnabled"] = "true", ["StockOpenShell:ConfigDir"] = Path.Combine(link, Path.GetFileName(_t._dir)) }).Build(),
                _t._cli, null!, _t._store, _t._host, _t._broker.Credentials,
                new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StockOpenShellRuntime>.Instance);
            Assert.Equal("managed_config_dir_not_private", await DirFault(linked));
        }
        finally { Directory.Delete(link); }

        Assert.Null(await DirFault(runtime));
        if (StockOpenShellRuntime.OwnerUid(_t._dir) == 0)
        {
            File.SetUnixFileMode(_t._dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(0, Chown(_t._dir, 65534, 65534));
            try { Assert.Equal("managed_config_dir_not_owned", await DirFault(runtime)); }
            finally { Chown(_t._dir, 0, 0); }
        }
    }

    [Fact]
    public async Task Docker_handoff_mirrors_sandbox_deletions_but_never_touches_control_state()
    {
        var ws = _t.DockerWorkspaceDir;
        Directory.CreateDirectory(Path.Combine(ws, "gone-dir", "nested"));
        File.WriteAllText(Path.Combine(ws, "gone-dir", "nested", "old.txt"), "deleted in sandbox");
        File.WriteAllText(Path.Combine(ws, "deleted.txt"), "deleted in sandbox");
        File.WriteAllText(Path.Combine(ws, "only-dir"), "became a directory in sandbox");
        Directory.CreateDirectory(Path.Combine(ws, ".ssh"));
        File.WriteAllText(Path.Combine(ws, ".ssh", "id"), "control");
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), "{\"docker\":true}");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = _t.Runtime();
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        _t._store.Get("agent1").RuntimeKind = "docker";
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        // The synthetic sandbox /workspace holds only marker-back.txt and only-dir/.
        Assert.Equal("from sandbox", File.ReadAllText(Path.Combine(ws, "marker-back.txt")));
        Assert.True(Directory.Exists(Path.Combine(ws, "only-dir")));
        Assert.False(File.Exists(Path.Combine(ws, "deleted.txt")));
        Assert.False(Directory.Exists(Path.Combine(ws, "gone-dir")));
        Assert.Equal("control", File.ReadAllText(Path.Combine(ws, ".ssh", "id")));
        Assert.Equal("{\"docker\":true}", File.ReadAllText(Path.Combine(ws, ".mcp.json")));
    }

    [Fact]
    public async Task Pinned_uploads_land_under_the_non_root_home_where_the_agent_reads_them()
    {
        Directory.CreateDirectory(_t.DockerWorkspaceDir);
        File.WriteAllText(Path.Combine(_t.DockerWorkspaceDir, "marker.txt"), "keep");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));

        Assert.True((await _t.Runtime().ApplyAsync("agent1", default)).Ok);

        var remote = _t._cli.Uploads.Select(u => u.Remote).ToHashSet();
        // Nothing nests (/app/app, /workspace/workspace) or targets the image-owned /app or root-owned /trust.
        Assert.DoesNotContain(remote, r => r.StartsWith("/app/", StringComparison.Ordinal) || r.StartsWith("/trust/", StringComparison.Ordinal)
            || r.StartsWith("/home/agent/app/app/", StringComparison.Ordinal) || r.StartsWith("/workspace/workspace/", StringComparison.Ordinal));
        Assert.Contains("/workspace/marker.txt", remote);
        Assert.Contains("/workspace/.mcp.json", remote);
        Assert.Contains("/home/agent/app/" + Fleet.Shared.HarnessDefaults.CodexRootTomlFileName, remote);
        var settings = JsonNode.Parse(_t._cli.Uploads.Last(u => u.Remote == "/home/agent/app/appsettings.json").Content!)!["StockOpenShell"]!;
        // Every trust path the agent is configured with is exactly where an upload placed it.
        foreach (var key in new[] { "EnrollmentFile", "AuthorityFile", "BrokerPasswordFile" })
            Assert.Contains(settings[key]!.GetValue<string>(), remote);
        Assert.Equal("/home/agent/app", settings["AppRoot"]!.GetValue<string>());
        Assert.Equal("/home/agent/trust/enrollment.json", _t._cli.Uploads.Last().Remote); // activated enrollment, same path
    }

    [Fact]
    public async Task Workspace_upload_gets_the_same_bound_as_the_workspace_download()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = _t.Runtime();
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
        _t._store.Get("agent1").RuntimeKind = "docker";
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        var upload = Assert.Single(_t._cli.Timeouts, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "workspace");
        var download = Assert.Single(_t._cli.Timeouts, c => c.Args.Take(2).SequenceEqual(["sandbox", "download"]));
        Assert.Equal(TimeSpan.FromMinutes(5), runtime.TransferTimeout);
        Assert.Equal(runtime.TransferTimeout, upload.Timeout);
        Assert.Equal(download.Timeout, upload.Timeout);
        Assert.NotEqual(runtime.LegTimeout, upload.Timeout);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    [InlineData("[{}]")]
    public async Task Broker_revoke_records_a_connections_parse_error_and_still_runs_every_other_step(string listing)
    {
        var epoch = Guid.NewGuid();
        var provisioner = _t.Provisioner();
        _t._broker.ConnectionsBody = listing;

        var revoke = await provisioner.RevokeEpochAsync(epoch, default);

        Assert.False(revoke.Ok);
        Assert.Equal("broker_revoke_unresolved", revoke.Code);
        Assert.DoesNotContain("broker_management_unavailable", revoke.OwnedPartials);
        Assert.Equal(StockBrokerProvisioner.Roles.Select(r => "connections:" + StockBrokerProvisioner.Principal(epoch, r)), revoke.OwnedPartials);
        foreach (var role in StockBrokerProvisioner.Roles)
        {
            var user = StockBrokerProvisioner.Principal(epoch, role);
            Assert.Contains(_t._broker.Deletes, d => d.StartsWith("/api/permissions/", StringComparison.Ordinal) && d.EndsWith("/" + user, StringComparison.Ordinal));
            Assert.Contains(_t._broker.Deletes, d => d == "/api/users/" + user);
        }
        // The management user's own grant on the epoch vhost is still removed after the failed listings.
        Assert.Equal(StockBrokerProvisioner.Roles.Length + 1, _t._broker.Deletes.Count(d => d.StartsWith("/api/permissions/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stop_ends_within_its_own_overall_bound_when_every_leg_hangs()
    {
        var binding = _t.Binding("fleet-agent1-d4d4d4d4", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t._cli.Hang.Add(binding.SandboxName); // every leg on the sandbox ignores its own leg bound

        var stop = _t.Runtime().StopAsync("agent1", default);
        var done = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(stop, done); // the fixture's 5 s FanoutTimeout, not an unbounded wait
        Assert.Equal("stop_unknown", (await stop).Code);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Null(record.Operation);
    }

    [Fact]
    public async Task Start_legs_ignore_a_client_disconnect()
    {
        var binding = _t.Binding("fleet-agent1-e5e5e5e5", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        using var client = new CancellationTokenSource();
        var probes = 0;
        // The client disconnects (RequestAborted) during staging; the next poll still runs on Start's own deadline.
        _t._cli.OnExec = script => { if (script.Contains(".fleet-staged") && probes++ == 0) { client.Cancel(); return 1; } return 0; };

        var start = await _t.Runtime().StartAsync("agent1", client.Token);

        Assert.True(start.Ok, start.Message);
        Assert.Equal("started", start.Code);
        Assert.Equal("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chown")]
    private static extern int Chown([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    private Task<string?> DirFault(StockOpenShellRuntime runtime) =>
        Task.FromResult((string?)typeof(StockOpenShellRuntime).GetMethod("DescribeConfigDirFault",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(runtime, null));
}
