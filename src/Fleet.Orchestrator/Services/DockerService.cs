using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Wraps the Docker Engine API over a Unix socket.
/// The socket path defaults to /var/run/docker.sock but can be overridden via
/// the Docker__SocketPath configuration key (e.g. in appsettings.json or as an
/// environment variable). This is needed on macOS with Colima, where the socket
/// lives at ~/.colima/default/docker.sock.
/// </summary>
public sealed class DockerService : IDisposable
{
    private const string DefaultSocketPath = "/var/run/docker.sock";
    private const string DockerApiVer      = "v1.41";

    private readonly HttpClient _http;
    private readonly ILogger<DockerService> _logger;

    public DockerService(ILogger<DockerService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var socketPath = configuration["Docker:SocketPath"] ?? DefaultSocketPath;

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket   = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(socketPath);
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        _logger.LogInformation("DockerService using socket {SocketPath}", socketPath);

        // Base address must use http://localhost so the URI path is used as-is
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>Returns the raw JSON from Docker container inspect, or null if not found / socket unavailable.</summary>
    public async Task<string?> InspectContainerAsync(string containerName, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/{DockerApiVer}/containers/{containerName}/json", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Container {Name} not found (HTTP {Status})", containerName, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker API unavailable — cannot inspect container {Name}", containerName);
            return null;
        }
    }

    /// <summary>Returns the time the container was last started (State.StartedAt), or null if not found / not running.</summary>
    public async Task<DateTimeOffset?> GetContainerStartedAtAsync(string containerName)
    {
        try
        {
            var response = await _http.GetAsync($"/{DockerApiVer}/containers/{containerName}/json");
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var startedAtStr = doc.RootElement
                                  .GetProperty("State")
                                  .GetProperty("StartedAt")
                                  .GetString();
            return DateTimeOffset.TryParse(startedAtStr, out var ts) && ts.Year > 1 ? ts : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker API unavailable — cannot get StartedAt for container {Name}", containerName);
            return null;
        }
    }

    /// <summary>Returns Docker container state string (running, exited, …) or null if not found / socket unavailable.</summary>
    public async Task<string?> GetContainerStatusAsync(string containerName)
    {
        try
        {
            var response = await _http.GetAsync($"/{DockerApiVer}/containers/{containerName}/json");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Container {Name} not found (HTTP {Status})", containerName, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                      .GetProperty("State")
                      .GetProperty("Status")
                      .GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker API unavailable — cannot get status for container {Name}", containerName);
            return null;
        }
    }

    /// <summary>Sends a stop request to Docker for the named container. Returns true on success.</summary>
    public async Task<bool> StopContainerAsync(string containerName)
    {
        try
        {
            // t=10: wait up to 10 s for graceful shutdown before SIGKILL
            var response = await _http.PostAsync(
                $"/{DockerApiVer}/containers/{containerName}/stop?t=10",
                content: null);

            // 304 Not Modified = container already stopped
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                _logger.LogInformation("Container {Name} stopped via Docker API", containerName);
                return true;
            }

            _logger.LogWarning("Docker stop returned HTTP {Status} for container {Name}",
                (int)response.StatusCode, containerName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot stop container {Name}", containerName);
            return false;
        }
    }

    /// <summary>Sends a start request to Docker for the named container. Returns true on success.</summary>
    public async Task<bool> StartContainerAsync(string containerName)
    {
        try
        {
            var response = await _http.PostAsync(
                $"/{DockerApiVer}/containers/{containerName}/start",
                content: null);

            // 304 Not Modified = container already running
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                _logger.LogInformation("Container {Name} started via Docker API", containerName);
                return true;
            }

            _logger.LogWarning("Docker start returned HTTP {Status} for container {Name}",
                (int)response.StatusCode, containerName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot start container {Name}", containerName);
            return false;
        }
    }

    /// <summary>Sends a restart request to Docker for the named container. Returns true on success.</summary>
    public async Task<bool> RestartContainerAsync(string containerName)
    {
        try
        {
            // t=10: wait up to 10 s for graceful shutdown before SIGKILL
            var response = await _http.PostAsync(
                $"/{DockerApiVer}/containers/{containerName}/restart?t=10",
                content: null);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Container {Name} restarted via Docker API", containerName);
                return true;
            }

            _logger.LogWarning("Docker restart returned HTTP {Status} for container {Name}",
                (int)response.StatusCode, containerName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot restart container {Name}", containerName);
            return false;
        }
    }

