using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>
/// Grammar for the folded acceptance visibility record. Routing and authority never read it.
/// Empty or invalid values mean no current record. Counters remain workflow variables.
/// </summary>
public static class AcceptanceAttribute
{
    // Unanchored body, so non-.NET readers can use their whole-string matcher as well.
    public const string Pattern = @"v1;head=[0-9a-f]{40};result=(?:(?:running|passed|failed|not_required);iso=(?:isolated|production);block=none|blocked;iso=(?:isolated|production);block=(?!none;)[a-z][a-z0-9_]{0,39});fix=(?:0|[1-9][0-9]{0,3});batch=[0-5]";

    private static readonly Regex WholeValue = new(@"\A(?:" + Pattern + @")\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValid(string? value) => value is not null && WholeValue.IsMatch(value);
}
