using System.Security.Cryptography;
namespace Fleet.Agent.Services.JournalFiles;
public sealed class JournalUploadIntegrityException : IOException
{
    public JournalUploadIntegrityException() : base("journal_upload_integrity_failed") { }
}
/// <summary>Known-length stream. Failed integrity never produces EOF or a multipart terminator.</summary>
public sealed class VerifyingUploadContent : StreamContent
{
    private readonly long _length;
    public VerifyingUploadContent(Stream source, long length, string sha256) : base(new VerifiedStream(source, length, sha256))
    { _length = length; Headers.ContentLength = length; }
    protected override bool TryComputeLength(out long length) { length = _length; return true; }
    private sealed class VerifiedStream(Stream source, long length, string sha256) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _count;
        private bool _complete;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_complete) return 0;
            var n = await source.ReadAsync(buffer, ct);
            _count += n;
            if (_count > length) throw new JournalUploadIntegrityException();
            if (n > 0) _hash.AppendData(buffer.Span[..n]);
            else
            {
                if (_count != length || !string.Equals(Convert.ToHexStringLower(_hash.GetHashAndReset()), sha256, StringComparison.Ordinal))
                    throw new JournalUploadIntegrityException();
                _complete = true;
            }
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _count; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { source.Dispose(); _hash.Dispose(); } base.Dispose(disposing); }
    }
}
