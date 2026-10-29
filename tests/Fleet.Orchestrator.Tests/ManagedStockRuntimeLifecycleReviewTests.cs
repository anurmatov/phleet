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
    [InlineData("staging_cancelled")]
    [InlineData("activation_cancelled")]
    public async Task Client_disconnect_during_staging_or_activation_still_reaches_the_bounded_stop(string code)
    {
        // The poll for the staged/ready marker waits on the cancelled request token (review N2).
        _t._cli.OnExec = script => code == "staging_cancelled"
            ? (script.Contains("-f \"$HOME/.fleet-staged\"") && !script.Contains('!') ? 1 : 0)
            : (script == StockOpenShellRuntime.DaemonReadyScript ? 1 : 0);
        var binding = _t.Binding("fleet-agent1-ffffffff", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
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

        var (deprovision, kept) = await harness.Service.DeprovisionForDeleteAsync("agent1");

        Assert.Equal(keepRow, kept);
        if (keepRow is not null)
        {
            Assert.False(deprovision.Success);
            Assert.Equal(0, harness.DockerRequestCount); // nothing stopped or removed behind the refusal
        }
        else if (applied == "openshell") Assert.True(deprovision.Success, deprovision.Message);
    }
}
