using Fleet.Conversations.Journal;

namespace Fleet.Conversations.Tests;

public sealed class JournalRuntimeStatsWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UploadWindow_IdlePastOneHour_ReportsNoSamples()
    {
        var time = new ManualTime(Start);
        var stats = new JournalRuntimeStats(time);
        stats.RecordUploadDurationSample(100);
        time.Advance(TimeSpan.FromSeconds(3601));

        var snapshot = stats.Read();

        Assert.Equal(0, snapshot.UploadSamples);
        Assert.Null(snapshot.UploadP50Milliseconds);
        Assert.Null(snapshot.UploadP95Milliseconds);
    }

    [Fact]
    public void UploadWindow_ExactlyOneHour_StillCounts()
    {
        var time = new ManualTime(Start);
        var stats = new JournalRuntimeStats(time);
        stats.RecordUploadDurationSample(100);
        time.Advance(TimeSpan.FromSeconds(3600));

        var snapshot = stats.Read();

        Assert.Equal(1, snapshot.UploadSamples);
        Assert.Equal(100d, snapshot.UploadP50Milliseconds);
        Assert.Equal(100d, snapshot.UploadP95Milliseconds);
    }

    [Fact]
    public void UploadWindow_PartialExpiry_KeepsOnlyRecent()
    {
        var time = new ManualTime(Start);
        var stats = new JournalRuntimeStats(time);
        stats.RecordUploadDurationSample(100);
        time.Advance(TimeSpan.FromSeconds(1800));
        stats.RecordUploadDurationSample(300);
        time.Advance(TimeSpan.FromSeconds(1801));

        var snapshot = stats.Read();

        Assert.Equal(1, snapshot.UploadSamples);
        Assert.Equal(300d, snapshot.UploadP50Milliseconds);
        Assert.Equal(300d, snapshot.UploadP95Milliseconds);
    }

    [Fact]
    public void IngestWindow_IdlePastOneHour_Unchanged()
    {
        var time = new ManualTime(Start);
        var stats = new JournalRuntimeStats(time);
        stats.RecordIngestDuration(100);
        time.Advance(TimeSpan.FromSeconds(3601));

        var snapshot = stats.Read();

        Assert.Equal(0, snapshot.LatencySamples);
        Assert.Null(snapshot.IngestP50Milliseconds);
        Assert.Null(snapshot.IngestP95Milliseconds);
    }

    [Fact]
    public void IngestWindow_ExactlyOneHour_Unchanged()
    {
        var time = new ManualTime(Start);
        var stats = new JournalRuntimeStats(time);
        stats.RecordIngestDuration(100);
        time.Advance(TimeSpan.FromSeconds(3600));

        var snapshot = stats.Read();

        Assert.Equal(1, snapshot.LatencySamples);
        Assert.Equal(100d, snapshot.IngestP50Milliseconds);
        Assert.Equal(100d, snapshot.IngestP95Milliseconds);
    }

    [Fact]
    public void Windows_PruneAgainstOneInstant()
    {
        var time = new ManualTime(Start);
        var stats = new JournalRuntimeStats(time);
        stats.RecordIngestDuration(100);
        stats.RecordUploadDurationSample(100);
        time.Advance(TimeSpan.FromSeconds(3601));

        var snapshot = stats.Read();

        Assert.Equal(0, snapshot.LatencySamples);
        Assert.Equal(0, snapshot.UploadSamples);
        Assert.Null(snapshot.IngestP50Milliseconds);
        Assert.Null(snapshot.IngestP95Milliseconds);
        Assert.Null(snapshot.UploadP50Milliseconds);
        Assert.Null(snapshot.UploadP95Milliseconds);
    }
}
