using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Shared.Security;

public sealed class StockIdentityDenied(string reason) : Exception(reason);

// One operator-owned file, read afresh at each admission. This is NOT a lifecycle or grant store.
public sealed record StockEnrollmentAuthority
{
    public string State { get; init; } = "";
    public string Agent { get; init; } = "";
    public string SandboxId { get; init; } = "";
    public Guid Epoch { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string GatewayIssuer { get; init; } = "";
    public string GatewayAudience { get; init; } = "";
    public string GatewayJwksPath { get; init; } = "";
    public string AssertionPrivateKeyPath { get; init; } = "";
    public string AssertionPublicKeyPath { get; init; } = "";
    public string MemoryHost { get; init; } = "";
    public string BrokerHost { get; init; } = "";
    public string PolicySha256 { get; init; } = "";
    public string StockCommit { get; init; } = "";

    public static byte[] ReadBounded(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 16384) throw new StockIdentityDenied("authority_size");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new StockIdentityDenied("authority_size");
        return bytes;
    }
    public static string ReadText(string path) => new UTF8Encoding(false, true).GetString(ReadBounded(path));

    public static StockEnrollmentAuthority Load(string path)
    {
        try
        {
            var bytes = ReadBounded(path);
            using var document = JsonDocument.Parse(bytes);
            StockRequestProof.Unique(document.RootElement);
            var record = JsonSerializer.Deserialize<StockEnrollmentAuthority>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })!;
            if (record.State != "active" || record.ExpiresAt <= DateTimeOffset.UtcNow || record.Epoch == Guid.Empty
                || string.IsNullOrWhiteSpace(record.Agent) || record.Agent.Length > 64 || record.Agent.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                || string.IsNullOrWhiteSpace(record.SandboxId) || record.SandboxId.Length > 256
                || string.IsNullOrWhiteSpace(record.GatewayIssuer) || string.IsNullOrWhiteSpace(record.GatewayAudience)
                || !Path.IsPathFullyQualified(record.GatewayJwksPath)
                || !Path.IsPathFullyQualified(record.AssertionPrivateKeyPath)
                || !Path.IsPathFullyQualified(record.AssertionPublicKeyPath)
                || Uri.CheckHostName(record.MemoryHost) != UriHostNameType.Dns)
                throw new StockIdentityDenied("enrollment_denied");
            return record;
        }
        catch (StockIdentityDenied) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { throw new StockIdentityDenied("authority_unavailable"); }
    }
}

public sealed record StockRequestProof(string Agent, string Sandbox, Guid Epoch, string Method, string SessionHash,
    string BodyHash, long Expires, string Nonce)
{
    public const string Header = "X-Fleet-Assertion";
    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public static string HashSession(string? value) => Hash(Encoding.UTF8.GetBytes(value ?? ""));
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value)
    {
        if (value.Length > 16384 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || value.Length % 4 == 1)
            throw new StockIdentityDenied("assertion_encoding");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (Encode(bytes) != value) throw new StockIdentityDenied("assertion_encoding");
        return bytes;
    }
    public static void Unique(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
            if (!names.Add(p.Name)) throw new StockIdentityDenied("duplicate_field");
    }
    public static string Issue(StockEnrollmentAuthority record, string method, byte[] body, string? session)
    {
        var proof = new StockRequestProof(record.Agent, record.SandboxId, record.Epoch, method, HashSession(session),
            Hash(body), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 15, Guid.NewGuid().ToString("N"));
        using var key = ECDsa.Create(); key.ImportFromPem(StockEnrollmentAuthority.ReadText(record.AssertionPrivateKeyPath));
        RequireP256(key);
        var input = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "stock-memory-v1" })) + "."
            + Encode(JsonSerializer.SerializeToUtf8Bytes(proof));
        return input + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    public static StockRequestProof Verify(StockEnrollmentAuthority record, string token, string method, byte[] body, string? session)
    {
        try
        {
            if (token.Length > 16384) throw new StockIdentityDenied("assertion_size");
            var parts = token.Split('.'); if (parts.Length != 3) throw new StockIdentityDenied("assertion_shape");
            using var header = JsonDocument.Parse(Decode(parts[0])); Unique(header.RootElement);
            if (header.RootElement.EnumerateObject().Count() != 2 || header.RootElement.GetProperty("alg").GetString() != "ES256"
                || header.RootElement.GetProperty("typ").GetString() != "stock-memory-v1") throw new StockIdentityDenied("assertion_header");
            using var key = ECDsa.Create(); key.ImportFromPem(StockEnrollmentAuthority.ReadText(record.AssertionPublicKeyPath)); RequireP256(key);
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new StockIdentityDenied("assertion_signature");
            var bytes = Decode(parts[1]); using var claims = JsonDocument.Parse(bytes); Unique(claims.RootElement);
            var proof = JsonSerializer.Deserialize<StockRequestProof>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (proof.Agent != record.Agent || proof.Sandbox != record.SandboxId || proof.Epoch != record.Epoch
                || proof.Method != method || proof.BodyHash != Hash(body) || proof.SessionHash != HashSession(session)
                || proof.Expires <= now || proof.Expires > now + 15 || !Guid.TryParseExact(proof.Nonce, "N", out _))
                throw new StockIdentityDenied("assertion_binding");
            return proof;
        }
        catch (StockIdentityDenied) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or CryptographicException
            or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw new StockIdentityDenied("assertion_invalid"); }
    }
    private static void RequireP256(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new StockIdentityDenied("assertion_key");
    }
}
