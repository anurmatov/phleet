using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

/// <summary>Read only pinned context metadata; never invoke Docker or discover another endpoint.</summary>
public static class DockerConfigurationBinding
{
    public static void Verify(ProcessQualificationManifest manifest, string root)
    {
        if (Path.GetFileName(manifest.Docker.ConfigFile) != "config.json"
            || manifest.Docker.Context == "default")
            throw new QualificationBlockedException("an explicit pinned named Docker context is required");
        var configPin = manifest.ConfigFiles.SingleOrDefault(f => f.Path == manifest.Docker.ConfigFile)
            ?? throw new QualificationException("Docker config hash is missing");
        using var config = Read(root, configPin);
        if (!config.RootElement.TryGetProperty("currentContext", out var current)
            || current.ValueKind != JsonValueKind.String || current.GetString() != manifest.Docker.Context)
            throw new QualificationException("Docker current context differs from the pinned context");
        var contextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.Docker.Context)));
        var relative = Path.Combine(Path.GetDirectoryName(manifest.Docker.ConfigFile) ?? "",
            "contexts", "meta", contextHash, "meta.json");
        var metaPin = manifest.ConfigFiles.SingleOrDefault(f => f.Path == relative)
            ?? throw new QualificationException("Docker context metadata hash is missing");
        using var metadata = Read(root, metaPin);
        var meta = metadata.RootElement;
        if (!meta.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String
            || name.GetString() != manifest.Docker.Context
            || !meta.TryGetProperty("Endpoints", out var endpoints)
            || endpoints.ValueKind != JsonValueKind.Object || !endpoints.TryGetProperty("docker", out var docker)
            || docker.ValueKind != JsonValueKind.Object || !docker.TryGetProperty("Host", out var host)
            || host.ValueKind != JsonValueKind.String || host.GetString() != manifest.Docker.Endpoint)
            throw new QualificationException("Docker context endpoint differs from the immutable target");
        // The concrete reader currently supports only Unix transport. Do not qualify TLS
        // material or overrides that a later CLI path could interpret differently.
        if (!manifest.Docker.Endpoint.StartsWith("unix://", StringComparison.Ordinal)
            || docker.TryGetProperty("SkipTLSVerify", out var skip) && skip.ValueKind != JsonValueKind.False
            || meta.TryGetProperty("TLSMaterial", out var tls)
                && (tls.ValueKind != JsonValueKind.Object || tls.EnumerateObject().Any()))
            throw new QualificationBlockedException("Docker context transport requires separate qualification");
    }

    private static JsonDocument Read(string root, HashedFile pin)
    {
        var path = OwnedPaths.File(root, pin.Path, pin.Sha256);
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new QualificationException("Docker context exceeds the read bound");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != pin.Sha256)
            throw new QualificationException("Docker context changed during its bounded read");
        var doc = JsonDocument.Parse(bytes);
        try { Check(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }

    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new QualificationException("duplicate Docker context property");
                Check(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Check(item);
    }
}
