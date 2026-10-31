using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests;

/// <summary>#492 review: lifecycle regressions over the synthetic adapter fakes (source checks, not #493).</summary>
public sealed class ManagedStockRuntimeLifecycleReviewTests : IDisposable
{
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    private static bool Verb(ManagedStockRuntimeAdapterTests.Call c, string verb) => c.Args.Take(2).SequenceEqual(["sandbox", verb]);

    [Fact]
    public async Task Repeat_start_on_an_applied_running_agent_never_restages_or_stops_it()
    {
        var binding = _t.Binding("fleet-agent1-11111111", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        // A running daemon consumed its staged marker long ago.
        _t._cli.OnExec = script => script.Contains("-f \"$HOME/.fleet-staged\"") && !script.Contains('!') ? 1 : 0;
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t.Enroll(binding);

        var result = await _t.Runtime().StartAsync("agent1", default);

        Assert.True(result.Ok, result.Code);
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop") || Verb(c, "start"));
        Assert.Equal("Ready", _t._cli.Sandboxes[binding.SandboxName].Phase);
        Assert.Equal("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Start_whose_preparation_fails_after_activation_is_never_recorded_applied()
    {
        var binding = _t.Binding("fleet-agent1-22222222", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        // Activation consumed (no staged marker) but preparation failed: no ready marker, no daemon.
        _t._cli.OnExec = script => script.Contains(".fleet-ready") ? 1 : 0;
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);

        var result = await _t.Runtime().StartAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.Equal("activation_unconfirmed", result.Code);
        Assert.NotEqual("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
    }

    [Fact]
    public async Task Apply_records_applied_only_after_the_daemon_ready_check()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._cli.OnExec = script => script == StockOpenShellRuntime.DaemonReadyScript ? 1 : 0;

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.NotEqual("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Start_whose_daemon_pid_is_up_but_health_never_answers_is_never_recorded_applied()
    {
        var binding = _t.Binding("fleet-agent1-f4f4f4f4", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        // Review F4: PID/cmdline and Ready pass, but the daemon's GET /health never answers 200.
        _t._cli.OnExec = script => script == StockOpenShellRuntime.DaemonHealthScript ? 1 : 0;
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);

        var result = await _t.Runtime().StartAsync("agent1", default);

        Assert.Equal("activation_unconfirmed", result.Code);
        Assert.NotEqual("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "exec", "-n", binding.SandboxName, "--", "bash", "-c", StockOpenShellRuntime.DaemonHealthScript]));
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
    }

    [Fact]
    public async Task Error_phase_binding_gets_a_verified_stop_before_any_other_lifecycle_action()
    {
        var binding = _t.Binding("fleet-agent1-33333333", Guid.NewGuid());
        // Error/Completed phases may leave a native supervisor running.
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Error");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "revoked", Applied = binding }));

        await _t.Runtime().ApplyAsync("agent1", default);

        var lifecycle = _t._cli.Calls.Where(c => Verb(c, "stop") || Verb(c, "start")).Select(c => c.Args[1]).ToList();
        Assert.NotEmpty(lifecycle);
        Assert.Equal("stop", lifecycle[0]);
    }

    [Fact]
    public async Task Stop_after_revoke_keeps_the_binding_revoked_so_start_refuses()
    {
        var binding = _t.Binding("fleet-agent1-44444444", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = _t.Runtime();
        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);

        await runtime.StopAsync("agent1", default);
        var start = await runtime.StartAsync("agent1", default);

        Assert.Equal("revoked", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Equal((409, "revoked"), (start.Status, start.Code));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "start"));
    }

    [Fact]
    public async Task Client_disconnect_never_cancels_revoke_cleanup_legs_or_stop()
    {
        var binding = _t.Binding("fleet-agent1-55555555", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        using var disconnected = new CancellationTokenSource();
        disconnected.Cancel();

        var revoke = await _t.Runtime().RevokeAsync("agent1", disconnected.Token);

        Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Equal("revoked", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Planned_gateway_restart_rebinds_a_durably_stopped_binding_on_start()
    {
        var binding = _t.Binding("fleet-agent1-66666666", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@planned";

        // The status refresh leaves a durably stopped, quiescent binding alone.
        await runtime.CheckGenerationAsync("agent1", default);
        Assert.Equal("stopped", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);

        var start = await runtime.StartAsync("agent1", default);

        Assert.True(start.Ok, start.Code + ":" + string.Join(",", start.Errors));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("applied", record.State);
        Assert.Equal("gateway-2@planned", record.Applied!.GatewayGeneration);
    }

    [Fact]
    public async Task Planned_gateway_restart_never_permanently_refuses_reapply()
    {
        var binding = _t.Binding("fleet-agent1-77777777", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t._host.Generation = "gateway-2@planned";

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Code + ":" + string.Join(",", apply.Errors));
        Assert.NotEqual("gateway_generation_changed", apply.Code);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.DoesNotContain("gateway_generation_changed", record.Errors);
        Assert.Equal("gateway-2@planned", record.Applied!.GatewayGeneration);
    }

    [Fact]
    public async Task Unplanned_generation_change_on_a_running_binding_still_holds_and_fans_out_on_start()
    {
        var binding = _t.Binding("fleet-agent1-88888888", Guid.NewGuid());
        // Recorded stopped, but the sandbox is running: not a planned restart.
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t._host.Generation = "gateway-2@unexpected";

        var start = await _t.Runtime().StartAsync("agent1", default);

        Assert.Equal((409, "gateway_generation_changed"), (start.Status, start.Code));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Equal(binding.GatewayGeneration, record.Applied!.GatewayGeneration);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "start"));
    }

    [Fact]
    public async Task Start_whose_daemon_acknowledges_while_the_sandbox_is_in_error_is_never_recorded_applied()
    {
        var binding = _t.Binding("fleet-agent1-99999999", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        // The PID/cmdline check passes, but the exact UUID is then observed in Error (review F4).
        _t._cli.OnExec = script =>
        {
            if (script == StockOpenShellRuntime.DaemonReadyScript) _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Error");
            return 0;
        };
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);

        var result = await _t.Runtime().StartAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.NotEqual("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
    }

    private Agent StoppedPeer(string name, string sandbox)
    {
        var binding = _t.Binding(sandbox, Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        var agent = _t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding });
        agent.Name = name;
        agent.ShortName = name;
        agent.ContainerName = name;
        _t.Enroll(binding, name);
        return _t._store.Add(agent);
    }

    [Fact]
    public async Task Planned_gateway_restart_starts_every_stopped_agent_after_the_first_is_rebound()
    {
        StoppedPeer("agent1", "fleet-agent1-aaaaaaaa");
        StoppedPeer("agent2", "fleet-agent2-bbbbbbbb");
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@planned";

        var first = await runtime.StartAsync("agent1", default);
        var second = await runtime.StartAsync("agent2", default);

        // Review F5/N1: the running, rebound first agent is a verified current-generation peer.
        Assert.True(first.Ok, first.Code + ":" + string.Join(",", first.Errors));
        Assert.True(second.Ok, second.Code + ":" + string.Join(",", second.Errors));
        foreach (var name in new[] { "agent1", "agent2" })
        {
            var record = ManagedRecord.Parse(_t._store.Get(name).AppliedRuntimeBinding);
            Assert.Equal("applied", record.State);
            Assert.Equal("gateway-2@planned", record.Applied!.GatewayGeneration);
        }
    }

    private void DeletedPeer(string appliedKind, string state)
    {
        // Its sandbox was deleted after the clean revoke/handoff: it no longer exists on the gateway.
        var binding = _t.Binding("fleet-agent2-cccccccc", Guid.NewGuid());
        var agent = _t.ManagedAgent(desired: appliedKind, applied: appliedKind, record: new ManagedRecord { State = state, Applied = binding });
        agent.Name = agent.ShortName = agent.ContainerName = "agent2";
        _t._store.Add(agent);
        var path = Path.Combine(_t._dir, "managed", "authority", "agent2.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new System.Text.Json.Nodes.JsonObject { ["State"] = appliedKind == "docker" ? "retired" : "revoked", ["Agent"] = "agent2" }.ToJsonString());
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("openshell")]
    public async Task Deleted_retired_sandbox_of_another_agent_never_blocks_a_planned_restart(string peerKind)
    {
        StoppedPeer("agent1", "fleet-agent1-dddddddd");
        DeletedPeer(peerKind, "revoked");
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@planned";

        var start = await runtime.StartAsync("agent1", default);

        Assert.True(start.Ok, start.Code + ":" + string.Join(",", start.Errors));
        Assert.Equal("gateway-2@planned", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Applied!.GatewayGeneration);
    }

    [Fact]
    public async Task Deleted_peer_is_proven_absent_only_after_every_stock_list_page()
    {
        StoppedPeer("agent1", "fleet-agent1-d1d1d1d1");
        DeletedPeer("openshell", "revoked");
        _t._cli.ListPages = [["fleet-agent1-d1d1d1d1"], ["fleet-other-00000000"]];
        _t._host.Generation = "gateway-2@planned";

        var start = await _t.Runtime().StartAsync("agent1", default);

        Assert.True(start.Ok, start.Code + ":" + string.Join(",", start.Errors));
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "list", "-o", "json", "--page-token", "p1"]));
    }

    [Fact]
    public async Task Peer_listed_on_the_second_stock_page_is_never_treated_as_deleted()
    {
        StoppedPeer("agent1", "fleet-agent1-d2d2d2d2");
        DeletedPeer("openshell", "revoked");
        // Review F9/F6r: the get fails, but the gateway still lists the name, on page 2.
        _t._cli.ListPages = [["fleet-agent1-d2d2d2d2"], ["fleet-agent2-cccccccc"]];
        _t._host.Generation = "gateway-2@planned";

        var start = await _t.Runtime().StartAsync("agent1", default);

        Assert.Equal((409, "gateway_generation_changed"), (start.Status, start.Code));
    }

    [Fact]
    public async Task Needs_attention_record_whose_sandbox_is_verifiably_deleted_is_cleared_by_revoke()
    {
        // Review F6r: the sandbox is gone from every stock list page; revoke is the supported clear.
        var binding = _t.Binding("fleet-agent1-f6f6f6f6", Guid.NewGuid());
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Errors = ["stop_unresolved"] }));
        _t._cli.ListPages = [["fleet-other-00000000"], ["fleet-other-11111111"]];

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("revoked", 0), (record.State, record.Errors.Count));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop") || Verb(c, "start"));
    }

    [Fact]
    public async Task Needs_attention_record_whose_sandbox_is_still_listed_on_page_two_stays_held_after_revoke()
    {
        var binding = _t.Binding("fleet-agent1-f7f7f7f7", Guid.NewGuid());
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Errors = ["stop_unresolved"] }));
        _t._cli.ListPages = [["fleet-other-00000000"], [binding.SandboxName]];

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.Equal("revoke_unresolved", revoke.Code);
        Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Unresolved_peer_with_a_deleted_sandbox_still_holds_a_planned_restart()
    {
        StoppedPeer("agent1", "fleet-agent1-eeeeeeee");
        DeletedPeer("docker", "needs_attention");
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@planned";

        var start = await runtime.StartAsync("agent1", default);

        Assert.Equal((409, "gateway_generation_changed"), (start.Status, start.Code));
    }

    [Theory]
    [InlineData("staging_unconfirmed")]
    [InlineData("activation_unconfirmed")]
    public async Task Client_disconnect_during_staging_or_activation_still_reaches_the_bounded_stop(string code)
    {
        // Review A8: the client token no longer reaches Start's legs; the never-ready poll ends on Start's own
        // bound and still reaches the bounded stop (review N2).
        _t._cli.OnExec = script => code == "staging_unconfirmed"
            ? (script.Contains("-f \"$HOME/.fleet-staged\"") && !script.Contains('!') ? 1 : 0)
            : (script == StockOpenShellRuntime.DaemonReadyScript ? 1 : 0);
        var binding = _t.Binding("fleet-agent1-ffffffff", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);
        using var disconnected = new CancellationTokenSource();
        disconnected.Cancel();

        var result = await _t.Runtime().StartAsync("agent1", disconnected.Token);

        Assert.Equal(code, result.Code);
        Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("dangling_link")]
    [InlineData("fifo")]
    public void Authority_retirement_never_treats_an_unsafe_entry_as_absent(string kind)
    {
        var path = _t.AuthorityPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (kind == "fifo" && !OperatingSystem.IsLinux()) return;
        switch (kind)
        {
            case "directory": Directory.CreateDirectory(path); break;
            case "dangling_link": File.CreateSymbolicLink(path, Path.Combine(_t._dir, "missing.json")); break;
            default: System.Diagnostics.Process.Start("mkfifo", path)!.WaitForExit(); break;
        }

        // Review F6: only verified absence or a verified retired write is success.
        Assert.False(_t.Runtime().RetireAuthority("agent1"));
        Assert.Null(StockOpenShellRuntime.AuthorityEntry(Path.Combine(_t._dir, "managed", "authority", "absent.json")));
    }

    [Fact]
    public async Task Directory_in_place_of_the_authority_keeps_the_docker_handoff_unresolved()
    {
        var binding = _t.Binding("fleet-agent1-f6f6f6f6", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t.WriteAuthority("active");
        _t._cli.OnExec = _ =>
        {
            File.Delete(_t.AuthorityPath());
            Directory.CreateDirectory(_t.AuthorityPath());
            return 0;
        };

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal("docker_handoff_unresolved", result.Code);
        Assert.Contains("authority_retire_failed", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Errors);
    }

    [Fact]
    public async Task Failed_authority_retirement_is_persisted_and_reported_as_an_unresolved_docker_handoff()
    {
        var binding = _t.Binding("fleet-agent1-12121212", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t.WriteAuthority("active");
        // After the cut, the revoked record becomes unreadable, so retirement after the handoff fails (review F6).
        _t._cli.OnExec = script =>
        {
            File.WriteAllText(_t.AuthorityPath(), "{not json");
            return 0;
        };

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.Equal("docker_handoff_unresolved", result.Code);
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains("authority_retire_failed", record.Errors);
        // Review R3: the swap that clears Operation already carries a hold (pending retirement, then the failure);
        // no clean record is ever released.
        Assert.All(_t._store.History.Select(ManagedRecord.Parse).Where(r => r.Operation is null), r =>
        {
            Assert.Equal("needs_attention", r.State);
            Assert.True(r.Errors.Contains(StockOpenShellRuntime.RetirePending) || r.Errors.Contains("authority_retire_failed"));
        });
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("throw")]
    public async Task Failed_final_binding_cas_keeps_ordinary_admission_held_for_a_docker_handoff(string failure)
    {
        var binding = _t.Binding("fleet-agent1-r2f1f1f1", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t.WriteAuthority("active");
        if (failure == "reject") _t._store.RejectKind = "docker"; else _t._store.ThrowKind = "docker";
        var directory = Path.GetDirectoryName(_t.AuthorityPath())!;
        var hold = new StockRuntimeMutationHold(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:AuthorityDirectory"] = directory }).Build());

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        // Review R2: the Docker binding never committed, so the retired record is revoked again and the hold stands.
        Assert.False(result.Ok);
        Assert.Equal(failure == "reject" ? "managed_state_changed_during_apply" : "apply_state_unknown", result.Code);
        Assert.Equal("openshell", _t._store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("revoked", AuthorityState());
        Assert.False(Fleet.Shared.Security.StockAuthoritySource.IsRetired(directory, "agent1"));
        Assert.True(hold.HoldsOrdinaryTraffic("agent1"));
    }

    [Fact]
    public async Task Committed_docker_handoff_still_retires_the_authority_and_releases_admission()
    {
        var binding = _t.Binding("fleet-agent1-r2c0c0c0", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t.WriteAuthority("active");
        var directory = Path.GetDirectoryName(_t.AuthorityPath())!;
        var hold = new StockRuntimeMutationHold(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:AuthorityDirectory"] = directory }).Build());

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("retired", AuthorityState());
        Assert.False(hold.HoldsOrdinaryTraffic("agent1"));
    }

    private StockRuntimeMutationHold Hold() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["StockOpenShell:AuthorityDirectory"] = Path.GetDirectoryName(_t.AuthorityPath())! }).Build());

    private void DockerHandoff(string suffix)
    {
        var binding = _t.Binding("fleet-agent1-" + suffix, Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t.WriteAuthority("active");
    }

    [Fact]
    public async Task Ordinary_admission_stays_held_while_the_final_docker_binding_commit_is_awaited()
    {
        DockerHandoff("r6a0a0a0");
        var hold = Hold();
        var during = new List<bool>();
        // Interceptor: the final Docker-binding swap is in flight (not yet committed) across real awaits.
        _t._store.OnSwap = async (kind, _) =>
        {
            if (kind != "docker") return;
            during.Add(hold.HoldsOrdinaryTraffic("agent1"));
            await Task.Yield();
            during.Add(hold.HoldsOrdinaryTraffic("agent1"));
            await Task.Delay(20);
            during.Add(hold.HoldsOrdinaryTraffic("agent1"));
            Assert.Equal("openshell", _t._store.Get("agent1").AppliedRuntimeKind);
        };

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
        Assert.Equal(3, during.Count);
        Assert.All(during, Assert.True);
        // Released only after the durable commit, and the follow-up clears the pending marker.
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("retired", AuthorityState());
        Assert.False(hold.HoldsOrdinaryTraffic("agent1"));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.DoesNotContain(StockOpenShellRuntime.RetirePending, record.Errors);
        Assert.NotEqual("needs_attention", record.State);
    }

    [Fact]
    public async Task Concurrent_writers_get_conflict_until_the_docker_handoff_final_cas_clears_the_claim()
    {
        DockerHandoff("h1a0a0a0");
        var runtime = _t.Runtime();
        var probes = new List<(string Moment, string Verb, int Status, bool Unchanged)>();
        var inFlight = false;
        async Task Probe(string moment)
        {
            inFlight = true;
            try
            {
                var before = _t._store.Get("agent1").AppliedRuntimeBinding;
                var kind = _t._store.Get("agent1").AppliedRuntimeKind;
                foreach (var (verb, call) in new (string, Func<Task<StockOpenShellRuntime.Result>>)[]
                {
                    ("revoke", () => runtime.RevokeAsync("agent1", default)),
                    ("apply", () => runtime.ApplyAsync("agent1", default)),
                    ("start", () => runtime.StartAsync("agent1", default)),
                })
                {
                    var r = await call();
                    probes.Add((moment, verb, r.Status, _t._store.Get("agent1").AppliedRuntimeBinding == before
                        && _t._store.Get("agent1").AppliedRuntimeKind == kind));
                }
            }
            finally { inFlight = false; }
        }
        // Review H1: interceptor over the actual RunAsync — while the held Docker commit is in flight, and again after it
        // committed and the authority was retired but before the final CAS lands, other writers try to take the agent.
        _t._store.OnSwap = async (kind, next) =>
        {
            if (inFlight) return;
            if (kind == "docker") await Probe("held_commit");
            else if (kind is null && _t._store.Get("agent1").AppliedRuntimeKind == "docker" && !next.Contains(StockOpenShellRuntime.RetirePending))
            {
                Assert.Equal("retired", AuthorityState());
                Assert.NotNull(ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Operation);
                await Probe("retired_before_final_cas");
            }
        };

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
        Assert.Equal(6, probes.Count);
        Assert.All(probes, p => Assert.True(p.Status == 409 && p.Unchanged, p.Moment + "/" + p.Verb + ":" + p.Status));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Null(record.Operation);
        Assert.DoesNotContain(StockOpenShellRuntime.RetirePending, record.Errors);
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("retired", AuthorityState());
    }

    [Fact]
    public async Task Post_commit_swap_exception_is_resolved_by_rereading_and_completes_the_handoff()
    {
        DockerHandoff("v4c0c0c0");
        _t._store.ThrowAfterCommitKind = "docker";
        var hold = Hold();

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        // Review V4: the re-read shows the Docker binding committed, so retirement proceeds; no hold on Docker.
        Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("retired", AuthorityState());
        Assert.False(hold.HoldsOrdinaryTraffic("agent1"));
        Assert.DoesNotContain(StockOpenShellRuntime.RetirePending, ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Errors);
    }

    [Fact]
    public async Task Ambiguous_post_commit_swap_exception_keeps_the_hold_and_reports_needs_attention()
    {
        DockerHandoff("v4a0a0a0");
        _t._store.ThrowAfterCommitKind = "docker";
        _t._store.ThrowOnLoadAfterCommit = true;
        var hold = Hold();

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.Equal("needs_attention", result.Code);
        Assert.Contains("commit_ambiguous", result.Errors);
        // Never assumed either way: the authority is not retired and the stored record carries the pending hold.
        Assert.Equal("revoked", AuthorityState());
        Assert.True(hold.HoldsOrdinaryTraffic("agent1"));
        Assert.Contains(StockOpenShellRuntime.RetirePending, ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Errors);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("throw")]
    public async Task Failed_retirement_with_a_failed_follow_up_cas_keeps_traffic_held(string failure)
    {
        DockerHandoff("r6f0f0f0");
        _t._cli.OnExec = _ => { File.WriteAllText(_t.AuthorityPath(), "{not json"); return 0; };
        if (failure == "reject") _t._store.RejectFollowUp = true; else _t._store.ThrowFollowUp = true;
        var hold = Hold();

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.Equal("docker_handoff_unresolved", result.Code);
        Assert.Equal(["authority_retire_failed", "handoff_record_unfinalized"], result.Errors);
        // Fail-closed: the authority was never retired, and the committed record still shows the held pending marker.
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
        Assert.True(hold.HoldsOrdinaryTraffic("agent1"));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains(StockOpenShellRuntime.RetirePending, record.Errors);
    }

    [Fact]
    public async Task Successful_retirement_with_a_failed_follow_up_cas_leaves_a_visible_held_record()
    {
        DockerHandoff("r6b0b0b0");
        _t._store.RejectFollowUp = true;

        var result = await _t.Runtime().ApplyAsync("agent1", default);

        // Docker is durably committed before the authority retired; the stale record stays held and revocable.
        Assert.Equal("docker_handoff_unresolved", result.Code);
        Assert.Equal([StockOpenShellRuntime.RetirePending, "handoff_record_unfinalized"], result.Errors);
        Assert.Equal("retired", AuthorityState());
        Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Unfinalized_handoff_claim_is_held_as_interrupted_after_the_call_returns_never_stuck_in_progress()
    {
        DockerHandoff("h1b0b0b0");
        _t._store.RejectFollowUp = true;
        var runtime = _t.Runtime();
        Assert.Equal("docker_handoff_unresolved", (await runtime.ApplyAsync("agent1", default)).Code);
        // Review H1: the retained claim belongs to a call that has returned, so it reads as interrupted, not busy.
        Assert.NotNull(ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Operation);
        _t._store.RejectFollowUp = false;

        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.Equal("needs_attention", revoke.Code);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Null(record.Operation);
        Assert.Contains("interrupted_apply", record.Errors);
        Assert.Contains(StockOpenShellRuntime.RetirePending, record.Errors);
        var retry = await runtime.RevokeAsync("agent1", default);
        Assert.NotEqual("managed_operation_in_progress", retry.Code);
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, 0x800 | 0x20000 | 0x80000)]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, 0x800 | 0x20000 | 0x80000)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, 0x800 | 0x8000 | 0x80000)]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm, 0x800 | 0x8000 | 0x80000)]
    public void Retirement_no_follow_open_flags_match_the_process_architecture(System.Runtime.InteropServices.Architecture architecture, int expected)
    {
        // Review W6: arm64 O_NOFOLLOW is 0x8000 (0x20000 is O_LARGEFILE there and never refuses a link); x64 is 0x20000.
        Assert.Equal(expected, StockOpenShellRuntime.NoFollowOpenFlags(architecture));
    }

    [Fact]
    public void Retirement_no_follow_open_flags_refuse_an_unknown_architecture()
    {
        Assert.Null(StockOpenShellRuntime.NoFollowOpenFlags(System.Runtime.InteropServices.Architecture.Wasm));
        Assert.Null(StockOpenShellRuntime.NoFollowOpenFlags(System.Runtime.InteropServices.Architecture.S390x));
    }

    [Fact]
    public async Task Apply_never_writes_the_observer_password_into_the_shared_authority_directory()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var authority = Path.GetDirectoryName(_t.AuthorityPath())!;
        Directory.CreateDirectory(authority);
        File.WriteAllText(Path.Combine(authority, "agent1.observer.password"), "legacy-observer"); // pre-split layout
        var runtime = _t.Runtime(dockerStop: _ => { _t._host.DockerStates = ["exited"]; return Task.FromResult(true); });

        var result = await runtime.ApplyAsync("agent1", default);

        // Review W4: the authority directory Telegram/Temporal/Bridge mount holds records and control only.
        Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
        Assert.True(File.Exists(Path.Combine(authority, "agent1.control.password")));
        Assert.Empty(Directory.EnumerateFiles(authority, "*observer*"));
        var observerDir = Path.Combine(_t._dir, "managed", "observer");
        var observer = Path.Combine(observerDir, "agent1.observer.password");
        Assert.Equal(observer, Fleet.Shared.Security.StockAuthoritySource.RolePassword(authority, "agent1", "observer"));
        Assert.Equal(64, File.ReadAllText(observer).Length);
        Assert.NotEqual(File.ReadAllText(observer), File.ReadAllText(Path.Combine(authority, "agent1.control.password")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(observer));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(observerDir));
        }
    }

    [Fact]
    public void Observer_role_password_resolves_the_private_sibling_and_refuses_links()
    {
        var root = Directory.CreateTempSubdirectory("managed-w4-").FullName;
        try
        {
            var authority = Path.Combine(root, "managed", "authority");
            var observer = Path.Combine(root, "managed", "observer");
            Directory.CreateDirectory(authority);
            Assert.Equal(Path.Combine(authority, "agent1.control.password"),
                Fleet.Shared.Security.StockAuthoritySource.RolePassword(authority, "agent1", "control"));
            Assert.Equal(Path.Combine(observer, "agent1.observer.password"),
                Fleet.Shared.Security.StockAuthoritySource.RolePassword(authority + Path.DirectorySeparatorChar, "agent1", "observer"));
            Directory.CreateSymbolicLink(observer, authority); // a linked observer directory is never followed
            Assert.Equal("broker_credential_unavailable", Assert.Throws<Fleet.Shared.Security.StockIdentityDenied>(
                () => Fleet.Shared.Security.StockAuthoritySource.RolePassword(authority, "agent1", "observer")).Message);
            Assert.Throws<Fleet.Shared.Security.StockIdentityDenied>(
                () => Fleet.Shared.Security.StockAuthoritySource.RolePassword(Path.GetPathRoot(root)!, "agent1", "observer"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private ManagedBinding HandedOffDocker(string state, string authority, params string[] errors)
    {
        var binding = _t.Binding("fleet-agent1-r2r2r2r2", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "docker", record: new ManagedRecord { State = state, Applied = binding, Errors = [.. errors] }));
        _t.WriteAuthority(authority);
        return binding;
    }

    private string AuthorityState() =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!["State"]!.GetValue<string>();

    [Fact]
    public async Task Revoke_on_a_handed_off_docker_agent_keeps_its_authority_retired()
    {
        HandedOffDocker("revoked", "retired");

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
        Assert.Equal("retired", AuthorityState());
        Assert.Equal("revoked", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Revoke_retries_a_failed_authority_retirement_and_leaves_the_agent_docker_retired()
    {
        // Review R2: apply refuses after the handoff; revoke is the supported retire path.
        HandedOffDocker("needs_attention", "revoked", "authority_retire_failed");

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
        Assert.Equal("retired", AuthorityState());
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("revoked", 0), (record.State, record.Errors.Count));
        Assert.Equal("docker", _t._store.Get("agent1").AppliedRuntimeKind);
    }

    [Fact]
    public async Task Revoke_whose_retirement_still_fails_keeps_the_docker_handoff_unresolved()
    {
        HandedOffDocker("needs_attention", "revoked", "authority_retire_failed");
        File.Delete(_t.AuthorityPath());
        Directory.CreateDirectory(_t.AuthorityPath());

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.Equal("docker_handoff_unresolved", revoke.Code);
        Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("listed_on_page_two", false)]
    [InlineData("present", false)]
    [InlineData("unobservable", false)]
    public async Task Failed_pre_uuid_docker_apply_is_cleared_by_revoke_only_once_its_owned_name_is_proven_absent(string gateway, bool clears)
    {
        // Review A1: the Docker->OpenShell apply fails before any sandbox UUID is recorded.
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = null;
        var runtime = _t.Runtime(dockerStop: _ => Task.FromResult(true));
        Assert.Equal("docker_observation_failed", (await runtime.ApplyAsync("agent1", default)).Code);
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", held.State);
        var name = held.Candidate!.SandboxName;
        Assert.True(string.IsNullOrEmpty(held.Candidate.SandboxId));
        switch (gateway)
        {
            case "absent": _t._cli.ListPages = [["fleet-other-00000000"], ["fleet-other-11111111"]]; break;
            case "listed_on_page_two": _t._cli.ListPages = [["fleet-other-00000000"], [name]]; break;
            case "present": _t._cli.Sandboxes[name] = (Guid.NewGuid().ToString(), "Ready"); break;
            default: _t._cli.ListFails = true; break;
        }
        _t._cli.Calls.Clear();

        var revoke = await runtime.RevokeAsync("agent1", default);

        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        if (clears)
        {
            Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
            Assert.Equal(("revoked", 0), (record.State, record.Errors.Count));
            Assert.Null(record.Candidate);
            Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "list", "-o", "json", "--page-token", "p1"]));
        }
        else
        {
            Assert.False(revoke.Ok);
            Assert.Equal("needs_attention", record.State);
            Assert.Equal(name, record.Candidate!.SandboxName);
        }
        // No name guessing and no destructive action against whatever owns the name.
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop") || Verb(c, "start") || Verb(c, "delete") || c.Args.Contains("detach"));
    }

    [Theory]
    [InlineData("sandbox_unobservable", false)]
    [InlineData("quiescence_unobservable", false)]
    [InlineData("running", true)]
    public async Task Status_refresh_never_reads_an_unobservable_stopped_binding_as_an_unexpected_restart(string observed, bool holds)
    {
        // Review A5: the gateway restarted but is not ready yet, so the stopped sandbox cannot be observed.
        var binding = _t.Binding("fleet-agent1-a5a5a5a5", Guid.NewGuid());
        if (observed != "sandbox_unobservable") _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, observed == "running" ? "Ready" : "Stopped");
        if (observed == "quiescence_unobservable") _t._host.RunningContainers = null;
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        var before = _t._store.Get("agent1").AppliedRuntimeBinding;
        _t._host.Generation = "gateway-2@restarting";

        await _t.Runtime().CheckGenerationAsync("agent1", default);

        if (holds)
        {
            Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
            Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        }
        else
        {
            Assert.Equal(before, _t._store.Get("agent1").AppliedRuntimeBinding);
            Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop") || c.Args.Contains("detach"));
            Assert.Empty(_t._broker.Deletes);
        }
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("cancel")]
    public async Task Logs_or_cancel_after_a_planned_gateway_restart_never_hold_a_stopped_quiescent_binding(string action)
    {
        // Review A4: the guard gets the same planned-restart exemption as the status refresh.
        var binding = _t.Binding("fleet-agent1-a4a4a4a4", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@planned";

        var result = action == "logs" ? (await runtime.LogsAsync("agent1", 10, default)).Result : await runtime.CancelAsync("agent1", null, default);

        Assert.Equal((409, "gateway_generation_changed"), (result.Status, result.Code));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("stopped", 0, binding.GatewayGeneration), (record.State, record.Errors.Count, record.Applied!.GatewayGeneration));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop") || c.Args.Contains("detach"));
        Assert.Empty(_t._broker.Deletes);
        var start = await runtime.StartAsync("agent1", default);
        Assert.True(start.Ok, start.Code + ":" + string.Join(",", start.Errors));
    }

    [Theory]
    [InlineData("needs_attention")]
    [InlineData("revoked")]
    public async Task Repeated_logs_after_an_unexpected_restart_never_rerun_the_revoke_fanout(string state)
    {
        var binding = _t.Binding("fleet-agent1-a4b4c4d4", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = state == "revoked" ? "revoked" : "applied", Applied = binding }));
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@unexpected";
        if (state == "needs_attention")
        {
            // The first read holds the running binding and fans out once.
            Assert.Equal("gateway_generation_changed", (await runtime.LogsAsync("agent1", 10, default)).Result.Code);
            Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
            Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        }
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        var deletes = _t._broker.Deletes.Count;
        _t._cli.Calls.Clear();

        for (var i = 0; i < 3; i++)
            Assert.Equal("gateway_generation_changed", (await runtime.LogsAsync("agent1", 10, default)).Result.Code);

        var after = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal((held.State, held.Errors.Count), (after.State, after.Errors.Count));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop") || c.Args.Contains("detach"));
        Assert.Equal(deletes, _t._broker.Deletes.Count);
    }

    [Theory]
    [InlineData("openshell", "applied", "managed_binding_not_revoked")]
    [InlineData("openshell", "stopped", "managed_binding_not_revoked")]
    [InlineData("openshell", "revoked_with_errors", "managed_binding_not_revoked")]
    [InlineData("openshell", "needs_attention", "managed_state_unresolved")]
    [InlineData("docker", "held_candidate", "managed_state_unresolved")]
    [InlineData("openshell", "revoked", null)]
    [InlineData("docker", "revoked", null)]
    [InlineData("docker", "none", null)]
    public async Task Agent_delete_keeps_the_row_until_managed_state_is_cleanly_revoked(string applied, string state, string? keepRow)
    {
        // Review A2: DELETE /api/agents/{name} never drops managed state untracked behind a failed or ignored Stop.
        var binding = _t.Binding("fleet-agent1-a2a2a2a2", Guid.NewGuid());
        var record = state switch
        {
            "none" => null,
            "held_candidate" => new ManagedRecord { State = "needs_attention", Candidate = binding with { SandboxId = "" }, Errors = ["docker_observation_failed"] },
            "revoked_with_errors" => new ManagedRecord { State = "revoked", Applied = binding, Errors = ["revoke_unresolved"] },
            _ => new ManagedRecord { State = state, Applied = binding },
        };
        await using var harness = Fleet.Orchestrator.Tests.Services.ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
            ContainerName = "agent1", IsEnabled = true, RuntimeKind = "openshell", AppliedRuntimeKind = applied, AppliedRuntimeBinding = record?.Serialize() }));

        var (kept, guard) = await harness.Service.PrepareDeleteAsync("agent1");

        Assert.Equal(keepRow, kept);
        Assert.Equal(keepRow is null, guard is not null);
        Assert.Equal(0, harness.DockerRequestCount); // inspection alone never stops or removes anything
        if (keepRow is null && applied == "openshell")
        {
            var (fault, deprovision) = await DeleteWithGuardAsync(harness, guard!);
            Assert.Null(fault);
            Assert.True(deprovision!.Success, deprovision.Message);
            Assert.Equal(0, harness.DockerRequestCount); // a cleanly revoked binding was stopped by its revoke
        }
    }

    private static async Task<string?> BindingAsync(Fleet.Orchestrator.Tests.Services.ProvisioningHarness harness)
    {
        string? binding = null;
        await harness.MutateAsync(async db => binding = (await db.Agents.AsNoTracking().SingleAsync(a => a.Name == "agent1")).AppliedRuntimeBinding);
        return binding;
    }

    private async Task<Fleet.Orchestrator.Tests.Services.ProvisioningHarness> RevokedAgentAsync(string? kind = "openshell")
    {
        var harness = Fleet.Orchestrator.Tests.Services.ProvisioningHarness.Create();
        var revoked = new ManagedRecord { State = "revoked", Applied = _t.Binding("fleet-agent1-d1d1d1d1", Guid.NewGuid()) };
        await harness.SeedAsync(db => db.Agents.Add(new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
            ContainerName = "agent1", IsEnabled = true, RuntimeKind = "openshell", AppliedRuntimeKind = kind, AppliedRuntimeBinding = revoked.Serialize() }));
        return harness;
    }

    private static async Task<(string? Fault, ProvisionResult? Deprovision)> DeleteWithGuardAsync(Fleet.Orchestrator.Tests.Services.ProvisioningHarness harness, ManagedDeleteGuard guard)
    {
        (string?, ProvisionResult?) result = ("not-run", null);
        await harness.MutateAsync(async db => result = await guard.DeleteAsync(db, await db.Agents.SingleAsync(a => a.Name == "agent1")));
        return result;
    }

    private static async Task<bool> RowExistsAsync(Fleet.Orchestrator.Tests.Services.ProvisioningHarness harness)
    {
        var exists = false;
        await harness.MutateAsync(async db => exists = await db.Agents.AnyAsync(a => a.Name == "agent1"));
        return exists;
    }

    private static bool StopsOrRemoves(string request) => request.EndsWith("/stop", StringComparison.Ordinal) || request.StartsWith("DELETE ", StringComparison.Ordinal);

    [Fact]
    public async Task Agent_delete_conflicts_when_a_concurrent_apply_claims_the_binding_after_inspection()
    {
        // Review A2: inspection passed on a clean revoke, then an apply claimed the row before the delete commit.
        await using var harness = await RevokedAgentAsync();
        var (kept, guard) = await harness.Service.PrepareDeleteAsync("agent1");
        Assert.Null(kept);
        var applying = (ManagedRecord.Parse(await BindingAsync(harness)) with { Operation = "apply:other-process" }).Serialize();
        await harness.MutateAsync(async db =>
            await db.Agents.Where(a => a.Name == "agent1").ExecuteUpdateAsync(u => u.SetProperty(a => a.AppliedRuntimeBinding, applying)));

        Assert.Equal(ManagedDeleteGuard.Changed, (await DeleteWithGuardAsync(harness, guard!)).Fault);

        Assert.True(await RowExistsAsync(harness));
        Assert.Equal(applying, await BindingAsync(harness)); // the apply's claim is untouched
        Assert.Equal(0, harness.DockerRequestCount);
    }

    [Fact]
    public async Task Docker_agent_delete_conflicts_before_any_stop_or_remove_when_a_concurrent_apply_claims_it()
    {
        // Review A2 (d752ca65): a Docker-applied agent with a live container; an apply's claim CAS commits between
        // inspection and delete. The delete refuses before touching Docker, so the apply keeps its runtime.
        await using var harness = await EmptyDockerAgentAsync();
        harness.ExistingContainers.Add("agent1");
        var (kept, guard) = await harness.Service.PrepareDeleteAsync("agent1");
        Assert.Null(kept);
        var applying = new ManagedRecord { State = "applying", Operation = "apply:other-process:synthetic" }.Serialize();
        await harness.MutateAsync(async db =>
            await db.Agents.Where(a => a.Name == "agent1").ExecuteUpdateAsync(u => u.SetProperty(a => a.AppliedRuntimeBinding, applying)));

        var (fault, deprovision) = await DeleteWithGuardAsync(harness, guard!);

        Assert.Equal(ManagedDeleteGuard.Changed, fault);
        Assert.Null(deprovision);
        Assert.Equal(0, harness.DockerRequestCount); // no inspect, stop or remove
        Assert.Contains("agent1", harness.ExistingContainers);
        Assert.True(await RowExistsAsync(harness));
        Assert.Equal(applying, await BindingAsync(harness));
    }

    [Fact]
    public async Task Docker_agent_delete_never_stops_a_concurrently_applied_OpenShell_replacement()
    {
        // Review D1 (d752ca65): inspect a Docker agent, then an OpenShell apply commits its replacement binding. The
        // delete refuses without stopping that sandbox, through the actual adapter, or touching Docker.
        var replacement = _t.Binding("fleet-agent1-e5e5e5e5", Guid.NewGuid());
        _t._cli.Sandboxes[replacement.SandboxName] = (replacement.SandboxId, "Ready");
        var applied = new ManagedRecord { State = "applied", Applied = replacement };
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: applied));
        var runtime = _t.Runtime();
        await using var harness = Fleet.Orchestrator.Tests.Services.ProvisioningHarness.Create(extraServices: s => s.AddSingleton(runtime));
        harness.ExistingContainers.Add("agent1");
        await harness.SeedAsync(db => db.Agents.Add(new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
            ContainerName = "agent1", IsEnabled = true, RuntimeKind = "openshell", AppliedRuntimeKind = "docker", AppliedRuntimeBinding = null }));
        var (kept, guard) = await harness.Service.PrepareDeleteAsync("agent1");
        Assert.Null(kept);
        var appliedJson = applied.Serialize();
        await harness.MutateAsync(async db => await db.Agents.Where(a => a.Name == "agent1").ExecuteUpdateAsync(u => u
            .SetProperty(a => a.AppliedRuntimeBinding, appliedJson).SetProperty(a => a.AppliedRuntimeKind, "openshell")));

        var (fault, deprovision) = await DeleteWithGuardAsync(harness, guard!);

        Assert.Equal(ManagedDeleteGuard.Changed, fault);
        Assert.Null(deprovision);
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop"));
        Assert.Equal("Ready", _t._cli.Sandboxes[replacement.SandboxName].Phase);
        Assert.Equal(0, harness.DockerRequestCount);
        Assert.True(await RowExistsAsync(harness));
        Assert.Equal(appliedJson, await BindingAsync(harness));
    }

    [Theory]
    [InlineData("openshell")]
    [InlineData("docker")] // a default-off Docker agent: nothing is ever written to its binding outside the delete
    public async Task Agent_delete_removes_exactly_the_inspected_row_and_persists_no_claim_before_it(string kind)
    {
        await using var harness = kind == "docker" ? await EmptyDockerAgentAsync() : await RevokedAgentAsync();
        if (kind == "docker") harness.ExistingContainers.Add("agent1");
        var inspected = await BindingAsync(harness);

        var (kept, guard) = await harness.Service.PrepareDeleteAsync("agent1");

        Assert.Null(kept);
        Assert.Equal(inspected, await BindingAsync(harness)); // no claim survives between inspection and delete
        var (fault, deprovision) = await DeleteWithGuardAsync(harness, guard!);
        Assert.Null(fault);
        Assert.True(deprovision!.Success, deprovision.Message);
        Assert.False(await RowExistsAsync(harness));
        if (kind == "docker")
        {
            Assert.Equal(2, harness.DockerRequests.Count(StopsOrRemoves)); // stop, then remove, under the lock
            Assert.Empty(harness.ExistingContainers);
        }
        else Assert.Equal(0, harness.DockerRequestCount);
    }

    [Fact]
    public async Task Deprovision_failure_under_delete_ownership_restores_the_binding_and_stays_retryable()
    {
        // Review D1: an exception after the guard took ownership and before the delete commits rolls back: the row
        // and its (empty) binding are as inspected, Docker was not touched, and the agent stays deletable.
        await using var harness = await EmptyDockerAgentAsync();
        harness.ExistingContainers.Add("agent1");
        harness.Service.BeforeDeprovision = () => throw new HttpRequestException("synthetic deprovision failure");
        var (_, guard) = await harness.Service.PrepareDeleteAsync("agent1");

        await Assert.ThrowsAsync<HttpRequestException>(() => DeleteWithGuardAsync(harness, guard!));

        Assert.Null(await BindingAsync(harness));
        Assert.True(await RowExistsAsync(harness));
        Assert.Equal(0, harness.DockerRequestCount);
        harness.Service.BeforeDeprovision = null;
        var (kept, retry) = await harness.Service.PrepareDeleteAsync("agent1"); // nothing left behind holds it
        Assert.Null(kept);
        Assert.Null((await DeleteWithGuardAsync(harness, retry!)).Fault);
        Assert.False(await RowExistsAsync(harness));
        Assert.Empty(harness.ExistingContainers);
    }

    [Fact]
    public async Task Agent_delete_reports_a_database_outage_as_unavailable_not_a_conflict()
    {
        // Review D2: an unreachable/broken store is 503, never managed_state_changed_during_delete, and nothing is deprovisioned.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(); // an empty database: every statement against agents fails
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(connection).Options);
        var deprovisions = 0;
        var guard = new ManagedDeleteGuard(1, null, "docker", _ => { deprovisions++; return Task.FromResult(ProvisionResult.Ok("agent1", "synthetic")); });

        var (fault, _) = await guard.DeleteAsync(db, new Agent { Id = 1, Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic", ContainerName = "agent1" });

        Assert.Equal(ManagedDeleteGuard.Unavailable, fault);
        Assert.Equal(0, deprovisions);
    }

    private static async Task<Fleet.Orchestrator.Tests.Services.ProvisioningHarness> EmptyDockerAgentAsync()
    {
        var harness = Fleet.Orchestrator.Tests.Services.ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(new Agent { Name = "agent1", DisplayName = "Synthetic", Role = "synthetic", Model = "synthetic",
            ContainerName = "agent1", IsEnabled = true, AppliedRuntimeKind = "docker", AppliedRuntimeBinding = null }));
        return harness;
    }
}

