using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// Who is calling one of the fleet's own MCP servers: <c>fa1.&lt;purpose&gt;.&lt;subject&gt;.&lt;mac&gt;</c>,
/// where <c>mac = base64url(HMAC-SHA256(key, "fa1|" + purpose + "|" + subject))</c> with no padding.
/// </summary>
/// <remarks>
/// <para>
/// Before this, a caller said who it was in the URL (<c>?agent=name</c>) and was believed. A token
/// is the same claim signed by the orchestrator: an agent holds its own and cannot make another's.
/// The orchestrator mints one per agent at provisioning and writes it into that agent's MCP
/// configuration as an <c>Authorization</c> header; each server verifies it with the same key
/// (<c>FLEET_AGENT_IDENTITY_KEY</c>) and takes the caller's name from the subject.
/// </para>
/// <para>
/// Identity is derived, not stored: there is no token table, and the same agent always gets the
/// same token under the same key. Every configured key verifies; only the first mints. Rotation
/// is: prepend the new key, reprovision the agents, remove the old key.
/// </para>
/// <para>
/// MUST NOT log, echo or return a token, or use it as a metric label: it is a credential. The
/// subject may be logged. The key is its own, never the journal's: an identity must not depend on
/// whether the journal is on.
/// </para>
/// </remarks>
public static class AgentIdentityTokens
{
    public const string Version = "fa1";

    /// <summary>The subject is an agent's name. What it may do is decided by that agent's grants.</summary>
    public const string PurposeAgent = "agent";

    /// <summary>
    /// The subject is a fleet service or the operator. It is not an agent and has no tool grants:
    /// a server accepts it only where it says so (the orchestrator reading fleet-memory, an
    /// operator's own MCP client).
    /// </summary>
    public const string PurposeService = "service";

    /// <summary>Smallest accepted key, in bytes after decoding.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>The header scheme a token is sent under.</summary>
    public const string Scheme = "Bearer";

    private static readonly Regex SubjectPattern =
        new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidSubject(string? subject) =>
        subject is not null && SubjectPattern.IsMatch(subject);

    private static bool IsKnownPurpose(string purpose) => purpose is PurposeAgent or PurposeService;

    /// <summary>
    /// Parses the key setting: comma-separated base64url keys, the first of which mints.
    /// </summary>
    /// <exception cref="FormatException">
    /// An entry that is not base64url, or one shorter than <see cref="MinimumKeyBytes"/>. The
    /// message names the entry's position and never its value. No entry at all is not an error:
    /// it is a deployment without the key, and the empty list says so.
    /// </exception>
    public static IReadOnlyList<byte[]> ParseKeys(string? value)
    {
        var keys = new List<byte[]>();
        var entries = (value ?? string.Empty).Split(',');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i].Trim();
            if (entry.Length == 0) continue;

            var decoded = DecodeBase64Url(entry)
                ?? throw new FormatException($"agent identity key {i + 1} is not base64url");
            if (decoded.Length < MinimumKeyBytes)
                throw new FormatException($"agent identity key {i + 1} decodes to fewer than {MinimumKeyBytes} bytes");
            keys.Add(decoded);
        }

        return keys;
    }

    /// <summary>The token for <paramref name="subject"/> under <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException">The purpose is not one of this type's, or the subject is not a valid name.</exception>
    public static string Mint(byte[] key, string purpose, string subject)
    {
        if (!IsKnownPurpose(purpose)) throw new ArgumentException("unknown purpose", nameof(purpose));
        if (!IsValidSubject(subject)) throw new ArgumentException("subject is not a valid name", nameof(subject));
        return $"{Version}.{purpose}.{subject}.{Mac(key, purpose, subject)}";
    }

    /// <summary>
    /// Whether <paramref name="token"/> was minted under one of <paramref name="keys"/>. False for
    /// anything else, including a well-formed token under another key, and never a throw.
    /// </summary>
    public static bool TryVerify(string? token, IReadOnlyList<byte[]> keys, out string purpose, out string subject)
    {
        purpose = subject = "";
        if (string.IsNullOrEmpty(token) || keys.Count == 0) return false;

        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Version) return false;
        if (!IsKnownPurpose(parts[1]) || !IsValidSubject(parts[2])) return false;

        var presented = DecodeBase64Url(parts[3]);
        if (presented is null) return false;

        var verified = false;
        foreach (var key in keys)
        {
            var expected = MacBytes(key, parts[1], parts[2]);
            // Every key is tried, in constant time each, so the answer does not say which one matched.
            verified |= CryptographicOperations.FixedTimeEquals(expected, presented);
        }
        if (!verified) return false;

        purpose = parts[1];
        subject = parts[2];
        return true;
    }

    /// <summary>The token of an <c>Authorization: Bearer …</c> header, or null when the header is not one.</summary>
    public static string? BearerOf(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return null;
        var value = authorization.Trim();
        if (value.Length <= Scheme.Length + 1
            || !value.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = value[(Scheme.Length + 1)..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>Whether a bearer value is one of this type's tokens by its shape, valid or not.</summary>
    public static bool LooksLikeToken(string? bearer) =>
        bearer is not null && bearer.StartsWith(Version + ".", StringComparison.Ordinal);

    private static string Mac(byte[] key, string purpose, string subject) =>
        Convert.ToBase64String(MacBytes(key, purpose, subject)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] MacBytes(byte[] key, string purpose, string subject) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{Version}|{purpose}|{subject}"));

    private static byte[]? DecodeBase64Url(string value)
    {
        if (value.Length == 0 || value.AsSpan().IndexOfAnyExcept(Base64UrlAlphabet) >= 0) return null;
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", 0 => padded, _ => "" };
        if (padded.Length == 0) return null;
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }

    private static readonly System.Buffers.SearchValues<char> Base64UrlAlphabet =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");
}
