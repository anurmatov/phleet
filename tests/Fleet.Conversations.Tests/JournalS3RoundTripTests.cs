using System.Security.Cryptography;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The journal object store against a REAL S3-compatible bucket (#388).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JournalUploadTests"/> and <see cref="JournalObjectSweeperTests"/> run against a fake
/// bucket, which is right for them: they assert which keys exist and in what order. What no fake can
/// assert is that the three configuration choices in <see cref="S3ObjectStore"/> — path-style
/// addressing, a known-length stream, and <c>WHEN_REQUIRED</c> checksums — actually produce a byte
/// for byte object on a server that is not ours. Every one of those three has a failure mode that
/// looks like success in a fake: a fake bucket accepts the request whatever the addressing style,
/// reads as many bytes as it is handed, and never rejects a checksum trailer.
/// </para>
/// <para>
/// ⚠️ See <see cref="S3Fixture"/> for what the SeaweedFS substitution does NOT establish. These
/// tests do not cover the start-up credential classes.
/// </para>
/// </remarks>
[Collection("s3")]
public sealed class JournalS3RoundTripTests(S3Fixture fixture)
{
    [Fact]
    public async Task A_put_streams_the_declared_length_and_reports_the_digest_the_bytes_make()
    {
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var key = JournalObjectKeys.For(TestId());

        using var store = fixture.Store();
        await using var body = new MemoryStream(bytes);

        var result = await store.PutAsync(key, body, bytes.LongLength, "image/jpeg");

        Assert.True(result.Succeeded, $"the store reported {nameof(result.StoreFailure)}");
        Assert.False(result.Overflow);
        Assert.Equal(bytes.LongLength, result.ByteSize);

        // The digest the store computed, not the one the caller claimed. This is the assertion the
        // dedup rule rests on: a committed object's digest is what the bucket holds.
        Assert.Equal(expected, result.Sha256);
    }

    [Fact]
    public async Task An_object_written_through_the_store_reads_back_byte_identical()
    {
        var bytes = new byte[7423];
        Random.Shared.NextBytes(bytes);
        var key = JournalObjectKeys.For(TestId());

        using var store = fixture.Store();
        await using (var body = new MemoryStream(bytes))
            Assert.True((await store.PutAsync(key, body, bytes.LongLength, "application/octet-stream")).Succeeded);

        Assert.True(await store.ExistsAsync(key));

        var read = await store.GetAsync(key);
        Assert.NotNull(read);
        Assert.Equal(bytes.LongLength, read!.ByteSize);

        using var ms = new MemoryStream();
        await using (read.Content)
            await read.Content.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task A_body_longer_than_the_declared_size_overflows_rather_than_truncating()
    {
        var key = JournalObjectKeys.For(TestId());
        using var store = fixture.Store();

        // Declared 10 bytes, offered 5_000. A store that read the whole stream would write an
        // object nothing's row describes; one that truncated would write an object whose digest
        // matches nothing.
        await using var body = new MemoryStream(new byte[5_000]);
        var result = await store.PutAsync(key, body, 10, "image/png");

        Assert.False(result.Succeeded);
        Assert.True(result.Overflow);

        // The caller's contract is that it deletes an overflowed object. Leaving it is acceptable
        // for the store, so assert the ANSWER rather than the bucket, then clean up.
        await store.DeleteAsync(key);
    }

    [Fact]
    public async Task A_missing_object_is_absent_rather_than_an_error()
    {
        using var store = fixture.Store();
        var key = JournalObjectKeys.For(TestId());

        Assert.False(await store.ExistsAsync(key));
        Assert.Null(await store.GetAsync(key));

        // Deleting a missing object is success: the sweeper must converge on repeat runs.
        await store.DeleteAsync(key);
    }

    [Fact]
    public async Task The_listing_sees_journal_keys_and_is_not_truncated_at_the_first_page()
    {
        using var store = fixture.Store();

        // Enough objects to cross a page boundary on a store that defaults to 1000 would need 1000
        // keys, which is not worth a CI minute. What IS worth one: the listing follows the
        // continuation token rather than returning page 1, which is asserted by writing more than
        // any smaller internal page size and counting what comes back.
        var written = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var key = JournalObjectKeys.For(TestId());
            await using var body = new MemoryStream(new byte[32]);
            Assert.True((await store.PutAsync(key, body, 32, "image/jpeg")).Succeeded);
            written.Add(key);
        }

        var listed = await store.ListAsync(JournalObjectKeys.Prefix);
        var mine = listed.Select(k => k.Key).Intersect(written).ToArray();

        Assert.Equal(written.Count, mine.Length);

        // Every listed key carries a modification time, because the orphan sweep ages on it. A
        // server that omitted it would make every object look brand new and never be swept.
        Assert.All(listed.Where(k => written.Contains(k.Key)),
            k => Assert.True(k.LastModified > DateTimeOffset.UtcNow.AddDays(-1),
                $"{nameof(JournalObjectListing.LastModified)} is not populated by this store"));

        foreach (var key in written) await store.DeleteAsync(key);
    }

    [Fact]
    public async Task The_probe_answers_for_the_configured_bucket_and_calls_a_missing_one_a_failure()
    {
        using var store = fixture.Store();
        Assert.True(await store.ProbeAsync());

        // A bucket that does not exist is a configuration the operator can fix, and Comms exits 1
        // on it rather than starting degraded. That distinction is `ProbeFailure`, not false —
        // the difference between "retry every 30 s" and "the deployment is wrong".
        using var missing = fixture.Store("journal-no-such-" + Guid.NewGuid().ToString("N")[..8]);
        await Assert.ThrowsAsync<JournalProbeFailureException>(() => missing.ProbeAsync());
    }

    [Fact]
    public async Task A_store_pointed_at_an_unreachable_endpoint_is_unavailable_not_broken()
    {
        // Port 1 is never listening. The store must report a store failure — the state that leaves
        // the row `uploading` and answers the agent 503 — rather than throwing an unclassified
        // exception out of the route.
        var store = new S3ObjectStore(new JournalMediaOptions
        {
            Endpoint = "http://127.0.0.1:1",
            Bucket = "whatever",
            AccessKey = "any",
            SecretKey = "any",
            Region = "us-east-1",
            RequestTimeout = TimeSpan.FromSeconds(5),
        }, NullLogger.Instance);

        using (store)
        {
            Assert.False(await store.ProbeAsync());

            await using var body = new MemoryStream(new byte[16]);
            var result = await store.PutAsync("j1/anything", body, 16, "image/jpeg");
            Assert.True(result.StoreFailure);

            await Assert.ThrowsAsync<JournalObjectStoreUnavailableException>(() => store.ExistsAsync("j1/anything"));
        }
    }

    private static string TestId() => Ulid.NewUlid().ToString();
}