/// <summary>
/// #502 subscription lifecycle over the synthetic adapter fakes: persisted workspace scoping with same-named resources in
/// two workspaces, cleanup after source loss/change, clock-driven pending/running expiry, and actual Docker/API
/// retirement transitions with retained history. Source checks, not runtime qualification (#493/#501).
/// </summary>
public sealed class ManagedStockSubscriptionLifecycleTests : IDisposable
{
    private const string ProviderId = StockSubscriptionBindingTests.ProviderId;
    private const string Provider = "agent1-codex-sub";
    private static readonly string SyntheticProfileHash = SyntheticProfile.Digest;
    private readonly ManagedStockRuntimeAdapterTests _t = new(PrivateBase.Create("managed-sub-"));
    private readonly MutableClock _clock = new() { Now = DateTimeOffset.UtcNow };
    private readonly ScopedCli _cli;

    public ManagedStockSubscriptionLifecycleTests()
    {
        _cli = new ScopedCli(_clock);
        // The agent's provider in ws-a, and a same-named foreign provider in ws-b that must never be touched.
        _cli.Providers[("ws-a", Provider)] = (ProviderId, "codex-subscription-native", ["CODEX_AUTH_ACCESS_TOKEN"]);
        _cli.Providers[("ws-b", Provider)] = ("9a9a9a9a-0000-4000-8000-000000000001", "codex-subscription-native", ["CODEX_AUTH_ACCESS_TOKEN"]);
        _t._store.ProfileHashes = ManagedStockRuntimeAdapterTests.FakeStore.PinnedProfiles.TrimEnd('}')
            + ",\"codex-subscription-native\":\"" + SyntheticProfileHash + "\"}";
    }

