using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleet.Conversations.Journal;

/// <summary>
/// Journal publisher tokens: <c>cj1.&lt;purpose&gt;.&lt;subject&gt;.&lt;mac&gt;</c>, where
/// <c>mac = base64url(HMAC-SHA256(key, "cj1|" + purpose + "|" + subject))</c> with no padding.
/// </summary>
/// <remarks>
/// <para>
/// Identity is DERIVED, not stored: there is no token table. The subject names the publishing
/// runtime and becomes the observer of everything it writes.
/// </para>
/// <para>
/// Every configured key verifies; only the first mints. Rotation is: prepend the new key, re-mint,
/// remove the old key.
/// </para>
/// <para>
/// ⚠️ A token is a credential. It is never logged, echoed or used as a metric label. The subject may
/// be logged.
/// </para>
/// </remarks>
public static class JournalTokens
{
    public const string Version = "cj1";

    public const string PurposeIngest = "ingest";
    public const string PurposeStatus = "status";

    /// <summary>
    /// The read tools (#394): <c>POST /journal/v1/mcp</c> and nothing else. It carries no scope —
    /// what a subject may read is the deployment's decision, made on the server.
    /// </summary>
    public const string PurposeRead = "read";

    /// <summary>Reserved for the service-publisher slice. Refused by every route in this slice.</summary>
    public const string PurposeIngestService = "ingest-service";

    /// <summary>Smallest accepted key, in bytes after decoding.</summary>
    public const int MinimumKeyBytes = 32;

    private static readonly Regex SubjectPattern =
        new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PurposePattern =
        new("^[a-z-]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidSubject(string? subject) =>
        subject is not null && SubjectPattern.IsMatch(subject);

    /// <summary>
    /// Parses <c>Comms__Journal__TokenKeys</c>: comma-separated base64url keys.
    /// </summary>
    /// <exception cref="FormatException">
    /// No key, an entry that is not base64url, or one shorter than
    /// <see cref="MinimumKeyBytes"/>. The message names the entry's position and never its value.
    /// </exception>
    public static IReadOnlyList<byte[]> ParseKeys(string? value)
    {
        var keys = new List<byte[]>();
        var entries = (value ?? string.Empty).Split(',');

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i].Trim();
            if (entry.Length == 0) continue;

            var decoded = DecodeBase64Url(entry);
            if (decoded is null)
                throw new FormatException($"key {i + 1} is not base64url");

            if (decoded.Length < MinimumKeyBytes)
                throw new FormatException($"key {i + 1} decodes to fewer than {MinimumKeyBytes} bytes");

            keys.Add(decoded);
        }

        if (keys.Count == 0)
            throw new FormatException("no key is configured");

        return keys;
    }

    /// <summary>Mints a token with <paramref name="key"/> — the first configured key.</summary>
    public static string Mint(byte[] key, string purpose, string subject)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!PurposePattern.IsMatch(purpose ?? string.Empty))
            throw new ArgumentException("not a token purpose", nameof(purpose));

        if (!IsValidSubject(subject))
            throw new ArgumentException("a subject is 1-128 characters of [A-Za-z0-9_-]", nameof(subject));

        return $"{Version}.{purpose}.{subject}.{EncodeBase64Url(Mac(key, purpose!, subject))}";
    }

    /// <summary>
    /// Verifies <paramref name="token"/> for <paramref name="requiredPurpose"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The MAC for the PRESENTED purpose and subject is recomputed under every key and compared with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>. The loop never stops at the first key
    /// that matches, and the purpose check comes after it, so a wrong-purpose token costs what a
    /// right-purpose one does.
    /// </para>
    /// <para>
    /// Absent, malformed, bad-MAC and wrong-purpose tokens are all simply <c>false</c>; the caller
    /// answers them identically.
    /// </para>
    /// </remarks>
    public static bool TryVerify(
        string? token, string requiredPurpose, IReadOnlyList<byte[]> keys, out string subject)
    {
        subject = string.Empty;

        var parts = (token ?? string.Empty).Split('.');
        var wellFormed = parts.Length == 4
            && string.Equals(parts[0], Version, StringComparison.Ordinal)
            && PurposePattern.IsMatch(parts[1])
            && IsValidSubject(parts[2]);

        var purpose = wellFormed ? parts[1] : string.Empty;
        var presentedSubject = wellFormed ? parts[2] : string.Empty;
        var presented = (wellFormed ? DecodeBase64Url(parts[3]) : null) ?? [];

        var matched = false;
        foreach (var key in keys)
        {
            var expected = Mac(key, purpose, presentedSubject);
            matched |= CryptographicOperations.FixedTimeEquals(expected, presented);
        }

        if (!wellFormed || !matched
            || string.IsNullOrEmpty(requiredPurpose)
            || !string.Equals(purpose, requiredPurpose, StringComparison.Ordinal))
            return false;

        subject = presentedSubject;
        return true;
    }

    private static byte[] Mac(byte[] key, string purpose, string subject) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{Version}|{purpose}|{subject}"));

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Base64url with optional padding; null for anything else.</summary>
    private static byte[]? DecodeBase64Url(string value)
    {
        var body = value.TrimEnd('=');
        if (body.Length == 0 || value.Length - body.Length > 2) return null;

        foreach (var c in body)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return null;
        }

        var standard = body.Replace('-', '+').Replace('_', '/');
        standard = (standard.Length % 4) switch
        {
            2 => standard + "==",
            3 => standard + "=",
            0 => standard,
            _ => null!,
        };

        if (standard is null) return null;

        try
        {
            return Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
