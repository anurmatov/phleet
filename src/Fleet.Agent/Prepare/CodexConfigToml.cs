using System.Text;
using System.Text.Json;

namespace Fleet.Agent.Prepare;

/// <summary>
/// The Codex CLI's <c>~/.codex/config.toml</c> (#460): the orchestrator-generated root settings
/// first, then one <c>[mcp_servers.&lt;name&gt;]</c> table per URL server with its
/// <c>enabled_tools</c> whitelist.
/// </summary>
/// <remarks>
/// Root keys and their <c>[analytics]</c>/<c>[history]</c> tables must come before any
/// <c>[mcp_servers.*]</c> table: a key written after a table header belongs to that table. The text
/// is byte-identical to what the Node program in <c>entrypoint.sh</c> wrote, value conversions
/// included (<see cref="ScriptCompat"/>).
/// </remarks>
public static class CodexConfigToml
{
    /// <summary>The prefix of an MCP tool grant: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.</summary>
    public const string McpToolPrefix = "mcp__";

    /// <summary>
    /// Builds the file. <paramref name="rootToml"/> is the generated root file's text, or empty when
    /// it could not be read (the caller has already warned); <paramref name="mcpJson"/> is the bytes
    /// of <c>.mcp.json</c>, or null when there is none. <paramref name="warn"/> gets one line per
    /// server left out. Throws when <c>.mcp.json</c> is not usable; the caller then writes nothing,
    /// as before.
    /// </summary>
    public static string Build(string rootToml, byte[]? mcpJson, IReadOnlyList<string> allowedTools, bool mcpHeaderSupport, Action<string> warn)
    {
        var toml = new StringBuilder();
        if (mcpJson is not null)
        {
            using var document = JsonDocument.Parse(new UTF8Encoding(false).GetString(mcpJson));
            foreach (var (name, server) in Servers(document.RootElement))
            {
                if (server is not { } s)
                    continue;
                if (s.ValueKind == JsonValueKind.Null)
                    throw new InvalidOperationException($"MCP server '{name}' is null");
                if (s.ValueKind != JsonValueKind.Object || !TryGetLast(s, "url", out var url) || !ScriptCompat.JsTruthy(url))
                    continue;

                var headers = TryGetLast(s, "headers", out var h) && ScriptCompat.JsTruthy(h)
                    ? ScriptCompat.JsEntries(h)
                    : [];
                // False until a pinned-CLI tools/list proof flips ContainerProvisioningService.McpHeaderSupport.
                // Fail closed: do not turn an authenticated server into a header-less one.
                if (headers.Count > 0 && !mcpHeaderSupport)
                {
                    warn($"WARN: skipping MCP server '{name}' (headers unsupported by pinned codex CLI)");
                    continue;
                }

                var prefix = McpToolPrefix + name + "__";
                var enabled = allowedTools
                    .Where(t => t.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(t => t[prefix.Length..])
                    .ToList();

                toml.Append("[mcp_servers.").Append(name).Append("]\n");
                toml.Append("url = \"").Append(ScriptCompat.JsString(url)).Append("\"\n");
                if (headers.Count > 0)
                {
                    var pairs = headers.Select(p =>
                        ScriptCompat.JsonStringify(p.Key) + " = " + ScriptCompat.JsonStringify(HeaderValue(name, p)));
                    toml.Append("http_headers = { ").Append(string.Join(", ", pairs)).Append(" }\n");
                }
                if (enabled.Count > 0)
                    toml.Append("enabled_tools = [").Append(string.Join(", ", enabled.Select(t => '"' + t + '"'))).Append("]\n");
                toml.Append('\n');
            }
        }

        var tables = toml.ToString();
        return rootToml.Length > 0 && tables.Length > 0 ? rootToml + "\n" + tables : rootToml + tables;
    }

    /// <summary><c>Object.entries(config.mcpServers || {})</c>.</summary>
    private static List<KeyValuePair<string, JsonElement?>> Servers(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException(".mcp.json is null");
        if (root.ValueKind != JsonValueKind.Object || !TryGetLast(root, "mcpServers", out var servers) || !ScriptCompat.JsTruthy(servers))
            return [];
        return ScriptCompat.JsEntries(servers);
    }

    private static string HeaderValue(string server, KeyValuePair<string, JsonElement?> header) =>
        header.Value is { } value
            ? ScriptCompat.JsString(value)
            : throw new InvalidOperationException($"MCP server '{server}' has headers that are not an object");

    /// <summary>The last property of that name, which is the one <c>JSON.parse</c> keeps.</summary>
    private static bool TryGetLast(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in obj.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            value = property.Value;
            found = true;
        }
        return found;
    }
}