    public void Dispose() => _t.Dispose();

    private string WriteSource(string workspace = "ws-a", DateTimeOffset? expires = null) =>
        StockSubscriptionBindingTests.WriteSource(_t._dir, Source(workspace, expires));

    private System.Text.Json.Nodes.JsonObject Source(string workspace = "ws-a", DateTimeOffset? expires = null)
    {
        var source = StockSubscriptionBindingTests.Source(workspace: workspace, expires: expires ?? _clock.Now.AddHours(2), profile: SyntheticProfileHash);
        source["gatewayGeneration"] = _t._host.Generation;
        return source;
    }

    private Agent SubscriptionAgent(string desired, string applied, ManagedRecord? record = null)
    {
        var agent = _t.ManagedAgent(desired, applied, record);
        agent.NativeAuthMode = "subscription";
        agent.ProviderApiKeyEnvRef = null;
        return agent;
    }

    private StockOpenShellRuntime Runtime(Func<string, Task<bool>>? dockerStop = null, Func<string, bool>? directorySync = null) =>
        _t.Runtime(dockerStop, cli: _cli, clock: _clock, directorySync: directorySync);

    private ManagedBinding SubBinding(string name, string phase = "Ready", string? workspace = "ws-a", DateTimeOffset? expires = null, bool attached = true)
    {
        var id = Guid.NewGuid().ToString();
        if (workspace is not null)
        {
            _cli.Cli(workspace).Sandboxes[name] = (id, phase);
            if (attached) _cli.Attached[(workspace, name)] = [Provider];
        }
        return new ManagedBinding(name, id, Guid.NewGuid(), "stock-agent:synthetic", 1, Fleet.Orchestrator.Endpoints.SandboxPolicyValidation.Hash(_t._store.PolicyYaml), "subscription", "", Provider, _t._host.Generation)
        {
            SandboxWorkspace = workspace,
            Subscription = new ManagedSubscription("ws-a", ProviderId, "synthetic-account_01", Guid.Parse(StockSubscriptionBindingTests.Owner),
                new string('d', 64), expires ?? _clock.Now.AddHours(2)),
        };
    }

