using System.Security.Cryptography;
using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client;

/// <summary>Why one attachment's bytes did not reach the object store.</summary>
public enum JournalMediaReason
{
    /// <summary>Uploaded and proven. The attachment carries an <c>uploadId</c>.</summary>
    Uploaded,

    /// <summary>Media is off on this deployment (the listener answered <c>409 media_disabled</c>).</summary>
    MediaDisabled,

    /// <summary>The platform reported the file above its own Bot API cap.</summary>
    OverBotApiLimit,

    /// <summary>The file is above this agent's download cap or the store's object cap.</summary>
    OverSizeCap,

    /// <summary>Nothing to archive for this kind of media.</summary>
    UnsupportedKind,

    /// <summary>The bytes could not be read from the platform or from disk.</summary>
    DownloadFailed,

    /// <summary>The spool's hardlink source is gone — the attachment directory moved on.</summary>
    SourceExpired,
}

/// <summary>One attachment's answer: an upload reference, or a reason there is none.</summary>
public readonly record struct JournalMediaUpload(
    JournalMediaReason Reason, string? UploadId, string? Sha256)
{
    public bool IsUploaded => Reason == JournalMediaReason.Uploaded && UploadId is not null;

    public static JournalMediaUpload Uploaded(string uploadId, string sha256) =>
        new(JournalMediaReason.Uploaded, uploadId, sha256);

    public static JournalMediaUpload Declined(JournalMediaReason reason) => new(reason, null, null);

    /// <summary>The wire field a declined upload writes.</summary>
    public JournalNotArchivedReason NotArchivedReason => Reason switch
    {
        JournalMediaReason.MediaDisabled => JournalNotArchivedReason.MediaDisabled,
        JournalMediaReason.OverBotApiLimit => JournalNotArchivedReason.OverBotApiLimit,
        JournalMediaReason.OverSizeCap => JournalNotArchivedReason.OverSizeCap,
        JournalMediaReason.UnsupportedKind => JournalNotArchivedReason.UnsupportedKind,
        JournalMediaReason.DownloadFailed => JournalNotArchivedReason.DownloadFailed,
        _ => JournalNotArchivedReason.SourceExpired,
    };
}

/// <summary>
/// Why one attachment's bytes are not on disk, decided from what the agent already knows.
/// </summary>
/// <remarks>
/// <para>
/// The Bot API's own file cap and the agent's tighter download caps mean a file can be known-absent
/// before anything is read. Distinguishing the two is the difference between
/// <c>over_bot_api_limit</c> ("this deployment will never hold the bytes") and
/// <c>over_size_cap</c> ("the agent chose not to download them"), and the operator reading the
/// journal needs the first to know the file is gone and the second to know a limit can be raised.
/// </para>
/// <para>
/// ⚠️ <b>No Bot API call is made to find this out.</b> <paramref name="platformFileSize"/> is the
/// <c>file_size</c> that arrived with the message — the platform's refusal is a fact the update
/// already carries.
/// </para>
/// </remarks>
public static class JournalMediaAbsent
{
    /// <summary>The Bot API's file cap, which is also the store's object cap.</summary>
    public const long MaxObjectBytes = JournalMediaUploader.MaxObjectBytes;

    public static JournalMediaReason Reason(long? platformFileSize, long maxLocalBytes)
    {
        if (platformFileSize > MaxObjectBytes) return JournalMediaReason.OverBotApiLimit;
        if (platformFileSize > maxLocalBytes) return JournalMediaReason.OverSizeCap;

        // The platform would have served it and the agent's own cap allowed it, so the only
        // remaining explanation is that the download did not produce a file.
        return JournalMediaReason.DownloadFailed;
    }
}

