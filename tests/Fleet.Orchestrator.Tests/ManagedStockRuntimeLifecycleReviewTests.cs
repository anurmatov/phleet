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
}
