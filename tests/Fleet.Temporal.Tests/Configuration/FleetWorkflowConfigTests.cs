using System.Reflection;
using Fleet.Temporal.Configuration;

namespace Fleet.Temporal.Tests.Configuration;

/// <summary>
/// Tests for FleetWorkflowConfig static singleton.
/// Uses reflection to reset private state between tests to keep them independent.
/// </summary>
public sealed class FleetWorkflowConfigTests : IDisposable
{
    public FleetWorkflowConfigTests()
    {
        ResetInstance();
    }

    public void Dispose()
    {
        ResetInstance();
    }

    private static void ResetInstance()
    {
        var field = typeof(FleetWorkflowConfig)
            .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, null);
    }

    [Fact]
    public void Instance_BeforeInitialize_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FleetWorkflowConfig.Instance);
        Assert.Contains("not been initialized", ex.Message);
    }

    [Fact]
    public void Initialize_SetsInstance_ReturnsOptions()
    {
        var opts = new FleetWorkflowOptions { EscalationTarget = "cto" };

        FleetWorkflowConfig.Initialize(opts);

        Assert.Same(opts, FleetWorkflowConfig.Instance);
        Assert.Equal("cto", FleetWorkflowConfig.Instance.EscalationTarget);
    }

    [Fact]
    public void Initialize_CalledTwice_IsNoOp_RetainsFirstInstance()
    {
        // Initialize is intentionally a no-op on subsequent calls so that peer-config hosted
        // services can call it during deferred bootstrap without clobbering the first instance.
        var first = new FleetWorkflowOptions { EscalationTarget = "first" };
        var second = new FleetWorkflowOptions { EscalationTarget = "second" };

        FleetWorkflowConfig.Initialize(first);
        FleetWorkflowConfig.Initialize(second);

        Assert.Same(first, FleetWorkflowConfig.Instance);
        Assert.Equal("first", FleetWorkflowConfig.Instance.EscalationTarget);
    }

    [Fact]
    public void Initialize_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FleetWorkflowConfig.Initialize(null!));
    }
}
