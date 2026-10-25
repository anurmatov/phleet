using System.Security.Cryptography;
using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

public sealed record ProcessQualificationPlan(
    string ManifestSha256,
    string SourceCommit,
    string SourceTree,
    string DockerEngineId,
    string TemporalServerVersion,
    IReadOnlyDictionary<string, string> ImageIds,
    IReadOnlyDictionary<string, string> FileSha256s,
    IReadOnlyDictionary<string, string> NestedContainerIds);

public sealed record ProcessQualificationReceipt(
    int SchemaVersion,
    string Kind,
    string ManifestSha256,
    string SourceCommit,
    string SourceTree,
    string DockerEngineId,
    string TemporalServerVersion,
    IReadOnlyDictionary<string, string> ImageIds,
    IReadOnlyDictionary<string, string> FileSha256s,
    IReadOnlyList<ProcessScenarioResult> Scenarios,
    int Passed,
    int Failed,
    int Blocked,
    int Unrun);

public sealed record ProcessReceiptReadback(ProcessQualificationPlan Target, ProcessReceiptObservation Receipt);

public sealed class ProcessQualificationDriver
{
    public const string ReceiptKind = "workflow-outcomes-process-qualification-receipt";

    private readonly string _manifestPath;
    private readonly byte[] _manifestBytes;
    private readonly string _manifestSha256;
    private readonly IProcessCommandRunner _runner;
    private readonly IDockerEngineReader _docker;
    private readonly ITemporalMetadataReader _temporal;
    private readonly Func<string, string, IProcessReceiptReader> _receiptFactory;

