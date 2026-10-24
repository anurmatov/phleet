using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Openshell.V1;

namespace Fleet.Orchestrator.Services;

/// <summary>Immutable serialized request, never shared mutable protobufs or credentials.</summary>
public sealed record OpenShellLaunchPlan(string GatewayId, string Workspace, string SpecBase64,
    string InputHash, string PolicyHash, string ImageHash, string ExpectedProvidersJson = "[]", string BootstrapIngress = "", string BootstrapCapabilityFile = "", string ResourceAdmissionJson = "")
{
    public SandboxSpec MaterializeBootstrap(Guid launchId, Guid epoch, DateTime deadline)
    {
        if (launchId == Guid.Empty || epoch == Guid.Empty || deadline.Kind != DateTimeKind.Utc || deadline <= DateTime.UtcNow
            || deadline > DateTime.UtcNow.AddSeconds(60) || !Uri.TryCreate(BootstrapIngress, UriKind.Absolute, out var ingress)
            || ingress.Scheme != "https" || ingress.AbsolutePath != "/identity/ready" || ingress.Query != "" || ingress.Fragment != "" || ingress.UserInfo != ""
            || BootstrapCapabilityFile != "/app/generated/bootstrap.capability")
            throw new OpenShellStateConflictException("bootstrap_launch_inputs_invalid");
        var spec = ReadSpec();
        spec.Command.Add("--OpenShell:BootstrapIngress=" + BootstrapIngress);
        spec.Command.Add("--OpenShell:LaunchId=" + launchId.ToString("D"));
        spec.Command.Add("--OpenShell:IdentityEpoch=" + epoch.ToString("D"));
        spec.Command.Add("--OpenShell:PlanHash=" + Hash);
        spec.Command.Add("--OpenShell:Deadline=" + deadline.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        spec.Command.Add("--OpenShell:BootstrapCapabilityFile=" + BootstrapCapabilityFile);
        return spec;
    }
    public OpenShellResourceAdmission ReadResourceAdmission() => System.Text.Json.JsonSerializer.Deserialize<OpenShellResourceAdmission>(ResourceAdmissionJson)
        ?? throw new OpenShellStateConflictException("bootstrap_resource_admission_missing");
    public OpenShellExpectedProvider[] ReadExpectedProviders() => System.Text.Json.JsonSerializer.Deserialize<OpenShellExpectedProvider[]>(ExpectedProvidersJson)
        ?? throw new OpenShellStateConflictException("expected_providers_missing");
    public SandboxSpec ReadSpec() => SandboxSpec.Parser.ParseFrom(Convert.FromBase64String(SpecBase64));
    [System.Text.Json.Serialization.JsonIgnore]
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(this))));
}

/// <summary>
/// Conservative bootstrap compiler. This is not upstream lint or image/mount qualification.
/// Those independent prerequisites must succeed before the preparation driver stops anything.
/// </summary>
public sealed record OpenShellExpectedProvider(string Name, string ProviderId, ulong ResourceVersion,
    ulong EnvironmentRevision, ulong ConfigurationRevision, string PolicyHash)
{
    public bool Matches(ProviderDesiredIdentity desired) => desired.ProviderId == ProviderId
        && desired.ProviderResourceVersion == ResourceVersion && desired.ProviderEnvRevision == EnvironmentRevision
        && desired.ConfigRevision == ConfigurationRevision && desired.PolicyHash == PolicyHash;
}

