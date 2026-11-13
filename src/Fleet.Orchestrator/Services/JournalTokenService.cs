namespace Fleet.Orchestrator.Services;

/// <summary>
/// Mints the derived conversation-journal credentials that provisioning places in agent config, and
/// verifies the ones the chat service presents, with the key in <c>Journal:TokenKey</c>.
/// </summary>
/// <remarks>
/// The token format is <see cref="JournalTokens"/>, the one implementation the chat service verifies
/// and mints with too. What is here is the orchestrator's own part: where the key comes from and
/// what a missing or unusable key is called.
/// </remarks>
public sealed class JournalTokenService(IConfiguration configuration)
{
    public const string PurposeIngest = JournalTokens.PurposeIngest;
    public const string PurposeRead = JournalTokens.PurposeRead;
    public const string PurposeStatus = JournalTokens.PurposeStatus;
    public const string PurposeReadCrossChat = JournalTokens.PurposeReadCrossChat;
    public const string PurposeCrossChatAuthz = JournalTokens.PurposeCrossChatAuthz;
    public const int MinimumKeyBytes = JournalTokens.MinimumKeyBytes;

    /// <summary>The fault code provisioning must return before touching a container, or null.</summary>
    public string? DescribeKeyFault()
    {
        var raw = configuration["Journal:TokenKey"];
        if (string.IsNullOrWhiteSpace(raw)) return "journal_key_missing";

        try
        {
            _ = JournalTokens.ParseKeys(raw);
            return null;
        }
        catch (FormatException)
        {
            return "journal_key_invalid";
        }
    }

    /// <summary>Mints with the first configured key.</summary>
    /// <exception cref="JournalConfigurationException">The key is missing or unusable.</exception>
    public string Mint(string purpose, string subject)
    {
        var fault = DescribeKeyFault();
        if (fault is not null) throw new JournalConfigurationException(fault);

        return JournalTokens.Mint(JournalTokens.ParseKeys(configuration["Journal:TokenKey"])[0], purpose, subject);
    }

    /// <summary>Verifies under every configured key. False, never a throw, when the key is unusable.</summary>
    public bool TryVerify(string? token, string purpose, out string subject)
    {
        subject = "";
        if (DescribeKeyFault() is not null) return false;

        return JournalTokens.TryVerify(
            token, purpose, JournalTokens.ParseKeys(configuration["Journal:TokenKey"]), out subject);
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
}

public sealed class JournalConfigurationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