    public ProcessQualificationDriver(
        string manifestPath,
        IProcessCommandRunner runner,
        IDockerEngineReader docker,
        ITemporalMetadataReader temporal,
        string? expectedManifestSha256 = null,
        Func<string, string, IProcessReceiptReader>? receiptFactory = null)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            throw new QualificationException("manifest file does not exist");
        _manifestPath = Path.GetFullPath(manifestPath);
        if (new FileInfo(_manifestPath).Length > 1024 * 1024)
            throw new QualificationException("manifest exceeds the size bound");
        _manifestBytes = File.ReadAllBytes(_manifestPath);
        _manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(_manifestBytes));
        if (expectedManifestSha256 is not null && expectedManifestSha256 != _manifestSha256)
            throw new QualificationException("manifest changed while binding clients");
        OwnedPaths.File(Path.GetDirectoryName(_manifestPath)!, Path.GetFileName(_manifestPath), _manifestSha256);
        _runner = runner;
        _docker = docker;
        _temporal = temporal;
        _receiptFactory = receiptFactory ?? ((origin, token) => new ProcessReceiptClient(origin,token));
    }

    public async Task<ProcessQualificationPlan> ValidateAsync(CancellationToken ct = default)
    {
        var manifest = await ParseManifestAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(manifest.MaxDurationSeconds));
        ct = deadline.Token;
        var directory = Path.GetDirectoryName(_manifestPath)!;
        var planner = new CommandPlanner(directory);

        var manifestHash = _manifestSha256;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.ConfigFiles.Concat(manifest.PolicyFiles).Concat(manifest.Executables.Values).Concat([new HashedFile(manifest.Runtime.SeedSql, manifest.Runtime.SeedSqlSha256)]))
        {
            if (files.ContainsKey(file.Path)) throw new QualificationException("duplicate hashed file '" + file.Path + "'");
            files[file.Path] = file.Sha256;
        }
        foreach (var pair in files)
        {
            OwnedPaths.File(directory, pair.Key, pair.Value);
        }

        DockerConfigurationBinding.Verify(manifest, directory);
        var nested = NestedContainerBindings.Load(manifest, directory);

        var source = await _runner.RunAsync(planner.Build(manifest, "git-source"), ct);
        RequireSuccess(source, "git-source");
        var sourceParts = source.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sourceParts.Length != 2
            || !string.Equals(sourceParts[0], manifest.Target.SourceCommit, StringComparison.Ordinal)
            || !string.Equals(sourceParts[1], manifest.Target.SourceTree, StringComparison.Ordinal))
            throw new QualificationException("source commit/tree does not match the immutable target");

        var status = await _runner.RunAsync(planner.Build(manifest, "git-status"), ct);
        RequireSuccess(status, "git-status");
        if (!string.IsNullOrWhiteSpace(status.Stdout))
            throw new QualificationException("source checkout is not clean");
        if (!manifest.ConfigFiles.Any(file => file.Path == manifest.Docker.ConfigFile))
            throw new QualificationException("Docker context/config must have an explicit pinned hash");

        var engine = await _docker.GetEngineInfoAsync(ct);
        if (!string.Equals(engine.EngineId, manifest.Docker.EngineId, StringComparison.Ordinal)
            || engine.ServerVersion != manifest.Docker.ServerVersion)
            throw new QualificationException("Docker engine ID does not match the immutable target");

        var imageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in manifest.Target.Images.Keys)
        {
            var image = await _docker.GetImageAsync(manifest.Target.Images[role].Reference, ct);
            if (!image.RepoDigests.Contains(manifest.Target.Images[role].Reference, StringComparer.Ordinal)
                || image.BuildCommit != manifest.Target.SourceCommit)
                throw new QualificationException("image digest does not match the immutable target for " + role);
            imageIds[role] = image.ImageId;
        }

        var nestedIds = await nested.VerifyAsync(manifest, directory, imageIds, _docker, ct);
        var temporal = await _temporal.GetSystemInfoAsync(ct);
        if (temporal.ServerVersion != manifest.Temporal.ServerVersion)
            throw new QualificationException("Temporal GetSystemInfo did not return a server version");

        var codex = await _runner.RunAsync(planner.Build(manifest, "codex-version"), ct);
        RequireSuccess(codex, "codex-version");
        if (codex.Stdout.Trim() != "codex-cli 0.159.2")
            throw new QualificationException("native CLI version is not the pinned 0.159.2 protocol build");

        foreach (var pair in files) OwnedPaths.File(directory, pair.Key, pair.Value);
        if (manifestHash != Sha256File(_manifestPath)) throw new QualificationException("manifest changed during validation");
        return new ProcessQualificationPlan(
            manifestHash, manifest.Target.SourceCommit, manifest.Target.SourceTree,
            engine.EngineId, temporal.ServerVersion, imageIds, files, nestedIds);
    }

    /// <summary>
    /// Read-only API collector, not a campaign result. The endpoint and exact scenario are bound
    /// to the unchanged target before and after GET; it never posts delivery/ack/decision.
    /// </summary>
    public async Task<ProcessReceiptReadback> ReadReceiptAsync(
        string approval, string tokenFile, CancellationToken ct = default)
    {
        var manifest = await ParseManifestAsync(ct);
        if (approval != manifest.Target.ApprovalRef)
            throw new QualificationException("receipt approval does not match the target");
        if (!manifest.AllowedOperations.Contains("receipt-read"))
            throw new QualificationException("receipt-read is not in allowedOperations");
        if (!OperatingSystem.IsLinux()) throw new QualificationBlockedException("Linux receipt custody is required");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(manifest.MaxDurationSeconds));
        ct = deadline.Token;
        var pins = manifest.ConfigFiles.Where(p => p.Path == tokenFile).ToArray();
        if (pins.Length != 1) throw new QualificationException("receipt token must be an exact hash-pinned config file");
        var before = await ValidateAsync(ct);
        var directory = Path.GetDirectoryName(_manifestPath)!;
        var path = OwnedPaths.File(directory,tokenFile,pins[0].Sha256);
        if (new FileInfo(path).Length is < 1 or > 4096) throw new QualificationException("receipt token file exceeds limit");
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute
                |UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute|UnixFileMode.UserExecute)) != 0
                || (mode & UnixFileMode.UserRead) == 0)
                throw new QualificationException("receipt token file must be owner-readable and private");
        }
        await using var tokenStream = File.OpenRead(path);
        using var tokenBytes = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await tokenStream.ReadAsync(chunk,ct)) != 0)
        {
            if (tokenBytes.Length + read > 4096) throw new QualificationException("receipt token file exceeds limit");
            tokenBytes.Write(chunk,0,read);
        }
        var bytes = tokenBytes.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != pins[0].Sha256)
            throw new QualificationException("receipt token changed before read");
        var token = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd((char)13,(char)10);
        using var reader = _receiptFactory(manifest.Runtime.OrchestratorBaseUrl,token);
        if (reader.Origin != new Uri(manifest.Runtime.OrchestratorBaseUrl).GetLeftPart(UriPartial.Authority))
            throw new QualificationException("receipt reader origin differs from target");
        var receipt = await reader.ReadAsync(manifest.Runtime,ct);
        var after = await ValidateAsync(ct);
        if (before.ManifestSha256 != after.ManifestSha256 || before.DockerEngineId != after.DockerEngineId
            || before.SourceCommit != after.SourceCommit || before.SourceTree != after.SourceTree
            || before.TemporalServerVersion != after.TemporalServerVersion
            || !Same(before.ImageIds,after.ImageIds) || !Same(before.FileSha256s,after.FileSha256s)
            || !Same(before.NestedContainerIds,after.NestedContainerIds))
            throw new QualificationException("receipt target changed during collection");
        ct.ThrowIfCancellationRequested();
        return new ProcessReceiptReadback(after,receipt);
    }

    private static bool Same(IReadOnlyDictionary<string,string> left, IReadOnlyDictionary<string,string> right) =>
        left.Count == right.Count && left.All(p => right.TryGetValue(p.Key,out var value) && value == p.Value);

    public async Task<ProcessQualificationReceipt> RunAsync(
        string approval,
        string observationsPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approval))
            throw new QualificationException("approval is required");
        if (string.IsNullOrWhiteSpace(observationsPath) || !File.Exists(observationsPath))
            throw new QualificationException("observations file does not exist");

        var manifest = await ParseManifestAsync(ct);
        if (!string.Equals(approval, manifest.Target.ApprovalRef, StringComparison.Ordinal))
            throw new QualificationException("approval does not match the immutable target approvalRef");
        // A supplied observation document is not evidence that the shipped daemon, native CLI,
        // shell, API and broker ran. The connected collectors and cleanup registry are incomplete.
        // Refuse before even read-only infrastructure probes, rather than issue a false PASS.
        throw new QualificationBlockedException("connected process collectors and exact-owned cleanup are not implemented");
    }

    public async Task<ProcessQualificationManifest> ParseManifestAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        OwnedPaths.File(Path.GetDirectoryName(_manifestPath)!, Path.GetFileName(_manifestPath), _manifestSha256);
        await using var stream = new MemoryStream(_manifestBytes, writable: false);
        return QualificationManifestParser.Parse(stream);
    }

    public static async Task WriteReceiptAsync(ProcessQualificationReceipt receipt, string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, receipt, options);
    }

    private static void RequireSuccess(ProcessCommandResult result, string operation)
    {
        if (result.ExitCode != 0 || result.TimedOut)
            throw new QualificationException(operation + " failed with exit code " + result.ExitCode);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
