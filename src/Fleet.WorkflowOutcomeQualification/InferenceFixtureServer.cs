using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Fleet.WorkflowOutcomeQualification;

public sealed class InferenceFixtureServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly InferenceFixtureTarget _target;
    private readonly InferenceFixtureMode _mode;
    private readonly string _nonce;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public InferenceFixtureServer(InferenceFixtureTarget target, InferenceFixtureMode mode, string nonce, int port)
    {
        _target = target;
        _mode = mode;
        _nonce = nonce;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        while (!linked.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(linked.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            using var _ = client;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var stream = client.GetStream();
                await InferenceFixtureHttpHandler.HandleAsync(stream, stream, _target, _mode, _nonce, timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}

public static class InferenceFixtureHttpHandler
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;

    public static async Task HandleAsync(
        Stream input, Stream output, InferenceFixtureTarget target, InferenceFixtureMode mode, string nonce, CancellationToken ct)
    {
        var header = await ReadHeaderAsync(input, ct);
        var request = ParseRequest(header);
        if (request.Method != "POST" || request.Path != "/v1/responses")
        {
            await WriteErrorAsync(output, request.Method == "POST" ? 404 : 405, "route_not_allowed", ct);
            return;
        }
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.CharSet is not null && !string.Equals(mediaType.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(output, 415, "content_type_not_json", ct);
            return;
        }
        if (request.ContentLength is < 0 or null or > MaxBodyBytes)
        {
            await WriteErrorAsync(output, 413, "request_body_too_large", ct);
            return;
        }
        var body = new byte[request.ContentLength.Value];
        await ReadExactAsync(input, body, ct);
        if (!WorkflowOutcomeInferenceFixture.TryCreateResponse(
                Encoding.UTF8.GetString(body), target, mode, nonce, out var sse, out var error))
        {
            await WriteErrorAsync(output, 400, error, ct);
            return;
        }
        var payload = Encoding.UTF8.GetBytes(sse);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: "
            + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
        await output.WriteAsync(head, ct);
        await output.WriteAsync(payload, ct);
        await output.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(1024);
        var last = new byte[4];
        var next = new byte[1];
        while (buffer.Count < MaxHeaderBytes)
        {
            if (await stream.ReadAsync(next, ct) == 0) throw new IOException("client closed before headers");
            var current = next[0];
            buffer.Add((byte)current);
            last[0] = last[1]; last[1] = last[2]; last[2] = last[3]; last[3] = (byte)current;
            if (last.SequenceEqual("\r\n\r\n"u8)) return [.. buffer];
        }
        throw new IOException("header block exceeded the bounded size");
    }

    private static ParsedRequest ParseRequest(byte[] header)
    {
        var text = Encoding.ASCII.GetString(header).TrimEnd('\r', '\n');
        var lines = text.Split("\r\n");
        if (lines.Length == 0) throw new IOException("empty HTTP request");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3) throw new IOException("invalid HTTP request line");
        if (requestLine[2] != "HTTP/1.1") throw new IOException("unsupported HTTP version");
        var request = new ParsedRequest(requestLine[0], requestLine[1]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines[1..])
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new IOException("malformed HTTP header");
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!seen.Add(name) || name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase))
                throw new IOException("ambiguous HTTP framing");
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) request.ContentType = value;
            else if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, CultureInfo.InvariantCulture, out var length))
                    throw new IOException("invalid content-length");
                request.ContentLength = length;
            }
        }
        return request;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (count == 0) throw new IOException("client closed before the request body");
            read += count;
        }
    }

    private static async Task WriteErrorAsync(Stream stream, int status, string reason, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"" + reason + "\"}}\n");
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " "
            + (status == 405 ? "Method Not Allowed" : status == 413 ? "Payload Too Large" : "Bad Request")
            + "\r\nContent-Type: application/json\r\nContent-Length: "
            + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private sealed record ParsedRequest(string Method, string Path)
    {
        public string? ContentType { get; set; }
        public int? ContentLength { get; set; }
    }
}
