using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public class MediaGroupBufferTests
{
    private static IncomingMessage MakeTemplate(long chatId = 1) => new()
    {
        ChatId = chatId,
        UserId = 100,
        Text = "",
        Sender = "@user",
        IsGroupChat = false,
    };

    private static MessageImage MakeImage(byte seed = 1) =>
        new(new byte[] { seed }, "image/jpeg");

    // ── basic flush ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SinglePhoto_FlushesAfterDebounce_WithOneImage()
    {
        // Arrange — very short maxTotalMs so the test stays fast
        var buffer = new MediaGroupBuffer(maxTotalMs: 5000);
        var received = new List<IncomingMessage>();

        // Act
        await buffer.AddPhotoAsync("g1", MakeImage(), MakeTemplate(), m =>
        {
            received.Add(m);
            return Task.CompletedTask;
        });

        // Wait for the 1500 ms debounce + slack
        await Task.Delay(1700);

        // Assert
        Assert.Single(received);
        Assert.Single(received[0].Images);
    }

    [Fact]
    public async Task ThreePhotosWithinDebounce_FlushesOnce_WithAllThreeImages()
    {
        // Photos arrive 200 ms apart — all within the 1500 ms debounce window.
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("g2", MakeImage(1), MakeTemplate(), flush);
        await Task.Delay(200);
        await buffer.AddPhotoAsync("g2", MakeImage(2), MakeTemplate(), flush);
        await Task.Delay(200);
        await buffer.AddPhotoAsync("g2", MakeImage(3), MakeTemplate(), flush);

        // Wait for debounce to fire
        await Task.Delay(1700);

        // All three photos in a single flush
        Assert.Single(received);
        Assert.Equal(3, received[0].Images.Count);
    }

    [Fact]
    public async Task TwoGroups_FlushIndependently()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("groupA", MakeImage(1), MakeTemplate(chatId: 1), flush);
        await buffer.AddPhotoAsync("groupB", MakeImage(2), MakeTemplate(chatId: 2), flush);

        await Task.Delay(1700);

        // Two independent flushes
        Assert.Equal(2, received.Count);
        Assert.Equal(1, received.Single(m => m.ChatId == 1).Images.Count);
        Assert.Equal(1, received.Single(m => m.ChatId == 2).Images.Count);
    }

    // ── hard cap (maxTotalMs) ────────────────────────────────────────────────

    [Fact]
    public async Task HardCap_ForceFlushesWhenTotalTimeExceeded()
    {
        // maxTotalMs = 100 ms; photos keep arriving every ~150 ms (resetting debounce).
        // After the cap elapses, the next arrival force-flushes even though the debounce
        // would keep resetting. Delays are 50 % above the cap so CI scheduler slop can't
        // leave the elapsed comparison on the exact boundary (previously flaky with
        // cap=200, delays=100, total=200 — zero margin).
        var buffer = new MediaGroupBuffer(maxTotalMs: 100);
        var received = new List<IncomingMessage>();

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("g3", MakeImage(1), MakeTemplate(), flush);
        await Task.Delay(150);
        // Second arrival is ~150 ms after the first — elapsed already exceeds the
        // 100 ms cap, so this call triggers the force-flush synchronously before
        // returning (FlushGroupAsync is awaited inside AddPhotoAsync on the
        // force-flush path, so no trailing sleep is needed).
        await buffer.AddPhotoAsync("g3", MakeImage(2), MakeTemplate(), flush);

        Assert.Single(received);
    }

    // ── GetImageCount ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetImageCount_ReturnsCorrectCount()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        await buffer.AddPhotoAsync("cnt", MakeImage(1), MakeTemplate(), m =>
        {
            received.Add(m);
            return Task.CompletedTask;
        });
        await buffer.AddPhotoAsync("cnt", MakeImage(2), MakeTemplate(), m =>
        {
            received.Add(m);
            return Task.CompletedTask;
        });

        Assert.Equal(2, buffer.GetImageCount("cnt"));

        // Wait for flush
        await Task.Delay(1700);
        // After flush the entry is removed
        Assert.Equal(0, buffer.GetImageCount("cnt"));
    }

    [Fact]
    public void GetImageCount_UnknownGroup_ReturnsZero()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        Assert.Equal(0, buffer.GetImageCount("nonexistent"));
    }

    // ── null images (skipped downloads) ─────────────────────────────────────

    [Fact]
    public async Task NullImage_DoesNotInflateCount_ButResetsDebounce()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("g4", MakeImage(1), MakeTemplate(), flush);
        // Skipped image (download failed)
        await buffer.AddPhotoAsync("g4", null, MakeTemplate(), flush);

        Assert.Equal(1, buffer.GetImageCount("g4"));

        await Task.Delay(1700);

        // Only the 1 non-null image in the result
        Assert.Single(received);
        Assert.Single(received[0].Images);
    }

    // ── template (metadata from first update) ───────────────────────────────

    [Fact]
    public async Task FlushedMessage_UsesFirstUpdateMetadata()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        var firstTemplate = MakeTemplate(chatId: 42);
        var secondTemplate = MakeTemplate(chatId: 99); // different chatId — must be ignored

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("g5", MakeImage(1), firstTemplate, flush);
        await buffer.AddPhotoAsync("g5", MakeImage(2), secondTemplate, flush);

        await Task.Delay(1700);

        Assert.Single(received);
        Assert.Equal(42L, received[0].ChatId); // first template wins
    }

    // ── caption concatenation (AC#MEDIUM) ───────────────────────────────────

    [Fact]
    public async Task Captions_FromMultiplePhotos_AreConcatenatedAndDeduped()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        static IncomingMessage Captioned(string caption) => new()
        {
            ChatId = 1, UserId = 1, Text = caption, StrippedText = caption,
            Sender = "@u", IsGroupChat = false,
        };

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("cap1", MakeImage(1), Captioned("photo one"), flush);
        await buffer.AddPhotoAsync("cap1", MakeImage(2), Captioned("photo two"), flush);
        // duplicate of first — should appear only once
        await buffer.AddPhotoAsync("cap1", MakeImage(3), Captioned("photo one"), flush);

        await Task.Delay(1700);

        Assert.Single(received);
        // "photo one" and "photo two" joined; "photo one" deduped
        Assert.Equal("photo one photo two", received[0].StrippedText);
    }

    [Fact]
    public async Task Captions_EmptyCaption_NotIncluded()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();

        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        await buffer.AddPhotoAsync("cap2", MakeImage(1), MakeTemplate(), flush);
        await buffer.AddPhotoAsync("cap2", MakeImage(2), MakeTemplate(), flush);

        await Task.Delay(1700);

        Assert.Single(received);
        Assert.Equal("", received[0].StrippedText); // no captions → empty string
    }

    // ── AC#6: count cap (TryAddPhotoWithCapAsync returns false) ─────────────

    [Fact]
    public async Task TryAddPhotoWithCap_RejectsWhenAtCap()
    {
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();
        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        const int cap = 2;
        Assert.True(await buffer.TryAddPhotoWithCapAsync("g6", MakeImage(1), MakeTemplate(), cap, flush));
        Assert.True(await buffer.TryAddPhotoWithCapAsync("g6", MakeImage(2), MakeTemplate(), cap, flush));
        // cap reached — third photo rejected
        Assert.False(await buffer.TryAddPhotoWithCapAsync("g6", MakeImage(3), MakeTemplate(), cap, flush));

        await Task.Delay(1700);

        // Only the two accepted photos in the flush
        Assert.Single(received);
        Assert.Equal(2, received[0].Images.Count);
    }

    [Fact]
    public async Task TryAddPhotoWithCap_AcceptsNullImageBelowCap()
    {
        // A null image (skipped download) still counts as an "accepted" slot
        // so the debounce timer resets — but it must not inflate the image count.
        var buffer = new MediaGroupBuffer(maxTotalMs: 10_000);
        var received = new List<IncomingMessage>();
        Func<IncomingMessage, Task> flush = m => { received.Add(m); return Task.CompletedTask; };

        Assert.True(await buffer.TryAddPhotoWithCapAsync("g7", MakeImage(1), MakeTemplate(), 5, flush));
        Assert.True(await buffer.TryAddPhotoWithCapAsync("g7", null,         MakeTemplate(), 5, flush));

        await Task.Delay(1700);

        Assert.Single(received);
        // null photo skipped — only 1 real image
        Assert.Single(received[0].Images);
    }
}
