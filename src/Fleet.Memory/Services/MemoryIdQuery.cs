namespace Fleet.Memory.Services;

/// <summary>
/// Decides whether a search query is a memory id, or the start of one.
///
/// It is when the whole query, trimmed, is six or more hexadecimal digits, with or without the
/// dashes of a UUID where a UUID has them (after digits 8, 12, 16 and 20). Ids are compared without
/// dashes and without case, so <c>1a2b3c</c>, <c>1A2B3C4D</c>, <c>1a2b3c4d-00</c> and the full
/// 36 characters all name the memories whose id begins that way.
/// </summary>
public static class MemoryIdQuery
{
    public const int MinDigits = 6;
    private const int UuidDigits = 32;

    /// <summary>The digits to look for, lower case and without dashes, when the query is an id.</summary>
    public static bool TryParse(string query, out string idPrefix)
    {
        idPrefix = "";
        var text = query.Trim();
        Span<char> digits = stackalloc char[UuidDigits];
        var count = 0;

        foreach (var c in text)
        {
            if (c == '-')
            {
                // Only where a UUID has one.
                if (count is not (8 or 12 or 16 or 20)) return false;
                continue;
            }
            if (!char.IsAsciiHexDigit(c) || count == UuidDigits) return false;
            digits[count++] = char.ToLowerInvariant(c);
        }

        if (count < MinDigits || text.Contains("--"))
            return false;

        idPrefix = new string(digits[..count]);
        return true;
    }

    /// <summary>An id as it is compared: lower case, no dashes.</summary>
    public static string Key(string id) => id.Replace("-", "").ToLowerInvariant();
}
