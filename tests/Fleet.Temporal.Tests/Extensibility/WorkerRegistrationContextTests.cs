using Fleet.Temporal.Extensibility;
using NSubstitute;
using Temporalio.Extensions.Hosting;

namespace Fleet.Temporal.Tests.Extensibility;

public sealed class WorkerRegistrationContextTests
{
    [Fact]
    public void GetWorkerBuilder_KnownNamespace_ReturnsBuilder()
    {
        var builder = Substitute.For<ITemporalWorkerServiceOptionsBuilder>();
        var ctx = new WorkerRegistrationContext(new Dictionary<string, ITemporalWorkerServiceOptionsBuilder>
        {
            ["fleet"] = builder,
        });

        var result = ctx.GetWorkerBuilder("fleet");

        Assert.Same(builder, result);
    }

    [Fact]
    public void GetWorkerBuilder_UnknownNamespace_ThrowsKeyNotFoundException()
    {
        var ctx = new WorkerRegistrationContext(new Dictionary<string, ITemporalWorkerServiceOptionsBuilder>
        {
            ["fleet"] = Substitute.For<ITemporalWorkerServiceOptionsBuilder>(),
        });

        var ex = Assert.Throws<KeyNotFoundException>(() => ctx.GetWorkerBuilder("unknown"));
        Assert.Contains("unknown", ex.Message);
        Assert.Contains("fleet", ex.Message);
    }

    [Fact]
    public void Namespaces_ReturnsAllConfiguredNamespaces()
    {
        var ctx = new WorkerRegistrationContext(new Dictionary<string, ITemporalWorkerServiceOptionsBuilder>
        {
            ["fleet"] = Substitute.For<ITemporalWorkerServiceOptionsBuilder>(),
            ["other"] = Substitute.For<ITemporalWorkerServiceOptionsBuilder>(),
        });

        Assert.Contains("fleet", ctx.Namespaces);
        Assert.Contains("other", ctx.Namespaces);
        Assert.Equal(2, ctx.Namespaces.Count);
    }
}
