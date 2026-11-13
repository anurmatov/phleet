namespace Fleet.Agent.Tests;

/// <summary>
/// The temp directory with every symbolic link in it resolved.
///
/// <c>JournalFileStore</c> refuses a path that has a link anywhere in it, parents included, and it is
/// right to. On macOS <see cref="Path.GetTempPath"/> answers <c>/var/folders/…</c>, and <c>/var</c> is a
/// link to <c>/private/var</c>, so every journal-file test was refused before it started and none of
/// them could run on a developer's Mac. On Linux <c>/tmp</c> is a real directory and this returns it
/// unchanged, so what the tests exercise there is the same as before.
/// </summary>
internal static class TestTemp
{
    public static string Root { get; } = Resolve(Path.GetTempPath());

    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            // The target of a link may itself sit under a link, so it is resolved the same way.
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = Resolve(target.FullName);
        }
        return current;
    }
}
