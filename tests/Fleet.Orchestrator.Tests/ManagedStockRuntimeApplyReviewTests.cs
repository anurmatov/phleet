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

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chown")]
    private static extern int Chown([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    private Task<string?> DirFault(StockOpenShellRuntime runtime) =>
        Task.FromResult((string?)typeof(StockOpenShellRuntime).GetMethod("DescribeConfigDirFault",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(runtime, null));
}
