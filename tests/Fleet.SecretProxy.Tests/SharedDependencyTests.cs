using System.Text.Json;
using System.Xml.Linq;

namespace Fleet.SecretProxy.Tests;

/// <summary>
/// What the secret forwarder ships beside its own code.
/// </summary>
/// <remarks>
/// The forwarder holds every bound secret's value and has no use for a Telegram client or a broker
/// client. It references <c>Fleet.Shared</c> for the state file's shape, so whatever
/// <c>Fleet.Shared</c> depends on is in its image: until the formatters and the RabbitMQ helper were
/// split out, <c>Telegram.Bot.dll</c> and <c>RabbitMQ.Client.dll</c> were.
/// </remarks>
public sealed class SharedDependencyTests
{
    private static DirectoryInfo RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                directory = directory.Parent;

            Assert.True(directory is not null, "could not locate the repository root");
            return directory!;
        }
    }

    [Fact]
    public void Fleet_Shared_declares_no_package_and_no_project_reference()
    {
        var project = XDocument.Load(
            Path.Combine(RepositoryRoot.FullName, "src", "Fleet.Shared", "Fleet.Shared.csproj"));

        var references = project.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference" or "FrameworkReference")
            .Select(e => $"{e.Name.LocalName} {e.Attribute("Include")?.Value}")
            .ToArray();

        Assert.True(
            references.Length == 0,
            $"Fleet.Shared declares {string.Join(", ", references)}. The secret forwarder and the chat "
            + "service reference Fleet.Shared, so this would ship in both. A Telegram type belongs in "
            + "Fleet.Shared.Telegram and a broker type in Fleet.Shared.Messaging.");
    }

    /// <summary>
    /// The closure NuGet resolved for the forwarder, read from its restore output: a package can
    /// arrive through another package or a shared build file without appearing in any project file.
    /// </summary>
    [Fact]
    public void The_forwarder_resolves_neither_a_Telegram_nor_a_broker_client()
    {
        var path = Path.Combine(RepositoryRoot.FullName, "src", "Fleet.SecretProxy", "obj", "project.assets.json");
        Assert.True(File.Exists(path), $"{path} is missing: the forwarder was not restored before this test ran.");

        using var assets = JsonDocument.Parse(File.ReadAllText(path));
        var target = assets.RootElement.GetProperty("targets").EnumerateObject().First().Value;
        var resolved = target.EnumerateObject()
            .Select(library => $"{library.Value.GetProperty("type").GetString()} {library.Name.Split('/')[0]}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain("package Telegram.Bot", resolved);
        Assert.DoesNotContain("package RabbitMQ.Client", resolved);
        // The one project it references, which itself references nothing.
        Assert.Equal(["project Fleet.Shared"], resolved.Where(library => library.StartsWith("project ", StringComparison.Ordinal)));
    }
}
