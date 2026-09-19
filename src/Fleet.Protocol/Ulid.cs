using System.Security.Cryptography;

namespace Fleet.Protocol;

/// <summary>
/// A 26-character Crockford base32 ULID: 48 bits of millisecond timestamp followed by 80 bits of
/// randomness.
/// </summary>
/// <remarks>
/// <para>
/// Sortable, opaque, index-friendly, and readable during incident triage — which is why the schema
/// uses it rather than a UUID for every internal identifier.
/// </para>
/// <para>
/// The sort order is not cosmetic. <c>appender_epoch</c> is compared with <c>&lt;</c> and
/// <c>&gt;</c> to decide whether an append is stale, a restart, or in-epoch, and that comparison is
/// only meaningful because ULIDs sort lexicographically in time order. The encoding is
/// fixed-width and uses a zero-padded big-endian timestamp for exactly that reason: a variable-width
/// or little-endian encoding would still round-trip while silently destroying the ordering.
/// </para>
/// </remarks>
public static class Ulid
{
    /// <summary>Crockford base32: no I, L, O or U, so a transcribed id cannot be misread.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Length = 26;
    private const int TimestampChars = 10;
    private const int RandomnessChars = 16;
    private const int RandomnessBytes = 10;

    /// <summary>A new ULID for the current instant.</summary>
    public static string NewUlid() => NewUlid(DateTimeOffset.UtcNow);

    /// <summary>A new ULID for a given instant. Exposed so tests can pin ordering.</summary>
    public static string NewUlid(DateTimeOffset timestamp)
    {
        var milliseconds = timestamp.ToUnixTimeMilliseconds();

        if (milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "ULID timestamps start at the Unix epoch.");

        Span<char> buffer = stackalloc char[Length];

        // Timestamp: 48 bits, most-significant first, so lexicographic order is time order.
        for (var i = TimestampChars - 1; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(milliseconds & 0x1F)];
            milliseconds >>= 5;
        }

        Span<byte> randomness = stackalloc byte[RandomnessBytes];
        RandomNumberGenerator.Fill(randomness);

        // 80 bits of randomness into 16 base32 characters, five bits at a time.
        var bits = 0;
        var accumulator = 0UL;
        var written = 0;

        for (var i = 0; i < RandomnessBytes; i++)
        {
            accumulator = (accumulator << 8) | randomness[i];
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                buffer[TimestampChars + written++] = Alphabet[(int)((accumulator >> bits) & 0x1F)];
            }
        }

        // 80 is a multiple of 5, so every bit is consumed and no padding is needed.
        if (written != RandomnessChars)
            throw new InvalidOperationException($"ULID randomness encoded to {written} characters, expected {RandomnessChars}.");

        return new string(buffer);
    }

    /// <summary>
    /// True when <paramref name="value"/> is a syntactically valid ULID.
    /// </summary>
    /// <remarks>
    /// Used on identifiers arriving from the south surface. The column is
    /// <c>CHAR(26) ascii_bin</c>, so an over-long or non-alphabet value would be truncated or
    /// rejected by the database rather than by us — and a truncated identifier that still matches a
    /// row is worse than a refused one.
    /// </remarks>
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != Length) return false;

        foreach (var c in value)
            if (Alphabet.IndexOf(c) < 0)
                return false;

        return true;
    }
}
