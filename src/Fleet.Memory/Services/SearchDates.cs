using System.Globalization;

namespace Fleet.Memory.Services;

/// <summary>
/// Reads the <c>since</c> of a search: an ISO 8601 date (<c>2026-03-01</c>, the start of that day
/// in UTC) or date and time (<c>2026-03-01T09:30:00Z</c>, with or without seconds, a fraction or an
/// offset; no offset means UTC). Nothing else: a value read loosely would silently mean another day.
/// </summary>
public static class SearchDates
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public static bool TryParseSince(string value, out DateTimeOffset since) =>
        DateTimeOffset.TryParseExact(value.Trim(), Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out since);
}
