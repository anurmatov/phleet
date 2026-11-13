using System.Text;
using System.Text.Json;
using Fleet.Agent.Prepare;

namespace Fleet.Agent.Tests;

/// <summary>
/// The header translation in the provider CLI configuration <c>entrypoint.sh</c> has the agent
/// binary write (<see cref="CodexConfigToml"/>, <see cref="GeminiSettingsJson"/>): a server that
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

        var result = GeminiSettingsJson.WithMcpServers(Utf8(McpJson), existingSettings: null, supported, warnings.Add);

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
            Assert.Equal(["WARN: skipping MCP server 'journal' (headers unsupported by pinned gemini CLI)"], warnings);
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

        var gemini = GeminiSettingsJson.WithMcpServers(mcp, existingSettings: null, mcpHeaderSupport: false, _ => { });
        using var json = JsonDocument.Parse(gemini.Text);
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("fleet-journal-files");
        Assert.Equal("http://127.0.0.1:8091/journal-files/v1/mcp", server.GetProperty("httpUrl").GetString());
        Assert.False(server.TryGetProperty("url", out _));
        Assert.False(server.TryGetProperty("headers", out _));

        var toml = CodexConfigToml.Build(
            rootToml: "", mcp, ["mcp__fleet-journal-files__fetch_attachment"], mcpHeaderSupport: false, _ => { });
        Assert.Contains("enabled_tools = [\"fetch_attachment\"]", toml);
        Assert.Contains("url = \"http://127.0.0.1:8091/journal-files/v1/mcp\"", toml);
        Assert.DoesNotContain("http_headers", toml);
    }

    private const string McpJson = """
        {"mcpServers":{"journal":{"url":"http://journal/mcp","headers":{"Authorization":"Bearer secret"}}}}
        """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
