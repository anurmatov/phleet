using System.Text;
using System.Text.Json;
using Fleet.Agent.Prepare;

namespace Fleet.Agent.Tests;

/// <summary>
/// The header translation in the provider CLI configuration <c>entrypoint.sh</c> has the agent
/// binary write (<see cref="CodexConfigToml"/>, <see cref="AntigravityMcpConfig"/>): a server that
/// carries headers is written with them when the pinned CLI sends headers, and left out otherwise.
/// It is never written without them.
/// </summary>
public sealed class EntrypointMcpHeaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gemini_copies_headers_only_when_supported(bool supported)
    {
        var warnings = new List<string>();

        var result = AntigravityMcpConfig.Build(Utf8(McpJson), supported, warnings.Add);

        using var doc = JsonDocument.Parse(result.Text);
        var servers = doc.RootElement.GetProperty("mcpServers");
        if (supported)
        {
            Assert.Equal("Bearer secret", servers.GetProperty("journal").GetProperty("headers").GetProperty("Authorization").GetString());
            Assert.Empty(warnings);
        }
        else
        {
            Assert.False(servers.TryGetProperty("journal", out _));
            Assert.Equal(["WARN: skipping MCP server 'journal' (it has headers and Agent.McpHeaderSupport is not set)"], warnings);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Codex_writes_http_headers_only_when_supported(bool supported)
    {
        var warnings = new List<string>();

        var toml = CodexConfigToml.Build(rootToml: "", Utf8(McpJson), allowedTools: [], supported, warnings.Add);

        if (supported)
        {
            Assert.Contains("http_headers = { \"Authorization\" = \"Bearer secret\" }", toml);
            Assert.Empty(warnings);
        }
        else
        {
            Assert.DoesNotContain("[mcp_servers.journal]", toml);
            Assert.Equal(["WARN: skipping MCP server 'journal' (headers unsupported by pinned codex CLI)"], warnings);
        }
    }

    [Fact]
    public void FilesServerIsHeaderlessAndCodexToolIsEnabled()
    {
        var mcp = Utf8("{\"mcpServers\":{\"fleet-journal-files\":{\"url\":\"http://127.0.0.1:8091/journal-files/v1/mcp\"}}}");

        var gemini = AntigravityMcpConfig.Build(mcp, mcpHeaderSupport: false, _ => { });
        using var json = JsonDocument.Parse(gemini.Text);
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("fleet-journal-files");
        // The Antigravity CLI speaks Streamable HTTP on "url"; the Gemini CLI's "httpUrl" is not read.
        Assert.Equal("http://127.0.0.1:8091/journal-files/v1/mcp", server.GetProperty("url").GetString());
        Assert.False(server.TryGetProperty("httpUrl", out _));
        Assert.False(server.TryGetProperty("headers", out _));

        var toml = CodexConfigToml.Build(
            rootToml: "", mcp, ["mcp__fleet-journal-files__fetch_attachment"], mcpHeaderSupport: false, _ => { });
        Assert.Contains("enabled_tools = [\"fetch_attachment\"]", toml);
        Assert.Contains("url = \"http://127.0.0.1:8091/journal-files/v1/mcp\"", toml);
        Assert.DoesNotContain("http_headers", toml);
    }

    /// <summary>
    /// #593 A5: the two mapped headers <c>.mcp.json</c> gives Claude are the two Codex gets as native
    /// <c>http_headers</c>, value for value, and the server's tool whitelist is unchanged.
    /// </summary>
    [Fact]
    public void Mapped_headers_are_the_same_in_claude_json_and_codex_http_headers()
    {
        var toml = CodexConfigToml.Build(rootToml: "", Utf8(MappedMcpJson), ["mcp__example__ping", "mcp__fleet-memory__memory_get"], mcpHeaderSupport: true, _ => { });

        using var json = JsonDocument.Parse(MappedMcpJson);
        var expected = json.RootElement.GetProperty("mcpServers").GetProperty("example").GetProperty("headers").EnumerateObject()
            .Select(h => $"\"{h.Name}\" = \"{h.Value.GetString()}\"");
        Assert.Contains("[mcp_servers.example]\nurl = \"https://mcp.example.test/mcp\"\n"
            + $"http_headers = {{ {string.Join(", ", expected)} }}\n"
            + "enabled_tools = [\"ping\"]\n", toml);
    }

    /// <summary>
    /// #593 A5: <c>prepare config</c> makes the Codex config owner-only before writing a header value
    /// into it, including over an existing world-readable file, and the owner reads it back.
    /// </summary>
    [Fact]
    public void Prepare_writes_the_codex_config_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"mcp-header-prepare-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app"));
            Directory.CreateDirectory(Path.Combine(root, "workspace"));
            Directory.CreateDirectory(Path.Combine(root, "root/.codex"));
            File.WriteAllText(Path.Combine(root, "app/appsettings.json"),
                """{"Agent":{"Provider":"codex","McpHeaderSupport":true,"AllowedTools":["mcp__example__ping"]}}""");
            File.WriteAllText(Path.Combine(root, "workspace/.mcp.json"), MappedMcpJson);
            var config = Path.Combine(root, "root/.codex/config.toml");
            File.WriteAllText(config, "stale");
            File.SetUnixFileMode(config, (UnixFileMode)0b110_100_100);   // 0644

            var stderr = new StringWriter();
            Assert.Equal(0, EntrypointPrepare.Run(["config", EntrypointPrepare.RootOption, root], new StringWriter(), stderr, _ => null));

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
            Assert.Contains("http_headers = { \"X-Example-Key\" = \"fleet_placeholder_key\", \"X-Example-Secret\" = \"fleet_placeholder_secret\" }", File.ReadAllText(config));
            Assert.DoesNotContain("skipping MCP server 'example'", stderr.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private const string MappedMcpJson = """
        {"mcpServers":{"example":{"type":"http","url":"https://mcp.example.test/mcp","headers":{"X-Example-Key":"fleet_placeholder_key","X-Example-Secret":"fleet_placeholder_secret"}}}}
        """;

    private const string McpJson = """
        {"mcpServers":{"journal":{"url":"http://journal/mcp","headers":{"Authorization":"Bearer secret"}}}}
        """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
