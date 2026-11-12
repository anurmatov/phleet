using Fleet.Comms.Auth;

namespace Fleet.Comms.Tests;

/// <summary>
/// Which way the dependencies run between the agent runtime and this service.
/// </summary>
/// <remarks>
/// Revalidation and redaction of credentials moved with sign-in to the orchestrator (#530); what is
/// left here of them is the client that asks it, covered by <see cref="OrchestratorSignInCheckTests"/>.
/// </remarks>
public sealed class DependencyDirectionTests
{
    /// <summary>
    /// The agent runtime must not depend on the server-side auth implementation. If it ever did,
    /// the boundary would exist only by convention, and a change here could alter Telegram
    /// behaviour — which this slice must leave untouched.
    /// </summary>
    [Fact]
    public void FleetAgentDoesNotReferenceFleetComms()
    {
        var agent = typeof(Fleet.Agent.Services.TaskManager).Assembly;

        Assert.DoesNotContain(agent.GetReferencedAssemblies(),
            a => a.Name?.StartsWith("Fleet.Comms", StringComparison.Ordinal) == true);
    }

    /// <summary>The dependency runs the other way, and only onto the shared protocol contract.</summary>
    [Fact]
    public void FleetCommsDependsOnTheProtocolAndNotOnTheAgent()
    {
        var comms = typeof(OrchestratorSignInCheck).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.Contains("Fleet.Protocol", comms);
        Assert.DoesNotContain("Fleet.Agent", comms);
        Assert.DoesNotContain("Fleet.Telegram", comms);
    }
}
