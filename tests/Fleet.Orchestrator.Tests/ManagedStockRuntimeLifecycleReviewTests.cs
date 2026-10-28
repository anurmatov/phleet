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
}
