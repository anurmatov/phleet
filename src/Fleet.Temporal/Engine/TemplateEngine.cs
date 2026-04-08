namespace Fleet.Temporal.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Resolves {{template}} expressions against variable scopes.
///
/// Scopes:
///   input   — workflow start arguments (JsonElement)
///   vars    — step outputs (Dictionary&lt;string, object?&gt;)
///   config  — FleetWorkflowOptions serialized as JsonElement
///   tpl     — instruction template variables (set per-delegate by the engine)
///
/// Filters (pipe-separated):
///   | default: value          — return fallback if left side is null/empty string
///   | extract: 'PATTERN'      — regex match; returns first capture group if present, else full match
///   | json                    — parse string as JSON into JsonElement
///
/// Full-expression optimization:
///   If the template is exactly {{expr}} (no surrounding text), the raw resolved object is returned
///   instead of its string representation. Enables passing arrays/objects between steps.
/// </summary>
public sealed class TemplateEngine
{
    private static readonly Regex TemplatePattern = new(@"\{\{(.+?)\}\}", RegexOptions.Compiled);

    private readonly Dictionary<string, object?> _variables;

    /// <summary>
    /// Per-iteration variable overlay for ForEach parallel branches. AsyncLocal ensures each
    /// concurrent async branch sees only the item bound for its own iteration, avoiding the
    /// shared-dict race when tasks interleave at await boundaries. (#686)
    /// </summary>
    internal readonly AsyncLocal<Dictionary<string, object?>?> IterationOverlay = new();

    public TemplateEngine(Dictionary<string, object?> variables) => _variables = variables;

    /// <summary>
    /// Resolve all {{...}} expressions in the template string.
    /// If the entire string is a single {{...}} expression, returns the raw object (not stringified).
    /// Returns the original string as-is if no template expressions are found.
    /// Returns null if template is null.
    /// </summary>
    public object? Resolve(string? template)
    {
        if (template is null) return null;

        var matches = TemplatePattern.Matches(template);
        if (matches.Count == 0) return template;

        // Full-expression optimization: single {{expr}} with nothing else
        if (matches.Count == 1 && matches[0].Value == template)
        {
            var expr = matches[0].Groups[1].Value.Trim();
            return ResolveExpression(expr);
        }

        // Multiple expressions or mixed with literal text: stringify all
        return TemplatePattern.Replace(template, m =>
        {
            var expr = m.Groups[1].Value.Trim();
            var result = ResolveExpression(expr);
            return result?.ToString() ?? "";
        });
    }

    /// <summary>Resolve to string (calls ToString() on non-string results, returns "" for null).</summary>
    public string ResolveString(string? template)
        => Resolve(template)?.ToString() ?? "";

    public void SetVariable(string key, object? value) => _variables[key] = value;

    // -------------------------------------------------------------------------

    private object? ResolveExpression(string expr)
    {
        // Split off filters: expr | filter1 | filter2 (quote-aware — don't split on | inside quoted args)
        var parts = SplitRespectingQuotes(expr, '|');
        var basePart = parts[0].Trim();

        var value = ResolvePath(basePart);

        // Apply filters in order
        for (int i = 1; i < parts.Count; i++)
        {
            var filter = parts[i].Trim();
            value = ApplyFilter(value, filter);
        }

        return value;
    }

    private object? ResolvePath(string path)
    {
        // Handle bracket indexer: e.g. config.AgentPerspectives[cto]
        string? bracketKey = null;
        var bracketIdx = path.IndexOf('[');
        if (bracketIdx >= 0)
        {
            var end = path.IndexOf(']', bracketIdx);
            if (end >= 0)
            {
                bracketKey = path.Substring(bracketIdx + 1, end - bracketIdx - 1).Trim();
                path = path.Substring(0, bracketIdx).Trim();
            }
        }

        // Split dot path
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        // First segment is the scope
        var scope = segments[0];
        if (!_variables.TryGetValue(scope, out var current)) return null;

        // Navigate remaining segments
        for (int i = 1; i < segments.Length; i++)
        {
            // For vars.key: check per-iteration overlay first so ForEach parallel branches
            // don't share state through the global vars dict. (#686)
            if (i == 1 && scope == "vars"
                && IterationOverlay.Value is { } overlay
                && overlay.TryGetValue(segments[i], out var iterVal))
            {
                current = iterVal;
                continue;
            }
            current = Navigate(current, segments[i]);
        }

        // Apply bracket indexer if present
        if (bracketKey != null)
        {
            // Bracket key may itself be a template expression
            var resolvedKey = bracketKey.StartsWith("{{") && bracketKey.EndsWith("}}")
                ? ResolveString(bracketKey)
                : (ResolveString($"{{{{{bracketKey}}}}}") is { } s && s != bracketKey ? s : bracketKey);

            current = Navigate(current, resolvedKey);
        }

        return current;
    }

    private static object? Navigate(object? obj, string key)
    {
        return obj switch
        {
            JsonElement el when el.ValueKind == JsonValueKind.Object
                => el.TryGetProperty(key, out var prop)
                    ? (object)prop
                    : el.EnumerateObject()
                        .Where(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                        .Select(p => (object?)p.Value)
                        .FirstOrDefault(),

            Dictionary<string, object?> dict
                => dict.TryGetValue(key, out var val) ? val : null,

            // Support case-insensitive dictionary lookup
            IDictionary<string, object?> dict
                => dict.TryGetValue(key, out var val)
                    ? val
                    : dict.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) is { } match
                        ? dict[match]
                        : null,

            _ => null
        };
    }

    private object? ApplyFilter(object? value, string filter)
    {
        if (filter.StartsWith("default:", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = filter.Substring("default:".Length).Trim().Trim('\'', '"');
            if (value is null) return fallback;
            if (value is string s && string.IsNullOrEmpty(s)) return fallback;
            if (value is JsonElement el && (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined))
                return fallback;
            return value;
        }

        if (filter.StartsWith("extract:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = filter.Substring("extract:".Length).Trim().Trim('\'', '"');
            var input = value?.ToString() ?? "";
            var match = Regex.Match(input, pattern);
            if (!match.Success) return "";
            // Return first capture group if present, otherwise full match
            return match.Groups.Count > 1 && match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Value;
        }

        if (filter.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = value?.ToString() ?? "null";
            // Use Deserialize<JsonElement> instead of JsonDocument.Parse to avoid
            // leaking the pooled JsonDocument (which implements IDisposable). (#688)
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        return value;
    }

    /// <summary>
    /// Splits <paramref name="input"/> on <paramref name="delimiter"/> but ignores
    /// occurrences inside single- or double-quoted strings, so filter arguments like
    /// <c>extract: 'foo(bar|baz)'</c> are not split on the <c>|</c> inside the regex.
    /// </summary>
    private static List<string> SplitRespectingQuotes(string input, char delimiter)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char? inQuote = null;

        foreach (var ch in input)
        {
            if (inQuote == null && (ch == '\'' || ch == '"'))
                inQuote = ch;
            else if (ch == inQuote)
                inQuote = null;

            if (ch == delimiter && inQuote == null)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }
}
