using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Tests;

// #341 — the dashboard nginx proxy must resolve the orchestrator per request (Docker DNS),
// not once at config load. These tests pin the structural properties of nginx.conf that make
// that true, and the request-preservation properties a variable proxy_pass can silently break.

public partial class DashboardNginxConfigTests
{
    private static readonly Lazy<IReadOnlyList<NginxDirective>> Directives = new(LoadConfig);

    private static IReadOnlyList<NginxDirective> LoadConfig()
    {
        var path = Path.Combine(RepoRoot(), "src/fleet-dashboard/nginx.conf");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dashboard nginx config not found at '{path}'.");

        var result = new List<NginxDirective>();
        var context = new Stack<string>();
        context.Push("server");
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.EndsWith('{'))
            {
                context.Push(line[..^1].TrimEnd());
                continue;
            }

            if (line == "}")
            {
                context.Pop();
                continue;
            }

            if (!line.EndsWith(';'))
                throw new FormatException($"Unsupported multi-line nginx directive: '{rawLine}'.");

            var nameEnd = line.IndexOf(' ');
            var name = nameEnd < 0 ? line[..^1] : line[..nameEnd];
            var value = nameEnd < 0 ? string.Empty : line[(nameEnd + 1)..^1].Trim();
            result.Add(new NginxDirective(context.Peek(), name, value));
        }

        return result;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no Fleet.sln above '{AppContext.BaseDirectory}').");
    }

    private IReadOnlyList<NginxDirective> In(string context) =>
        Directives.Value.Where(d => d.Context == context).ToList();

    private IReadOnlyList<NginxDirective> Named(string name) =>
        Directives.Value.Where(d => d.Name == name).ToList();

    [Fact]
    public void Resolver_UsesDockerDnsWithShortCache()
    {
        var resolvers = Named("resolver");
        var resolver = Assert.Single(resolvers);
        Assert.Equal("server", resolver.Context);
        Assert.StartsWith("127.0.0.11", resolver.Value, StringComparison.Ordinal);

        var valid = ValidRegex().Match(resolver.Value);
        Assert.True(valid.Success, $"resolver must carry valid=: '{resolver.Value}'");
        var cacheSeconds = int.Parse(valid.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.True(cacheSeconds <= 30,
            $"resolver valid={cacheSeconds}s exceeds the 30s stale-address bound.");

        Assert.Contains("ipv6=off", resolver.Value, StringComparison.Ordinal);

        var timeout = Assert.Single(Named("resolver_timeout"));
        Assert.Equal("5s", timeout.Value);
    }

    [Fact]
    public void Upstream_IsDeclaredAsASchemeOnlyVariable()
    {
        var sets = Named("set");
        Assert.Equal(
            ["$comms_upstream http://fleet-comms:8080", "$orchestrator_upstream http://fleet-orchestrator:3600"],
            sets.Select(s => s.Value).Order().ToArray());
        Assert.All(sets, set =>
        {
            Assert.Equal("server", set.Context);
            Assert.False(set.Value.EndsWith('/'), "a URI part in the variable would replace every request URI.");
        });
    }

    [Fact]
    public void EveryProxyPass_UsesTheUpstreamVariableWithNoUriPart()
    {
        var passes = Named("proxy_pass");
        Assert.NotEmpty(passes);

        var expected = new Dictionary<string, string>
        {
            ["location /api/"] = "$orchestrator_upstream",
            ["location /ws"] = "$orchestrator_upstream",
            ["location /health"] = "$orchestrator_upstream",
            ["location /v1/"] = "$comms_upstream",
        };
        Assert.Equal(
            expected.OrderBy(e => e.Key).Select(e => (e.Key, e.Value)),
            passes.OrderBy(p => p.Context).Select(p => (p.Context, p.Value)));
    }

    [Fact]
    public void NoStartupTimeResolutionIsReintroduced()
    {
        Assert.Empty(Named("upstream"));
        Assert.DoesNotContain(Directives.Value,
            d => d.Name == "proxy_pass" && d.Value.Contains("fleet-orchestrator", StringComparison.Ordinal));
        Assert.DoesNotContain(Directives.Value,
            d => d.Name == "proxy_pass" && d.Value.Contains("fleet-comms", StringComparison.Ordinal));
        Assert.DoesNotContain(Directives.Value,
            d => d.Name == "proxy_pass" && d.Value.Contains("http://", StringComparison.Ordinal));
    }

    [Fact]
    public void WebSocketLocation_PreservesUpgradeAndTimeout()
    {
        var ws = In("location /ws");

        Assert.Contains(ws, d => d is { Name: "proxy_http_version", Value: "1.1" });
        Assert.Contains(ws, d => d is { Name: "proxy_set_header", Value: "Upgrade $http_upgrade" });
        Assert.Contains(ws, d => d is { Name: "proxy_set_header", Value: "Connection \"Upgrade\"" });
        Assert.Contains(ws, d => d is { Name: "proxy_read_timeout", Value: "3600s" });
    }

    [Fact]
    public void ApiLocation_PreservesHostHeaders()
    {
        var api = In("location /api/");

        Assert.Contains(api, d => d is { Name: "proxy_set_header", Value: "Host $host" });
        Assert.Contains(api, d => d is { Name: "proxy_set_header", Value: "X-Real-IP $remote_addr" });
    }

    /// <summary>
    /// The mark that makes the orchestrator ask for sign-in on reads (docs/sign-in.md). It must be
    /// set on every route that reaches the orchestrator's API, and set by <c>proxy_set_header</c>,
    /// which replaces a value the client sent — a client must not be able to remove it.
    /// </summary>
    [Theory]
    [InlineData("location /api/")]
    [InlineData("location /ws")]
    public void EveryOrchestratorApiRoute_IsMarkedAsComingFromOutside(string context) =>
        Assert.Contains(In(context), d => d is { Name: "proxy_set_header", Value: "X-Fleet-Edge 1" });

    [Fact]
    public void TheMarkIsNeverCopiedFromTheClient() =>
        Assert.DoesNotContain(Directives.Value,
            d => d.Name == "proxy_set_header" && d.Value.Contains("$http_x_fleet_edge", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ApiLocation_ReportsTheSchemeTheOuterProxySaw() =>
        // What marks the session cookie Secure when TLS ends at a proxy in front of this one.
        Assert.Contains(In("location /api/"),
            d => d is { Name: "proxy_set_header", Value: "X-Forwarded-Proto $http_x_forwarded_proto" });

    [Fact]
    public void ChatLocation_ServesPlainRequestsAndTheStreamUpgrade()
    {
        var chat = In("location /v1/");

        Assert.Contains(chat, d => d is { Name: "proxy_http_version", Value: "1.1" });
        Assert.Contains(chat, d => d is { Name: "proxy_set_header", Value: "Upgrade $http_upgrade" });
        // Passed through, not forced: this location also serves ordinary requests.
        Assert.Contains(chat, d => d is { Name: "proxy_set_header", Value: "Connection $http_connection" });
        Assert.Contains(chat, d => d is { Name: "proxy_set_header", Value: "Host $host" });
        Assert.Contains(chat, d => d is { Name: "proxy_read_timeout", Value: "3600s" });
    }

    [Theory]
    [InlineData("X-Content-Type-Options \"nosniff\" always")]
    [InlineData("X-Frame-Options \"DENY\" always")]
    [InlineData("Referrer-Policy \"same-origin\" always")]
    public void EveryResponse_CarriesTheBrowserHardeningHeaders(string header) =>
        Assert.Contains(Directives.Value, d => d.Context == "server" && d.Name == "add_header" && d.Value == header);

    // nginx inherits add_header only into a block that declares none of its own, so one
    // add_header inside a location would silently drop all three headers for that location.
    [Fact]
    public void NoLocation_DeclaresItsOwnResponseHeaders() =>
        Assert.DoesNotContain(Directives.Value, d => d.Context != "server" && d.Name == "add_header");

    [GeneratedRegex(@"\bvalid=(\d+)s\b")]
    private static partial Regex ValidRegex();

    private sealed record NginxDirective(string Context, string Name, string Value);
}
