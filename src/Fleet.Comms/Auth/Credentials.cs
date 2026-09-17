using System.Security.Cryptography;

namespace Fleet.Comms.Auth;

/// <summary>
/// The three credentials this boundary issues, and the one shape they share.
///
/// <para>Every credential is <b>256 bits from a CSPRNG</b> (docs/first-party-api.md §3) presented
/// as <c>&lt;recordId&gt;.&lt;secret&gt;</c>.</para>
///
/// <para><b>Why the id prefix.</b> The secret is stored only as a salted Argon2id hash, and a
/// salted hash is not deterministic — so it cannot be used to look a record up. Without a
/// non-secret handle the server would have to hash the presented value against every stored
/// record, which is both O(n) Argon2 evaluations per request and a timing oracle. The prefix is
/// the handle; the entropy is entirely in the secret half.</para>
///
/// <para>The device secret is the exception and carries no prefix: <c>deviceId</c> is already
/// presented alongside it in the same request body.</para>
/// </summary>
public static class Credentials
{
    /// <summary>Secret length. 256 bits, per §3, for all three credential kinds.</summary>
    public const int SecretBytes = 32;

    /// <summary>Record-handle length. Not a secret; sized only to avoid collisions.</summary>
    public const int RecordIdBytes = 12;

    private const char Separator = '.';

    /// <summary>A fresh 256-bit secret, base64url, unpadded.</summary>
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>A fresh non-secret record handle.</summary>
    public static string NewRecordId() => Encode(RandomNumberGenerator.GetBytes(RecordIdBytes));

    /// <summary>Compose the wire form of a prefixed credential.</summary>
    public static string Compose(string recordId, string secret) => $"{recordId}{Separator}{secret}";

    /// <summary>
    /// Split a presented credential into its handle and secret.
    ///
    /// <para>A malformed value returns false and the caller answers exactly as it would for an
    /// unknown record — a caller must never be able to tell "you sent nonsense" from "that is not
    /// a credential I know" (§3.6).</para>
    /// </summary>
    public static bool TryParse(string? presented, out string recordId, out string secret)
    {
        recordId = "";
        secret = "";
        if (string.IsNullOrEmpty(presented))
            return false;

        var separator = presented.IndexOf(Separator);
        if (separator <= 0 || separator == presented.Length - 1)
            return false;

        // Exactly one separator: a value with two is not a credential this server issued, and
        // accepting it would leave the handle ambiguous.
        if (presented.IndexOf(Separator, separator + 1) >= 0)
            return false;

        recordId = presented[..separator];
        secret = presented[(separator + 1)..];
        return true;
    }

    private static string Encode(byte[] bytes) => Base64Url.Encode(bytes);
}

/// <summary>Unpadded base64url, so a credential is safe in a header without escaping.</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