    /// <summary>Fetches the last <paramref name="tail"/> log lines from a container as a single string. Non-streaming snapshot.</summary>
    public async Task<string?> GetContainerLogsAsync(string containerName, int tail = 100)
    {
        var url = $"/{DockerApiVer}/containers/{containerName}/logs?follow=false&stdout=1&stderr=1&tail={tail}&timestamps=1";
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker log fetch unavailable for container {Name}", containerName);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Docker logs returned HTTP {Status} for container {Name}", (int)response.StatusCode, containerName);
            response.Dispose();
            return null;
        }

        using (response)
        {
            var stream = await response.Content.ReadAsStreamAsync();
            using (stream)
            {
                var lines = new List<string>();
                var header = new byte[8];
                while (true)
                {
                    int read = 0;
                    try
                    {
                        while (read < 8)
                        {
                            int n = await stream.ReadAsync(header.AsMemory(read, 8 - read));
                            if (n == 0) goto done;
                            read += n;
                        }
                    }
                    catch { goto done; }

                    int frameLen = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
                    if (frameLen == 0) continue;
                    if (frameLen > 1024 * 1024) break;

                    var payload = new byte[frameLen];
                    read = 0;
                    try
                    {
                        while (read < frameLen)
                        {
                            int n = await stream.ReadAsync(payload.AsMemory(read, frameLen - read));
                            if (n == 0) goto done;
                            read += n;
                        }
                    }
                    catch { goto done; }

                    var text = System.Text.Encoding.UTF8.GetString(payload).TrimEnd('\n', '\r');
                    if (!string.IsNullOrEmpty(text))
                        lines.Add(text);
                }
                done:
                return lines.Count > 0 ? string.Join('\n', lines) : "(no log output)";
            }
        }
    }

    /// <summary>Streams container log lines via Docker API follow mode. Yields decoded lines until cancelled.</summary>
    public async IAsyncEnumerable<string> StreamContainerLogsAsync(
        string containerName,
        int tail,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"/{DockerApiVer}/containers/{containerName}/logs?follow=true&stdout=1&stderr=1&tail={tail}&timestamps=1";
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker log stream unavailable for container {Name}", containerName);
            yield break;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Docker logs returned HTTP {Status} for container {Name}", (int)response.StatusCode, containerName);
            response.Dispose();
            yield break;
        }

        using (response)
        {
            Stream stream;
            try { stream = await response.Content.ReadAsStreamAsync(ct); }
            catch { yield break; }

            using (stream)
            {
                var header = new byte[8];
                while (!ct.IsCancellationRequested)
                {
                    // Read 8-byte frame header
                    int read = 0;
                    try
                    {
                        while (read < 8)
                        {
                            int n = await stream.ReadAsync(header.AsMemory(read, 8 - read), ct);
                            if (n == 0) yield break;
                            read += n;
                        }
                    }
                    catch (OperationCanceledException) { yield break; }
                    catch { yield break; }

                    // Parse frame length from bytes 4-7 (big-endian uint32)
                    int frameLen = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
                    if (frameLen == 0) continue;
                    if (frameLen > 1024 * 1024) yield break; // 1MB sanity limit

                    var payload = new byte[frameLen];
                    read = 0;
                    try
                    {
                        while (read < frameLen)
                        {
                            int n = await stream.ReadAsync(payload.AsMemory(read, frameLen - read), ct);
                            if (n == 0) yield break;
                            read += n;
                        }
                    }
                    catch (OperationCanceledException) { yield break; }
                    catch { yield break; }

                    var text = System.Text.Encoding.UTF8.GetString(payload).TrimEnd('\n', '\r');
                    if (!string.IsNullOrEmpty(text))
                        yield return text;
                }
            }
        }
    }

    /// <summary>
    /// Returns the set of Docker network names that currently exist.
    /// Returns null if the Docker socket is unavailable.
    /// </summary>
    public async Task<HashSet<string>?> ListNetworkNamesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/{DockerApiVer}/networks", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Docker networks list returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var net in doc.RootElement.EnumerateArray())
            {
                if (net.TryGetProperty("Name", out var nameEl) && nameEl.GetString() is string name)
                    names.Add(name);
            }
            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker API unavailable — cannot list networks");
            return null;
        }
    }

    /// <summary>
    /// Creates a bridge network with the given name if it does not already exist.
    /// Returns true if the network was created or already existed, false on error.
    /// </summary>
    public async Task<bool> CreateNetworkIfMissingAsync(string networkName, CancellationToken ct = default)
    {
        try
        {
            // Check existence first
            var checkResponse = await _http.GetAsync(
                $"/{DockerApiVer}/networks/{Uri.EscapeDataString(networkName)}", ct);

            if (checkResponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("Network '{Name}' already exists", networkName);
                return true;
            }

            if (checkResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Unexpected HTTP {Status} checking network '{Name}'",
                    (int)checkResponse.StatusCode, networkName);
                return false;
            }

            // Create
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                Name   = networkName,
                Driver = "bridge",
                CheckDuplicate = true,
            });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var createResponse = await _http.PostAsync($"/{DockerApiVer}/networks/create", content, ct);

            if (createResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("Created Docker network '{Name}'", networkName);
                return true;
            }

            _logger.LogWarning("Docker network create returned HTTP {Status} for '{Name}'",
                (int)createResponse.StatusCode, networkName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot create network '{Name}'", networkName);
            return false;
        }
    }

    /// <summary>
    /// Creates a container using the Docker API. Does NOT start it.
    /// Returns the container ID on success, or null on failure.
    /// When <paramref name="hostPort"/> is set, binds container port 8080 to
    /// 127.0.0.1:{hostPort} so the orchestrator (a native host process) can reach
    /// the agent's HTTP API without needing to resolve Docker bridge IPs.
    /// </summary>
    public async Task<string?> CreateContainerAsync(
        string containerName,
        string image,
        long memoryBytes,
        IReadOnlyList<string> env,
        IReadOnlyList<string> binds,
        string primaryNetwork,
        int? hostPort = null,
        CancellationToken ct = default)
    {
        try
        {
            object hostConfig;
            if (hostPort.HasValue)
            {
                hostConfig = new
                {
                    Memory        = memoryBytes,
                    Binds         = binds,
                    RestartPolicy = new { Name = "unless-stopped" },
                    NetworkMode   = primaryNetwork,
                    PortBindings  = new Dictionary<string, object>
                    {
                        ["8080/tcp"] = new object[]
                        {
                            new { HostIp = "127.0.0.1", HostPort = hostPort.Value.ToString() }
                        }
                    }
                };
            }
            else
            {
                hostConfig = new
                {
                    Memory        = memoryBytes,
                    Binds         = binds,
                    RestartPolicy = new { Name = "unless-stopped" },
                    NetworkMode   = primaryNetwork,
                };
            }

            var bodyObj = hostPort.HasValue
                ? (object)new
                {
                    Image = image,
                    Env   = env,
                    ExposedPorts = new Dictionary<string, object> { ["8080/tcp"] = new { } },
                    HostConfig   = hostConfig,
                    NetworkingConfig = new
                    {
                        EndpointsConfig = new Dictionary<string, object>
                        {
                            [primaryNetwork] = new { }
                        }
                    }
                }
                : new
                {
                    Image = image,
                    Env   = env,
                    ExposedPorts = (Dictionary<string, object>?)null,
                    HostConfig   = hostConfig,
                    NetworkingConfig = new
                    {
                        EndpointsConfig = new Dictionary<string, object>
                        {
                            [primaryNetwork] = new { }
                        }
                    }
                };

            var body = System.Text.Json.JsonSerializer.Serialize(bodyObj, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"/{DockerApiVer}/containers/create?name={Uri.EscapeDataString(containerName)}",
                content, ct);

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Docker container create returned HTTP {Status} for '{Name}': {Body}",
                    (int)response.StatusCode, containerName, responseBody);
                return null;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var id = doc.RootElement.GetProperty("Id").GetString();
            _logger.LogInformation("Created container '{Name}' (id={Id})", containerName, id?[..12]);
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot create container '{Name}'", containerName);
            return null;
        }
    }

    /// <summary>
    /// Removes a container by name. Uses force=true so it works even if running.
    /// Returns true on success or if the container was already gone.
    /// </summary>
    public async Task<bool> RemoveContainerAsync(string containerName, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.DeleteAsync(
                $"/{DockerApiVer}/containers/{Uri.EscapeDataString(containerName)}?force=true&v=false", ct);

            // 404 = already gone — treat as success
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Removed container '{Name}'", containerName);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Docker container remove returned HTTP {Status} for '{Name}': {Body}",
                (int)response.StatusCode, containerName, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot remove container '{Name}'", containerName);
            return false;
        }
    }

    /// <summary>
    /// Connects a container to an additional Docker network.
    /// Returns true on success or if already connected.
    /// </summary>
    public async Task<bool> ConnectToNetworkAsync(string networkName, string containerId, CancellationToken ct = default)
    {
        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { Container = containerId });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(
                $"/{DockerApiVer}/networks/{Uri.EscapeDataString(networkName)}/connect",
                content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Connected container '{Id}' to network '{Network}'", containerId[..12], networkName);
                return true;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            // 403 can mean already connected
            if (responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Container already connected to '{Network}'", networkName);
                return true;
            }

            _logger.LogWarning(
                "Docker network connect returned HTTP {Status} for '{Network}': {Body}",
                (int)response.StatusCode, networkName, responseBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Docker API unavailable — cannot connect container to network '{Network}'", networkName);
            return false;
        }
    }

    /// <summary>
    /// Returns the set of names of all currently running containers.
    /// Returns null if the Docker socket is unavailable.
    /// </summary>
    public async Task<HashSet<string>?> ListRunningContainerNamesAsync(CancellationToken ct = default)
    {
        try
        {
            // filters={"status":["running"]} selects only running containers
            var filters = Uri.EscapeDataString("{\"status\":[\"running\"]}");
            var response = await _http.GetAsync($"/{DockerApiVer}/containers/json?filters={filters}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Docker container list returned HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in doc.RootElement.EnumerateArray())
            {
                if (container.TryGetProperty("Names", out var namesEl))
                    foreach (var nameEl in namesEl.EnumerateArray())
                    {
                        // Docker returns names with a leading slash, e.g. "/fleet-myagent"
                        var name = nameEl.GetString()?.TrimStart('/');
                        if (!string.IsNullOrEmpty(name))
                            names.Add(name);
                    }
            }
            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker API unavailable — cannot list running containers");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
