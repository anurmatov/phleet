using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// The placeholder an agent holds instead of a bound secret. The orchestrator mints one per bound
/// Env Ref on every provision and keeps only its SHA-256; <c>fleet-secrets</c> swaps it for the real
/// value on a request to a bound host. One definition, so the two cannot disagree on the format.
/// </summary>
/// <remarks>
/// A placeholder is not a secret value, but inside the secrets network it is a capability: it names
/// one agent's grant of one secret. It is 256 random bits, so it cannot be guessed, and it dies when
/// the agent is reprovisioned or the grant is removed.
/// </remarks>
public static partial class SecretPlaceholders
{
    /// <summary>
    /// Says what the value is, and matches no vendor's key format. An earlier <c>phs_</c> prefix had
    /// the shape of a PostHog secret key: secret scanners flagged every placeholder, and a real key of
    /// that kind would have been taken for one.
    /// </summary>
    public const string Prefix = "fleet_placeholder_";

    /// <summary>Base64url of 32 bytes without padding.</summary>
    public const int RandomLength = 43;

    public const int Length = 18 + RandomLength;

    /// <summary>The value the provision preview shows where a real provision mints a placeholder.</summary>
    public const string PreviewValue = "<placeholder>";

    [GeneratedRegex(@"fleet_placeholder_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Create()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return Prefix + Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool IsPlaceholder(string? value) =>
        value is { Length: Length } && Pattern().Match(value) is { Success: true, Index: 0 };

    /// <summary>Every placeholder in <paramref name="text"/>, in order, duplicates included.</summary>
    public static IReadOnlyList<string> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(Prefix, StringComparison.Ordinal))
            return [];
        return Pattern().Matches(text).Select(m => m.Value).ToArray();
    }

    /// <summary>Lowercase hex SHA-256 of the placeholder's UTF-8 bytes: what is stored and compared.</summary>
    public static string Hash(string placeholder) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(placeholder)));
}