    private bool ResourceCall((string? Ws, List<string> Args, IReadOnlyDictionary<string, string>? Env) c) => c.Args[0] is not "--version";

    // ── C3: first apply, persisted scopes, no provider mutation, same-named foreign workspace untouched ──

    [Fact]
    public async Task First_apply_persists_both_scopes_and_scopes_every_resource_call()
    {
        WriteSource();
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        var applied = record.Applied!;
        Assert.Equal(("subscription", "ws-a", Provider, ""), (applied.AuthMode, applied.SandboxWorkspace, applied.Provider, applied.KeyRef));
        Assert.Equal(("ws-a", ProviderId, "synthetic-account_01"), (applied.Subscription!.ProviderWorkspace, applied.Subscription.ProviderId, applied.Subscription.AccountId));
        Assert.Empty(applied.ServiceProviders);
        // Every resource call carries exactly --workspace ws-a; nothing ran in ws-b or unscoped.
        Assert.All(_cli.Calls.Where(ResourceCall), c => Assert.Equal("ws-a", c.Ws));
        Assert.Empty(_cli.Cli("ws-b").Calls);
        // No provider create/update/import/rotate/delete and no credential value in any child environment.
        Assert.DoesNotContain(_cli.Calls, c => c.Args[0] == "provider" && c.Args[1] is "create" or "update" or "delete" or "get");
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Contains("rotate") || c.Args.Contains("configure"));
        Assert.All(_cli.Calls, c => Assert.Null(c.Env));
        var create = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Equal([Provider], create.Args.Select((a, i) => (a, i)).Where(p => p.a == "--provider").Select(p => create.Args[p.i + 1]));
        // The candidate (with both scopes) was checkpointed before the create.
        var first = ManagedRecord.Parse(_t._store.History.First(h => ManagedRecord.Parse(h).Candidate is not null));
        Assert.Equal(("ws-a", ProviderId), (first.Candidate!.SandboxWorkspace, first.Candidate.Subscription!.ProviderId));
        // Workload bundle: nonsecret selection only, no API-key reference; enrollment capped at the outside expiry.
        var settings = System.Text.Json.Nodes.JsonNode.Parse(_cli.Cli("ws-a").Uploads.Last(u => u.Remote == "/home/agent/app/appsettings.json").Content!)!;
        Assert.Equal("subscription", settings["Agent"]!["NativeAuthMode"]!.GetValue<string>());
        Assert.Null(settings["Agent"]!["ProviderApiKeyEnvRef"]);
        Assert.Equal("synthetic-account_01", settings["StockOpenShell"]!["SubscriptionAccountId"]!.GetValue<string>());
        var authority = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!;
        Assert.True(authority["ExpiresAt"]!.GetValue<DateTimeOffset>() <= applied.Subscription.ExpiresAt);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("wrong_type")]
    [InlineData("extra_key")]
    [InlineData("wrong_uuid")]
    [InlineData("reauthorize")]
    [InlineData("window")]
    [InlineData("generation")]
    [InlineData("profile")]
    public async Task Unverifiable_provider_refuses_before_any_mutation(string fault)
    {
        WriteSource();
        switch (fault)
        {
            case "absent": _cli.Providers.Remove(("ws-a", Provider)); break;
            case "wrong_type": _cli.Providers[("ws-a", Provider)] = (ProviderId, "openai", ["CODEX_AUTH_ACCESS_TOKEN"]); break;
            case "extra_key": _cli.Providers[("ws-a", Provider)] = (ProviderId, "codex-subscription-native", ["CODEX_AUTH_ACCESS_TOKEN", "OPENAI_API_KEY"]); break;
            case "wrong_uuid": _cli.Providers[("ws-a", Provider)] = ("9a9a9a9a-0000-4000-8000-000000000002", "codex-subscription-native", ["CODEX_AUTH_ACCESS_TOKEN"]); break;
            case "reauthorize": _cli.RefreshState = ("reauthorization_required", "reauthorize", "oauth_invalid_grant"); break;
            case "window": _cli.NextRefreshIn = TimeSpan.FromSeconds(100); break;
            case "generation": _t._host.Generation = "gateway-2@later"; break;
            case "profile": _cli.ProfileExport = SyntheticProfile.Mutated; break;
        }
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "docker"));
        var dockerStops = 0;

        var result = await Runtime(_ => { dockerStops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal(409, result.Status);
        Assert.Equal(fault == "profile" ? "provider_profile_hash_mismatch:codex-subscription-native" : "subscription_provider_unverified", result.Code);
        Assert.Equal(0, dockerStops);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        var unchanged = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("none", null, null, "docker"), (unchanged.State, unchanged.Candidate, unchanged.Applied, _t._store.Get("agent1").AppliedRuntimeKind));
    }

    [Fact]
    public async Task Conflicting_recorded_owner_is_refused()
    {
        WriteSource();
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "docker"));
        var other = SubscriptionAgent(desired: "openshell", applied: "openshell",
            new ManagedRecord { State = "stopped", Applied = SubBinding("fleet-agent2-77777777", "Stopped") });
        other.Name = other.ShortName = other.ContainerName = "agent2";
        _t._store.Add(other);

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal((409, "subscription_owner_conflict"), (result.Status, result.Code));
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
    }

    // ── C2: outside source checks and expiry ──

    [Theory]
    [InlineData("missing", "subscription_binding_missing")]
    [InlineData("mode", "subscription_binding_invalid")]
    [InlineData("expired", "subscription_binding_expired")]
    [InlineData("api_ref", "openshell_requires_codex_api_key_policy_and_no_socket")]
    [InlineData("extra_route", "managed_route_unsupported:mcp:other")]
    public async Task Source_and_selection_faults_refuse_before_effects(string fault, string code)
    {
        var path = fault == "missing" ? null : WriteSource(expires: fault == "expired" ? _clock.Now.AddSeconds(-5) : null);
        if (fault == "mode") File.SetUnixFileMode(path!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var agent = SubscriptionAgent(desired: "openshell", applied: "docker");
        if (fault == "api_ref") agent.ProviderApiKeyEnvRef = "SYNTHETIC_API_KEY";
        if (fault == "extra_route") agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "other", Url = "https://other.example.test/mcp", TransportType = "http" });
        _t._store.Add(agent);

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal((409, code), (result.Status, result.Code));
        Assert.Empty(_cli.Calls.Where(ResourceCall));
        var unchanged = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("none", null, null, "docker"), (unchanged.State, unchanged.Candidate, unchanged.Applied, _t._store.Get("agent1").AppliedRuntimeKind));
    }

    [Fact]
    public async Task Expiry_during_a_pending_apply_cuts_the_candidate_without_activation()
    {
        WriteSource(expires: _clock.Now.AddMinutes(10));
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        _cli.Cli("ws-a").OnCreate = () => _clock.Now = _clock.Now.AddMinutes(11);

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal((409, "subscription_binding_expired"), (result.Status, result.Code));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("revoked", record.State);
        Assert.Equal(["subscription_binding_expired"], record.Errors);
        Assert.Null(record.Candidate);
        var kept = Assert.Single(record.Superseded); // custody kept, still countable
        Assert.Equal("ws-a", kept.SandboxWorkspace);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Any(a => a.Contains(".fleet-activate")));
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "attach"]));
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "stop", kept.SandboxName]));
        Assert.Equal("Stopped", _cli.Cli("ws-a").Sandboxes[kept.SandboxName].Phase);
        Assert.Empty(_cli.Attached[("ws-a", kept.SandboxName)]);
        Assert.Equal(1, Count(await Runtime().RollbackPreflightAsync(default), "subscriptionBindings"));
    }

    [Fact]
    public async Task Running_expiry_on_the_monitor_pass_revokes_and_stops_once()
    {
        var binding = SubBinding("fleet-agent1-e1e1e1e1", expires: _clock.Now.AddMinutes(1));
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "applied", Applied = binding }));
        _t.WriteAuthority("active");
        var runtime = Runtime();

        await runtime.CheckSubscriptionExpiryAsync("agent1", default); // not yet expired: nothing happens
        Assert.Empty(_cli.Calls);
        _clock.Now = _clock.Now.AddMinutes(2);
        await runtime.CheckSubscriptionExpiryAsync("agent1", default);

        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("revoked", "subscription_binding_expired"), (record.State, Assert.Single(record.Errors)));
        Assert.Equal("revoked", System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!["State"]!.GetValue<string>());
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "provider", "detach", binding.SandboxName, Provider]));
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        var calls = _cli.Calls.Count;
        await runtime.CheckSubscriptionExpiryAsync("agent1", default);
        Assert.Equal(calls, _cli.Calls.Count);
    }

    [Fact]
    public async Task Partial_running_expiry_is_held_and_never_retried_by_the_monitor()
    {
        var binding = SubBinding("fleet-agent1-e2e2e2e2", expires: _clock.Now.AddSeconds(-1));
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "applied", Applied = binding }));
        _cli.AttachedListFails = true;
        var runtime = Runtime();

        await runtime.CheckSubscriptionExpiryAsync("agent1", default);

        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains("subscription_binding_expired", record.Errors);
        Assert.Contains(record.Errors, e => e.Contains("provider_attachment_unobservable"));
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName])); // stop still ran
        var calls = _cli.Calls.Count;
        await runtime.CheckSubscriptionExpiryAsync("agent1", default);
        await runtime.CheckSubscriptionExpiryAsync("agent1", default);
        Assert.Equal(calls, _cli.Calls.Count);
    }

    [Fact]
    public async Task Start_after_expiry_is_revoke_and_stop_not_activation()
    {
        var binding = SubBinding("fleet-agent1-e3e3e3e3", phase: "Stopped", expires: _clock.Now.AddSeconds(-1));
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "stopped", Applied = binding }));

        var result = await Runtime().StartAsync("agent1", default);

        Assert.Equal("subscription_binding_expired", result.Code);
        Assert.Equal("revoked", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "start"]));
    }

    [Fact]
    public async Task Saved_desired_change_never_redirects_mcp_start_of_the_applied_subscription_binding()
    {
        WriteSource();
        var binding = SubBinding("fleet-agent1-5a5a5a5a", phase: "Stopped");
        // Desired selection already saved as Docker/API key: save is desired-only, dispatch follows the applied binding.
        var agent = _t.ManagedAgent(desired: "docker", applied: "openshell", new ManagedRecord { State = "stopped", Applied = binding });
        _t._store.Add(agent);
        _t.Enroll(binding);
        WriteSourceMatching(binding);

        var tool = await Runtime().DispatchToolAsync("agent1", "start");

        Assert.Equal("started", tool);
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "start", binding.SandboxName]));
        Assert.Empty(_cli.Unscoped.Calls);
    }

    /// <summary>Rewrites the outside source so its digest matches a persisted test binding (a recorded, unchanged source).</summary>
    private void WriteSourceMatching(ManagedBinding binding)
    {
        var path = Path.Combine(_t._dir, "managed", "agent1", "subscription.json");
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        var source = StockSubscriptionBinding.Read(_t._dir, "agent1", _clock.Now, StockOpenShellRuntime.OwnerUid(_t._dir)!.Value, StockOpenShellRuntime.OwnerUid).Source!;
        var applied = binding with { Subscription = binding.Subscription! with { SourceSha256 = digest, ExpiresAt = source.ExpiresAt } };
        _t._store.Get("agent1").AppliedRuntimeBinding = (record with { Applied = applied }).Serialize();
    }

    [Fact]
    public async Task Start_with_a_changed_provider_profile_is_refused_before_activation()
    {
        // Review S1: the scoped profile export is rehashed against the applied binding's pinned policy and the source.
        WriteSource();
        var binding = SubBinding("fleet-agent1-51515151", phase: "Stopped");
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);
        WriteSourceMatching(binding);
        _cli.ProfileExport = SyntheticProfile.Mutated; // a real field change after apply

        var result = await Runtime().StartAsync("agent1", default);

        Assert.Equal((409, "provider_profile_hash_mismatch:codex-subscription-native"), (result.Status, result.Code));
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.Take(2).SequenceEqual(["profile", "export"]));
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "start"]) || c.Args.Any(a => a.Contains(".fleet-activate")));
        Assert.Equal("stopped", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);

        _cli.ProfileExport = SyntheticProfile.Reordered(SyntheticProfile.Export); // the pinned document, other map order: Start proceeds
        Assert.True((await Runtime().StartAsync("agent1", default)).Ok);
    }

    [Fact]
    public async Task Expiry_during_start_activation_is_revoke_and_stop_never_applied()
    {
        // Review S2: the deadline passes while the daemon activates; Start must cut instead of committing applied.
        WriteSource(expires: _clock.Now.AddMinutes(5));
        var binding = SubBinding("fleet-agent1-52525252", phase: "Stopped", expires: _clock.Now.AddMinutes(5));
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "stopped", Applied = binding }));
        WriteSourceMatching(binding);
        _t.Enroll(binding);
        _cli.Cli("ws-a").OnExec = script =>
        {
            if (script.Contains(".fleet-activate")) _clock.Now = _clock.Now.AddMinutes(6);
            return script.Contains("-f \"$HOME/.fleet-staged\"") && !script.Contains('!') ? 0 : 0;
        };

        var result = await Runtime().StartAsync("agent1", default);

        Assert.Equal("subscription_binding_expired", result.Code);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("revoked", "subscription_binding_expired"), (record.State, Assert.Single(record.Errors)));
        Assert.Contains(_cli.Calls, c => c.Args.Any(a => a.Contains(".fleet-activate")));
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Equal("Stopped", _cli.Cli("ws-a").Sandboxes[binding.SandboxName].Phase);
        Assert.Equal("revoked", System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!["State"]!.GetValue<string>());
    }

    // ── C6: cleanup uses persisted tuples after source loss/change ──

    [Theory]
    [InlineData("deleted")]
    [InlineData("changed")]
    public async Task Revoke_after_source_loss_or_change_uses_the_persisted_workspace(string change)
    {
        var binding = SubBinding("fleet-agent1-c6c6c6c6");
        // A same-named sandbox with the same-named provider attached in the foreign workspace.
        _cli.Cli("ws-b").Sandboxes[binding.SandboxName] = (Guid.NewGuid().ToString(), "Ready");
        _cli.Attached[("ws-b", binding.SandboxName)] = [Provider];
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "applied", Applied = binding }));
        var path = WriteSource();
        if (change == "deleted") File.Delete(path);
        else WriteSource(workspace: "ws-b");

        var result = await Runtime().RevokeAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Stopped", _cli.Cli("ws-a").Sandboxes[binding.SandboxName].Phase);
        Assert.Empty(_cli.Attached[("ws-a", binding.SandboxName)]);
        Assert.Empty(_cli.Cli("ws-b").Calls);
        Assert.DoesNotContain(_cli.Calls, c => c.Ws == "ws-b");
        Assert.Equal(("Ready", 1), (_cli.Cli("ws-b").Sandboxes[binding.SandboxName].Phase, _cli.Attached[("ws-b", binding.SandboxName)].Count));
        Assert.All(_cli.Calls.Where(ResourceCall), c => Assert.Equal("ws-a", c.Ws));
    }

    [Fact]
    public async Task Missing_persisted_scope_is_unrecoverable_without_any_unscoped_command()
    {
        var binding = SubBinding("fleet-agent1-00000000", workspace: null);
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready"); // an ambient same-name sandbox exists
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "applied", Applied = binding }));

        var result = await Runtime().RevokeAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.EndsWith("subscription_binding_unrecoverable", StringComparison.Ordinal));
        Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Empty(_cli.Calls);
        Assert.Empty(_cli.Unscoped.Calls);
    }

    [Fact]
    public async Task Independent_detach_failure_never_suppresses_stop_and_keeps_state()
    {
        var binding = SubBinding("fleet-agent1-d1d1d1d1");
        _cli.DetachFails = true;
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "applied", Applied = binding }));

        var result = await Runtime().RevokeAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.StartsWith("detach:", StringComparison.Ordinal) && e.EndsWith("provider_detach_unknown", StringComparison.Ordinal));
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.NotEmpty(_t._broker.Deletes);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("needs_attention", binding.SandboxWorkspace), (record.State, record.Applied!.SandboxWorkspace));
    }

    // ── C9: actual adapter transitions retire subscription history atomically ──

    private Agent DockerTargetWithHistory(out ManagedBinding applied, out ManagedBinding history, string state = "applied")
    {
        applied = SubBinding("fleet-agent1-a9a9a9a9");
        history = SubBinding("fleet-agent1-b9b9b9b9", phase: "Stopped", attached: false);
        var agent = SubscriptionAgent(desired: "docker", applied: "openshell", new ManagedRecord { State = state, Applied = applied, Superseded = [history] });
        agent.NativeAuthMode = null; // desired selection explicitly changed away from subscription
        _t._store.Add(agent);
        _t.WriteAuthority("active");
        return agent;
    }

    [Fact]
    public async Task Subscription_to_docker_archives_and_retires_every_tuple_in_one_commit()
    {
        DockerTargetWithHistory(out var applied, out var history);
        var runtime = Runtime();
        Assert.Equal(1, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        var stored = _t._store.Get("agent1");
        Assert.Equal("docker", stored.AppliedRuntimeKind);
        var record = ManagedRecord.Parse(stored.AppliedRuntimeBinding);
        Assert.Null(record.Applied);
        Assert.Empty(record.SubscriptionTuples());
        Assert.Equal(new[] { applied.Epoch, history.Epoch }.Order(), record.RetiredSubscriptions.Select(r => r.Epoch).Order());
        foreach (var retired in record.RetiredSubscriptions)
        {
            Assert.Equal(("ws-a", ProviderId), (retired.SandboxWorkspace, retired.ProviderId));
            Assert.Equal(retired.ArchiveSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(retired.ArchivePath))));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(retired.ArchivePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(retired.ArchivePath)!));
            var archive = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(retired.ArchivePath))!;
            Assert.Equal("docker", archive["handoff"]!["target"]!.GetValue<string>());
            Assert.Equal(retired.SandboxId, archive["binding"]!["sandboxId"]!.GetValue<string>());
        }
        Assert.Equal(0, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));
        Assert.All(_cli.Calls.Where(ResourceCall), c => Assert.Equal("ws-a", c.Ws));
        // Inert history: a later Start/revoke never dispatches to the retired sandboxes.
        Assert.Equal("managed_binding_missing", (await runtime.StartAsync("agent1", default)).Code);
        // C12a: the named operator's seven-day evidence deletion removes archives only; the compact retirement
        // references stay and nothing is revived or counted again.
        foreach (var retired in record.RetiredSubscriptions) File.Delete(retired.ArchivePath);
        Assert.Equal(2, ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).RetiredSubscriptions.Count);
        Assert.Equal(0, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));
        await runtime.CheckSubscriptionExpiryAsync("agent1", default);
        Assert.Equal("managed_binding_missing", (await runtime.RevokeAsync("agent1", default)).Code);
    }

    [Theory]
    [InlineData("docker_failed")]
    [InlineData("archive_conflict")]
    [InlineData("lost_cas")]
    [InlineData("still_attached")]
    public async Task Interrupted_retirement_keeps_old_tuples_countable(string fault)
    {
        DockerTargetWithHistory(out _, out var history);
        switch (fault)
        {
            case "docker_failed": _t._dockerResult = (false, "image missing"); break;
            case "archive_conflict":
                var dir = Path.Combine(_t._dir, "managed", "agent1", "retired");
                Directory.CreateDirectory(dir);
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.WriteAllText(Path.Combine(dir, history.Epoch.ToString("N") + ".json"), "{\"binding\":{\"different\":true}}");
                break;
            case "lost_cas": _t._store.RejectKind = "docker"; break;
            case "still_attached": _cli.Attached[("ws-a", history.SandboxName)] = [Provider]; _cli.DetachFails = true; break;
        }
        var runtime = Runtime();

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Empty(record.RetiredSubscriptions);
        Assert.Equal(2, record.SubscriptionTuples().Count());
        Assert.Equal(1, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));
        if (fault == "lost_cas") // archives written before a lost CAS remain evidence only
            Assert.Equal(2, Directory.GetFiles(Path.Combine(_t._dir, "managed", "agent1", "retired")).Length);
    }

    [Fact]
    public async Task Failed_archive_sync_holds_and_a_retry_reusing_the_leftover_archive_syncs_before_retiring()
    {
        // Review L1: a reused archive (left by an earlier failed attempt) is synced too before the retirement CAS.
        DockerTargetWithHistory(out _, out _);
        var syncs = 0;
        var syncOk = false;
        var runtime = Runtime(directorySync: _ => { syncs++; return syncOk; });
        var archives = Path.Combine(_t._dir, "managed", "agent1", "retired");
        void Held(StockOpenShellRuntime.Result result)
        {
            Assert.Equal("subscription_archive_failed", result.Code);
            Assert.Contains("archive_directory_sync_failed", result.Errors);
            var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
            Assert.Equal("needs_attention", held.State);
            Assert.Empty(held.RetiredSubscriptions);
            Assert.Equal(2, held.SubscriptionTuples().Count());
        }

        Held(await runtime.ApplyAsync("agent1", default));
        Assert.Equal(1, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));
        var leftover = Directory.GetFiles(archives).Order().Select(f => (f, File.ReadAllBytes(f))).ToList();
        Assert.Equal(2, leftover.Count);

        // Recovery: revoke the held handoff, then retry the transition; the leftover archives are reused.
        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        syncs = 0;
        Held(await runtime.ApplyAsync("agent1", default)); // reuse without a successful sync never retires
        Assert.True(syncs > 0);
        Assert.Equal(1, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));

        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        syncOk = true;
        syncs = 0;
        var retried = await runtime.ApplyAsync("agent1", default);

        Assert.True(retried.Ok, retried.Message);
        Assert.True(syncs > 0);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(2, record.RetiredSubscriptions.Count);
        // The exact leftover archive bytes were reused (same epoch, same tuple identity), not rewritten.
        Assert.Equal(leftover.Select(l => l.Item2), Directory.GetFiles(archives).Order().Select(File.ReadAllBytes));
        Assert.All(record.RetiredSubscriptions, r => Assert.Equal(r.ArchiveSha256,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(r.ArchivePath)))));
        Assert.Equal(0, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));
    }

    [Fact]
    public async Task Subscription_to_api_creates_a_verified_api_binding_and_retires_history()
    {
        var old = SubBinding("fleet-agent1-f9f9f9f9", phase: "Stopped", attached: false);
        var agent = _t.ManagedAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "revoked", Applied = old });
        _t._store.Add(agent);
        var runtime = Runtime();

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("api_key", null, "applied"), (record.Applied!.AuthMode, record.Applied.SandboxWorkspace, record.State));
        Assert.NotEqual(old.SandboxId, record.Applied.SandboxId);
        Assert.Equal(old.Epoch, Assert.Single(record.RetiredSubscriptions).Epoch);
        Assert.Empty(record.SubscriptionTuples());
        Assert.Equal(0, Count(await runtime.RollbackPreflightAsync(default), "subscriptionBindings"));
        // The old subscription sandbox was only touched in its persisted workspace; the API sandbox keeps the ambient scope.
        Assert.All(_cli.Calls.Where(c => c.Args.Contains(old.SandboxName)), c => Assert.Equal("ws-a", c.Ws));
        Assert.Contains(_cli.Unscoped.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
    }

    [Fact]
    public async Task Desired_subscription_alone_counts_and_unparsable_records_count()
    {
        _t._store.Add(SubscriptionAgent(desired: "docker", applied: "docker"));
        var broken = _t.ManagedAgent(desired: "docker", applied: "docker");
        broken.Name = broken.ShortName = "agent2";
        broken.NativeAuthMode = null;
        broken.AppliedRuntimeBinding = "{not json";
        _t._store.Add(broken);
        Assert.Equal(2, Count(await Runtime().RollbackPreflightAsync(default), "subscriptionBindings"));
    }

    private static int Count(object preflight, string key) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(preflight)).RootElement.GetProperty(key).GetInt32();

    // ── #503 W3: a create the pinned server rejects allocates nothing; the supported revoke clears it, keeping custody ──

    [Fact]
    public async Task Rejected_create_holds_without_a_uuid_and_revoke_clears_it_keeping_the_subscription_tuple()
    {
        WriteSource();
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        _cli.Cli("ws-a").CreateRejects = true;
        var runtime = Runtime();

        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.Equal("sandbox_create_unknown", apply.Code);
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", held.State);
        Assert.True(string.IsNullOrEmpty(held.Candidate!.SandboxId)); // nothing was allocated, so no UUID is guessed
        Assert.True(StockOpenShellRuntime.MaxSandboxNameLength >= held.Candidate.SandboxName.Length);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "stop"]) || c.Args.Contains("detach"));

        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Message);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("revoked", 0), (record.State, record.Errors.Count));
        Assert.Null(record.Candidate);
        var custody = Assert.Single(record.Superseded); // the cut subscription tuple stays countable custody
        Assert.Equal((held.Candidate.Epoch, "ws-a"), (custody.Epoch, custody.SandboxWorkspace));
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.Take(2).SequenceEqual(["sandbox", "list"]));
        Assert.All(_cli.Calls.Where(c => c.Args[0] != "--version"), c => Assert.Equal("ws-a", c.Ws));
    }

    // ── #503 W8: an owned, quiescent failed-main candidate is reconciled by revoke; subscription custody is unchanged ──

    [Theory]
    [InlineData("settled")]
    [InlineData("running")]
    [InlineData("operation_pending")] // F1: a pending driver operation holds even when quiescent
    public async Task Revoke_reconciles_an_owned_quiescent_failed_main_candidate_keeping_subscription_custody(string variant)
    {
        WriteSource();
        var history = SubBinding("fleet-agent1-f9f9f9f0", phase: "Stopped", attached: false);
        var candidate = SubBinding("fleet-agent1-f9f9f9f9", phase: "Error");
        var scoped = _cli.Cli("ws-a");
        scoped.Status[candidate.SandboxName] = ManagedStockRuntimeAdapterTests.FakeCli.FailedMain(operationPending: variant == "operation_pending");
        scoped.StopKeepsPhase.Add(candidate.SandboxName);
        var held = variant != "settled";
        if (variant == "running") _t._host.RunningContainers = ["workload"];
        _t._store.Add(SubscriptionAgent(desired: "openshell", applied: "docker",
            new ManagedRecord { State = "needs_attention", Candidate = candidate, Superseded = [history], Errors = ["stop_unknown"] }));
        var tuples = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).SubscriptionTuples().Count();

        var revoke = await Runtime().RevokeAsync("agent1", default);

        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Contains(_cli.Calls, c => c.Ws == "ws-a" && c.Args.SequenceEqual(["sandbox", "stop", candidate.SandboxName]));
        Assert.Empty(_cli.Attached[("ws-a", candidate.SandboxName)]); // the verified detach still ran
        Assert.All(_cli.Calls.Where(ResourceCall), c => Assert.Equal("ws-a", c.Ws));
        Assert.Equal(tuples, record.SubscriptionTuples().Count()); // custody stays countable either way
        Assert.Equal((candidate.SandboxId, "Error"), scoped.Sandboxes[candidate.SandboxName]); // observed, never rewritten
        if (held)
        {
            Assert.False(revoke.Ok);
            Assert.Equal("needs_attention", record.State);
            Assert.Contains($"stop:{candidate.SandboxName}:stop_unknown", revoke.Errors);
            return;
        }
        Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
        Assert.Equal(("revoked", 0), (record.State, record.Errors.Count));
        Assert.Null(record.Candidate);
        Assert.Contains(record.Superseded, h => h.Epoch == candidate.Epoch && h.SandboxWorkspace == "ws-a");
        Assert.Contains(record.Superseded, h => h.Epoch == history.Epoch);
    }

    internal sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The pinned CLI as seen per workspace: a leading <c>--workspace W</c> selects that workspace's sandboxes, providers
    /// and attachments; calls without it go to a separate unscoped fake. Stock outputs follow the pinned 0.1.5 shapes.
    /// </summary>
    internal sealed class ScopedCli(MutableClock clock) : IStockCli
    {
        public readonly List<(string? Ws, List<string> Args, IReadOnlyDictionary<string, string>? Env)> Calls = [];
        public readonly ManagedStockRuntimeAdapterTests.FakeCli Unscoped = new();
        private readonly Dictionary<string, ManagedStockRuntimeAdapterTests.FakeCli> _workspaces = new();
        public readonly Dictionary<(string Ws, string Name), (string Id, string Type, string[] Keys)> Providers = new();
        public readonly Dictionary<(string Ws, string Sandbox), HashSet<string>> Attached = new();
        public (string Status, string Recovery, string Failure) RefreshState = ("refreshed", "-", "");
        public TimeSpan NextRefreshIn = TimeSpan.FromMinutes(35);
        public string ProfileExport = SyntheticProfile.Export;
        private int _exports;
        public bool DetachFails, AttachedListFails;

        public ManagedStockRuntimeAdapterTests.FakeCli Cli(string workspace) =>
            _workspaces.TryGetValue(workspace, out var cli) ? cli : _workspaces[workspace] = new();

        public async Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan timeout, CancellationToken ct)
        {
            var list = args.ToList();
            string? ws = null;
            if (list.Count >= 2 && list[0] == "--workspace") { ws = list[1]; list = list[2..]; }
            if (list.Count >= 2 && list[0] == "--color") list = list[2..];
            lock (Calls) Calls.Add((ws, list, secretEnv));
            string At(int i) => list.Count > i ? list[i] : "";
            if (ws is null) return await Unscoped.RunAsync(list, secretEnv, timeout, ct);
            switch ((At(0), At(1), At(2)))
            {
                case ("provider", "list", _):
                    var entries = Providers.Where(p => p.Key.Ws == ws).Select(p => new
                    {
                        id = p.Value.Id, name = p.Key.Name, workspace = ws, type = p.Value.Type, credential_keys = p.Value.Keys,
                    });
                    return new(0, System.Text.Json.JsonSerializer.Serialize(new { providers = entries, next_page_token = "" }));
                case ("provider", "refresh", "status"):
                    var now = clock.Now;
                    return new(0, StockSubscriptionBindingTests.RefreshStatus(At(3), RefreshState.Status, RefreshState.Recovery,
                        expires: Stamp(now.AddMinutes(40)), next: Stamp(now + NextRefreshIn), last: Stamp(now.AddMinutes(-25)), failure: RefreshState.Failure));
                case ("profile", "export", _):
                    // #503 W2: consecutive exports alternate map order, as the pinned CLI's unordered maps do.
                    return new(0, Interlocked.Increment(ref _exports) % 2 == 0 ? SyntheticProfile.Reordered(ProfileExport) : ProfileExport);
                case ("sandbox", "provider", "list"):
                    if (AttachedListFails) return new(1, "");
                    var names = Attached.TryGetValue((ws, At(3)), out var set) ? set.ToArray() : [];
                    return new(0, System.Text.Json.JsonSerializer.Serialize(new { providers = names.Select(n => new { name = n, type = "codex-subscription-native", credential_keys = Array.Empty<string>(), config_keys = Array.Empty<string>() }), next_page_token = "" }));
                case ("sandbox", "provider", "attach"):
                    (Attached.TryGetValue((ws, At(3)), out var attach) ? attach : Attached[(ws, At(3))] = []).Add(At(4));
                    return new(0, "");
                case ("sandbox", "provider", "detach"):
                    if (DetachFails) return new(1, "");
                    if (Attached.TryGetValue((ws, At(3)), out var detach)) detach.Remove(At(4));
                    return new(0, "");
                case ("sandbox", "create", _):
                    var name = list[list.IndexOf("--name") + 1];
                    Attached[(ws, name)] = [.. list.Select((a, i) => (a, i)).Where(p => p.a == "--provider").Select(p => list[p.i + 1])];
                    return await Cli(ws).RunAsync(list, secretEnv, timeout, ct);
                default:
                    return await Cli(ws).RunAsync(list, secretEnv, timeout, ct);
            }
        }

        private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// #502 review F1/F3: managed lifecycle dispatch through the actual <see cref="StockLegacyRuntimeGuard"/> middleware on a
