using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for GroupBehavior.WriteCredentialFileAsync (issue #136).
/// Verifies that the bind-mount-safe copy-then-delete pattern works correctly:
/// File.Copy(overwrite) writes through an existing inode without rename(2),
/// which is what fails with EBUSY on Docker bind-mount targets.
///
/// Note: the Docker bind-mount EBUSY scenario (rename(2) blocked on a bind-mount
/// target while write(2) to the same path succeeds) cannot be reproduced in a unit
/// test without an actual bind mount. Correctness of the copy-then-delete path
/// against a live bind-mounted credential file is validated by canary integration
/// verification after deploy.
/// </summary>
public class GeminiCredentialFileWriteTests : IDisposable
{
    private readonly string _tempDir;

    public GeminiCredentialFileWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fleet-cred-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task WriteCredentialFileAsync_CreatesFileWithCorrectContent()
    {
        var path = Path.Combine(_tempDir, "oauth_creds.json");
        const string content = """{"access_token":"tok","expiry_date":9999}""";

        await GroupBehavior.WriteCredentialFileAsync(path, content);

        Assert.True(File.Exists(path));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteCredentialFileAsync_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "oauth_creds.json");
        await File.WriteAllTextAsync(path, """{"access_token":"old","expiry_date":1}""");

        const string newContent = """{"access_token":"new","expiry_date":9999}""";
        await GroupBehavior.WriteCredentialFileAsync(path, newContent);

        Assert.Equal(newContent, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteCredentialFileAsync_DeletesTempFile()
    {
        var path = Path.Combine(_tempDir, "oauth_creds.json");

        await GroupBehavior.WriteCredentialFileAsync(path, "{}");

        // The .tmp side-car must be cleaned up.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task WriteCredentialFileAsync_SetsMode0600OnUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return; // chmod not applicable on this platform

        var path = Path.Combine(_tempDir, "oauth_creds.json");
        await GroupBehavior.WriteCredentialFileAsync(path, "{}");

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
