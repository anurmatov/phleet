using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fleet.Agent.Prepare;

/// <summary>
/// The exact text conventions of the two programs <c>entrypoint.sh</c> used to embed: Node for the
/// Codex <c>config.toml</c> and Python for the Gemini <c>settings.json</c>.
/// </summary>
/// <remarks>
/// These files persist in the agent's home across reprovisions and are read by pinned provider CLIs,
/// so the C# that replaced the embedded programs writes the same bytes they did. That is why this
/// file reproduces <c>JSON.stringify</c>, <c>String(value)</c> and <c>Object.entries</c> order on one
/// side and <c>json.dump(indent=2)</c> on the other instead of using <c>System.Text.Json</c>'s own
/// writer, whose escaping and number formatting differ from both.
/// <c>tests/entrypoint-prepare/run.sh</c> runs the original programs beside this code and compares.
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

    // ── Python ───────────────────────────────────────────────────────────────

    /// <summary>A JSON object as Python's <c>dict</c>: insertion-ordered, a repeated key keeps its first position.</summary>
    public sealed class PyDict
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public int Count => _order.Count;

        public IEnumerable<KeyValuePair<string, object?>> Items => _order.Select(k => new KeyValuePair<string, object?>(k, _values[k]));

        public bool TryGet(string key, out object? value) => _values.TryGetValue(key, out value);

        public void Set(string key, object? value)
        {
            if (!_values.ContainsKey(key)) _order.Add(key);
            _values[key] = value;
        }
    }

    /// <summary>A JSON number kept as its token, so an integer of any size survives unchanged.</summary>
    public sealed record PyNumber(string Token)
    {
        public bool IsFloat => Token.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;

        public bool IsZero => IsFloat
            ? double.Parse(Token, NumberStyles.Float, CultureInfo.InvariantCulture) == 0
            : Token.TrimStart('-').All(c => c == '0');
    }

    /// <summary>
    /// <c>json.loads</c> of a file's bytes: strict UTF-8, no byte-order mark, standard JSON. Returns
    /// <c>dict</c> as <see cref="PyDict"/>, <c>list</c> as <c>List&lt;object?&gt;</c>, and
    /// <c>str</c>/<c>bool</c>/<c>None</c>/number as string, bool, null and <see cref="PyNumber"/>.
    /// Throws on anything Python would refuse. (Python also accepts the non-standard literals
    /// <c>NaN</c> and <c>Infinity</c>; here they are refused like any other malformed file.)
    /// </summary>
    public static object? PyLoad(byte[] bytes)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        if (text.StartsWith('﻿'))
            throw new JsonException("unexpected UTF-8 byte-order mark");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512 });
        return PyValue(document.RootElement);
    }

    private static object? PyValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new PyDict();
                foreach (var property in element.EnumerateObject())
                    dict.Set(property.Name, PyValue(property.Value));
                return dict;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(PyValue).ToList();
            case JsonValueKind.String: return element.GetString()!;
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return new PyNumber(element.GetRawText());
            default: return null;
        }
    }

    /// <summary>Python truthiness of a loaded JSON value.</summary>
    public static bool PyTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        PyNumber n => !n.IsZero,
        PyDict d => d.Count > 0,
        List<object?> l => l.Count > 0,
        _ => true,
    };

    /// <summary><c>json.dumps(value, indent=2)</c>: ASCII only, two-space indent, no trailing newline.</summary>
    public static string PyDump(object? value)
    {
        var sb = new StringBuilder();
        PyDump(sb, value, 0);
        return sb.ToString();
    }

    private static void PyDump(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: PyString(sb, s); break;
            case PyNumber n: sb.Append(PyNumberText(n)); break;
            case PyDict { Count: 0 }: sb.Append("{}"); break;
            case List<object?> { Count: 0 }: sb.Append("[]"); break;
            case PyDict dict:
                sb.Append('{');
                var firstItem = true;
                foreach (var (key, item) in dict.Items)
                {
                    sb.Append(firstItem ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                    firstItem = false;
                    PyString(sb, key);
                    sb.Append(": ");
                    PyDump(sb, item, depth + 1);
                }
                sb.Append('\n').Append(' ', 2 * depth).Append('}');
                break;
            case List<object?> list:
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', 2 * (depth + 1));
                    PyDump(sb, list[i], depth + 1);
                }
                sb.Append('\n').Append(' ', 2 * depth).Append(']');
                break;
            default:
                throw new InvalidOperationException($"not a JSON value: {value.GetType().Name}");
        }
    }

    private static void PyString(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    // ensure_ascii: everything outside space..~ is escaped, one \uXXXX per UTF-16 unit.
                    if (c is < ' ' or > '~')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static string PyNumberText(PyNumber number)
    {
        if (!number.IsFloat)
            return number.IsZero ? "0" : number.Token;
        return PyFloatRepr(double.Parse(number.Token, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    /// <summary>Python's <c>repr(float)</c>, with <c>json</c>'s spellings for the non-finite values.</summary>
    public static string PyFloatRepr(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        var (negative, digits, point) = ShortestDigits(value);
        var k = digits.Length;
        var sb = new StringBuilder(negative ? "-" : "");
        if (point is <= -4 or > 16)
        {
            var exponent = point - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(exponent < 0 ? '-' : '+').Append(Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (point <= 0)
            sb.Append("0.").Append('0', -point).Append(digits);
        else if (point >= k)
            sb.Append(digits).Append('0', point - k).Append(".0");
        else
            sb.Append(digits, 0, point).Append('.').Append(digits, point, k - point);
        return sb.ToString();
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
