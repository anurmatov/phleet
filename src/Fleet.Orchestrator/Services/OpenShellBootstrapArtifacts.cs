using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Openshell.V1;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Outside custody and Docker named-volume delivery. No process is started, image pulled or secret
/// placed in image/spec/environment/DB. Requires a separately qualified engine/image/mount authority;
/// deliberately not registered by default. A failed/ambiguous delivery is never retried or reused.
/// Pending owned resources remain recorded outside for operator reconciliation, not guessed cleanup.
/// </summary>
public sealed class OpenShellBootstrapArtifacts(HttpClient engine, string custodyRoot)
{
    private sealed record Custody(string PlanHash, string Capability, string OwnershipNonce, bool Delivered);
    private string DirectoryFor(Guid launch, Guid epoch)
    {
        if (!Path.IsPathFullyQualified(custodyRoot) || launch == Guid.Empty || epoch == Guid.Empty)
            throw new OpenShellStateConflictException("bootstrap_custody_unqualified");
        return Path.Combine(custodyRoot, launch.ToString("N") + "-" + epoch.ToString("N"));
    }
    public static string Reference(Guid launch, Guid epoch) => "bootstrap/" + launch.ToString("N") + "/" + epoch.ToString("N");
    public static string VolumeName(Guid launch, Guid epoch) => "bootstrap-" + launch.ToString("N") + "-" + epoch.ToString("N");