public sealed class OpenShellPolicyCompiler
{
    public OpenShellLaunchPlan Compile(string gatewayId, string workspace, SandboxSpec spec, string inputHash, IReadOnlyList<OpenShellExpectedProvider>? expectedProviders = null, string bootstrapIngress = "", string bootstrapCapabilityFile = "", OpenShellResourceAdmission? resourceAdmission = null)
    {
        if (!Opaque(gatewayId) || !Opaque(workspace) || !Hash(inputHash)) throw Blocked();
        if (resourceAdmission is null) throw new OpenShellStateConflictException("bootstrap_resource_admission_missing");
        _ = resourceAdmission.Validate(workspace);
        var copy = spec.Clone();
        var policy = copy.Policy;
        if (copy.Template is null || !copy.Template.Image.Contains("@sha256:", StringComparison.Ordinal)
            || !Hash(copy.Template.Image.Split("@sha256:").Last())
            || copy.Template.DriverConfig is { Fields.Count: > 0 }
            || copy.Template.Environment.Count != 0 || copy.Environment.Count != 0 || copy.Template.AgentSocket.Length != 0
            || copy.RestartPolicy != SandboxRestartPolicy.Never || copy.Tty || copy.Providers.Count == 0
            || copy.Providers.Count != copy.Providers.Distinct(StringComparer.Ordinal).Count()
            || copy.Providers.Any(p => !Opaque(p)) || copy.ProviderAttachmentEpoch.Length != 0
            || !copy.Command.SequenceEqual(new[] { "dotnet", "/app/Fleet.Agent.dll", "--OpenShell:Bootstrap=true" })
            || policy is null || policy.Version != 1 || policy.Landlock?.Compatibility != "hard_requirement"
            || policy.Process is null || policy.Process.RunAsUser != "1000" || policy.Process.RunAsGroup != "1000"
            || policy.Filesystem is null || policy.Filesystem.IncludeWorkdir
            || policy.Filesystem.ReadWrite.Any(p => !PathSafe(p) || p == "/")
            || policy.Filesystem.ReadOnly.Any(p => !PathSafe(p))
            || policy.Filesystem.ReadOnly.Count + policy.Filesystem.ReadWrite.Count > 256
            || policy.NetworkPolicies.Count == 0 || policy.NetworkMiddlewares.Count == 0)
            throw Blocked();
        foreach (var rule in policy.NetworkPolicies.Values)
        {
            if (rule.Endpoints.Count == 0 || rule.Binaries.Count == 0) throw Blocked();
            foreach (var endpoint in rule.Endpoints)
                OpenShellEndpointCompiler.ValidateBootstrap(endpoint);
        }
        var hosts = policy.NetworkPolicies.Values.SelectMany(r => r.Endpoints).Select(e => e.Host).Distinct(StringComparer.Ordinal).ToArray();
        if (hosts.Length != 1 || policy.NetworkMiddlewares.Count != 1) throw Blocked();
        foreach (var middleware in policy.NetworkMiddlewares.Values)
            if (middleware.OnError != "fail_closed" || middleware.Middleware.Length == 0
                || middleware.Endpoints is null || middleware.Endpoints.Exclude.Count != 0
                || middleware.Endpoints.Include.Count != 1 || middleware.Endpoints.Include[0] != hosts[0]
                || middleware.Config?.Fields.Count != 1
                || !middleware.Config.Fields.TryGetValue("service", out var service)
                || service.StringValue != "orchestrator") throw Blocked();
        if (expectedProviders is null || expectedProviders.Count != copy.Providers.Count
            || expectedProviders.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != expectedProviders.Count
            || expectedProviders.Any(p => !copy.Providers.Contains(p.Name) || !Opaque(p.ProviderId) || p.ResourceVersion == 0
                || !Hash(p.PolicyHash))) throw new OpenShellStateConflictException("expected_providers_missing_or_invalid");
        if (!Uri.TryCreate(bootstrapIngress, UriKind.Absolute, out var bootstrap) || bootstrap.Scheme != "https"
            || bootstrap.AbsolutePath != "/identity/ready" || bootstrap.UserInfo != "" || bootstrap.Query != "" || bootstrap.Fragment != ""
            || hosts[0] != bootstrap.Host || policy.NetworkPolicies.Values.SelectMany(r => r.Endpoints).Any(e => e.Port != bootstrap.Port)
            || bootstrapCapabilityFile != "/app/generated/bootstrap.capability") throw Blocked();
        var bytes = copy.ToByteArray();
        if (bytes.Length > 128 * 1024) throw Blocked();
        return new(gatewayId, workspace, Convert.ToBase64String(bytes), inputHash,
            Convert.ToHexStringLower(SHA256.HashData(policy.ToByteArray())), copy.Template.Image.Split("@sha256:").Last(),
            System.Text.Json.JsonSerializer.Serialize(expectedProviders.OrderBy(p => p.Name, StringComparer.Ordinal)), bootstrapIngress, bootstrapCapabilityFile, System.Text.Json.JsonSerializer.Serialize(resourceAdmission));
    }
    private static bool PathSafe(string p) => p.StartsWith('/') && p.Length <= 4096
        && !p.Any(char.IsControl) && !p.Split('/').Any(x => x is ".." or ".") && !p.Contains("//");
    private static bool Opaque(string p) => p.Length is > 0 and <= 128 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Hash(string p) => p.Length == 64 && p.All(char.IsAsciiHexDigitLower);
    private static OpenShellStateConflictException Blocked() => new("bootstrap_policy_unsupported");
}
