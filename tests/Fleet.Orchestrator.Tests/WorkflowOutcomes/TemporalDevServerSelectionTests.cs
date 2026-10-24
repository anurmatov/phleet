using Fleet.Tests.Shared;
using Temporalio.Testing;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// #476 AC25 preparation: the real-server tests either download the SDK default (both variables
/// unset) or start exactly the selected cached CLI and require exactly the selected server
/// version. Nothing in between: every partial or invalid selection fails before a server starts,
/// and a selected binary is never replaced by a download.
/// </summary>
public sealed class TemporalDevServerSelectionTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("wo-cli-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Executable(string name, string body = "#!/bin/sh\nexit 1\n", bool executable = true)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void BothUnset_IsTheOrdinaryDownload(string? path, string? version) =>
        Assert.Null(TemporalDevServerSelection.Resolve(path, version));

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void ExactCachedBinaryAndVersion_AreSelected()
    {
        var cli = Executable("temporal");

        Assert.Equal(new TemporalDevServerSelection.Selection(cli, "1.29.1"), TemporalDevServerSelection.Resolve(cli, "1.29.1"));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public void PartialOrInvalidSelection_FailsClosed()
    {
        var cli = Executable("temporal");
        var notExecutable = Executable("temporal-noexec", executable: false);
        var cases = new (string? Path, string? Version, string Fault)[]
        {
            (cli, null, "must be set together"),
            (null, "1.29.1", "must be set together"),
            ("temporal", "1.29.1", "absolute path"),
            (Path.Combine(_dir.FullName, "missing"), "1.29.1", "existing file"),
            (_dir.FullName, "1.29.1", "existing file"),
            (cli, "v1.29.1", "exact x.y.z"),
            (cli, "1.29", "exact x.y.z"),
            (cli, "1.29.1\n", "exact x.y.z"),
            (cli, "latest", "exact x.y.z"),
        };
        if (!OperatingSystem.IsWindows()) cases = [.. cases, (notExecutable, "1.29.1", "not executable")];

        foreach (var (path, version, fault) in cases)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => TemporalDevServerSelection.Resolve(path, version));
            Assert.Contains(fault, ex.Message);
        }
    }

    [Theory]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    [InlineData("1.32.0")]
    [InlineData("1.29.10")]
    [InlineData(null)]
    public void ReportedServerVersion_MustBeExactlyTheSelectedOne(string? reported)
    {
        TemporalDevServerSelection.EnsureServerVersion("1.29.1", "1.29.1");
        var ex = Assert.Throws<InvalidOperationException>(() => TemporalDevServerSelection.EnsureServerVersion(reported, "1.29.1"));
        Assert.Contains("not 1.29.1", ex.Message);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public async Task InvalidSelection_ThrowsBeforeAnyServerOrDownload()
    {
        var downloads = _dir.CreateSubdirectory("downloads");

        await Assert.ThrowsAsync<InvalidOperationException>(() => TemporalDevServerSelection.StartLocalAsync(
            new WorkflowEnvironmentStartLocalOptions { DownloadDirectory = downloads.FullName }, Path.Combine(_dir.FullName, "missing"), "1.29.1"));

        Assert.Empty(downloads.GetFileSystemInfos());
    }

    /// <summary>A selected binary that cannot serve fails the start; the SDK does not fall back to a download.</summary>
    [Fact]
    [Trait("WorkflowOutcomeSuite", "terminal-matrix")]
    public async Task SelectedBinaryThatCannotStart_FailsWithoutDownloading()
    {
        var downloads = _dir.CreateSubdirectory("downloads");
        var broken = Executable("temporal-broken");

        await Assert.ThrowsAnyAsync<Exception>(() => TemporalDevServerSelection.StartLocalAsync(
            new WorkflowEnvironmentStartLocalOptions { DownloadDirectory = downloads.FullName, DevServerOptions = new DevServerOptions { LogLevel = "error" } },
            broken, "1.29.1"));

        Assert.Empty(downloads.GetFileSystemInfos());
    }
}
