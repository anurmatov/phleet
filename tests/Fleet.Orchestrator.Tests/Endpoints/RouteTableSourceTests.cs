using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The orchestrator's routes, read from its source: no test hosts the real <c>Program.cs</c>, which
/// needs MySQL, RabbitMQ and Docker to start, so a route lost while handlers move between files
/// would otherwise go unnoticed.
/// </summary>
/// <remarks>
/// <c>Fixtures/RouteTable/orchestrator-routes.txt</c> lists every route mapped with a literal path,
/// as <c>Verb /path</c>. It was written before the inline routes left <c>Program.cs</c> and matched
/// unchanged after. A new or removed route is a deliberate edit of that file.
/// </remarks>
public partial class RouteTableSourceTests
{
    [Fact]
    public void EveryRoute_IsStillMapped_AndNoneWasAdded()
    {
        var mapped = Sources()
            .SelectMany(source => RoutePattern().Matches(source.Text))
            .Select(match => $"{(match.Groups[1].Success ? match.Groups[1].Value : "Any")} {match.Groups[2].Value}")
            .Order(StringComparer.Ordinal)
            .ToList();

        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RouteTable", "orchestrator-routes.txt"))
            .Where(line => line.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, mapped);
    }

    /// <summary>
    /// <c>Program.cs</c> is wiring: configuration, services, middleware and one <c>Map…Endpoints()</c>
    /// call per surface. The MCP endpoint and the health check are the only routes it declares itself.
    /// </summary>
    [Fact]
    public void ProgramCs_DeclaresNoRouteButMcpAndTheHealthCheck()
    {
        var program = Sources().Single(source =>
            source.Path.EndsWith($"Fleet.Orchestrator{Path.DirectorySeparatorChar}Program.cs", StringComparison.Ordinal));

        var declared = RoutePattern().Matches(program.Text).Select(match => match.Groups[2].Value).ToList();

        Assert.Equal(["/mcp", "/health"], declared);
    }

    private static List<(string Path, string Text)> Sources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Fleet.Orchestrator"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                        && !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Select(file => (file, File.ReadAllText(file)))
            .ToList();
    }

    [GeneratedRegex(@"\.Map(Get|Post|Put|Delete|Patch|Mcp)?\(\s*""([^""]+)""")]
    private static partial Regex RoutePattern();
}
