using Fleet.Agent.Services.JournalFiles;
using static Fleet.Agent.Prepare.ScriptCompat;

namespace Fleet.Agent.Prepare;

/// <summary>
/// The Gemini CLI's <c>~/.gemini/settings.json</c>: the fleet's <c>/workspace/.mcp.json</c>
/// translated into the CLI's <c>mcpServers</c> block, and the OAuth auth selector.
/// </summary>
/// <remarks>
/// The CLI reads MCP servers only from this file; it does not discover <c>.mcp.json</c>. Whatever
/// else the file already holds (a theme, say) is kept. The text is byte-identical to what the Python
/// program in <c>entrypoint.sh</c> wrote (<see cref="ScriptCompat"/>).
/// </remarks>
public static class GeminiSettingsJson
{
    /// <param name="Text">The file's text.</param>
    /// <param name="ServerCount">How many servers the <c>mcpServers</c> block holds.</param>
    public sealed record Result(string Text, int ServerCount);

    /// <summary>
    /// Translates <paramref name="mcpJson"/> and merges it into <paramref name="existingSettings"/>
    /// (the current file's bytes, or null when there is none). <paramref name="warn"/> gets one line
    /// per server left out. Throws when <c>.mcp.json</c> or the existing file's shape cannot take
    /// the result; the caller then writes nothing, as before.
    /// </summary>
    public static Result WithMcpServers(byte[] mcpJson, byte[]? existingSettings, bool mcpHeaderSupport, Action<string> warn)
    {
        var mcp = PyLoad(mcpJson) as PyDict ?? throw new InvalidOperationException(".mcp.json is not an object");
        var servers = mcp.TryGet("mcpServers", out var declared)
            ? declared as PyDict ?? throw new InvalidOperationException("mcpServers is not an object")
            : new PyDict();

        var geminiServers = new PyDict();
        foreach (var (name, value) in servers.Items)
        {
            var config = value as PyDict ?? throw new InvalidOperationException($"MCP server '{name}' is not an object");
            object? url = config.TryGet("url", out var u) ? u : "";
            config.TryGet("headers", out var headers);
            // False until a pinned-CLI tools/list proof flips ContainerProvisioningService.McpHeaderSupport.
            // Fail closed: do not turn an authenticated server into a header-less one.
            if (PyTruthy(headers) && !mcpHeaderSupport)
            {
                warn($"WARN: skipping MCP server '{name}' (headers unsupported by pinned gemini CLI)");
                continue;
            }
            if (!PyTruthy(url))
            {
                warn($"WARN: skipping MCP server '{name}' (no URL — stdio transport not supported by gemini CLI)");
                continue;
            }

            // The CLI infers the transport from the key. No "transport" or "type": it rejects unknown
            // fields ("Unrecognized key(s) in object") and drops the server. Only the stateless
            // files server speaks streamable HTTP; `url` selects the legacy SSE transport.
            var server = new PyDict();
            server.Set(name == JournalFilesListener.ServerName ? "httpUrl" : "url", url);
            if (PyTruthy(headers))
                server.Set("headers", headers);
            geminiServers.Set(name, server);
        }

        var settings = LoadExisting(existingSettings) as PyDict
            ?? throw new InvalidOperationException("the existing settings.json is not an object");
        settings.Set("mcpServers", geminiServers);
        SelectOAuth(settings);
        return new Result(PyDump(settings), geminiServers.Count);
    }

    /// <summary>
    /// Without <c>.mcp.json</c>: only the auth selector, merged into the existing file. Without it
    /// the CLI reports "no Auth method configured" and refuses to run.
    /// </summary>
    public static string WithAuthSelectorOnly(byte[]? existingSettings)
    {
        var settings = LoadExisting(existingSettings) as PyDict
            ?? throw new InvalidOperationException("the existing settings.json is not an object");
        SelectOAuth(settings);
        return PyDump(settings);
    }

    /// <summary>The existing file, or an empty object when it is absent or cannot be parsed.</summary>
    private static object? LoadExisting(byte[]? existingSettings)
    {
        if (existingSettings is null)
            return new PyDict();
        try { return PyLoad(existingSettings); }
        catch (Exception) { return new PyDict(); }
    }

    /// <summary>
    /// Forces <c>security.auth.selectedType = "oauth-personal"</c>. Without the key the CLI asks for
    /// an auth method even when <c>oauth_creds.json</c> is present. Schema as written by
    /// <c>gemini auth login</c> (v0.40.1).
    /// </summary>
    private static void SelectOAuth(PyDict settings)
    {
        var security = Child(settings, "security");
        var auth = Child(security, "auth");
        auth.Set("selectedType", "oauth-personal");
    }

    /// <summary><c>parent.setdefault(key, {})</c>, which must then be an object to hold the next key.</summary>
    private static PyDict Child(PyDict parent, string key)
    {
        if (!parent.TryGet(key, out var value))
        {
            value = new PyDict();
            parent.Set(key, value);
        }
        return value as PyDict ?? throw new InvalidOperationException($"'{key}' in the existing settings.json is not an object");
    }
}
