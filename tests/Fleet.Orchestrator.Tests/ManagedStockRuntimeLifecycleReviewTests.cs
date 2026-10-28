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
}
