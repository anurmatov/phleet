using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.Orchestrator.Services.Push;

/// <summary>
/// The signed token Apple's push service asks a provider for: an ES256 JWT naming the key and the
/// team, signed with the team's <c>.p8</c> key.
/// </summary>
/// <remarks>
/// Apple rejects a token older than an hour and throttles a provider that mints one more often than
/// every twenty minutes, so one token is kept and replaced at fifty minutes. The key is parsed once
/// and held; neither it nor a token is ever logged.
/// </remarks>
public sealed class ApnsProviderToken : IDisposable
{
    /// <summary>Inside Apple's window on both sides: not before twenty minutes, not after sixty.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);

    private readonly ECDsa _key;
    private readonly string _keyId;
    private readonly string _teamId;
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _issuedAt;

    public ApnsProviderToken(string keyBase64OrPem, string keyId, string teamId)
    {
        _key = ImportKey(keyBase64OrPem);
        _keyId = keyId.Trim();
        _teamId = teamId.Trim();
    }

    /// <summary>The current token, minted if there is none or the held one is due for replacement.</summary>
    public string Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_token is null || now - _issuedAt >= Lifetime || now < _issuedAt)
            {
                _token = Mint(now);
                _issuedAt = now;
            }
            return _token;
        }
    }

    /// <summary>Drops the held token, so the next request mints a new one.</summary>
    public void Invalidate()
    {
        lock (_gate) _token = null;
    }

    private string Mint(DateTimeOffset now)
    {
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "ES256", ["kid"] = _keyId }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["iss"] = _teamId, ["iat"] = now.ToUnixTimeSeconds() }));
        var signingInput = header + "." + claims;
        // IEEE P1363 (r || s), which is what a JWT carries and what SignData produces by default.
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + Encode(signature);
    }

    /// <summary>
    /// Reads the key from base64 of the <c>.p8</c> file, or from the PEM text itself.
    /// Throws <see cref="FormatException"/> with no part of the value in the message.
    /// </summary>
    public static ECDsa ImportKey(string keyBase64OrPem)
    {
        var text = keyBase64OrPem.Trim();
        if (!text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
            catch (FormatException) { throw new FormatException("the APNs key is neither PEM text nor base64 of a .p8 file"); }
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(text);
            // ES256 is P-256 by name: other 256-bit curves import too, and Apple refuses their tokens.
            if (key.ExportParameters(false).Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new FormatException("the APNs key is not a P-256 key");
            // A public key imports as well and fails only when a token is signed. Sign once here instead.
            key.SignData([0], HashAlgorithmName.SHA256);
            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new FormatException("the APNs key could not be read as a private key");
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _key.Dispose();
}
