using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

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
}
