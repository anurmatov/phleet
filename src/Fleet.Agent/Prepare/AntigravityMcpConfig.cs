using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fleet.Agent.Prepare;

/// <summary>
/// The Antigravity CLI's <c>~/.gemini/config/mcp_config.json</c>: the fleet's
/// <c>/workspace/.mcp.json</c> in the shape the CLI (<c>agy</c>) reads.
/// </summary>
/// <remarks>
/// <para>The CLI reads MCP servers only from this file; it does not discover <c>.mcp.json</c>. A
/// server with a <c>url</c> is a Streamable HTTP server and keeps its <c>headers</c>; a server with
/// a <c>command</c> is a stdio server and keeps <c>args</c> and <c>env</c>. Nothing else is copied:
/// the CLI drops a server that carries a key it does not know (<c>type</c>, for one).</para>
/// <para>The pinned CLI sends the configured headers on every MCP request
/// (<c>docs/evidence/antigravity-cli-1.5.14.md</c>). A server with headers is still left out when
/// the orchestrator did not say so (<c>Agent.McpHeaderSupport</c>): an authenticated server is
/// never turned into a header-less one.</para>
/// <para>The file can hold a header value, so the caller writes it for its owner only.</para>
/// </remarks>
public static class AntigravityMcpConfig
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="Text">The file's text.</param>
    /// <param name="ServerCount">How many servers it holds.</param>
    public sealed record Result(string Text, int ServerCount);

    /// <summary>
    /// Translates <paramref name="mcpJson"/> (null: no file, so no servers). <paramref name="warn"/>
    /// gets one line per server left out. Throws when the file cannot be read as an object with an
    /// <c>mcpServers</c> object; the caller then writes a file without servers.
    /// </summary>
    public static Result Build(byte[]? mcpJson, bool mcpHeaderSupport, Action<string> warn)
    {
        var servers = new JsonObject();
        if (mcpJson is not null)
        {
            var declared = JsonNode.Parse(mcpJson) is JsonObject root
                ? root["mcpServers"] switch
                {
                    null => new JsonObject(),
                    JsonObject o => o,
                    _ => throw new InvalidOperationException("mcpServers is not an object"),
                }
                : throw new InvalidOperationException(".mcp.json is not an object");

            foreach (var (name, value) in declared)
            {
                if (value is not JsonObject config)
                {
                    warn($"WARN: skipping MCP server '{name}' (not an object)");
                    continue;
                }

                if (Translate(name, config, mcpHeaderSupport, warn) is { } server)
                    servers[name] = server;
            }
        }

        return new Result(new JsonObject { ["mcpServers"] = servers }.ToJsonString(Indented) + "\n", servers.Count);
    }

    private static JsonObject? Translate(string name, JsonObject config, bool mcpHeaderSupport, Action<string> warn)
    {
        if (Text(config["url"]) is { Length: > 0 } url)
        {
            var server = new JsonObject { ["url"] = url };
            if (config["headers"] is JsonObject { Count: > 0 } headers)
            {
                if (!mcpHeaderSupport)
                {
                    warn($"WARN: skipping MCP server '{name}' (it has headers and Agent.McpHeaderSupport is not set)");
                    return null;
                }

                if (Strings(headers) is not { } copied)
                {
                    warn($"WARN: skipping MCP server '{name}' (a header value is not a string)");
                    return null;
                }

                server["headers"] = copied;
            }
            else if (config["headers"] is not (null or JsonObject))
            {
                warn($"WARN: skipping MCP server '{name}' (headers is not an object)");
                return null;
            }

            return server;
        }

        if (Text(config["command"]) is { Length: > 0 } command)
        {
            var server = new JsonObject { ["command"] = command };
            switch (config["args"])
            {
                case null:
                    break;
                case JsonArray args when args.All(a => Text(a) is not null):
                    server["args"] = new JsonArray(args.Select(a => (JsonNode)Text(a)!).ToArray());
                    break;
                default:
                    warn($"WARN: skipping MCP server '{name}' (args is not a list of strings)");
                    return null;
            }

            switch (config["env"])
            {
                case null:
                    break;
                case JsonObject env when Strings(env) is { } copied:
                    if (copied.Count > 0) server["env"] = copied;
                    break;
                default:
                    warn($"WARN: skipping MCP server '{name}' (env is not an object of strings)");
                    return null;
            }

            return server;
        }

        warn($"WARN: skipping MCP server '{name}' (neither a url nor a command)");
        return null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A copy of an object whose values are all strings; null when one is not.</summary>
    private static JsonObject? Strings(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in source)
        {
            if (Text(value) is not { } text)
                return null;
            copy[key] = text;
        }

        return copy;
    }
}
