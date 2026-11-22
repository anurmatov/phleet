using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fleet.Agent.Prepare;

/// <summary>
/// The exact text conventions of the Node program <c>entrypoint.sh</c> used to embed for the Codex
/// <c>config.toml</c>.
/// </summary>
/// <remarks>
/// That file persists in the agent's home across reprovisions and is read by the pinned Codex CLI,
/// so the C# that replaced the embedded program writes the same bytes it did. That is why this file
/// reproduces <c>JSON.stringify</c>, <c>String(value)</c> and <c>Object.entries</c> order instead of
/// using <c>System.Text.Json</c>'s own writer, whose escaping and number formatting differ.
/// <c>tests/entrypoint-prepare/run.sh</c> runs the original program beside this code and compares.
/// The Python half that wrote the Gemini CLI's <c>settings.json</c> went with that CLI.
/// </remarks>
internal static class ScriptCompat
{
    // ── JavaScript ───────────────────────────────────────────────────────────

    /// <summary>JavaScript truthiness of a parsed JSON value.</summary>
    public static bool JsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => value.GetDouble() is var d && d != 0 && !double.IsNaN(d),
        _ => true,
    };

    /// <summary>
    /// <c>Object.entries(value)</c>: an object's own properties with integer-like keys first in
    /// ascending order and the rest in insertion order (a repeated key keeps its first position and
    /// its last value, as <c>JSON.parse</c> leaves it); an array's elements by index; a string's
    /// characters by index; nothing for a number or boolean.
    /// </summary>
    public static List<KeyValuePair<string, JsonElement?>> JsEntries(JsonElement value)
    {
        var entries = new List<KeyValuePair<string, JsonElement?>>();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var position = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (position.TryGetValue(property.Name, out var at))
                        entries[at] = new(property.Name, property.Value);
                    else
                    {
                        position[property.Name] = entries.Count;
                        entries.Add(new(property.Name, property.Value));
                    }
                }
                var indexed = entries.Where(e => IsArrayIndex(e.Key)).OrderBy(e => uint.Parse(e.Key, CultureInfo.InvariantCulture));
                return [.. indexed, .. entries.Where(e => !IsArrayIndex(e.Key))];
            case JsonValueKind.Array:
                var index = 0;
                foreach (var element in value.EnumerateArray())
                    entries.Add(new((index++).ToString(CultureInfo.InvariantCulture), element));
                return entries;
            case JsonValueKind.String:
                // Characters carry no properties the callers read; only the count matters to them.
                var text = value.GetString()!;
                for (var i = 0; i < text.Length; i++)
                    entries.Add(new(i.ToString(CultureInfo.InvariantCulture), null));
                return entries;
            default:
                return entries;
        }
    }

    private static bool IsArrayIndex(string key) =>
        key.Length is > 0 and <= 10
        && key.All(char.IsAsciiDigit)
        && (key.Length == 1 || key[0] != '0')
        && ulong.Parse(key, CultureInfo.InvariantCulture) <= 4294967294UL;

    /// <summary><c>String(value)</c> for a parsed JSON value.</summary>
    public static string JsString(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String: return value.GetString()!;
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Null: return "null";
            case JsonValueKind.Number: return JsNumber(value.GetDouble());
            case JsonValueKind.Object: return "[object Object]";
            case JsonValueKind.Array:
                // Array.prototype.join: null renders as the empty string, the rest as String(element).
                return string.Join(",", value.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.Null ? "" : JsString(e)));
            default: return "undefined";
        }
    }

    /// <summary>ECMAScript <c>Number::toString</c>.</summary>
    public static string JsNumber(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";

        var (negative, digits, point) = ShortestDigits(value);
        var k = digits.Length;
        var sb = new StringBuilder(negative ? "-" : "");
        if (k <= point && point <= 21)
            sb.Append(digits).Append('0', point - k);
        else if (0 < point && point <= 21)
            sb.Append(digits, 0, point).Append('.').Append(digits, point, k - point);
        else if (-6 < point && point <= 0)
            sb.Append("0.").Append('0', -point).Append(digits);
        else
        {
            var exponent = point - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(exponent < 0 ? '-' : '+').Append(Math.Abs(exponent).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>JSON.stringify(text)</c>: quotes, backslashes and control characters escaped, a lone
    /// surrogate written as <c>\udXXX</c>, everything else verbatim.
    /// </summary>
    public static string JsonStringify(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        sb.Append(c).Append(text[++i]);
                    else if (char.IsSurrogate(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// The shortest decimal digits that round-trip a finite non-zero double, and where the decimal
    /// point sits: the value is <c>0.DIGITS × 10^point</c>.
    /// </summary>
    private static (bool Negative, string Digits, int Point) ShortestDigits(double value)
    {
        // "R" is the shortest round-trippable form on every supported runtime, in either plain or
        // exponent notation; both are reduced to digits and a point position here.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        if (negative) text = text[1..];

        var exponent = 0;
        var e = text.IndexOf('E');
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.');
        var point = (dot < 0 ? text.Length : dot) + exponent;
        var digits = dot < 0 ? text : text.Remove(dot, 1);

        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits[leading..];
        point -= leading;
        digits = digits.TrimEnd('0');
        return (negative, digits, point);
    }
}