    public async Task DeliverAsync(OpenShellLaunchPlan plan, Guid launch, Guid epoch, CancellationToken ct)
    {
        // Reject unsupported driver settings before creating any outside resource.
        if (plan.ReadSpec().Template.DriverConfig is { Fields.Count: > 0 })
            throw new OpenShellStateConflictException("bootstrap_driver_mount_unqualified");
        var directory = DirectoryFor(launch, epoch);
        if (Directory.Exists(directory)) throw new OpenShellStateConflictException("bootstrap_artifact_already_attempted");
        // Custody root must be outside-owned, not workload writable. No caller-selected filenames.
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "custody.json");
        var capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await using (var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew,
            Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            await JsonSerializer.SerializeAsync(file, new Custody(plan.Hash, capability, nonce, false), cancellationToken: ct);
        var volume = VolumeName(launch, epoch);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        using (var found = await SendAsync(HttpMethod.Get, "/v1.41/volumes/" + volume, null, token))
            if (found.StatusCode != HttpStatusCode.NotFound) throw new OpenShellStateConflictException("bootstrap_volume_not_proven_absent");
        using (var created = await SendAsync(HttpMethod.Post, "/v1.41/volumes/create", EngineJson(new
        { Name = volume, Labels = new Dictionary<string, string> { ["bootstrap-owner"] = nonce } }), token))
        {
            created.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
            if (json.RootElement.GetProperty("Name").GetString() != volume
                || json.RootElement.GetProperty("Labels").GetProperty("bootstrap-owner").GetString() != nonce)
                throw new OpenShellStateConflictException("bootstrap_volume_ownership_unconfirmed");
        }
        string helper;
        using (var created = await SendAsync(HttpMethod.Post, "/v1.41/containers/create", EngineJson(new
        {
            Image = plan.ReadSpec().Template.Image, User = "1000:1000", Cmd = new[] { "/bin/false" },
            Labels = new Dictionary<string, string> { ["bootstrap-owner"] = nonce },
            HostConfig = new { NetworkMode = "none", ReadonlyRootfs = true, CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges:true" },
                Mounts = new[] { new { Type = "volume", Source = volume, Target = "/app/generated", ReadOnly = false } } },
        }), token))
        {
            created.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
            helper = json.RootElement.GetProperty("Id").GetString() ?? "";
            if (helper.Length != 64 || !helper.All(char.IsAsciiHexDigitLower))
                throw new OpenShellStateConflictException("bootstrap_helper_identity_unconfirmed");
        }
        await using (var identity = new FileStream(Path.Combine(directory, "helper-id"), new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            await identity.WriteAsync(Encoding.ASCII.GetBytes(helper), token);
        // The create response supplies the exact helper ID. Never resolve/delete a helper by name.
        using (var inspect = await SendAsync(HttpMethod.Get, "/v1.41/containers/" + helper + "/json", null, token))
        {
            inspect.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync(token));
            var root = json.RootElement; var mounts = root.GetProperty("Mounts").EnumerateArray().ToArray();
            if (root.GetProperty("Id").GetString() != helper || root.GetProperty("State").GetProperty("Running").GetBoolean()
                || root.GetProperty("State").GetProperty("Status").GetString() != "created"
                || root.GetProperty("Config").GetProperty("Image").GetString() != plan.ReadSpec().Template.Image
                || root.GetProperty("Config").GetProperty("User").GetString() != "1000:1000"
                || root.GetProperty("Config").GetProperty("Labels").GetProperty("bootstrap-owner").GetString() != nonce
                || root.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != "none"
                || !root.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean()
                || mounts.Length != 1 || mounts[0].GetProperty("Type").GetString() != "volume"
                || mounts[0].GetProperty("Name").GetString() != volume || mounts[0].GetProperty("Destination").GetString() != "/app/generated"
                || !mounts[0].GetProperty("RW").GetBoolean())
                throw new OpenShellStateConflictException("bootstrap_helper_mount_unconfirmed");
        }
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, leaveOpen: true))
        {
            using var bytes = new MemoryStream(Encoding.ASCII.GetBytes(capability));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "bootstrap.capability")
            { Uid = 1000, Gid = 1000, Mode = UnixFileMode.UserRead, DataStream = bytes });
        }
        archive.Position = 0;
        using (var put = await SendAsync(HttpMethod.Put, "/v1.41/containers/" + helper + "/archive?path=/app/generated&copyUIDGID=true&noOverwriteDirNonDir=true",
            new StreamContent(archive), token)) put.EnsureSuccessStatusCode();
        using (var delete = await SendAsync(HttpMethod.Delete, "/v1.41/containers/" + helper + "?v=false", null, token)) delete.EnsureSuccessStatusCode();
        using (var absent = await SendAsync(HttpMethod.Get, "/v1.41/containers/" + helper + "/json", null, token))
            if (absent.StatusCode != HttpStatusCode.NotFound) throw new OpenShellStateConflictException("bootstrap_helper_cleanup_unconfirmed");
        // Delivery only becomes usable after exact helper cleanup. Atomic replacement contains no
        // different identity or secret; readers reject the initial false record on interruption.
        var staged = path + ".delivered";
        await using (var file = new FileStream(staged, new FileStreamOptions { Mode = FileMode.CreateNew,
            Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            await JsonSerializer.SerializeAsync(file, new Custody(plan.Hash, capability, nonce, true), cancellationToken: token);
        File.Move(staged, path, overwrite: true);
    }

    public string ReadCapability(string planHash, Guid launch, Guid epoch)
    {
        var record = JsonSerializer.Deserialize<Custody>(File.ReadAllText(Path.Combine(DirectoryFor(launch, epoch), "custody.json")));
        if (record is not { Delivered: true } || record.PlanHash != planHash || record.Capability.Length != 64
            || !record.Capability.All(char.IsAsciiHexDigitLower)) throw new OpenShellStateConflictException("bootstrap_artifact_unconfirmed");
        return record.Capability;
    }

    public SandboxSpec Materialize(OpenShellLaunchPlan plan, Guid launch, Guid epoch, DateTime deadline)
    {
        _ = ReadCapability(plan.Hash, launch, epoch); // Never serialize the capability itself.
        var spec = plan.MaterializeBootstrap(launch, epoch, deadline);
        // Pinned Docker driver envelope. Do not replace or reinterpret caller driver settings.
        // This first delivery path is intentionally restricted to an otherwise empty driver block.
        if (spec.Template.DriverConfig is { Fields.Count: > 0 }) throw new OpenShellStateConflictException("bootstrap_driver_mount_unqualified");
        spec.Template.DriverConfig = Struct.Parser.ParseJson(JsonSerializer.Serialize(new
        { docker = new { mounts = new[] { new { type = "volume", source = VolumeName(launch, epoch), target = "/app/generated", read_only = true } } } }));
        return spec;
    }
    private static JsonContent EngineJson<T>(T value) => JsonContent.Create(value, options: new JsonSerializerOptions { PropertyNamingPolicy = null });
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        return await engine.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).WaitAsync(ct);
    }
}
