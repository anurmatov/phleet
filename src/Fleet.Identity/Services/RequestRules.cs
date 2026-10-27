using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Shared.Security;

namespace Fleet.Identity.Services;

public static class RequestRules
{
    public const int MaxBody = 262144;
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string SessionHash(string? session) => string.IsNullOrEmpty(session)
        ? FleetRequestBinding.AbsentSessionHash : Hash(Encoding.UTF8.GetBytes(session));
    public static bool ValidSession(string session) => session.Length is > 0 and <= 256
        && session.All(c => c is >= '!' and <= '~');
    public static void Validate(string method, string target, byte[] body, IEnumerable<KeyValuePair<string, string>> headers, bool ingress)
    {
        // One immutable route. Raw targets with queries are deliberately unsupported in this receiver.
        if (method is not ("POST" or "GET" or "DELETE") || target != "/mcp" || body.Length > MaxBody)
            throw new IdentityDeniedException("request_shape");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? session = null, contentType = null;
        foreach (var (name, value) in headers)
        {
            if (!seen.Add(name) && !name.Equals("Accept", StringComparison.OrdinalIgnoreCase) || value.Any(c => c is '\r' or '\n' or '\0') || value.Length > 16384
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Fleet-", StringComparison.OrdinalIgnoreCase)
                    && !(ingress && name.Equals("X-Fleet-Assertion", StringComparison.OrdinalIgnoreCase)))
                throw new IdentityDeniedException("identity_override");
            if (name.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase)) session = value;
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
        }
        if (session is not null && !ValidSession(session)) throw new IdentityDeniedException("session_shape");
        if (method == "POST")
        {
            if (contentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
                throw new IdentityDeniedException("content_type");
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("method", out var rpcMethod)
                || rpcMethod.ValueKind != JsonValueKind.String
                || session is null && rpcMethod.GetString() != "initialize"
                || session is not null && rpcMethod.GetString() == "initialize")
                throw new IdentityDeniedException("session_required");
        }
        else if (body.Length != 0 || session is null) throw new IdentityDeniedException("session_required");
    }
    public static void ValidateBootstrap(string method, string target, byte[] body, IEnumerable<KeyValuePair<string, string>> headers, bool ingress)
    {
        if (method != "POST" || target != "/identity/ready" || body.Length != 0) throw new IdentityDeniedException("bootstrap_shape");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            if (!names.Add(name) || value.Length > 16384 || value.Any(c => c is '\r' or '\n' or '\0')
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Fleet-", StringComparison.OrdinalIgnoreCase)
                    && !(ingress && name.Equals("X-Fleet-Assertion", StringComparison.OrdinalIgnoreCase)))
                throw new IdentityDeniedException("bootstrap_override");
    }
    public static FleetRequestBinding Bind(string method, string target, byte[] body, string? session) =>
        new(method, target, Hash(body), SessionHash(session));
}

public sealed class IdentityDeniedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// Only emitted after current identity and exact durable ownership were authenticated.
public sealed class IdentitySessionExpiredException : Exception;
