using System.Text.Json;

namespace Fleet.Shared.Security;

public static class StockMemoryRequest
{
    public static void Validate(string method, byte[] body, string? session)
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
                if (parameters.GetProperty("name").GetString() is not ("memory_get" or "memory_search" or "memory_list"))
                    throw new StockIdentityDenied("tool_unsupported");
            }
        }
    }
}
