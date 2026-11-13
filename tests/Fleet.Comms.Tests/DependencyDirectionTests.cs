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

    /// <summary>
    /// What NuGet resolved for this service, read from its restore output.
    /// </summary>
    /// <remarks>
    /// The test above looks at the assemblies the compiled service names, and missed what it only
    /// shipped: this service used <c>Fleet.Shared</c> for the sign-in names, reached it through
    /// <c>Fleet.Conversations</c>, and so carried <c>Telegram.Bot.dll</c> without naming it anywhere.
    /// </remarks>
    [Fact]
    public void FleetCommsResolvesNoTelegramClientAndNoAgentSideProject()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln")))
            root = root.Parent;
        Assert.True(root is not null, "could not locate the repository root");

        var path = Path.Combine(root!.FullName, "src", "Fleet.Comms", "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"{path} is missing: Fleet.Comms was not restored before this test ran.");

        using var assets = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var target = assets.RootElement.GetProperty("targets").EnumerateObject().First().Value;
        var resolved = target.EnumerateObject()
            .Select(library => $"{library.Value.GetProperty("type").GetString()} {library.Name.Split('/')[0]}")
            .ToArray();

        Assert.DoesNotContain("package Telegram.Bot", resolved);

        // RabbitMQ.Client is here on purpose: Fleet.Conversations publishes the outbox with it. The
        // helper other services connect with is not, and neither is anything of the agent's.
        Assert.Contains("package RabbitMQ.Client", resolved);
        Assert.Equal(
            ["project Fleet.Contracts", "project Fleet.Conversations", "project Fleet.Conversations.Contracts",
             "project Fleet.Protocol", "project Fleet.Shared"],
            resolved.Where(library => library.StartsWith("project ", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
    }
}