/// test host, and F2: subscription-aware delete refusal. Synthetic fakes only.
/// </summary>
public sealed class ManagedStockRouteAndDeleteTests : IDisposable
{
    private const string Token = "synthetic-operator-token";
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    private async Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client, List<string> Legacy)> HostAsync(string? token = Token)
    {
        var runtime = _t.Runtime();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(runtime);
        var app = builder.Build();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Orchestrator:AuthToken"] = token }).Build();
        app.UseStockLegacyRuntimeGuard(config);
        var legacy = new List<string>();
        foreach (var route in new[] { "/api/agents/{name}/start", "/api/agents/{name}/stop", "/api/agents/{name}/cancel", "/api/agents/{name}/cancel/{**taskId}" })
            app.MapMethods(route, ["GET", "HEAD", "OPTIONS", "POST"], (HttpContext c) => { legacy.Add(c.Request.Method + " " + c.Request.Path); return "legacy"; });
        await app.StartAsync();
        return (app, app.GetTestClient(), legacy);
    }

    private ManagedBinding Applied(string phase = "Ready")
    {
        var binding = _t.Binding("fleet-agent1-f1f1f1f1", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, phase);
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "applied", Applied = binding }));
        return binding;
    }

    private static HttpRequestMessage Request(string method, string path, string? token = Token)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Theory]
    [InlineData("GET", "/api/agents/agent1/stop", 405)]
    [InlineData("HEAD", "/api/agents/agent1/stop", 405)]
    [InlineData("OPTIONS", "/api/agents/agent1/restart", 405)]
    [InlineData("GET", "/api/agents/agent1/cancel/task-a", 405)]
    [InlineData("POST", "/api/agents/agent1/stop/extra", 404)]
    [InlineData("POST", "/api/agents/agent1/stop/", 404)]
    [InlineData("POST", "/api/agents/agent1//stop", 404)]
    [InlineData("POST", "/api/agents/agent1/cancel_bg/a/b", 404)]
    public async Task Reads_other_methods_and_malformed_routes_never_dispatch_or_persist(string method, string path, int status)
    {
        Applied();
        var (app, client, legacy) = await HostAsync();
        await using var _ = app;
        var writes = _t._store.History.Count;

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(_t._cli.Calls);
        Assert.Equal(writes, _t._store.History.Count);
        Assert.Empty(legacy);
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("wrong-token", 401)]
    public async Task Unauthenticated_post_is_refused_before_dispatch(string? token, int status)
    {
        Applied();
        var (app, client, _) = await HostAsync();
        await using var __ = app;

        using var response = await client.SendAsync(Request("POST", "/api/agents/agent1/stop", token));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(_t._cli.Calls);
        Assert.Empty(_t._store.History);
    }

    [Fact]
    public async Task Without_a_configured_operator_token_managed_dispatch_fails_closed()
    {
        Applied();
        var (app, client, _) = await HostAsync(token: null);
        await using var __ = app;

        using var response = await client.SendAsync(Request("POST", "/api/agents/agent1/stop"));

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Empty(_t._cli.Calls);
    }

    [Fact]
    public async Task Authenticated_exact_post_dispatches_the_managed_action()
    {
        var binding = Applied();
        var (app, client, legacy) = await HostAsync();
        await using var __ = app;

        using var response = await client.SendAsync(Request("POST", "/api/agents/agent1/stop"));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Empty(legacy);
    }

    [Theory]
    [InlineData("/api/agents/agent1/cancel/task-a", "task-a", "/cancel/task-a")]
    [InlineData("/api/agents/agent1/cancel/wf%2Fstep", "wf/step", "/cancel/wf%2Fstep")]
    [InlineData("/api/agents/agent1/cancel_bg/task-a", "task-a", "/cancel/task-a")]
    public async Task Targeted_cancel_keeps_the_exact_task_and_the_other_active_task_survives(string path, string target, string loopback)
    {
        // Review F3: the workload's loopback cancel is emulated with two active tasks; a bare /cancel clears both.
        Applied();
        var active = new HashSet<string> { target, "other-task" };
        var seen = new List<string>();
        _t._cli.OnExec = arg =>
        {
            if (!arg.StartsWith("/cancel", StringComparison.Ordinal)) return 0;
            seen.Add(arg);
            if (arg == "/cancel") active.Clear();
            else active.Remove(Uri.UnescapeDataString(arg["/cancel/".Length..]));
            return 0;
        };
        var (app, client, _) = await HostAsync();
        await using var __ = app;

        using var response = await client.SendAsync(Request("POST", path));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal([loopback], seen);
        Assert.Equal(["other-task"], active);

        using var all = await client.SendAsync(Request("POST", "/api/agents/agent1/cancel"));
        Assert.Equal(200, (int)all.StatusCode);
        Assert.Equal("/cancel", seen[^1]);
        Assert.Empty(active);
    }

    // ── F2: delete keeps desired or nonretired subscription state ──

    private ManagedBinding SubscriptionBinding(string name) =>
        _t.Binding(name, Guid.NewGuid()) with
        {
            AuthMode = "subscription", KeyRef = "", Provider = "agent1-codex-sub", SandboxWorkspace = "ws-a",
            Subscription = new ManagedSubscription("ws-a", StockSubscriptionBindingTests.ProviderId, "synthetic-account_01",
                Guid.Parse(StockSubscriptionBindingTests.Owner), new string('d', 64), DateTimeOffset.UtcNow.AddHours(1)),
        };

    [Fact]
    public async Task Clean_revoke_does_not_permit_delete_until_verified_retirement()
    {
        var applied = SubscriptionBinding("fleet-agent1-f2f2f2f2");
        var history = SubscriptionBinding("fleet-agent1-f2f2f2f3");
        var agent = _t.ManagedAgent(desired: "openshell", applied: "openshell",
            new ManagedRecord { State = "revoked", Applied = applied, Superseded = [history] });
        agent.NativeAuthMode = null;
        agent.ProviderApiKeyEnvRef = null;
        _t._store.Add(agent);
        var runtime = _t.Runtime();

        // revoke → DELETE: the clean revoke alone keeps the row, its rollback count and its custody history.
        Assert.Equal("managed_subscription_not_retired", ContainerProvisioningService.DescribeManagedDeleteFault(_t._store.Get("agent1")));
        Assert.Equal(1, SubscriptionCount(await runtime.RollbackPreflightAsync(default)));
        Assert.Equal(2, ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).SubscriptionTuples().Count());

        // Desired subscription with no binding at all also keeps the row.
        var desired = _t.ManagedAgent(desired: "docker", applied: "docker");
        desired.NativeAuthMode = "subscription";
        Assert.Equal("managed_subscription_not_retired", ContainerProvisioningService.DescribeManagedDeleteFault(desired));

        // Only inert retirement references remain after a verified archived retirement: delete is allowed again.
        var retired = new ManagedRecord
        {
            State = "revoked",
            RetiredSubscriptions = [new RetiredSubscription(applied.Epoch, "ws-a", applied.SandboxId, "ws-a", StockSubscriptionBindingTests.ProviderId,
                Guid.Parse(StockSubscriptionBindingTests.Owner), "/retired/x.json", new string('e', 64), DateTimeOffset.UtcNow)],
        };
        var done = _t.ManagedAgent(desired: "docker", applied: "docker", retired);
        done.NativeAuthMode = null;
        done.ProviderApiKeyEnvRef = null;
        Assert.Null(ContainerProvisioningService.DescribeManagedDeleteFault(done));
        // API-key behavior is unchanged: a clean API revoke still permits delete.
        var api = _t.ManagedAgent(desired: "openshell", applied: "openshell", new ManagedRecord { State = "revoked", Applied = _t.Binding("fleet-agent1-a0a0a0a0", Guid.NewGuid()) });
        Assert.Null(ContainerProvisioningService.DescribeManagedDeleteFault(api));
    }

    private static int SubscriptionCount(object preflight) =>
        System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(preflight)).RootElement.GetProperty("subscriptionBindings").GetInt32();
}

