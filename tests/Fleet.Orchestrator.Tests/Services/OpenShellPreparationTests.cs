using Fleet.Orchestrator.Services;
using Google.Protobuf.WellKnownTypes;
using Openshell.V1;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellPreparationTests
{
    [Fact]
    public async Task ProductionAuthority_DefaultDeniesWithoutLookingUpAnAgent()
    {
        Assert.Null(await new DenyOpenShellPreparationAuthority().SelectAsync("agent1", "start", default));
    }

    [Fact]
    public void Ready_RequiresCurrentCompleteSupervisorRatherThanPersistedOperation()
    {
        var status = Ready();
        var receipt = OpenShellSupervisorProof.Verify(Target(), "provider1", status, DateTime.UtcNow);
        Assert.Equal("session1", receipt.SessionId);
        Assert.Equal("process1", receipt.ProcessInstanceId);
        Assert.Equal("epoch1", receipt.Desired.AttachmentEpoch);
        Assert.Equal(1ul, receipt.Sequence);
        Assert.NotSame(status.Receipt.Desired, receipt.Desired);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("session")]
    [InlineData("process")]
    [InlineData("epoch")]
    [InlineData("environment")]
    [InlineData("configuration")]
    [InlineData("hash")]
    [InlineData("credentials")]
    [InlineData("policy")]
    [InlineData("launch")]
    [InlineData("evaluation")]
    [InlineData("observation")]
    [InlineData("network")]
    [InlineData("provider")]
    [InlineData("sandbox")]
    [InlineData("name")]
    [InlineData("sequence")]
    [InlineData("version")]
    public void Ready_RejectsIncompleteOrForeignAuthority(string fault)
    {
        var status = Ready();
        switch (fault)
        {
            case "state": status.State = ProviderReadinessState.Pending; break;
            case "session": status.Observed.SessionId = ""; break;
            case "process": status.Observed.ProcessInstanceId = "other"; break;
            case "epoch": status.Observed.AttachmentEpoch = "old"; break;
            case "environment": status.Observed.ProviderEnvRevision++; break;
            case "configuration": status.Observed.ConfigRevision++; break;
            case "hash": status.Observed.PolicyHash = new string('b', 64); break;
            case "credentials": status.Observed.CredentialsInstalled = false; break;
            case "policy": status.Observed.PolicyActive = false; break;
            case "launch": status.Observed.LaunchEnvironmentInstalled = false; break;
            case "evaluation": status.EvaluatedTime = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)); break;
            case "observation": status.ObservedTime = null; break;
            case "network": status.NetworkInstanceId = ""; break;
            case "provider": status.Receipt.Provider = "other"; break;
            case "sandbox": status.Receipt.Desired.SandboxId = "other"; break;
            case "name": status.Receipt.Desired.Sandbox = "other"; break;
            case "sequence": status.Observed.Sequence = 0; break;
            case "version": status.Receipt.Desired.ProviderResourceVersion = 0; break;
        }
        Assert.Throws<OpenShellStateConflictException>(() =>
            OpenShellSupervisorProof.Verify(Target(), "provider1", status, DateTime.UtcNow));
    }

    [Fact]
    public void LiveLeaseEvaluation_DoesNotInventAnOriginalObservationHeartbeat()
    {
        var status = Ready();
        status.ObservedTime = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1));
        Assert.NotNull(OpenShellSupervisorProof.Verify(Target(), "provider1", status, DateTime.UtcNow));
    }

    internal static OpenShellSandboxSnapshot Target() => new("gateway1", "workspace1", "sandbox1", "name1",
        SandboxPhase.Ready, "process1", 1, new Dictionary<string, string>()) { ResourceVersion = 1 };

    internal static ProviderReadinessStatus Ready() => new()
    {
        State = ProviderReadinessState.Ready,
        Receipt = new() { Provider = "provider1", Workspace = "workspace1", Desired = new()
        {
            SandboxId = "sandbox1", Sandbox = "name1", AttachmentEpoch = "epoch1", ProviderId = "provider-id1",
            ProviderResourceVersion = 1, ProviderEnvRevision = 2, ConfigRevision = 3, PolicyHash = new string('a', 64),
        } },
        Observed = new() { SessionId = "session1", Sequence = 1, AttachmentEpoch = "epoch1", ProviderEnvRevision = 2,
            ConfigRevision = 3, PolicyHash = new string('a', 64), CredentialsInstalled = true, PolicyActive = true,
            LaunchEnvironmentInstalled = true, ProcessInstanceId = "process1" },
        NetworkInstanceId = "network1", ObservedTime = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-1)),
        EvaluatedTime = Timestamp.FromDateTime(DateTime.UtcNow.AddMilliseconds(-10)),
    };
}
