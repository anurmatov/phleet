using Fleet.Comms.Operations;
using Xunit;

namespace Fleet.Comms.Tests;

/// <summary>
/// The media backup commands' file and manifest behaviour (#388 AC6).
/// </summary>
/// <remarks>
/// <para>
/// These run without MySQL or a bucket: they exercise the parts a broken backup is actually made
/// of — the manifest format, the newest-manifest choice, the path a key is allowed to take on disk,
/// and the re-hash that decides whether a file is trustworthy. The bucket round-trip itself needs
/// both services and lives in <c>JournalUploadTests</c>.
/// </para>
/// <para>
/// ⚠️ The path test is not paranoia. <c>media restore --in</c> reads a manifest someone can hand
/// you, and a key containing <c>..</c> would write outside the backup directory with the operator's
/// own credentials.
/// </para>
/// </remarks>
public sealed class MediaBackupCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "media-backup-" + Guid.NewGuid().ToString("N"));

    public MediaBackupCommandTests() => Directory.CreateDirectory(_dir);

    private const string Key = "j1/01J000000000000000000000AB";

    [Fact]
    public void A_journal_key_lands_beside_the_objects_directory()
    {
        var path = JournalMediaCommands.TargetPath(Path.Combine(_dir, "objects"), Key);

        Assert.Equal(Path.Combine(_dir, "objects", "j1", "01J000000000000000000000AB"), path);
    }

    [Theory]
    [InlineData("j1/../escape")]
    [InlineData("j1/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("other/01J000000000000000000000AB")]
    [InlineData("j1/01J000000000000000000000AB/extra")]
    [InlineData("j1/")]
    [InlineData("")]
    public void A_key_that_is_not_this_deployments_shape_is_refused(string key)
    {
        Assert.Throws<OperatorCommandException>(() => JournalMediaCommands.TargetPath(_dir, key));
    }

    [Fact]
    public void A_manifest_line_carries_key_sha_and_size()
    {
        var path = Path.Combine(_dir, "manifest-20260929T000000Z.jsonl");
        File.WriteAllText(path,
            """{"key":"j1/01J000000000000000000000AB","sha256":"aa","size":123}""" + "\n");

        var entries = JournalMediaCommands.ReadManifest(path);

        var entry = Assert.Single(entries);
        Assert.Equal(Key, entry.Key);
        Assert.Equal("aa", entry.Sha256);
        Assert.Equal(123, entry.ByteSize);
    }

    [Fact]
    public void Blank_lines_in_a_manifest_are_ignored()
    {
        var path = Path.Combine(_dir, "manifest-20260929T000000Z.jsonl");
        File.WriteAllText(path, "\n"
            + """{"key":"j1/01J000000000000000000000AB","sha256":"aa","size":1}""" + "\n\n");

        Assert.Single(JournalMediaCommands.ReadManifest(path));
    }

    /// <summary>
    /// A manifest is the index of the backup. Half a line is half an index, and reading past it
    /// would certify a truncated backup as complete.
    /// </summary>
    [Theory]
    [InlineData("{\"key\":\"j1/01J000000000000000000000AB\",\"sha256\":\"aa\"}")]
    [InlineData("{\"key\":\"j1/01J000000000000000000000AB\"}")]
    [InlineData("{\"sha256\":\"aa\",\"size\":1}")]
    [InlineData("{not json}")]
    public void A_manifest_that_is_not_complete_is_refused(string line)
    {
        var path = Path.Combine(_dir, "manifest-20260929T000000Z.jsonl");
        File.WriteAllText(path, line + "\n");

        Assert.Throws<OperatorCommandException>(() => JournalMediaCommands.ReadManifest(path));
    }

    /// <summary>The names sort as UTC times, so the newest run is the lexicographic newest.</summary>
    [Fact]
    public void The_newest_manifest_is_the_newest_run()
    {
        Write("manifest-20260928T235959Z.jsonl");
        Write("manifest-20260929T120000Z.jsonl");
        Write("manifest-20260929T115959Z.jsonl");

        Assert.EndsWith("manifest-20260929T120000Z.jsonl", JournalMediaCommands.NewestManifest(_dir));
    }

    [Fact]
    public void A_run_without_a_manifest_is_refused_not_assumed_empty()
    {
        Assert.Throws<OperatorCommandException>(() => JournalMediaCommands.NewestManifest(_dir));
    }

    [Fact]
    public void A_missing_directory_is_refused_before_anything_is_written()
    {
        var missing = Path.Combine(_dir, "nope");

        var e = Assert.Throws<OperatorCommandException>(
            () => JournalMediaCommands.NewestManifest(missing));

        Assert.Contains("does not exist", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    /// <summary>
    /// The re-hash is the whole reason a backup is trusted, so it must reject a file that is the
    /// right size and the wrong content as readily as one that is short.
    /// </summary>
    [Fact]
    public async Task The_hash_is_over_every_byte_and_reports_the_length()
    {
        await using var body = new MemoryStream([1, 2, 3, 4]);

        var (digest, length) = await JournalMediaCommands.HashAsync(body, default);

        Assert.Equal(4, length);
        Assert.Equal(
            "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", digest);
    }

    [Fact]
    public async Task An_empty_object_hashes_as_the_empty_digest()
    {
        await using var body = new MemoryStream([]);

        var (digest, length) = await JournalMediaCommands.HashAsync(body, default);

        Assert.Equal(0, length);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", digest);
    }

    /// <summary>
    /// The command table must route the four media verbs, or the operator's only backup path is a
    /// stack trace from an unknown command.
    /// </summary>
    [Theory]
    [InlineData("media", "backup")]
    [InlineData("media", "verify")]
    [InlineData("media", "restore")]
    [InlineData("journal", "verify-media")]
    public async Task Each_media_verb_is_routed_and_not_unknown(params string[] args)
    {
        var error = new StringWriter();

        // No media is configured in the test environment, so the honest outcome is a refusal that
        // names the configuration — which also proves the verb reached its handler.
        var exit = await OperatorCommands.RunAsync(args, new StringWriter(), error);

        Assert.Equal(1, exit);
        Assert.DoesNotContain("Unknown command", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_media_verb_without_its_required_argument_is_refused()
    {
        var error = new StringWriter();

        Assert.Equal(1, await OperatorCommands.RunAsync(["media", "backup"], new StringWriter(), error));
        Assert.Contains("--out is required", error.ToString(), StringComparison.Ordinal);
    }

    private void Write(string name) =>
        File.WriteAllText(Path.Combine(_dir, name),
            """{"key":"j1/01J000000000000000000000AB","sha256":"aa","size":1}""" + "\n");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
