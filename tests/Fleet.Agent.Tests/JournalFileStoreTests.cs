using System.Security.Cryptography;
using System.Text;
using Fleet.Agent.Services.JournalFiles;
namespace Fleet.Agent.Tests;

public sealed class JournalFileStoreTests : IDisposable
{
    private const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-file-test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("synthetic sentinel");
    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(Bytes));
    [Theory]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("image/png", ".png")]
    [InlineData("unrecognized/type", ".bin")]
    public async Task SafeNamesPermissionsAndCompleteVerifiedBytes(string mime, string ext)
    {
        var store = new JournalFileStore(_root);
        var path = await store.WriteAsync(Id, 0, mime, Bytes.Length, Digest, new MemoryStream(Bytes), default);
        Assert.Equal(Path.Combine(_root, "journal", Id + "-0" + ext), path);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
        if (!OperatingSystem.IsWindows())
        { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
          Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path)); }
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(100, false)]
    [InlineData(18, true)]
    public async Task IntegrityFailureDeletesTempAndPreservesPriorFile(int length, bool badDigest)
    {
        var store = new JournalFileStore(_root);
        var path = await store.WriteAsync(Id, 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), default);
        await Assert.ThrowsAsync<JournalFileIntegrityException>(() => store.WriteAsync(Id, 0, "text/plain",
            length, badDigest ? new string('0', 64) : Digest, new MemoryStream(Bytes), default));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
    [Fact]
    public async Task CancellationRemovesPartialFile()
    {
        var store = new JournalFileStore(_root);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(Id, 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), cts.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "journal")));
    }
    [Fact]
    public async Task CallerCannotChoosePathAndSymlinkCannotRedirectWrites()
    {
        var store = new JournalFileStore(_root);
        await Assert.ThrowsAsync<JournalFileIntegrityException>(() => store.WriteAsync("../outside", 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), default));
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root); var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "journal"), outside);
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Id, 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), default));
        Assert.Empty(Directory.GetFiles(outside));
    }
    [Fact]
    public async Task SweepDeletesExpiredJournalFilesButLeavesTopLevelAttachments()
    {
        var store = new JournalFileStore(_root);
        var path = await store.WriteAsync(Id, 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), default);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-25));
        var ordinary = Path.Combine(_root, "ordinary.pdf"); await File.WriteAllTextAsync(ordinary, "synthetic");
        store.Sweep(); Assert.False(File.Exists(path)); Assert.True(File.Exists(ordinary));
    }
    [Fact]
    public async Task QuotaEvictsOldestBeforePublishingDownload()
    {
        var store = new JournalFileStore(_root); store.Sweep();
        var dir = Path.Combine(_root, "journal");
        var oldest = Path.Combine(dir, "oldest.bin"); var newer = Path.Combine(dir, "newer.bin");
        using (var file = File.Create(oldest)) file.SetLength(110L * 1024 * 1024);
        using (var file = File.Create(newer)) file.SetLength(90L * 1024 * 1024);
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-1));
        var path = await store.WriteAsync(Id, 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), default);
        Assert.False(File.Exists(oldest)); Assert.True(File.Exists(newer)); Assert.True(File.Exists(path));
        Assert.True(Directory.GetFiles(dir).Sum(file => new FileInfo(file).Length) <= JournalFileStore.QuotaBytes);
    }
    [Fact]
    public async Task FinalSymlinkIsRefusedWithoutModifyingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new JournalFileStore(_root); store.Sweep();
        var target = Path.Combine(_root, "outside.txt"); await File.WriteAllTextAsync(target, "synthetic original");
        File.CreateSymbolicLink(Path.Combine(_root, "journal", Id + "-0.txt"), target);
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(Id, 0, "text/plain", Bytes.Length, Digest, new MemoryStream(Bytes), default));
        Assert.Equal("synthetic original", await File.ReadAllTextAsync(target));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
