using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Identity.Configuration;
using Fleet.Shared.Security;

namespace Fleet.Identity.Services;

public sealed class RequestAssertionIssuer(IdentityOptions options, ECDsa key)
{
    // ECDsa instances are not shared concurrently without synchronization.
    private readonly object _gate = new();
    private readonly bool _isP256 = key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7";
    public string Issue(VerifiedAgentIdentity identity, FleetRequestBinding binding)
    {
        if (!_isP256)
            throw new InvalidOperationException("Outside P-256 key required.");
        var audience = (identity.Service, identity.Route) switch
        {
            ("memory", "/mcp") => "memory:/mcp",
            ("orchestrator", "/identity/ready") when binding.Method == "POST" && binding.RawTarget == "/identity/ready"
                && binding.BodySha256 == FleetRequestBinding.AbsentSessionHash && binding.SessionHash == FleetRequestBinding.AbsentSessionHash
                => "orchestrator:/identity/ready",
            _ => throw new InvalidOperationException("assertion_scope_unsupported"),
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object> {
            ["iss"] = options.Issuer, ["aud"] = audience, ["sub"] = identity.AgentName,
            ["gateway_id"] = identity.GatewayId, ["sandbox_id"] = identity.SandboxId,
            ["launch_id"] = identity.LaunchId.ToString("D"), ["identity_epoch"] = identity.IdentityEpoch.ToString("D"),
            ["iat"] = now, ["exp"] = now + 15, ["jti"] = Guid.NewGuid().ToString("N"),
            ["method"] = binding.Method, ["target"] = binding.RawTarget,
            ["body_sha256"] = binding.BodySha256, ["session_hash"] = binding.SessionHash };
        var input = JwtEncoding.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "fleet-request+jwt", kid = options.KeyId }))
            + "." + JwtEncoding.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        lock (_gate) return input + "." + JwtEncoding.Encode(key.SignData(Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}

internal static class JwtEncoding
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value)
    {
        if (value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || value.Length % 4 == 1)
            throw new FormatException();
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (Encode(bytes) != value) throw new FormatException();
        return bytes;
    }
    public static Dictionary<string, JsonElement> Fields(JsonElement element)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!fields.TryAdd(property.Name, property.Value)) throw new FormatException();
        return fields;
    }
}
