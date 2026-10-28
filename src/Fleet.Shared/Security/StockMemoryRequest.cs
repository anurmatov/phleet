using System.Text.Json;

namespace Fleet.Shared.Security;

public static class StockMemoryRequest
{
    /// <summary>The memory route: only the fixed read-only tools are callable.</summary>
    public static void Validate(string method, byte[] body, string? session) => Validate(method, body, session, StockServices.MemoryTools);

    /// <summary>
    /// The same MCP request shape for every receiving service; <paramref name="tools"/> decides which
    /// <c>tools/call</c> names are callable (memory's fixed set, or the record's grants for that service).
    /// </summary>
    public static void Validate(string method, byte[] body, string? session, IReadOnlyCollection<string> tools)
    {
        if (body.Length > 262144 || method is not ("GET" or "POST" or "DELETE")
            || session is not null && (session.Length is < 1 or > 256 || session.Any(c => c is < '!' or > '~')))
            throw new StockIdentityDenied("request_shape");
        if (method != "POST")
        {
            if (body.Length != 0 || session is null) throw new StockIdentityDenied("session_required");
            return;
        }
        using var document = JsonDocument.Parse(body); var root = document.RootElement;
        StockRequestProof.Unique(root);
        var rpc = root.GetProperty("method").GetString();
        if (session is null)
        {
            if (rpc != "initialize") throw new StockIdentityDenied("session_required");
        }
        else
        {
            if (rpc is not ("tools/list" or "tools/call" or "ping" or "notifications/initialized"))
                throw new StockIdentityDenied("rpc_unsupported");
            if (rpc == "tools/call")
            {
                var parameters = root.GetProperty("params"); StockRequestProof.Unique(parameters);
                if (parameters.GetProperty("name").GetString() is not { } name || !tools.Contains(name, StringComparer.Ordinal))
                    throw new StockIdentityDenied("tool_unsupported");
            }
        }
    }
}
