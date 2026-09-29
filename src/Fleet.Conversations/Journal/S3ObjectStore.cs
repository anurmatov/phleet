using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations.Journal;

/// <summary>
/// The journal's object store: an S3-compatible bucket behind
/// <see cref="IJournalObjectStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three configuration choices are load-bearing and must not be "tidied" away.</b>
/// <see cref="AmazonS3Config.ForcePathStyle"/> is required because MinIO and SeaweedFS address the
/// bucket in the path, not the hostname — with virtual-host style the request goes to
/// <c>bucket.host</c>, which does not resolve. Both checksum properties are pinned to
/// <c>WHEN_REQUIRED</c> because the SDK's default adds a CRC-32 trailer that MinIO 2024 and later
/// rejects on <c>PutObject</c>; the journal's own integrity check is a SHA-256 the server computes,
/// so nothing is lost. And the timeout is the operator's constant, so a hung bucket is a
/// <see cref="JournalObjectStoreUnavailableException"/> in 30 s rather than a request that holds an
/// upload slot indefinitely.
/// </para>
/// <para>
/// ⚠️ Every failure leaves the ROW in place and is reported as a store failure, never as a
/// successful write. A row that claims <c>uploaded</c> for bytes the bucket does not hold is the
/// one state this class must make unreachable.
/// </para>
/// <para>
/// <b>⚠️ A PUT has to state the object's length twice, and this is the part that failed silently
/// against a real bucket while every fake-bucket test passed.</b> The AWS SDK resolves the length of
/// a <c>PutObject</c> body in a fixed order, and only one combination uploads the right number of
/// bytes from a non-seekable body:
/// </para>
/// <list type="bullet">
/// <item>a <b>seekable</b> stream uploads using the length it reports;</item>
/// <item>a non-seekable stream that <i>reports</i> <c>Length</c> still fails client-side with
/// "Could not determine content length" — the handler does not consult it;</item>
/// <item>a non-seekable stream carrying <c>Headers.ContentLength</c> is the only shape that
/// uploads.</item>
/// </list>
/// <para>
/// The order matters more than it looks: when <c>Headers.ContentLength</c> is set the SDK uses it
/// <i>instead of</i> reading <c>Stream.Length</c>. A stream that reports one length while the request
/// states another therefore uploads the request's number and <c>PutObject</c> still reports success —
/// a truncated object with a healthy-looking row. That combination is what
/// <see cref="HashingReadStream"/> must never be built with, and why it reports
/// <see cref="Stream.Length"/> as the declared size rather than the bytes read so far.
/// </para>
/// <para>
/// ⚠️ Nothing here logs a key. A key is an internal address that an agent must never learn; a log
/// line is the easiest way for one to leak.
/// </para>
/// </remarks>
public sealed class S3ObjectStore : IJournalObjectStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ILogger _logger;

    public S3ObjectStore(JournalMediaOptions options, ILogger logger, IAmazonS3? client = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(requireFields: true);
        RequestTimeout = options.RequestTimeout;

        _bucket = options.Bucket;
        _logger = logger;

        _s3 = client ?? new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = options.Region,
                RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
                Timeout = options.RequestTimeout,
            });
    }

    /// <summary>For tests: drive the store with a client of the caller's making.</summary>
    internal S3ObjectStore(string bucket, ILogger logger, IAmazonS3 client)
    {
        _bucket = bucket;
        _logger = logger;
        _s3 = client;
    }

    /// <summary>The per-request budget the client was built with.</summary>
    public TimeSpan RequestTimeout { get; }

    public async Task<JournalObjectWriteResult> PutAsync(
        string objectKey, Stream body, long byteSize, string contentType,
        CancellationToken ct = default, long? contentLength = null)
    {
        if (byteSize < 0) throw new ArgumentOutOfRangeException(nameof(byteSize));

        // The hash is computed while the bytes stream, so the caller never buffers a 20 MB object
        // and the digest covers exactly what was written to the bucket rather than what the caller
        // said it would send.
        await using var hashing = new HashingReadStream(body, SHA256.Create(), byteSize);

        try
        {
            // AutoCloseStream=false: the hashing stream owns the caller's body, and the SDK
            // closing it after a failure would leave the caller unable to drain or reuse anything.
            var put = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                InputStream = hashing,
                AutoCloseStream = false,
                ContentType = contentType,
            };

            // ⚠️ The length the transport sends comes from HERE, not from the stream. The SDK reads
            //    `Headers.ContentLength` when it is set and stops asking the stream anything; when it
            //    is not set and the stream is not seekable, the call fails client-side with "Could
            //    not determine content length" before a byte is sent. A request body is never
            //    seekable, so an upload without this line can never reach a bucket.
            //
            //    It is also the number the object's size ends up being, which is why the route passes
            //    the row's declared size and nothing else: the hashing stream reads at most
            //    byteSize + 1, so an oversized body is the Overflowed answer the caller deletes
            //    rather than a longer object stored under a size-matched row.
            if (contentLength is long declared) put.Headers.ContentLength = declared;

            await _s3.PutObjectAsync(put, ct);
        }
        catch (AmazonS3Exception e)
        {
            // Type and status only. An S3 message can carry the bucket, the endpoint and the
            // credential scope.
            _logger.LogWarning("journal object store rejected a write: {Status}", e.StatusCode);
            return JournalObjectWriteResult.Failed();
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            _logger.LogWarning("journal object store unreachable for a write: {Error}",
                e.GetType().Name);
            return JournalObjectWriteResult.Failed();
        }

        if (hashing.Overflowed)
        {
            // The object exists with more bytes than were declared. The caller deletes it.
            return JournalObjectWriteResult.Overflowed();
        }

        return JournalObjectWriteResult.Written(hashing.HexDigest, hashing.BytesRead);
    }

    public async Task<JournalObjectReadResult?> GetAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = objectKey }, ct);

            return new JournalObjectReadResult
            {
                Content = response.ResponseStream,
                ByteSize = response.ContentLength,
            };
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound
                                         || string.Equals(e.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            return null;
        }
        catch (AmazonS3Exception e)
        {
            _logger.LogWarning("journal object store rejected a read: {Status}", e.StatusCode);
            throw new JournalObjectStoreUnavailableException(e);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            _logger.LogWarning("journal object store unreachable for a read: {Error}", e.GetType().Name);
            throw new JournalObjectStoreUnavailableException(e);
        }
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(_bucket, objectKey, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound
                                         || string.Equals(e.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            return false;
        }
        catch (AmazonS3Exception e)
        {
            _logger.LogWarning("journal object store rejected a head: {Status}", e.StatusCode);
            throw new JournalObjectStoreUnavailableException(e);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            _logger.LogWarning("journal object store unreachable for a head: {Error}", e.GetType().Name);
            throw new JournalObjectStoreUnavailableException(e);
        }
    }

    /// <summary>
    /// Deletes one object. A missing object is success — the sweeper's whole job is to reach the
    /// state where nothing is left, and re-running it must converge rather than report failure.
    /// </summary>
    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = objectKey }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound
                                         || string.Equals(e.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
        }
        catch (AmazonS3Exception e)
        {
            _logger.LogWarning("journal object store rejected a delete: {Status}", e.StatusCode);
            throw new JournalObjectStoreUnavailableException(e);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            _logger.LogWarning("journal object store unreachable for a delete: {Error}", e.GetType().Name);
            throw new JournalObjectStoreUnavailableException(e);
        }
    }

    /// <summary>
    /// Every key under <paramref name="prefix"/>, following pagination to the end. A truncated
    /// listing would make the orphan sweep delete live objects the page did not reach, so
    /// pagination is not optional.
    /// </summary>
    public async Task<IReadOnlyList<JournalObjectListing>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var keys = new List<JournalObjectListing>();
        string? token = null;

        try
        {
            do
            {
                var response = await _s3.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, ct);

                // ⚠️ Null-tolerant, and this is not defensive padding: a bucket whose prefix holds
                // nothing is answered WITHOUT a `Contents` element at all, and the SDK surfaces that
                // as a null collection rather than an empty one. It happens on the first orphan
                // sweep of any fresh deployment — the common case, not an edge case — and reading it
                // as a sequence throws a NullReferenceException out of the sweep.
                foreach (var item in response.S3Objects ?? [])
                {
                    keys.Add(new JournalObjectListing
                    {
                        Key = item.Key,
                        // MinIO and SeaweedFS both report this; a store that omits it is treated as
                        // brand new, which is the safe direction for an orphan sweep.
                        LastModified = item.LastModified is { } last
                            ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc))
                            : DateTimeOffset.MinValue,
                    });
                }

                // bool? on purpose: a store that omits the flag is treated as truncated, and the
                // next page either arrives or terminates the loop. Treating it as complete would
                // hand the orphan sweep a truncated listing.
                //
                // ⚠️ A truncated answer with no token is a failure, not an end of page. Assigning a
                // null `NextContinuationToken` to the loop variable would exit the loop having
                // returned a PARTIAL listing, and the orphan sweep reads "absent from the listing"
                // as "no row claims this object" — a partial listing is therefore the one answer
                // that deletes bytes an attachment still points at. Refusing the listing costs a
                // retry next tick; trusting it loses data.
                if (response.IsTruncated is not false)
                {
                    if (response.NextContinuationToken is not { } next)
                    {
                        _logger.LogWarning("journal object store paginated a list without a token");
                        throw new JournalObjectStoreUnavailableException();
                    }

                    token = next;
                }
                else token = null;
            }
            while (token is not null);

            return keys;
        }
        catch (AmazonS3Exception e)
        {
            _logger.LogWarning("journal object store rejected a list: {Status}", e.StatusCode);
            throw new JournalObjectStoreUnavailableException(e);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            _logger.LogWarning("journal object store unreachable for a list: {Error}", e.GetType().Name);
            throw new JournalObjectStoreUnavailableException(e);
        }
    }

    /// <summary>
    /// <c>HeadBucket</c>. A configuration the operator can fix (unknown bucket, rejected
    /// credentials) is <see cref="ProbeFailure"/>; a bucket that is merely not answering is false,
    /// which is what lets the service start degraded.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await _s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = _bucket }, ct);
            return true;
        }
        catch (JournalProbeFailureException)
        {
            throw;
        }
        catch (AmazonS3Exception e)
        {
            // The failure classes the operator must fix, named here so the caller's message never
            // has to quote the SDK. A 403 on an unauthenticated bucket read is the same class as a
            // rejected signature: this account cannot use the bucket it was given.
            if (e.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                || e.ErrorCode is "NoSuchBucket" or "InvalidAccessKeyId" or "SignatureDoesNotMatch"
                    or "AccessDenied")
            {
                throw new JournalProbeFailureException(e);
            }

            _logger.LogWarning("journal object store probe deferred: {Status}", e.StatusCode);
            return false;
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            _logger.LogWarning("journal object store probe deferred: {Error}", e.GetType().Name);
            return false;
        }
    }

    public void Dispose() => _s3.Dispose();

    /// <summary>
    /// Transport-shaped failures only. <see cref="AmazonS3Exception"/> is deliberately absent: it
    /// means the store ANSWERED, and an answer is a fact about the request, not a degradation.
    /// </summary>
    private static bool IsTransportFailure(Exception e) =>
        e is AmazonServiceException or OperationCanceledException or System.Net.Http.HttpRequestException
            or System.Net.Sockets.SocketException or System.IO.IOException or TimeoutException
            or InvalidOperationException;

    /// <summary>
    /// A stream that hashes what passes through it and refuses to read past a declared length.
    /// Overflow is recorded rather than thrown: the caller has to tell a caller-side lie
    /// (<c>413</c>/<c>422</c>) from a store failure (<c>503</c>), and the object still has to be
    /// deleted afterwards.
    /// </summary>
    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly SHA256 _hash;
        private readonly long _limit;
        private long _read;

        public HashingReadStream(Stream inner, SHA256 hash, long limit)
        {
            _inner = inner;
            _hash = hash;
            _limit = limit;
        }

        public bool Overflowed { get; private set; }
        public long BytesRead => _read;

        /// <summary>
        /// Lowercase hex of what was read. Finalised on first read, because <c>SHA256.Hash</c>
        /// throws once the instance is finalised and the SDK may dispose the stream before the
        /// caller asks.
        /// </summary>
        public string HexDigest
        {
            get
            {
                if (_digest is null)
                {
                    // Finalize rather than read Hash: TransformBlock never completes the hash, and
                    // reading Hash on an unfinalised SHA256 throws
                    // CryptographicUnexpectedOperationException.
                    _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    _digest = Convert.ToHexStringLower(_hash.Hash ?? Array.Empty<byte>());
                }
                return _digest;
            }
        }

        private string? _digest;

        /// <summary>
        /// Delegates to <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> rather than reading
        /// the inner stream directly.
        /// </summary>
        /// <remarks>
        /// ⚠️ Not a convenience. The SDK takes the synchronous path when the request states a
        /// content length, and the inner stream is an ASP.NET Core request body — which throws
        /// <c>"Synchronous operations are disallowed. Call ReadAsync or set AllowSynchronousIO to
        /// true"</c> on a sync read. Reaching the body at all is therefore only possible through
        /// async, and the wrapper is the layer that decides which one the SDK gets.
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Overflowed) return 0;

            // One byte past the limit is read on purpose: reading exactly the limit cannot tell
            // "the body ended" from "the body continues".
            var allowed = (int)Math.Min(buffer.Length, _limit - _read + 1);
            if (allowed <= 0)
            {
                Overflowed = true;
                return 0;
            }

            // Buffered rather than read straight into the caller's memory: the SDK may hand over a
            // larger buffer than the limit allows and the hash must cover only what was kept.
            var scratch = buffer[..allowed].ToArray();
            var n = await _inner.ReadAsync(scratch, cancellationToken);
            if (n == 0) return 0;

            if (_read + n > _limit)
            {
                Overflowed = true;
                n = (int)(_limit - _read);
                if (n <= 0) return 0;
            }

            _hash.TransformBlock(scratch, 0, n, null, 0);
            scratch.AsSpan(0, n).CopyTo(buffer.Span[..n]);
            _read += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        /// <summary>
        /// The DECLARED length, not the bytes read so far.
        /// </summary>
        /// <remarks>
        /// ⚠️ This must not throw. The SDK's <c>AmazonS3PostMarshallHandler</c> asks the stream for
        /// its length and, when that is unavailable, fails the call client-side with
        /// "Could not determine content length" — before any request is sent. Throwing here is what
        /// made <c>PutAsync</c> report <see cref="JournalObjectWriteResult.StoreFailure"/> against
        /// every real bucket, which is the failure the media suite exists to catch.
        ///
        /// Reporting the declared size is also the honest number: the store writes at most that
        /// many bytes because <see cref="Read"/> reads at most <c>limit + 1</c> and discards the
        /// extra. A body longer than the declaration is the <see cref="Overflowed"/> answer, whose
        /// object the caller deletes — not a longer object.
        /// </remarks>
        public override long Length => _limit;

        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>The store is configured and cannot be reached. Callers answer 503 and keep the row.</summary>
public sealed class JournalObjectStoreUnavailableException(Exception? inner = null)
    : Exception("journal object store unavailable", inner);

/// <summary>
/// The bucket exists but this account cannot use it, or does not exist. Different from an outage:
/// the operator has to change something, so the service refuses to start rather than degrading.
/// </summary>
public sealed class JournalProbeFailureException(Exception? inner = null)
    : Exception("journal object store credentials_rejected", inner);