/// <summary>
/// #503 C4/C5: Start/restart and pre-activation admission against the persisted applied policy and its outside enrollment,
/// legacy unbound refusal and the supported repair, with cleanup never gated on the positive validator.
/// </summary>
public sealed class ManagedSignerLifecycleTests : IDisposable
{
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    private static bool Verb(ManagedStockRuntimeAdapterTests.Call c, string verb) => c.Args.Take(2).SequenceEqual(["sandbox", verb]);
    private static bool Activation(ManagedStockRuntimeAdapterTests.Call c) => c.Args.Any(a => a.Contains(".fleet-activate"));

    private void EditEnrollment(Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!.AsObject();
        edit(node);
        File.WriteAllText(_t.AuthorityPath(), node.ToJsonString());
    }

    private string EnrollmentState() => System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!["State"]!.GetValue<string>();

    private (Agent Agent, ManagedBinding Binding) Stopped(string sandbox, string phase = "Stopped", string state = "stopped")
    {
        var binding = _t.Binding(sandbox, Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, phase);
        var agent = _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = state, Applied = binding }));
        _t.Enroll(binding);
        return (agent, binding);
    }

    [Fact]
    public async Task Start_uses_the_applied_policy_and_enrollment_not_a_newer_desired_selection_or_template()
    {
        var (agent, binding) = Stopped("fleet-agent1-c4000001");
        // After the valid apply: an unbound version is selected as desired and the template's memory host moves.
        _t._store.PolicyYaml2 = ManagedStockRuntimeTests.PolicyYaml;
        agent.SandboxPolicyVersionId = 2;
        var runtime = _t.Runtime();
        var template = Path.Combine(_t._dir, "managed-authority.json");
        File.WriteAllText(template, File.ReadAllText(template).Replace("memory.example.test", "moved.example.test"));

        var start = await runtime.StartAsync("agent1", default);

        Assert.True(start.Ok, start.Message);
        Assert.Equal(1, ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Applied!.PolicyVersionId);
        Assert.Contains(_t._cli.Calls, Activation);
    }

    [Fact]
    public async Task Planned_restart_start_never_holds_an_applied_bound_binding_for_an_unbound_desired_selection()
    {
        var (agent, _) = Stopped("fleet-agent1-c4000004");
        _t._store.PolicyYaml2 = ManagedStockRuntimeTests.PolicyYaml; // saved as desired after the valid apply
        agent.SandboxPolicyVersionId = 2;
        var runtime = _t.Runtime();
        _t._host.Generation = "gateway-2@planned";

        var start = await runtime.StartAsync("agent1", default);

        Assert.True(start.Ok, start.Message);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("applied", 1, "gateway-2@planned"), (record.State, record.Applied!.PolicyVersionId, record.Applied.GatewayGeneration));
        Assert.Empty(_t._broker.Deletes); // no revoke fanout
    }

    [Theory]
    [InlineData("policy_row", StockManagedSignerPolicy.Missing)]
    [InlineData("hash", StockManagedSignerPolicy.Mismatch)]
    [InlineData("other_agent", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("uuid", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("epoch", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("stock_commit", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("revoked_enrollment", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("absent_enrollment", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("unknown_service", StockManagedSignerPolicy.AuthorityUnavailable)]
    [InlineData("enrollment_host", StockManagedSignerPolicy.Mismatch)]
    [InlineData("enrollment_extra_route", StockManagedSignerPolicy.Missing)]
    public async Task Start_refuses_an_unusable_applied_artifact_or_enrollment_before_staging(string fault, string code)
    {
        var binding = _t.Binding("fleet-agent1-c4000002", Guid.NewGuid());
        binding = fault switch
        {
            "policy_row" => binding with { PolicyVersionId = 99 },
            "hash" => binding with { PolicySha256 = new string('c', 64) },
            _ => binding,
        };
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        if (fault != "absent_enrollment") _t.Enroll(binding);
        switch (fault)
        {
            case "other_agent": EditEnrollment(e => e["Agent"] = "agent2"); break;
            case "uuid": EditEnrollment(e => e["SandboxId"] = Guid.NewGuid().ToString()); break;
            case "epoch": EditEnrollment(e => e["Epoch"] = Guid.NewGuid().ToString()); break;
            case "stock_commit": EditEnrollment(e => e["StockCommit"] = new string('0', 40)); break;
            case "revoked_enrollment": EditEnrollment(e => e["State"] = "revoked"); break;
            case "unknown_service": EditEnrollment(e => e["Services"] = new System.Text.Json.Nodes.JsonObject { ["journal"] = new System.Text.Json.Nodes.JsonObject { ["Host"] = "journal.example.test" } }); break;
            case "enrollment_host": EditEnrollment(e => e["MemoryHost"] = "other.example.test"); break;
            case "enrollment_extra_route": EditEnrollment(e => e["Services"] = new System.Text.Json.Nodes.JsonObject { ["temporal"] = new System.Text.Json.Nodes.JsonObject { ["Host"] = "temporal.example.test" } }); break;
        }

        var start = await _t.Runtime().StartAsync("agent1", default);

        Assert.Equal((409, code), (start.Status, start.Code));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "start") || Verb(c, "exec") || Verb(c, "upload"));
        Assert.Equal("stopped", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Theory]
    [InlineData("enrollment")]
    [InlineData("artifact")]
    public async Task Start_drift_while_staging_is_the_bounded_revoke_and_stop_hold(string drift)
    {
        var (_, binding) = Stopped("fleet-agent1-c4000003");
        var drifted = false;
        _t._cli.OnExec = script =>
        {
            if (!drifted && script.Contains("-f \"$HOME/.fleet-staged\"") && !script.Contains('!'))
            {
                drifted = true;
                if (drift == "enrollment") EditEnrollment(e => e["MemoryHost"] = "drifted.example.test");
                else _t._store.PolicyYaml = SignedPolicy.Yaml(SignedPolicy.Memory) + "# drifted artifact\n";
            }
            return 0;
        };

        var start = await _t.Runtime().StartAsync("agent1", default);

        Assert.Equal((409, StockManagedSignerPolicy.Mismatch), (start.Status, start.Code));
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains(StockManagedSignerPolicy.Mismatch, record.Errors);
        Assert.DoesNotContain(_t._cli.Calls, Activation);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Equal("revoked", EnrollmentState());
    }

    [Fact]
    public async Task Apply_drift_of_the_written_enrollment_before_activation_holds_and_cleans_up()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        var cli = new HookCli(_t._cli, args =>
        {
            // The outside record was just written active; drift it while the guest copy uploads.
            if (args.Count > 4 && args[1] == "upload" && args[4] == "/home/agent/trust/enrollment.json")
                EditEnrollment(e => e["MemoryHost"] = "drifted.example.test");
        });

        var apply = await _t.Runtime(cli: cli).ApplyAsync("agent1", default);

        Assert.Equal((409, StockManagedSignerPolicy.Mismatch), (apply.Status, apply.Code));
        var stored = _t._store.Get("agent1");
        var record = ManagedRecord.Parse(stored.AppliedRuntimeBinding);
        Assert.Equal(("needs_attention", "docker"), (record.State, stored.AppliedRuntimeKind));
        Assert.NotNull(record.Candidate);
        Assert.DoesNotContain(_t._cli.Calls, Activation);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", record.Candidate!.SandboxName]));
        Assert.Equal("revoked", EnrollmentState());
    }

    [Fact]
    public async Task Legacy_unbound_binding_refuses_start_and_restart_while_stop_revoke_and_the_repair_still_work()
    {
        // Applied before #503 from an unbound artifact; the binding and enrollment carry that artifact's real hash.
        _t._store.PolicyYaml = ManagedStockRuntimeTests.PolicyYaml;
        var (agent, binding) = Stopped("fleet-agent1-c5000001", phase: "Ready", state: "applied");
        var runtime = _t.Runtime();

        var restart = await runtime.RestartAsync("agent1", default);
        Assert.Equal((409, StockManagedSignerPolicy.Missing), (restart.Status, restart.Code));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "stop")); // a refused restart never stops it first

        Assert.True((await runtime.StopAsync("agent1", default)).Ok);
        var start = await runtime.StartAsync("agent1", default);
        Assert.Equal((409, StockManagedSignerPolicy.Missing), (start.Status, start.Code));
        Assert.DoesNotContain(_t._cli.Calls, c => Verb(c, "start") || Activation(c));
        var revoke = await runtime.RevokeAsync("agent1", default);
        Assert.True(revoke.Ok, revoke.Message);

        // Supported repair: append a reviewed bound version, select it, then a normal apply after the clean revoke.
        _t._store.PolicyYaml2 = SignedPolicy.MemoryOnly;
        agent.SandboxPolicyVersionId = 2;
        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        var applied = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Applied!;
        Assert.Equal((2, Fleet.Orchestrator.Endpoints.SandboxPolicyValidation.Hash(SignedPolicy.MemoryOnly)), (applied.PolicyVersionId, applied.PolicySha256));
        Assert.Equal(binding.SandboxId, applied.SandboxId);
        Assert.Contains(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["policy", "set"]));
        Assert.True((await runtime.StopAsync("agent1", default)).Ok);
        Assert.True((await runtime.StartAsync("agent1", default)).Ok);
    }

    [Fact]
    public async Task Cleanup_never_needs_the_positive_validator()
    {
        // An enrollment that would refuse Start (wrong host) still lets Stop and revoke run every leg.
        var (_, binding) = Stopped("fleet-agent1-c5000002", phase: "Ready", state: "applied");
        EditEnrollment(e => e["MemoryHost"] = "other.example.test");
        var runtime = _t.Runtime();

        Assert.True((await runtime.StopAsync("agent1", default)).Ok);
        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Message);
        Assert.Contains(_t._cli.Calls, c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "detach"]));
        Assert.Equal("revoked", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.Equal("revoked", EnrollmentState());
        Assert.Equal("Stopped", _t._cli.Sandboxes[binding.SandboxName].Phase);
    }

    private sealed class HookCli(IStockCli inner, Action<IReadOnlyList<string>> before) : IStockCli
    {
        public Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan timeout, CancellationToken ct)
        {
            before(args);
            return inner.RunAsync(args, secretEnv, timeout, ct);
        }
    }
}
