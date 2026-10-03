using System.Globalization;
using System.Net.Http.Headers;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
namespace Fleet.Agent.Services.JournalFiles;
/// <summary>Uses the transport's own bot client with only a destination and a file part.</summary>
public sealed class JournalUploadRequest(long chatId, bool photo, HttpContent content, string fileName) : FileRequestBase<Message>(photo ? "sendPhoto" : "sendDocument")
{
    public bool TerminatorWritten { get; private set; }
    public override HttpContent ToHttpContent()
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(chatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
        var safe = new string(System.IO.Path.GetFileName(fileName).Where(c => c is >= ' ' and <= '~' && c is not ('"' or '\\' or '/')).Take(120).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = photo ? "attachment.jpg" : "attachment.bin";
        content.Headers.ContentType ??= new MediaTypeHeaderValue(photo ? "image/jpeg" : "application/octet-stream");
        multipart.Add(content, photo ? "photo" : "document", safe);
        // A non-computable total would silently become a chunked Telegram request.
        if (multipart.Headers.ContentLength is null) { multipart.Dispose(); throw new JournalUploadIntegrityException(); }
        return new TrackedContent(multipart, () => TerminatorWritten = true);
    }
    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _complete;
        public TrackedContent(HttpContent inner, Action complete)
        {
            _inner = inner; _complete = complete;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength!.Value; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => SerializeToStreamAsync(stream, context, default);
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
        { await _inner.CopyToAsync(stream, ct); _complete(); }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
