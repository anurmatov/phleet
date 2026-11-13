namespace Fleet.Memory.Tests;

/// <summary>
/// For tests of the <c>memory_stats</c> directory scan that expect it to count something.
///
/// The scan is Linux-only by design (#518): it opens every directory and file relative to an open
/// handle with <c>openat</c> and <c>O_NOFOLLOW</c> and types what it opened with <c>statx</c>, and on any
/// other platform <c>MemoryFileStore</c> refuses the scan outright instead of falling back to paths. So
/// off Linux there is nothing to count and these tests cannot pass; the tests that expect a refusal still
/// run there, where every scan is one.
/// </summary>
internal static class LinuxOnly
{
    public const string Reason =
        "memory_stats scans with Linux-only openat/O_NOFOLLOW/statx and refuses on every other platform by design (#518). "
        + "Run it on Linux: scripts/test-linux.sh tests/Fleet.Memory.Tests";
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = LinuxOnly.Reason; }
}

public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = LinuxOnly.Reason; }
}
