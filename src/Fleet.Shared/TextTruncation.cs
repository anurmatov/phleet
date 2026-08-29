namespace Fleet.Shared;

/// <summary>
/// Surrogate-safe truncation shared by every caller that has to cut a string at a fixed
/// UTF-16 budget.
///
/// All caps in this codebase are measured as <c>string.Length</c> — UTF-16 code units, not
/// runes — because that is what the transport limits are expressed in. Cutting blindly at such
/// an index can land between the two halves of a surrogate pair and produce a lone surrogate,
/// which is not valid text and renders as a replacement character or is rejected outright.
///
/// This lives here rather than being reimplemented per call site so there is exactly one
/// definition of "where may I cut". A duplicated guard is a guard that will eventually drift.
/// </summary>
public static class TextTruncation
{
    /// <summary>
    /// Returns an index at or before <paramref name="requestedCut"/> that is safe to cut at.
    ///
    /// Clamps to the length of <paramref name="text"/>, and moves back exactly one code unit
    /// when the requested position sits on a low surrogate — i.e. inside a surrogate pair.
    /// One step is always enough: a low surrogate is by definition preceded by its high
    /// surrogate, so the position before it is a valid boundary.
    /// </summary>
    /// <param name="text">The text about to be cut.</param>
    /// <param name="requestedCut">The desired cut index, in UTF-16 code units.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="requestedCut"/> is negative. A negative budget is a caller bug, not
    /// something to silently clamp to zero — clamping would hide the miscalculation.
    /// </exception>
    public static int SafeCutIndex(ReadOnlySpan<char> text, int requestedCut)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedCut);

        var cut = Math.Min(requestedCut, text.Length);

        // Only meaningful strictly inside the span: at 0 there is nothing to back off from, and
        // at text.Length there is no character at that index to inspect.
        if (cut > 0 && cut < text.Length && char.IsLowSurrogate(text[cut]))
            cut--;

        return cut;
    }
}
