using System.Text.RegularExpressions;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// A Temporal client is constructed in exactly two files of <c>src</c>: the <c>TemporalConnect</c>
/// of the orchestrator and of the Temporal bridge. Whatever a deployment's Temporal requires of its
/// clients is applied there, so a client built anywhere else would be one that goes without it and
/// nothing would say so. This reads the source and fails on such a client, on a worker registered
/// with its own connection, and on a known site that stopped going through the seam.
/// </summary>
public sealed class TemporalConnectSeamTests
{
    private static readonly string[] Seams =
    [
        "src/Fleet.Orchestrator/Services/TemporalConnect.cs",
        "src/Fleet.Temporal/Services/TemporalConnect.cs",
    ];

    /// <summary>
    /// Every way the pinned SDK (Temporalio and Temporalio.Extensions.Hosting 1.11.1) makes a
    /// connection or the options for one. A comment that spells one of these out fails too: say it
    /// another way.
    /// </summary>
    private static readonly (string What, Regex Pattern)[] Constructions =
    [
        ("connects a client", new Regex(@"\bTemporalClient\s*\.\s*(ConnectAsync|CreateLazy)\b")),
        ("connects a connection", new Regex(@"\bTemporalConnection\s*\.\s*(ConnectAsync|CreateLazy)\b")),
        ("names the client connect options type", new Regex(@"\bTemporalClientConnectOptions\b")),
        ("names the connection options type", new Regex(@"\bTemporalConnectionOptions\b")),
        ("constructs a client", new Regex(@"\bnew\s+TemporalClient\s*\(")),
        ("registers a worker", new Regex(@"\bAddHostedTemporalWorker\s*\(")),
        ("registers a client", new Regex(@"\bAddTemporalClient\s*\(")),
        ("sets a worker's client options", new Regex(@"\.\s*ClientOptions\b")),
        ("loads client options from the environment", new Regex(@"\bClientEnvConfig\b")),
    ];

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
                return !parts.Contains("bin") && !parts.Contains("obj") && !parts.Contains("node_modules");
            });

    private static List<string> Violations(string relativePath, string source) =>
        Seams.Contains(relativePath)
            ? []
            : [.. Constructions.Where(c => c.Pattern.IsMatch(source)).Select(c => $"{relativePath} {c.What}")];

    [Fact]
    public void NoTemporalClient_IsConstructedOutsideTheTwoSeams()
    {
        var root = Root();
        var files = SourceFiles(root).ToList();
        Assert.True(files.Count > 200, $"only {files.Count} source files were found under src: the scan is not looking at the repository");

        var violations = files
            .SelectMany(path => Violations(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path)))
            .ToList();

        Assert.True(violations.Count == 0,
            "A Temporal client must be built by TemporalConnect, the one place that applies what the server requires of "
            + "its clients. Outside it:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>The scan sees what it is meant to see: each seam does every kind of thing it guards.</summary>
    [Fact]
    public void TheSeams_AreWhereClientsAreConstructed()
    {
        var root = Root();
        var orchestrator = File.ReadAllText(Path.Combine(root, Seams[0]));
        var bridge = File.ReadAllText(Path.Combine(root, Seams[1]));

        foreach (var seam in new[] { orchestrator, bridge })
        {
            Assert.Matches(@"TemporalClient\s*\.\s*ConnectAsync\(Options\(", seam);
            Assert.Matches(@"new TemporalClientConnectOptions\(", seam);
            // The one piece that decides what a client is given runs on every set of options.
            Assert.Matches(@"Authenticate\(connect, options\);\s*return connect;", seam);
        }
        Assert.Matches(@"AddHostedTemporalWorker\(taskQueue: @namespace\)\s*\.ConfigureOptions\(worker => worker\.ClientOptions = Options\(", bridge);
    }

    /// <summary>The five places that reach Temporal, each through its service's seam.</summary>
    [Theory]
    [InlineData("src/Fleet.Orchestrator/Services/TemporalClientRegistry.cs", @"TemporalConnect\.ConnectAsync\(opts\.Value, address, ns\)")]
    [InlineData("src/Fleet.Temporal/Mcp/TemporalClientFactory.cs", @"TemporalConnect\.ConnectAsync\(_opts, @namespace, CaseInsensitiveDataConverter\)")]
    [InlineData("src/Fleet.Temporal/Services/NamespaceInitializer.cs", @"TemporalConnect\.ConnectAsync\(opts, ""default""\)")]
    [InlineData("src/Fleet.Temporal/Services/SearchAttributeInitializer.cs", @"TemporalConnect\.ConnectAsync\(opts, ""default""\)")]
    [InlineData("src/Fleet.Temporal/Program.cs", @"TemporalConnect\s*\.AddWorker\(builder\.Services, temporalOpts, @namespace, caseInsensitiveDataConverter\)")]
    public void EachKnownSite_GoesThroughTheSeam(string file, string call)
    {
        Assert.Matches(call, File.ReadAllText(Path.Combine(Root(), file)));
    }

    [Theory]
    [InlineData("var c = await TemporalClient.ConnectAsync(options);")]
    [InlineData("var c = TemporalClient.CreateLazy(options);")]
    [InlineData("await TemporalConnection.ConnectAsync(new TemporalConnectionOptions(host));")]
    [InlineData("TemporalClientConnectOptions options = new(address) { Namespace = ns };")]
    [InlineData("var c = new TemporalClient(connection, clientOptions);")]
    [InlineData("services.AddHostedTemporalWorker(address, ns, queue);")]
    [InlineData("services.AddHostedTemporalWorker(\n    queue);")]
    [InlineData("services.AddTemporalClient(address, ns);")]
    [InlineData("builder.ConfigureOptions(o => o.ClientOptions = other);")]
    [InlineData("var o = ClientEnvConfig.LoadClientConnectOptions();")]
    public void TheScan_SeesEveryWayToBuildAClient(string code)
    {
        Assert.NotEmpty(Violations("src/Example/Elsewhere.cs", code));
    }

    [Theory]
    [InlineData("var client = await clientFactory.GetClientAsync(ns);")]
    [InlineData("client = await TemporalConnect.ConnectAsync(opts, \"default\");")]
    [InlineData("private readonly Dictionary<string, ITemporalClient> _clients = new();")]
    [InlineData("// uses ResultRunId (sdk-dotnet/1.11.1, TemporalClient.Workflow.cs:55-57).")]
    [InlineData("var handle = client.GetWorkflowHandle(id); var options = new TemporalClientOptions();")]
    public void TheScan_LeavesTheUseOfAClientAlone(string code)
    {
        Assert.Empty(Violations("src/Example/Elsewhere.cs", code));
    }
}
