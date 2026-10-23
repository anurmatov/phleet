using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.Shared.Security;

/// <summary>Phleet-owned ES256 request assertion format, not an upstream gateway JWT decoder.</summary>
public sealed class FleetRequestAssertionVerifier
{
    private readonly byte[] _publicKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _keyId;
    private readonly TimeProvider _clock;

    public FleetRequestAssertionVerifier(byte[] publicKey, string issuer, string audience, string keyId, TimeProvider? clock = null)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out var read);
        if (read != publicKey.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
            || string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Pinned P-256 assertion verification configuration is required.");
        _publicKey = publicKey.ToArray(); _issuer = issuer; _audience = audience; _keyId = keyId;
        _clock = clock ?? TimeProvider.System;
    }

    public (VerifiedAgentIdentity Identity, string Jti) Verify(string assertion, FleetRequestBinding request,
        string service, string route)
    {
        if (assertion.Length > 16384) throw new CryptographicException("Invalid request assertion.");
        try
        {
            var parts = assertion.Split('.');
            if (parts.Length != 3) throw new FormatException();
            using var header = JsonDocument.Parse(Decode(parts[0]));
            var h = Fields(header);
            if (h.Count != 3 || Text(h, "alg") != "ES256" || Text(h, "typ") != "fleet-request+jwt" || Text(h, "kid") != _keyId)
                throw new FormatException();
            using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(_publicKey, out _);
            var signature = Decode(parts[2]);
            if (signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new FormatException();
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var p = Fields(payload);
            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            var issued = p["iat"].GetInt64(); var expires = p["exp"].GetInt64();
            if (Text(p, "iss") != _issuer || Text(p, "aud") != _audience || issued > now || expires <= now
                || issued < now - 15 || expires <= issued || expires - issued > 15
                || Text(p, "method") != request.Method || Text(p, "target") != request.RawTarget
                || Text(p, "body_sha256") != request.BodySha256 || Text(p, "session_hash") != request.SessionHash)
                throw new FormatException();
            var agent = Opaque(Text(p, "sub"), 100);
            var gateway = Opaque(Text(p, "gateway_id"), 128);
            var sandbox = Opaque(Text(p, "sandbox_id"), 128);
            var jti = Opaque(Text(p, "jti"), 128);
            if (!Guid.TryParseExact(Text(p, "launch_id"), "D", out var launch) || launch == Guid.Empty
                || !Guid.TryParseExact(Text(p, "identity_epoch"), "D", out var epoch) || epoch == Guid.Empty)
                throw new FormatException();
            return (new(agent, gateway, sandbox, launch, epoch, service, route), jti);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new CryptographicException("Invalid request assertion."); }
    }

    private static Dictionary<string, JsonElement> Fields(JsonDocument json)
    {
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in json.RootElement.EnumerateObject())
            if (!fields.TryAdd(field.Name, field.Value)) throw new FormatException();
        return fields;
    }
    private static string Text(Dictionary<string, JsonElement> fields, string key) => fields[key].GetString() ?? throw new FormatException();
    private static string Opaque(string value, int max) => value.Length is > 0 && value.Length <= max
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? value : throw new FormatException();
    private static byte[] Decode(string text)
    {
        if (text.Length % 4 == 1 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new FormatException();
        var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
        if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != text) throw new FormatException();
        return bytes;
    }
}
