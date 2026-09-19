using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations;

/// <summary>
/// The only type in this system that touches an attachment byte on disk (#308 D3).
/// </summary>
/// <remarks>
/// <para>
/// One type, deliberately. Every path is derived from the attachment <b>id</b> and never from the
/// client-supplied <c>fileName</c>, and concentrating that in one place is what makes the property
/// checkable instead of a rule every new call site has to remember.
/// </para>
/// <para>
/// <b>The database row is authoritative.</b> A file with no row is garbage and is swept; a
/// <c>sealed</c> row whose file is missing serves <c>410 attachment_gone</c>. Neither is ever a 500
/// and neither is ever an empty 200 (MUST NOT 11).
/// </para>
/// </remarks>
public sealed class AttachmentStore(string rootPath, ILogger logger)
{
    /// <summary>The volume root. Created on first use; a failure to create is an operator condition.</summary>
    public string RootPath { get; } = rootPath;

    /// <summary>
    /// The file for an id: two levels of fan-out from the ULID's own prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fan-out because a flat directory of tens of thousands of files is slow to enumerate on every
    /// orphan sweep, and the sweep is the thing that runs forever.
    /// </para>
    /// <para>
    /// ⚠️ The id is validated before it reaches here. A ULID is 26 characters of Crockford base32,
    /// so it cannot contain a separator or a dot — which is exactly why the path is built from it
    /// rather than from <c>fileName</c>.
    /// </para>
    /// </remarks>
    public string PathFor(string attachmentId)
    {
        if (!IsSafeId(attachmentId))
            throw new ArgumentException("not a usable attachment id", nameof(attachmentId));

        return Path.Combine(RootPath, attachmentId[..2], attachmentId[2..4], attachmentId);
    }

    /// <summary>
    /// Stream bytes to disk, hashing and counting as they arrive, and abort the moment the
    /// reservation is exceeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Streamed, never buffered.</b> The excess of an over-sized upload is never held in memory:
    /// the write stops at the limit, the partial file is removed, and the caller marks the row
    /// failed. Buffering first would let a caller spend the process's memory rather than its disk.
    /// </para>
    /// <para>
    /// The digest is computed over what actually arrived, which is the whole point — the reservation
    /// carries a CLAIM about the bytes and this is what checks it.
    /// </para>
    /// </remarks>
    public async Task<WriteResult> WriteAsync(
        string attachmentId, Stream source, long maxBytes, CancellationToken ct)
    {
        var path = PathFor(attachmentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long written = 0;
        var overflowed = false;

        // The first bytes, kept so the container can be sniffed without re-opening the file. Bounded
        // by the sniffer's own need, not by the upload.
        var prefix = new byte[ImageSniffer.PrefixBytes];
        var prefixLength = 0;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using (var destination = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024,
                useAsync: true))
            {
                var buffer = new byte[64 * 1024];

                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read == 0) break;

                    written += read;

                    if (written > maxBytes)
                    {
                        // Aborted mid-stream. Nothing past the bound is written and nothing past it
                        // is buffered.
                        overflowed = true;
                        break;
                    }

                    if (prefixLength < prefix.Length)
                    {
                        var take = Math.Min(prefix.Length - prefixLength, read);
                        Array.Copy(buffer, 0, prefix, prefixLength, take);
                        prefixLength += take;
                    }

                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (overflowed)
            {
                Delete(attachmentId);
                return WriteResult.Overflowed();
            }

            return WriteResult.Written(
                written,
                Convert.ToHexStringLower(hasher.GetHashAndReset()),
                prefix.AsSpan(0, prefixLength).ToArray());
        }
        catch (IOException e)
        {
            // A full or unwritable volume. Distinguished from a verification failure because the
            // caller answers 507 rather than 422, and because text submissions must keep working
            // (MUST NOT 17).
            logger.LogWarning("attachment volume write failed: {Error}", e.GetType().Name);
            Delete(attachmentId);
            return WriteResult.VolumeFailure();
        }
    }

    /// <summary>Open the bytes for reading, or null when the file is gone.</summary>
    /// <remarks>
    /// Null is the <c>410</c> case and nothing else. The row said the attachment exists; the volume
    /// disagrees, and that disagreement is a state the client renders deliberately.
    /// </remarks>
    public Stream? OpenRead(string attachmentId)
    {
        var path = PathFor(attachmentId);

        try
        {
            return File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64 * 1024, useAsync: true)
                : null;
        }
        catch (IOException e)
        {
            logger.LogWarning("attachment volume read failed: {Error}", e.GetType().Name);
            return null;
        }
    }

    /// <summary>Remove the bytes. Idempotent, and never throws for an absent file.</summary>
    public void Delete(string attachmentId)
    {
        try
        {
            var path = PathFor(attachmentId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that could not be deleted is collected by the next orphan sweep. Throwing here
            // would fail a garbage-collection pass over a file nobody can reach.
            logger.LogWarning("attachment volume delete failed: {Error}", e.GetType().Name);
        }
    }

    /// <summary>
    /// Every id present on the volume. Drives the file-without-row sweep.
    /// </summary>
    public IReadOnlyList<string> EnumerateIds()
    {
        if (!Directory.Exists(RootPath)) return [];

        try
        {
            return Directory
                .EnumerateFiles(RootPath, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(name => name is not null && IsSafeId(name))
                .Select(name => name!)
                .ToList();
        }
        catch (IOException e)
        {
            logger.LogWarning("attachment volume enumerate failed: {Error}", e.GetType().Name);
            return [];
        }
    }

    /// <summary>A 26-character Crockford-base32 ULID, and nothing else.</summary>
    /// <remarks>
    /// Checked at every entry point rather than once at the edge. This is the function standing
    /// between a client-supplied string and a filesystem path, so it fails closed on anything it does
    /// not recognise — including the separators and dots a traversal would need.
    /// </remarks>
    public static bool IsSafeId(string? value) =>
        value is { Length: 26 }
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z');

    /// <summary>What a write produced, or why it did not.</summary>
    public sealed record WriteResult
    {
        public required bool Succeeded { get; init; }

        /// <summary>True when the bytes exceeded the reservation. The caller answers 422.</summary>
        public bool Overflow { get; init; }

        /// <summary>True when the volume refused the write. The caller answers 507.</summary>
        public bool VolumeUnavailable { get; init; }

        public long ByteSize { get; init; }
        public string Sha256 { get; init; } = "";

        /// <summary>Leading bytes, for container sniffing. Never the whole file.</summary>
        public byte[] Prefix { get; init; } = [];

        public static WriteResult Written(long size, string sha256, byte[] prefix) =>
            new() { Succeeded = true, ByteSize = size, Sha256 = sha256, Prefix = prefix };

        public static WriteResult Overflowed() => new() { Succeeded = false, Overflow = true };

        public static WriteResult VolumeFailure() =>
            new() { Succeeded = false, VolumeUnavailable = true };
    }
}
