using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.WorkflowOutcomeQualification;

public sealed record ContainerBindingPin(string Id, string ImageId, string SpecSha256);
internal sealed record ContainerMount(string Type, string Source, string Target);
internal sealed record ContainerBindingFile(int SchemaVersion, string OwnerNonce, string SocketTarget,
    Dictionary<string, ContainerBindingPin> Containers);

/// <summary>Selected immutable Docker observations, never raw environment/configuration output.</summary>
public sealed class ContainerBindingObservation
{
    public string Id { get; }
    public string ImageId { get; }
    public string SpecSha256 { get; }
    internal string OwnerNonce { get; }
    internal bool UnsafeHost { get; }
    internal IReadOnlyList<ContainerMount> Mounts { get; }
    internal IReadOnlyDictionary<string, string> EnvironmentHashes { get; }

    private ContainerBindingObservation(JsonElement root)
    {
        Id = root.GetProperty("Id").GetString()!;
        ImageId = root.GetProperty("Image").GetString()!;
        var config = root.GetProperty("Config");
        var host = root.GetProperty("HostConfig");
        OwnerNonce = config.GetProperty("Labels").GetProperty("org.phleet.qualification.owner").GetString()!;
        UnsafeHost = host.GetProperty("Privileged").GetBoolean()
            || host.GetProperty("NetworkMode").GetString() == "host"
            || host.GetProperty("PidMode").GetString() == "host"
            || host.GetProperty("IpcMode").GetString() == "host"
            || (host.TryGetProperty("CapAdd", out var caps) && caps.ValueKind == JsonValueKind.Array && caps.GetArrayLength() != 0)
            || (host.TryGetProperty("Devices", out var devices) && devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() != 0)
            || !root.GetProperty("State").GetProperty("Running").GetBoolean()
            || root.GetProperty("State").GetProperty("Pid").GetInt32() <= 0;
        Mounts = root.GetProperty("Mounts").EnumerateArray().Select(m => new ContainerMount(
            m.GetProperty("Type").GetString()!, m.GetProperty("Source").GetString()!,
            m.GetProperty("Destination").GetString()!)).ToArray();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in config.GetProperty("Env").EnumerateArray())
        {
            var text = value.GetString()!;
            var index = text.IndexOf('=');
            if (index < 1 || !seen.Add(text[..index])) throw new QualificationException("ambiguous nested environment");
            var key = text[..index];
            if (key.StartsWith("TemporalBridge__Namespaces__", StringComparison.Ordinal) && key != "TemporalBridge__Namespaces__0")
                throw new QualificationException("unregistered nested Temporal namespace");
            if (!SelectedEnvironment.Contains(key)) continue;
            var selected = text[(index + 1)..];
            if (key == "RabbitMq__Host")
            {
                var uri = new Uri(selected, UriKind.Absolute);
                selected = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            }
            env[key] = Hash(selected);
        }
        EnvironmentHashes = new ReadOnlyDictionary<string, string>(env);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var key in new[] { "Config", "HostConfig", "Mounts", "NetworkSettings" })
            { writer.WritePropertyName(key); WriteCanonical(writer, root.GetProperty(key)); }
            writer.WritePropertyName("Generation");
            writer.WriteStartObject();
            writer.WritePropertyName("Pid"); root.GetProperty("State").GetProperty("Pid").WriteTo(writer);
            writer.WritePropertyName("StartedAt"); root.GetProperty("State").GetProperty("StartedAt").WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        SpecSha256 = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static readonly IReadOnlySet<string> SelectedEnvironment = new HashSet<string>(
        ["Docker__SocketPath", "TemporalBridge__TemporalAddress", "TemporalBridge__Namespaces__0",
         "TemporalBridge__OrchestratorUrl", "RabbitMq__Host", "Agent__Provider", "Agent__Model",
         "Agent__CodexOssBaseUrl"], StringComparer.Ordinal);

    internal static ContainerBindingObservation Parse(JsonElement root)
    {
        try
        {
            RejectDuplicates(root);
            var result = new ContainerBindingObservation(root);
            if (!Hex(result.Id, 64) || !result.ImageId.StartsWith("sha256:", StringComparison.Ordinal)
                || !Hex(result.ImageId[7..], 64) || string.IsNullOrWhiteSpace(result.OwnerNonce))
                throw new QualificationException("invalid nested container identity");
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException
            or NullReferenceException or FormatException)
        { throw new QualificationException("invalid nested container observation"); }
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool Hex(string? value, int count) => value is not null && value.Length == count
        && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    internal static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new QualificationException("duplicate nested JSON member");
                RejectDuplicates(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(p.Name); WriteCanonical(writer, p.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

/// <summary>
/// Read-only application-container checkpoint. Its IDs/hashes do not authorize cleanup
/// or qualify the still-missing broker/SQL/server, helper and volume campaign registry.
/// </summary>
internal sealed class NestedContainerBindings
{
    private readonly ContainerBindingFile _file;
    private readonly IReadOnlyDictionary<string, ContainerBindingPin> _pins;
    private NestedContainerBindings(ContainerBindingFile file)
    {
        _file = file;
        _pins = new ReadOnlyDictionary<string, ContainerBindingPin>(
            new Dictionary<string, ContainerBindingPin>(file.Containers, StringComparer.Ordinal));
    }

    internal static NestedContainerBindings Load(ProcessQualificationManifest manifest, string directory)
    {
        var files = manifest.ConfigFiles.Where(f => Path.GetFileName(f.Path) == "runtime-bindings.json").ToArray();
        if (files.Length != 1) throw new QualificationException("one hash-pinned runtime-bindings.json is required");
        var path = OwnedPaths.File(directory, files[0].Path, files[0].Sha256);
        if (new FileInfo(path).Length > 1024 * 1024) throw new QualificationException("nested bindings exceed size limit");
        byte[] bytes;
        using (var stream = File.OpenRead(path))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
            {
                if (buffer.Length + read > 1024 * 1024) throw new QualificationException("nested bindings exceed size limit");
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != files[0].Sha256)
            throw new QualificationException("nested bindings changed before decode");
        using var doc = JsonDocument.Parse(bytes);
        ContainerBindingObservation.RejectDuplicates(doc.RootElement);
        var file = doc.RootElement.Deserialize<ContainerBindingFile>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false })
            ?? throw new QualificationException("missing nested bindings");
        if (file.SchemaVersion != 1 || file.OwnerNonce != manifest.Runtime.ScenarioNonce
            || file.SocketTarget is null || !file.SocketTarget.StartsWith("/run/", StringComparison.Ordinal)
            || file.SocketTarget.Contains("..", StringComparison.Ordinal)
            || file.Containers is null || !QualificationManifestParser.RequiredImageRoles.SetEquals(file.Containers.Keys))
            throw new QualificationException("invalid nested binding scope");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in file.Containers.Values)
            if (pin is null || !ContainerBindingObservation.Hex(pin.Id, 64) || !seen.Add(pin.Id)
                || pin.ImageId is null || !pin.ImageId.StartsWith("sha256:", StringComparison.Ordinal)
                || !ContainerBindingObservation.Hex(pin.ImageId[7..], 64)
                || !ContainerBindingObservation.Hex(pin.SpecSha256, 64))
                throw new QualificationException("invalid or reused nested immutable container ID");
        return new NestedContainerBindings(file);
    }

    internal async Task<IReadOnlyDictionary<string, string>> VerifyAsync(ProcessQualificationManifest manifest,
        string directory, IReadOnlyDictionary<string, string> images, IDockerEngineReader docker, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (role, pin) in _pins)
        {
            var engine = await docker.GetEngineInfoAsync(ct);
            if (engine.EngineId != manifest.Docker.EngineId || engine.ServerVersion != manifest.Docker.ServerVersion)
                throw new QualificationException("nested engine identity changed before inspect");
            var actual = await docker.GetContainerAsync(pin.Id, ct);
            engine = await docker.GetEngineInfoAsync(ct);
            if (engine.EngineId != manifest.Docker.EngineId || engine.ServerVersion != manifest.Docker.ServerVersion)
                throw new QualificationException("nested engine identity changed after inspect");
            if (actual.Id != pin.Id || actual.ImageId != pin.ImageId || pin.ImageId != images[role]
                || actual.SpecSha256 != pin.SpecSha256 || actual.OwnerNonce != _file.OwnerNonce
                || actual.UnsafeHost)
                throw new QualificationException("nested immutable container binding mismatch");
            foreach (var mount in actual.Mounts)
            {
                if (mount.Type == "volume") continue; // Not cleanup authority or a qualified volume generation.
                var controlSocket = role == "orchestrator" && mount.Type == "bind"
                    && manifest.Docker.Endpoint == "unix://" + mount.Source && mount.Target == _file.SocketTarget;
                if (!controlSocket && (mount.Type != "bind"
                    || !Path.GetFullPath(mount.Source).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || mount.Source.Contains("docker.sock", StringComparison.Ordinal)))
                    throw new QualificationException("nested mount escapes the owned target");
                if (controlSocket) continue;
                var relative = Path.GetRelativePath(directory, mount.Source);
                if (Directory.Exists(mount.Source)) OwnedPaths.Directory(directory, relative);
                else
                {
                    var pins = manifest.ConfigFiles.Concat(manifest.PolicyFiles).Where(f =>
                        Path.GetFullPath(Path.Combine(directory, f.Path)) == Path.GetFullPath(mount.Source)).ToArray();
                    if (pins.Length != 1) throw new QualificationException("nested bind file lacks an approved hash");
                    OwnedPaths.File(directory, pins[0].Path, pins[0].Sha256);
                }
            }
            RequireEnvironment(actual, "RabbitMq__Host", manifest.Runtime.BrokerEndpoint);
            if (role == "agent")
            {
                RequireEnvironment(actual, "Agent__Provider", "codex");
                RequireEnvironment(actual, "Agent__Model", "ollama/" + manifest.Fixture.Model);
                RequireEnvironment(actual, "Agent__CodexOssBaseUrl", manifest.Fixture.Endpoint.TrimEnd('/'));
                if (actual.Mounts.Any(m => m.Source == manifest.Docker.Endpoint["unix://".Length..]))
                    throw new QualificationException("native agent must not inherit the engine socket");
            }
            if (role == "orchestrator")
            {
                RequireEnvironment(actual, "Docker__SocketPath", _file.SocketTarget);
                if (!actual.Mounts.Any(m => m.Type == "bind" && "unix://" + m.Source == manifest.Docker.Endpoint
                    && m.Target == _file.SocketTarget))
                    throw new QualificationException("nested Docker API socket is not the pinned engine mount");
            }
            if (role == "temporalBridge")
            {
                RequireEnvironment(actual, "TemporalBridge__TemporalAddress", new Uri(manifest.Temporal.Endpoint).Authority);
                RequireEnvironment(actual, "TemporalBridge__Namespaces__0", manifest.Temporal.Namespace);
                RequireEnvironment(actual, "TemporalBridge__OrchestratorUrl", manifest.Runtime.OrchestratorBaseUrl);
            }
            result.Add(role, pin.Id);
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static void RequireEnvironment(ContainerBindingObservation actual, string key, string expected)
    {
        if (key == "RabbitMq__Host")
            expected = new Uri(expected).GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        if (!actual.EnvironmentHashes.TryGetValue(key, out var hash) || hash != ContainerBindingObservation.Hash(expected))
            throw new QualificationException("nested endpoint/configuration does not match the approved target");
    }
}
