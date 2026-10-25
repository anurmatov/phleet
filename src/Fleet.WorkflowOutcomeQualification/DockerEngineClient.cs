using System.Net.Sockets;
using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

public sealed record DockerEngineInfo(string EngineId, string ServerVersion);
public sealed record DockerImageInfo(string Reference, string ImageId, IReadOnlyList<string> RepoDigests, string? BuildCommit);

public interface IDockerEngineReader
{
    Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default);
    Task<DockerImageInfo> GetImageAsync(string reference, CancellationToken ct = default);
    Task<ContainerBindingObservation> GetContainerAsync(string id, CancellationToken ct = default);
}

public sealed class DockerEngineClient : IDockerEngineReader, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiVersion;

    public DockerEngineClient(DockerTarget target)
    {
        _apiVersion = target.ApiVersion;
        if (target.Endpoint.StartsWith("unix://", StringComparison.Ordinal))
        {
            var socketPath = target.Endpoint["unix://".Length..];
            if (string.IsNullOrWhiteSpace(socketPath)) throw new QualificationException("docker.endpoint has no socket path");
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        }
        else if (target.Endpoint.StartsWith("tcp://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint))
                throw new QualificationException("docker.endpoint is not a valid tcp endpoint");
            throw new QualificationBlockedException("TCP Docker transport requires separately qualified TLS identity; no plaintext fallback");
        }
        else
        {
            throw new QualificationException("docker.endpoint must be unix:// or tcp://");
        }
        _ownsHttp = true;
    }

    internal DockerEngineClient(HttpClient http, string apiVersion)
    {
        _http = http;
        _apiVersion = apiVersion;
        _ownsHttp = false;
    }

    public async Task<DockerEngineInfo> GetEngineInfoAsync(CancellationToken ct = default)
    {
        using var response = await GetAsync("/" + _apiVersion + "/info", ct);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("ID", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
            throw new QualificationException("Docker /info did not return an engine ID");
        var version = root.TryGetProperty("ServerVersion", out var serverVersion)
            && serverVersion.ValueKind == JsonValueKind.String ? serverVersion.GetString()! : "";
        return new DockerEngineInfo(id.GetString()!, version);
    }

    public async Task<DockerImageInfo> GetImageAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new QualificationException("image reference is empty");
        var encoded = Uri.EscapeDataString(reference).Replace("%2F", "/", StringComparison.Ordinal);
        using var response = await GetAsync("/" + _apiVersion + "/images/" + encoded + "/json", ct);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
            throw new QualificationException("Docker image inspect did not return an immutable image ID");
        var root = document.RootElement;
        var digests = root.TryGetProperty("RepoDigests", out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
        string? commit = null;
        if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object
            && labels.TryGetProperty("org.opencontainers.image.revision", out var revision) && revision.ValueKind == JsonValueKind.String)
            commit = revision.GetString();
        return new DockerImageInfo(reference, id.GetString()!, digests, commit);
    }

    public async Task<ContainerBindingObservation> GetContainerAsync(string id, CancellationToken ct = default)
    {
        if (!ContainerBindingObservation.Hex(id, 64))
            throw new QualificationException("nested inspect requires a full immutable container ID");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.GetAsync("/" + _apiVersion + "/containers/" + id + "/json",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new QualificationException("nested container inspect failed");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, timeout.Token);
            if (read == 0) break;
            if (buffer.Length + read > 4 * 1024 * 1024)
                throw new QualificationException("nested container inspect exceeds size limit");
            buffer.Write(bytes, 0, read);
        }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        return ContainerBindingObservation.Parse(doc.RootElement);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var response = await _http.GetAsync(path, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new QualificationException("Docker API read returned HTTP " + status);
        }
        return response;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
