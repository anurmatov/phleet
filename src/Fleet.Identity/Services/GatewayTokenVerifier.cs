using System.Text;
using System.Text.Json;
using Fleet.Identity.Configuration;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Fleet.Identity.Services;

public sealed class GatewayTokenVerifier
{
    private readonly IdentityOptions _options;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    public GatewayTokenVerifier(IdentityOptions options, string jwks)
    {
        _options = options;
        using var json = JsonDocument.Parse(jwks);
        foreach (var element in json.RootElement.GetProperty("keys").EnumerateArray())
        {
            var fields = JwtEncoding.Fields(element);
            if (fields.ContainsKey("d") || fields["kty"].GetString() != "OKP" || fields["crv"].GetString() != "Ed25519"
                || fields.TryGetValue("alg", out var alg) && alg.GetString() != "EdDSA"
                || fields.TryGetValue("use", out var use) && use.GetString() != "sig") throw new FormatException("Invalid pinned JWKS.");
            var kid = fields["kid"].GetString()!;
            var key = JwtEncoding.Decode(fields["x"].GetString()!);
            if (string.IsNullOrWhiteSpace(kid) || key.Length != 32 || !_keys.TryAdd(kid, key)) throw new FormatException("Invalid pinned JWKS.");
        }
        if (_keys.Count == 0) throw new FormatException("Empty pinned JWKS.");
    }
    public void Verify(string token, string? sandboxId)
    {
        try
        {
            if (token.Length > 16384) throw new FormatException();
            var parts = token.Split('.'); if (parts.Length != 3) throw new FormatException();
            using var header = JsonDocument.Parse(JwtEncoding.Decode(parts[0]));
            var h = JwtEncoding.Fields(header.RootElement);
            if (h.Count != 3 || h["alg"].GetString() != "EdDSA" || h["typ"].GetString() != "openshell-ext+jwt"
                || !_keys.TryGetValue(h["kid"].GetString()!, out var key)) throw new FormatException();
            var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(key));
            var input = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            verifier.BlockUpdate(input, 0, input.Length);
            if (!verifier.VerifySignature(JwtEncoding.Decode(parts[2]))) throw new FormatException();
            using var payload = JsonDocument.Parse(JwtEncoding.Decode(parts[1]));
            var p = JwtEncoding.Fields(payload.RootElement);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var issued = p["iat"].GetInt64(); var expires = p["exp"].GetInt64();
            if (p["iss"].GetString() != _options.GatewayIssuer || p["aud"].GetString() != _options.GatewayAudience
                || issued > now || issued < now - 900 || expires <= now || expires <= issued || expires - issued > 900
                || string.IsNullOrWhiteSpace(p["sub"].GetString()) || string.IsNullOrWhiteSpace(p["jti"].GetString())) throw new FormatException();
            var kind = p["caller_kind"].GetString();
            if (sandboxId is null)
            {
                if (kind != "gateway" || p.ContainsKey("sandbox_id")) throw new FormatException();
            }
            else if (kind != "supervisor" || p["sandbox_id"].GetString() != sandboxId || sandboxId.Length == 0) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new IdentityDeniedException("gateway_token"); }
    }
}