/// <summary>
/// The agent's media uploader: hash the spooled file, prove the bytes, then attach.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the contract.</b> Hash → declare → PUT → then the message. A record is only ever
/// written with an <c>uploadId</c> whose bytes this call already sent, because the alternative —
/// declaring the message first and the bytes later — is a window where the journal shows a message
/// whose attachment points at nothing.
/// </para>
/// <para>
/// <b>Every refusal is a reason code, never an exception.</b> A media failure must degrade to the
/// same <c>not_archived(...)</c> row that slice 2 wrote for every attachment, so a deployment with
/// no bucket, a bucket that is down, or a 30 MB file the Bot API will not hand over all produce a
/// journaled message. The journal never loses a message because of its media.
/// </para>
/// <para>
/// ⚠️ <b>No new Bot API calls.</b> This class reads the bytes the agent already downloaded for the
/// turn; it never asks Telegram for a file it has not already fetched.
/// </para>
/// </remarks>
public sealed class JournalMediaUploader(
    JournalMediaHttpClient http,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The store's object cap. Equal to the Bot API's own file cap.</summary>
    public const long MaxObjectBytes = 20_971_520;

    /// <summary>
    /// One attachment's bytes, from the spool. <paramref name="platformFileSize"/> is what the
    /// platform declared, which is the only way to know the Bot API refused the file before the
    /// agent ever asked for it.
    /// </summary>
    /// <param name="maxBytes">
    /// The cap this call enforces. <see cref="MaxObjectBytes"/> unless a caller enforces a tighter
    /// one — the agent's own photo and document limits are smaller than the store's, and a file that
    /// passed the platform but failed the agent's cap is <c>over_size_cap</c>, not a store refusal.
    /// </param>
    public async Task<JournalMediaUpload> UploadAsync(
        string? spoolPath, byte[]? bytes, long? platformFileSize, string mimeType,
        CancellationToken ct = default, long? maxBytes = null)
    {
        var cap = maxBytes is > 0 ? Math.Min(maxBytes.Value, MaxObjectBytes) : MaxObjectBytes;

        // 1. The platform's own limit, checked before any local read. A file the Bot API will not
        //    serve has no bytes here to hash, and asking for it is the call this slice must not add.
        if (platformFileSize > MaxObjectBytes)
            return JournalMediaUpload.Declined(JournalMediaReason.OverBotApiLimit);

        // The caller's tighter cap: the platform served it, the agent chose not to keep it.
        if (platformFileSize > cap)
            return JournalMediaUpload.Declined(JournalMediaReason.OverSizeCap);

        // 2. The agent's own download caps (10 MB photo, 32 MB document) mean a local file can be
        //    larger than the store accepts even when the platform served it.
        if (bytes is not null && bytes.LongLength > cap)
            return JournalMediaUpload.Declined(JournalMediaReason.OverSizeCap);

        byte[] payload;
        try
        {
            if (bytes is not null)
            {
                payload = bytes;
            }
            else if (spoolPath is not null && File.Exists(spoolPath))
            {
                var length = new FileInfo(spoolPath).Length;
                if (length > cap)
                    return JournalMediaUpload.Declined(JournalMediaReason.OverSizeCap);

                payload = await File.ReadAllBytesAsync(spoolPath, ct);
            }
            else
            {
                // The hardlink's source is gone: the attachment directory was pruned between the
                // capture and this drain. The record still goes, with the reason that says so.
                return JournalMediaUpload.Declined(JournalMediaReason.SourceExpired);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return JournalMediaUpload.Declined(JournalMediaReason.DownloadFailed);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));

        // 3. Declare.
        var declared = await http.DeclareAsync(digest, payload.LongLength, mimeType, ct);

        if (declared.Status == 409 && declared.Error == "media_disabled")
            return JournalMediaUpload.Declined(JournalMediaReason.MediaDisabled);

        if (declared.Status == 413) return JournalMediaUpload.Declined(JournalMediaReason.OverSizeCap);

        // A transport failure, a 5xx or an unexpected status is NOT a reason to give up on the
        // bytes: the caller keeps the record pending and retries the whole upload next pass. Only
        // the codes above are terminal, and returning a reason for anything else would silently
        // downgrade a retryable outage into a permanently un-archived attachment.
        if (declared.UploadId is null)
            throw new JournalMediaRetryableException(declared.Status, declared.Error);

        // 4. Prove the bytes.
        var stored = await http.PutAsync(declared.UploadId, payload, mimeType, ct);

        if (stored.Status == 409 && stored.Error == "upload_incomplete")
            // Swept or committed between the two calls: re-upload from the spool on the next pass.
            throw new JournalMediaRetryableException(stored.Status, stored.Error);

        if (stored.Status == 409 && stored.Error == "media_disabled")
            return JournalMediaUpload.Declined(JournalMediaReason.MediaDisabled);

        if (stored.Status is 422 && stored.Error == "sha256_mismatch")
            // The store hashed something different from what we hashed. Retrying sends the same
            // bytes to the same bucket, so this is terminal and honest rather than a loop.
            return JournalMediaUpload.Declined(JournalMediaReason.DownloadFailed);

        if (stored.Status is not (200 or 201))
            throw new JournalMediaRetryableException(stored.Status, stored.Error);

        return JournalMediaUpload.Uploaded(declared.UploadId, digest);
    }
}

/// <summary>
/// The upload could not complete for a reason that will probably go away. The drainer keeps the
/// record pending and backs off, exactly as it does for a 5xx on the message post.
/// </summary>
public sealed class JournalMediaRetryableException(int status, string? error)
    : Exception($"journal media upload retryable: {error ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
{
    public int Status { get; } = status;
    public string? Error { get; } = error;
}
