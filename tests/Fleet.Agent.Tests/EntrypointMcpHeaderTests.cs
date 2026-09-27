using System.Diagnostics;
using System.Text.Json;
using Fleet.Agent.Tests.Harness;

namespace Fleet.Agent.Tests;

/// <summary>Runs the header translation programs embedded in entrypoint.sh against temp files.</summary>
public sealed class EntrypointMcpHeaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gemini_copies_headers_only_when_supported(bool supported)
    {
        using var temp = new TempDirectory();
        var script = ExtractBetween(
            File.ReadAllText(RepoPaths.Resolve("entrypoint.sh")),
            "python3 - \"${MCP_CONFIG}\" \"${GEMINI_SETTINGS}\" <<'PYEOF'\n",
            "\nPYEOF");
        var appsettings = temp.Write("appsettings.json", Appsettings(supported));
        script = script.Replace("'/app/appsettings.json'", "sys.argv[3]", StringComparison.Ordinal);
        var scriptPath = temp.Write("translate.py", script);
        var mcp = temp.Write("mcp.json", McpJson());
        var output = Path.Combine(temp.Path, "settings.json");

        var result = await RunAsync("python3", [scriptPath, mcp, output, appsettings]);

        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var servers = doc.RootElement.GetProperty("mcpServers");
        if (supported)
            Assert.Equal("Bearer secret", servers.GetProperty("journal").GetProperty("headers").GetProperty("Authorization").GetString());
        else
        {
            Assert.False(servers.TryGetProperty("journal", out _));
            Assert.Contains("WARN: skipping MCP server 'journal'", result.Error);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Codex_writes_http_headers_only_when_supported(bool supported)
    {
        using var temp = new TempDirectory();
        var script = ExtractBetween(
            File.ReadAllText(RepoPaths.Resolve("entrypoint.sh")),
            "        node -e \"\n",
            "\n\" || true");
        var appsettings = temp.Write("appsettings.json", Appsettings(supported));
        var mcp = temp.Write("mcp.json", McpJson());
        var output = Path.Combine(temp.Path, "config.toml");
        script = script
            .Replace("/app/appsettings.json", appsettings, StringComparison.Ordinal)
            .Replace("$MCP_JSON", mcp, StringComparison.Ordinal)
            .Replace("/root/.codex/config.toml", output, StringComparison.Ordinal);
        var scriptPath = temp.Write("translate.js", script);

        var result = await RunAsync("node", [scriptPath]);

        Assert.Equal(0, result.ExitCode);
        var toml = File.Exists(output) ? await File.ReadAllTextAsync(output) : "";
        if (supported)
            Assert.Contains("http_headers = { \"Authorization\" = \"Bearer secret\" }", toml);
        else
        {
            Assert.DoesNotContain("[mcp_servers.journal]", toml);
            Assert.Contains("WARN: skipping MCP server 'journal'", result.Error);
        }
    }

    private static string Appsettings(bool supported) => JsonSerializer.Serialize(new
    {
        Agent = new { McpHeaderSupport = supported, AllowedTools = Array.Empty<string>() },
    });

    private static string McpJson() => """
        {"mcpServers":{"journal":{"url":"http://journal/mcp","headers":{"Authorization":"Bearer secret"}}}}
        """;

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing start marker: {startMarker}");
        start += startMarker.Length;
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"missing end marker: {endMarker}");
        return source[start..end];
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"entrypoint-headers-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
