using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Mints the derived conversation-journal credentials that provisioning places in agent config.
/// The wire format deliberately mirrors Fleet.Comms' verifier without taking a dependency on the
/// conversations implementation assembly.
/// </summary>
public sealed class JournalTokenService(IConfiguration configuration)
{
    public const string PurposeIngest = "ingest";
    public const string PurposeRead = "read";
    public const string PurposeStatus = "status";
    public const int MinimumKeyBytes = 32;

    private static readonly Regex SubjectPattern =
        new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PurposePattern =
        new("^[a-z-]{1,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The fault code provisioning must return before touching a container, or null.</summary>
    public string? DescribeKeyFault()
    {
        var raw = configuration["Journal:TokenKey"];
        if (string.IsNullOrWhiteSpace(raw)) return "journal_key_missing";

        try
        {
            _ = ParseKeys(raw);
            return null;
        }
        catch (FormatException)
        {
            return "journal_key_invalid";
        }
    }

    public string Mint(string purpose, string subject)
    {
        var fault = DescribeKeyFault();
        if (fault is not null) throw new JournalConfigurationException(fault);
        if (!PurposePattern.IsMatch(purpose ?? string.Empty))
            throw new ArgumentException("not a token purpose", nameof(purpose));
        if (!SubjectPattern.IsMatch(subject ?? string.Empty))
            throw new ArgumentException("a subject is 1-128 characters of [A-Za-z0-9_-]", nameof(subject));

        var key = ParseKeys(configuration["Journal:TokenKey"]!)[0];
        var payload = Encoding.UTF8.GetBytes($"cj1|{purpose}|{subject}");
        var mac = EncodeBase64Url(HMACSHA256.HashData(key, payload));
        return $"cj1.{purpose}.{subject}.{mac}";
    }

    /// <summary>Union configured by the operator; blanks are ignored and duplicates collapse.</summary>
    public IReadOnlyList<long> ExcludedChatIds()
    {
        var result = new SortedSet<long>();
        foreach (var part in (configuration["Journal:ExcludedChatIds"] ?? string.Empty).Split(','))
        {
            if (long.TryParse(part.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id))
                result.Add(id);
        }
        return [.. result];
    }

    private static IReadOnlyList<byte[]> ParseKeys(string raw)
    {
        var keys = new List<byte[]>();
        foreach (var entry in raw.Split(','))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var decoded = DecodeBase64Url(entry.Trim())
                ?? throw new FormatException("journal key is not base64url");
            if (decoded.Length < MinimumKeyBytes)
                throw new FormatException("journal key is too short");
            keys.Add(decoded);
        }

        if (keys.Count == 0) throw new FormatException("no journal key is configured");
        return keys;
    }

    private static byte[]? DecodeBase64Url(string value)
    {
        var body = value.TrimEnd('=');
        if (body.Length == 0 || value.Length - body.Length > 2) return null;
        if (body.Any(c => c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
                or (>= '0' and <= '9') or '-' or '_')))
            return null;

        var standard = body.Replace('-', '+').Replace('_', '/');
        standard = (standard.Length % 4) switch
        {
            0 => standard,
            2 => standard + "==",
            3 => standard + "=",
            _ => null!,
        };
        if (standard is null) return null;

        try { return Convert.FromBase64String(standard); }
        catch (FormatException) { return null; }
    }

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class JournalConfigurationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
