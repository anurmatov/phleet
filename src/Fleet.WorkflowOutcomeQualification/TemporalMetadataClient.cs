using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Fleet.WorkflowOutcomeQualification;

public sealed record TemporalSystemInfo(string ServerVersion, string ResponseSha256);

/// <summary>
/// Read-only <c>WorkflowService/GetSystemInfo</c> metadata client. It performs no workflow, signal,
/// namespace or history mutation and never falls back to an ambient Temporal address.
/// </summary>
public interface ITemporalMetadataReader
{
    Task<TemporalSystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
}

public sealed class TemporalMetadataClient : ITemporalMetadataReader, IDisposable
{
    private const string Path = "/temporal.api.workflowservice.v1.WorkflowService/GetSystemInfo";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public TemporalMetadataClient(TemporalTarget target)
    {
        if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new QualificationException("temporal.endpoint must be https for server identity binding");
        var expected = Convert.FromHexString(target.IdentitySha256);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = target.ServerName,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (errors != SslPolicyErrors.None || certificate is not X509Certificate2 certificate2)
                        return false;
                    var hash = SHA256.HashData(certificate2.GetPublicKey());
                    return hash.AsSpan().SequenceEqual(expected);
                }
            }
        };
        _http = new HttpClient(handler) { BaseAddress = endpoint };
        _ownsHttp = true;
    }

    internal TemporalMetadataClient(HttpClient http)
    {
        _http = http;
        _ownsHttp = false;
    }

    public async Task<TemporalSystemInfo> GetSystemInfoAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent([0, 0, 0, 0, 0])
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("te", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", "identity");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new QualificationException("Temporal GetSystemInfo returned HTTP " + (int)response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "application/grpc")
            throw new QualificationException("Temporal GetSystemInfo did not return application/grpc");
        if (!response.TrailingHeaders.TryGetValues("grpc-status", out var statuses)
            || !statuses.SequenceEqual(new[] { "0" }, StringComparer.Ordinal))
            throw new QualificationException("Temporal GetSystemInfo returned a non-zero grpc-status");

        var payload = GrpcFrame.Payload(body);
        var version = ProtobufReader.ReadStringField(payload.Span, 1);
        return new TemporalSystemInfo(version, Convert.ToHexStringLower(SHA256.HashData(body)));
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public static class GrpcFrame
{
    public static ReadOnlyMemory<byte> Payload(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) throw new QualificationException("gRPC frame is shorter than its header");
        if (frame[0] != 0) throw new QualificationException("compressed gRPC frames are not supported");
        var length = BinaryPrimitives.ReadInt32BigEndian(frame[1..5]);
        if (length < 0 || frame.Length - 5 != length)
            throw new QualificationException("gRPC frame length does not match its payload");
        return frame[5..].ToArray();
    }
}

public static class ProtobufReader
{
    public static string ReadStringField(ReadOnlySpan<byte> payload, int fieldNumber)
    {
        var expectedTag = (uint)(fieldNumber << 3 | 2);
        while (payload.Length > 0)
        {
            var tag = ReadVarint(ref payload);
            if (tag == expectedTag)
            {
                var length = (int)ReadVarint(ref payload);
                if (length < 0 || length > payload.Length)
                    throw new QualificationException("protobuf string length is out of range");
                return Encoding.UTF8.GetString(payload[..length]);
            }
            var wireType = tag & 7;
            switch (wireType)
            {
                case 0:
                    ReadVarint(ref payload);
                    break;
                case 1:
                    if (payload.Length < 8) throw new QualificationException("truncated fixed64 field");
                    payload = payload[8..];
                    break;
                case 2:
                    var length = (int)ReadVarint(ref payload);
                    if (length < 0 || length > payload.Length) throw new QualificationException("truncated length-delimited field");
                    payload = payload[length..];
                    break;
                case 5:
                    if (payload.Length < 4) throw new QualificationException("truncated fixed32 field");
                    payload = payload[4..];
                    break;
                default:
                    throw new QualificationException("protobuf wire type " + wireType + " is unsupported");
            }
        }
        throw new QualificationException("protobuf field " + fieldNumber + " was not present");
    }

    private static ulong ReadVarint(ref ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (data.Length == 0) throw new QualificationException("protobuf varint ended early");
            var current = data[0];
            data = data[1..];
            value |= (ulong)(current & 0x7f) << (7 * index);
            if ((current & 0x80) == 0) return value;
        }
        throw new QualificationException("protobuf varint is too long");
    }
}
