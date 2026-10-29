using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

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
    }
}
