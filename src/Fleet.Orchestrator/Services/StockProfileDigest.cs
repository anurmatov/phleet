using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// #503 W2: the one content digest of a stock provider profile, computed over its full <c>profile export NAME -o json</c>
/// document. The pinned CLI renders maps (such as <c>annotations</c>) from an unordered map, so identical exports differ
/// byte for byte and a raw-stdout hash is not a pin. Canonical form, every field kept:
/// <list type="bullet">
/// <item>object keys sorted by Unicode code point at every level; duplicates refuse;</item>
/// <item>arrays in their exported order (order is meaningful);</item>
/// <item>no insignificant whitespace; strings escaped as Python <c>json.dumps(ensure_ascii=False)</c>;</item>
/// <item>integers only, within signed 64-bit; any other number, invalid UTF-16 or depth beyond 64 refuses.</item>
/// </list>
/// The digest is the lowercase SHA-256 of the canonical UTF-8 bytes. <c>scripts/stock-profile-digest.sh</c> is the
/// operator producer of the same value; the two are kept equal by a shared fixture test.
/// </summary>
internal static class StockProfileDigest
{
    internal const int MaxDepth = 64;

    /// <summary>The canonical digest, or null when the export is not a canonicalizable JSON object.</summary>
    internal static string? Compute(string export)
    {
        try
        {
            using var document = JsonDocument.Parse(export, new JsonDocumentOptions { MaxDepth = MaxDepth + 1 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var canonical = new StringBuilder();
            if (!Write(document.RootElement, canonical, 1)) return null;
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(canonical.ToString());
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or EncoderFallbackException or ArgumentException) { return null; }
    }

    private static bool Write(JsonElement element, StringBuilder output, int depth)
    {
        if (depth > MaxDepth && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return false; // containers only
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count) return false;
                output.Append('{');
                var first = true;
                foreach (var property in properties.OrderBy(p => p.Name, CodePointComparer.Instance))
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(property.Name, output);
                    output.Append(':');
                    if (!Write(property.Value, output, depth + 1)) return false;
                }
                output.Append('}');
                return true;
            case JsonValueKind.Array:
                output.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0) output.Append(',');
                    if (!Write(item, output, depth + 1)) return false;
                }
                output.Append(']');
                return true;
            case JsonValueKind.String:
                WriteString(element.GetString()!, output);
                return true;
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var number)) return false;
                output.Append(number.ToString(CultureInfo.InvariantCulture));
                return true;
            case JsonValueKind.True: output.Append("true"); return true;
            case JsonValueKind.False: output.Append("false"); return true;
            case JsonValueKind.Null: output.Append("null"); return true;
            default: return false;
        }
    }

    /// <summary>Python <c>json.dumps(ensure_ascii=False)</c> string escaping: quote, backslash and C0 controls only.</summary>
    private static void WriteString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }

    /// <summary>
    /// Orders strings by Unicode code point (as Python does), not UTF-16 code unit. A lone surrogate never reaches a
    /// digest: the strict UTF-8 encoding of the canonical text refuses it.
    /// </summary>
    private sealed class CodePointComparer : IComparer<string>
    {
        internal static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var left = x!.EnumerateRunes();
            var right = y!.EnumerateRunes();
            while (true)
            {
                var hasLeft = left.MoveNext();
                var hasRight = right.MoveNext();
                if (!hasLeft || !hasRight) return hasLeft.CompareTo(hasRight);
                var order = left.Current.Value.CompareTo(right.Current.Value);
                if (order != 0) return order;
            }
        }
    }
}
