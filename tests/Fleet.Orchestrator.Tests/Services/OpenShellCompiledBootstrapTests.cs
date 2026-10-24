extern alias FleetAgent;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Guard = FleetAgent::Fleet.Agent.Services.OpenShellStartupGuard;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellCompiledBootstrapTests
{
    [Fact]
    public void CompiledPerLaunchArguments_PassActualStartupGuardWithOwnedReadOnlyMountSeam()
    {
        var plan = OpenShellPolicyCompilerTests.Plan();
        var id = Guid.NewGuid(); var epoch = Guid.NewGuid(); var deadline = DateTime.UtcNow.AddSeconds(50);
        var spec = plan.MaterializeBootstrap(id, epoch, deadline);
        var config = new ConfigurationBuilder().AddCommandLine(spec.Command.Skip(2).ToArray()).Build();
        var root = Path.Combine(Path.GetTempPath(), "bootstrap-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app/generated"));
        File.WriteAllText(Path.Combine(root, "app/generated/bootstrap.capability"), new string('x', 48));
        Guard guard;
        try { guard = new Guard(config, root); } finally { Directory.Delete(root, true); }
        Assert.Equal(id, guard.LaunchId); Assert.Equal(epoch, guard.IdentityEpoch); Assert.Equal(plan.Hash, guard.PlanHash);
        Assert.Equal(deadline, guard.Deadline); Assert.Equal(plan.BootstrapIngress, guard.Ingress.AbsoluteUri);
        Assert.Empty(spec.Environment);
        Assert.Equal(spec.ToByteArray(), plan.MaterializeBootstrap(id, epoch, deadline).ToByteArray());
    }
}
