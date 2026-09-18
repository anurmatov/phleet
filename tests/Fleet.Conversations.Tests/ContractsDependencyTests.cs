using System.Reflection;
using System.Xml.Linq;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The credential boundary this slice rests on, asserted structurally.
/// </summary>
/// <remarks>
/// <para>
/// The agent holds no database credential and reaches the store only through the south endpoints.
/// That boundary becomes decorative the moment a future agent-side client can reach a driver simply
/// by referencing the contract — and the damage is silent, because every other assertion in the
/// suite would keep passing while it happened.
/// </para>
/// <para>
/// So the rule is checked rather than written down: the <c>Fleet.Conversations.Contracts</c> project
/// file cites these tests in a comment, and here they are.
/// </para>
/// </remarks>
public sealed class ContractsDependencyTests
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

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot.FullName, relativePath));

    private static IEnumerable<string> PackageReferences(XDocument project) =>
        project.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(v => v.Length > 0);

    private static IEnumerable<string> ProjectReferences(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(
                (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .Where(v => v.Length > 0);

    /// <summary>
    /// The contracts project declares no package at all, and exactly one project reference.
    /// </summary>
    [Fact]
    public void The_contracts_project_declares_no_package_and_only_references_the_protocol()
    {
        var project = LoadProject(Path.Combine("src", "Fleet.Conversations.Contracts", "Fleet.Conversations.Contracts.csproj"));

        Assert.Empty(PackageReferences(project));
        Assert.Equal(["Fleet.Protocol"], ProjectReferences(project).ToArray());
    }

    /// <summary>
    /// The contracts assembly's actual runtime closure is BCL plus <c>Fleet.Protocol</c> — asserted
    /// from the compiled assembly rather than from the project file, because a transitive dependency
    /// arrives without ever appearing in the <c>.csproj</c>.
    /// </summary>
    [Fact]
    public void The_contracts_assembly_closure_is_the_bcl_and_the_protocol()
    {
        var contracts = typeof(Contracts.IConversationStore).Assembly;

        var nonFramework = Closure(contracts)
            .Where(name => !IsFrameworkAssembly(name))
            .Where(name => !string.Equals(name, contracts.GetName().Name, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Fleet.Protocol"], nonFramework);
    }

    /// <summary>
    /// No database driver is reachable from the contracts, by any path.
    /// </summary>
    /// <remarks>
    /// The previous test already implies this, but it is asserted by name too: the failure this
    /// guards against is specifically "a driver became reachable", and a message naming
    /// <c>MySqlConnector</c> is a great deal clearer than a set difference.
    /// </remarks>
    [Fact]
    public void No_database_driver_is_reachable_from_the_contracts()
    {
        var closure = Closure(typeof(Contracts.IConversationStore).Assembly);

        foreach (var forbidden in DatabaseAssemblies)
            Assert.False(
                closure.Any(name => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)),
                $"'{forbidden}' is reachable from Fleet.Conversations.Contracts. The agent holds no "
                + "database credential and reaches the store only through the south endpoints; a "
                + "driver reachable through the contract makes that boundary decorative.");
    }

    /// <summary>
    /// <c>Fleet.Agent</c> takes no dependency on the conversation store or on any ORM.
    /// </summary>
    /// <remarks>
    /// Checked at the project level rather than the assembly level, because the agent is not built
    /// by this test project and loading it here would prove only that this test could load it.
    /// </remarks>
    [Fact]
    public void The_agent_project_references_neither_the_conversation_store_nor_an_orm()
    {
        var project = LoadProject(Path.Combine("src", "Fleet.Agent", "Fleet.Agent.csproj"));

        var references = ProjectReferences(project).ToArray();
        Assert.DoesNotContain("Fleet.Conversations", references);

        foreach (var package in PackageReferences(project))
        {
            foreach (var forbidden in DatabaseAssemblies.Concat(OrmAssemblies))
                Assert.False(
                    package.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"Fleet.Agent references '{package}'. The agent must reach the store only through "
                    + "the south endpoints.");
        }
    }

    private static readonly string[] DatabaseAssemblies =
        ["MySqlConnector", "MySql.Data", "Npgsql", "Microsoft.Data.Sqlite", "System.Data.SqlClient"];

    private static readonly string[] OrmAssemblies =
        ["Microsoft.EntityFrameworkCore", "Pomelo.EntityFrameworkCore", "Dapper"];

    /// <summary>Every assembly reachable from <paramref name="root"/>, transitively.</summary>
    private static HashSet<string> Closure(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([root]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var reference in current.GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || !seen.Add(name)) continue;

                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (Exception e) when (e is FileNotFoundException or BadImageFormatException)
                {
                    // Unresolvable at runtime; its name is already recorded, which is what is asserted.
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// The BCL. <c>Microsoft.Extensions.*</c> is deliberately NOT on this list — it is a package,
    /// not the framework, and the contracts project is supposed to be reachable by a client that has
    /// not taken it.
    /// </summary>
    private static bool IsFrameworkAssembly(string name) =>
        name is "System" or "netstandard" or "mscorlib"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);
}
