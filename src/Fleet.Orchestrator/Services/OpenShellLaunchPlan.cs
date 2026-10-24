using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Openshell.V1;

namespace Fleet.Orchestrator.Services;

/// <summary>Immutable serialized request, never shared mutable protobufs or credentials.</summary>
public sealed record OpenShellLaunchPlan(string GatewayId, string Workspace, string SpecBase64,
    string InputHash, string PolicyHash, string ImageHash)
{
    public SandboxSpec ReadSpec() => SandboxSpec.Parser.ParseFrom(Convert.FromBase64String(SpecBase64));
    [System.Text.Json.Serialization.JsonIgnore]
    public string Hash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(this))));
}

/// <summary>
/// Conservative bootstrap compiler. This is not upstream lint or image/mount qualification.
/// Those independent prerequisites must succeed before the preparation driver stops anything.
/// </summary>
public sealed class OpenShellPolicyCompiler
{
    public OpenShellLaunchPlan Compile(string gatewayId, string workspace, SandboxSpec spec, string inputHash)
    {
        if (!Opaque(gatewayId) || !Opaque(workspace) || !Hash(inputHash)) throw Blocked();
        var copy = spec.Clone();
        var policy = copy.Policy;
        if (copy.Template is null || !copy.Template.Image.Contains("@sha256:", StringComparison.Ordinal)
            || !Hash(copy.Template.Image.Split("@sha256:").Last())
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
        var bytes = copy.ToByteArray();
        if (bytes.Length > 128 * 1024) throw Blocked();
        return new(gatewayId, workspace, Convert.ToBase64String(bytes), inputHash,
            Convert.ToHexStringLower(SHA256.HashData(policy.ToByteArray())), copy.Template.Image.Split("@sha256:").Last());
    }
    private static bool PathSafe(string p) => p.StartsWith('/') && p.Length <= 4096
        && !p.Any(char.IsControl) && !p.Split('/').Any(x => x is ".." or ".") && !p.Contains("//");
    private static bool Opaque(string p) => p.Length is > 0 and <= 128 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static bool Hash(string p) => p.Length == 64 && p.All(char.IsAsciiHexDigitLower);
    private static OpenShellStateConflictException Blocked() => new("bootstrap_policy_unsupported");
}
