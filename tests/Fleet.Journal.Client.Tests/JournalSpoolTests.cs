using System.Text.Json.Nodes;

namespace Fleet.Journal.Client.Tests;

public sealed class JournalSpoolTests
{
    private static JsonObject Record(long messageId = 1) => Records.Json(Records.Record(messageId));

    [Fact]
    public void A_record_is_written_with_its_bookkeeping_and_nothing_half_written_is_left()
    {
        using var dir = new TempDir();
        var time = new ManualTime(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var spool = new JournalSpool(dir.Path, time);

        Assert.Equal(SpoolWriteOutcome.Written, spool.Write(Record(), "inbound", []));

        var id = Assert.Single(spool.PendingIds());
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(spool.PendingDir, id + ".json")))!;
        Assert.Equal(0, raw["attempts"]!.GetValue<int>());
        Assert.NotNull(raw["nextAttemptAt"]);
        Assert.Null(raw["lastError"]);
        Assert.Equal(1, raw["record"]!["telegram"]!["messageId"]!.GetValue<long>());

        // temp + fsync + rename: no temp file survives a completed write.
        Assert.Empty(Directory.GetFiles(spool.PendingDir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(spool.PendingDir, ".*"));
    }

    [Fact]
    public void Inbound_media_is_hardlinked_and_outbound_media_is_copied()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(Path.Combine(dir.Path, "spool"));
        var inbound = Path.Combine(dir.Path, "in.jpg");
        var outbound = Path.Combine(dir.Path, "out.png");
        File.WriteAllBytes(inbound, [1, 2, 3]);
        File.WriteAllBytes(outbound, [4, 5, 6]);

        spool.Write(Record(1), "inbound", [new SpoolMedia(0, inbound, null, SpoolMediaMode.Hardlink)]);
        var inboundId = spool.PendingIds().Single();
        spool.Write(Record(2), "outbound", [new SpoolMedia(0, outbound, null, SpoolMediaMode.Copy)]);
        var outboundId = spool.PendingIds().Single(id => id != inboundId);

        // Rewriting a source in place shows through a hardlink and not through a copy — which is
        // why a workspace file, which can be overwritten, is copied.
        using (var s = new FileStream(inbound, FileMode.Open, FileAccess.Write)) s.Write([9, 9, 9]);
        using (var s = new FileStream(outbound, FileMode.Open, FileAccess.Write)) s.Write([9, 9, 9]);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.Equal([9, 9, 9], File.ReadAllBytes(spool.MediaPath(inboundId, 0)));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(spool.MediaPath(outboundId, 0)));
    }

    [Fact]
    public void Media_given_as_bytes_is_written_and_a_missing_source_is_skipped()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path);

        spool.Write(Record(), "outbound", [
            new SpoolMedia(0, null, [7, 7], SpoolMediaMode.Copy),
            new SpoolMedia(1, Path.Combine(dir.Path, "gone.jpg"), null, SpoolMediaMode.Hardlink),
        ]);

        var entry = spool.Load(spool.PendingIds().Single())!;
        Assert.Equal([0], entry.MediaOrdinals);
        Assert.Equal([7, 7], File.ReadAllBytes(spool.MediaPath(entry.Id, 0)));
    }

    [Fact]
    public void A_full_spool_drops_the_new_record_and_keeps_the_queued_ones()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path) { MaxRecords = 2 };

        Assert.Equal(SpoolWriteOutcome.Written, spool.Write(Record(1), "inbound", []));
        Assert.Equal(SpoolWriteOutcome.Written, spool.Write(Record(2), "inbound", []));
        Assert.Equal(SpoolWriteOutcome.Full, spool.Write(Record(3), "inbound", []));

        Assert.Equal(2, spool.PendingIds().Count);
        Assert.DoesNotContain(spool.Pending(), e => e.Record["telegram"]!["messageId"]!.GetValue<long>() == 3);
    }

    [Fact]
    public void The_byte_limit_counts_media_too()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path) { MaxBytes = 4096 };

        Assert.Equal(SpoolWriteOutcome.Written, spool.Write(Record(1), "outbound", [new SpoolMedia(0, null, new byte[5000], SpoolMediaMode.Copy)]));
        Assert.Equal(SpoolWriteOutcome.Full, spool.Write(Record(2), "inbound", []));
    }

    [Fact]
    public void The_limits_are_ten_thousand_records_or_one_gibibyte()
    {
        Assert.Equal(10_000, JournalOptions.MaxSpoolRecords);
        Assert.Equal(1L << 30, JournalOptions.MaxSpoolBytes);
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path);
        Assert.Equal(JournalOptions.MaxSpoolRecords, spool.MaxRecords);
        Assert.Equal(JournalOptions.MaxSpoolBytes, spool.MaxBytes);
    }

    [Fact]
    public void A_write_error_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path);
        Directory.Delete(spool.PendingDir);
        File.WriteAllText(spool.PendingDir, "not a directory");

        Assert.Equal(SpoolWriteOutcome.Failed, spool.Write(Record(), "outbound", [new SpoolMedia(0, null, [1], SpoolMediaMode.Copy)]));
        Assert.Empty(Directory.GetFiles(spool.MediaDir));
    }

    [Fact]
    public void A_restart_counts_what_the_previous_run_left()
    {
        using var dir = new TempDir();
        var first = new JournalSpool(dir.Path);
        first.Write(Record(1), "inbound", [new SpoolMedia(0, null, [1, 2, 3], SpoolMediaMode.Copy)]);
        first.Write(Record(2), "inbound", []);

        var second = new JournalSpool(dir.Path);

        Assert.Equal(2, second.PendingCount);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.PendingIds(), second.PendingIds());
    }

    [Fact]
    public void A_dead_record_keeps_its_media_for_a_redrive_and_the_sweep_removes_both()
    {
        using var dir = new TempDir();
        var time = new ManualTime(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        var spool = new JournalSpool(dir.Path, time);
        spool.Write(Record(), "inbound", [new SpoolMedia(0, null, [1], SpoolMediaMode.Copy)]);
        var entry = spool.Load(spool.PendingIds().Single())!;

        spool.MoveToDead(entry, "too_large");

        Assert.Equal(0, spool.PendingCount);
        Assert.Equal(1, spool.DeadCount);
        Assert.True(File.Exists(spool.MediaPath(entry.Id, 0)));

        time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, spool.SweepDead(TimeSpan.FromDays(30)));

        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, spool.SweepDead(TimeSpan.FromDays(30)));
        Assert.Equal(0, spool.DeadCount);
        Assert.False(File.Exists(spool.MediaPath(entry.Id, 0)));
    }

    [Fact]
    public void An_unreadable_pending_file_is_skipped_not_fatal()
    {
        using var dir = new TempDir();
        var spool = new JournalSpool(dir.Path);
        spool.Write(Record(), "inbound", []);
        File.WriteAllText(Path.Combine(spool.PendingDir, "00000000000000000000000000.json"), "{ truncated");

        Assert.Null(spool.Load("00000000000000000000000000"));
        Assert.Single(spool.Pending());
    }
}
