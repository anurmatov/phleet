namespace Fleet.Memory.Data;

/// <summary>
/// Thrown by <see cref="MemoryFileStore.FindFileById"/> when more than one memory file matches an ID,
/// so a caller never reads or writes an arbitrary one of them (#469).
/// </summary>
public sealed class AmbiguousMemoryIdException(string id, int matchCount, bool fullId)
    : Exception(fullId
        ? $"Ambiguous memory ID '{id}': {matchCount} memory files carry this ID."
        : $"Ambiguous memory ID '{id}': {matchCount} memories share this short ID. Pass the full UUID.");
