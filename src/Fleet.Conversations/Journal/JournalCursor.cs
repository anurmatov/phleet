using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The read tools' page cursor: opaque base64url over
/// <c>{v, tool, filterHash, conversationId?, direction?, last:{key, id}}</c> (#394).
/// </summary>
/// <remarks>
/// <para>
/// <b>It holds no server state</b>, so it survives a Comms restart and needs no table. It is also
/// not signed, and does not need to be: scope is re-applied on every page, so a forged cursor can
/// only move the position inside what the caller could already read.
/// </para>
/// <para>
/// ⚠️ Whether a cursor fits a call is decided from the cursor and the arguments ALONE — tool,
/// filter hash, conversation and direction — and never from stored data. A mismatch answered by a
/// lookup would tell the caller something about rows it cannot see.
/// </para>
/// </remarks>
public sealed record JournalCursor
{
    public const int Version = 1;

    /// <summary>Longest encoded cursor accepted. Far above any cursor this class writes.</summary>
    public const int MaxEncodedLength = 1024;

    public required string Tool { get; init; }

    /// <summary>
    /// <see cref="HashFilters"/> over every argument that selects rows, so a cursor reused with
    /// other filters is refused rather than silently resumed in a different result set.
    /// </summary>
    public required string FilterHash { get; init; }

    /// <summary>The conversation a replay cursor belongs to. Null for search.</summary>
    public string? ConversationId { get; init; }

    /// <summary><c>forward</c> or <c>backward</c> for a replay cursor. Null for search.</summary>
    public string? Direction { get; init; }

    public required JournalKeyPosition Last { get; init; }

    public string Encode()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteString("tool", Tool);
            json.WriteString("filterHash", FilterHash);
            if (ConversationId is not null) json.WriteString("conversationId", ConversationId);
            if (Direction is not null) json.WriteString("direction", Direction);
            json.WriteStartObject("last");
            json.WriteNumber("key", Last.Key);
            json.WriteString("id", Last.Id);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Convert.ToBase64String(buffer.WrittenSpan).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes a cursor this class wrote. Anything else — wrong alphabet, not JSON, an unknown or
    /// repeated field, a wrong type, another version, a malformed id — is <c>false</c>.
    /// </summary>
    public static bool TryDecode(string? value, out JournalCursor? cursor)
    {
        cursor = null;

        if (string.IsNullOrEmpty(value) || value.Length > MaxEncodedLength) return false;

        var bytes = DecodeBase64Url(value);
        if (bytes is null) return false;

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            int? version = null;
            string? tool = null, filterHash = null, conversationId = null, direction = null;
            JournalKeyPosition? last = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return false;

                switch (property.Name)
                {
                    case "v" when property.Value.ValueKind == JsonValueKind.Number
                                  && property.Value.TryGetInt32(out var v):
                        version = v;
                        break;
                    case "tool" when property.Value.ValueKind == JsonValueKind.String:
                        tool = property.Value.GetString();
                        break;
                    case "filterHash" when property.Value.ValueKind == JsonValueKind.String:
                        filterHash = property.Value.GetString();
                        break;
                    case "conversationId" when property.Value.ValueKind == JsonValueKind.String:
                        conversationId = property.Value.GetString();
                        if (!Ulid.IsValid(conversationId)) return false;
                        break;
                    case "direction" when property.Value.ValueKind == JsonValueKind.String:
                        direction = property.Value.GetString();
                        break;
                    case "last" when property.Value.ValueKind == JsonValueKind.Object:
                        last = ReadPosition(property.Value);
                        if (last is null) return false;
                        break;
                    default:
                        return false;
                }
            }

            if (version != Version || string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(filterHash)
                || last is null)
                return false;

            cursor = new JournalCursor
            {
                Tool = tool,
                FilterHash = filterHash,
                ConversationId = conversationId,
                Direction = direction,
                Last = last.Value,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A short, stable hash of the arguments that select rows. Each value is length-prefixed and a
    /// null is distinct from an empty string, so no two argument sets can collide by concatenation.
    /// </summary>
    public static string HashFilters(params (string Name, string? Value)[] filters)
    {
        var canonical = new StringBuilder();
        foreach (var (name, value) in filters)
        {
            canonical.Append(name);
            if (value is null) canonical.Append("\0-");
            else canonical.Append('=').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            canonical.Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToBase64String(digest, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // ── the search key ───────────────────────────────────────────────────────
    //
    // `sent_at` is DATETIME(6), so microseconds since the Unix epoch round-trip it exactly: a key
    // that lost precision would repeat or skip the rows sharing the last timestamp of a page.

    private static readonly long MinSentAtTicks = new DateTime(1000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long MaxSentAtTicks = new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc).Ticks + 9_999_990;

    /// <summary>The search key of a <c>sent_at</c> value.</summary>
    public static long SentAtKey(DateTimeOffset sentAt) => (sentAt.UtcTicks - DateTime.UnixEpoch.Ticks) / 10;

    /// <summary>True when <paramref name="key"/> names a <c>DATETIME(6)</c> the column could hold.</summary>
    public static bool IsSentAtKey(long key) =>
        key >= (MinSentAtTicks - DateTime.UnixEpoch.Ticks) / 10
        && key <= (MaxSentAtTicks - DateTime.UnixEpoch.Ticks) / 10;

    /// <summary>The <c>sent_at</c> a search key names. Call only after <see cref="IsSentAtKey"/>.</summary>
    public static DateTime SentAtOf(long key) => new(DateTime.UnixEpoch.Ticks + key * 10, DateTimeKind.Utc);

    private static JournalKeyPosition? ReadPosition(JsonElement element)
    {
        long? key = null;
        string? id = null;
        var count = 0;

        foreach (var property in element.EnumerateObject())
        {
            count++;
            switch (property.Name)
            {
                case "key" when property.Value.ValueKind == JsonValueKind.Number
                                && property.Value.TryGetInt64(out var k):
                    key = k;
                    break;
                case "id" when property.Value.ValueKind == JsonValueKind.String:
                    id = property.Value.GetString();
                    break;
                default:
                    return null;
            }
        }

        return count == 2 && key is { } keyValue && Ulid.IsValid(id)
            ? new JournalKeyPosition(keyValue, id!)
            : null;
    }

    /// <summary>Unpadded base64url only; null for anything else.</summary>
    private static byte[]? DecodeBase64Url(string value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return null;
        }

        var standard = value.Replace('-', '+').Replace('_', '/');
        switch (standard.Length % 4)
        {
            case 2: standard += "=="; break;
            case 3: standard += "="; break;
            case 1: return null;
        }

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
