using Fleet.Orchestrator.Services;
using Google.Protobuf.WellKnownTypes;
using Openshell.Sandbox.V1;
using Openshell.V1;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellPolicyCompilerTests
{
    [Fact]
    public void CompiledPlan_OwnsRequestBytes_AndHashIncludesInputs()
    {
        var input = Spec();
        var compiler = new OpenShellPolicyCompiler();
        var plan = compiler.Compile("gateway1", "workspace1", input, new string('b', 64), Expected(), "https://ingress.example.invalid:17443/identity/ready", "/app/generated/bootstrap.capability", Admission());
        Assert.Equal(plan, compiler.Compile("gateway1", "workspace1", input, new string('b', 64), Expected(), "https://ingress.example.invalid:17443/identity/ready", "/app/generated/bootstrap.capability", Admission()));
        Assert.Equal(64, plan.Hash.Length);
        input.Template.Image = "altered";
        Assert.Contains("@sha256:", plan.ReadSpec().Template.Image);
        Assert.NotEqual(plan.Hash, (plan with { InputHash = new string('c', 64) }).Hash);
        var copy = plan.ReadSpec(); copy.Providers.Clear();
        Assert.Single(plan.ReadSpec().Providers);
    }

    [Theory]
    [InlineData("driver")]
    [InlineData("image")]
    [InlineData("root")]
    [InlineData("landlock")]
    [InlineData("intake")]
    [InlineData("provider")]
    [InlineData("restart")]
    [InlineData("filesystem")]
    [InlineData("direct")]
    [InlineData("route")]
    [InlineData("ip")]
    [InlineData("middleware")]
    [InlineData("host")]
    public void UnsupportedBootstrapInputs_AreDenied(string fault)
    {
        var spec = Spec();
        var endpoint = spec.Policy.NetworkPolicies["identity"].Endpoints[0];
        switch (fault)
        {
            case "driver": spec.Template.DriverConfig = new Struct { Fields = { ["docker"] = Value.ForString("unqualified") } }; break;
            case "image": spec.Template.Image = "image:latest"; break;
            case "root": spec.Policy.Process.RunAsUser = "0"; break;
            case "landlock": spec.Policy.Landlock.Compatibility = "best_effort"; break;
            case "intake": spec.Command.RemoveAt(2); break;
            case "provider": spec.Providers.Clear(); break;
            case "restart": spec.RestartPolicy = SandboxRestartPolicy.Unspecified; break;
            case "filesystem": spec.Policy.Filesystem.ReadWrite.Add("/"); break;
            case "direct": endpoint.Tls = NetworkTlsMode.Skip; break;
            case "route": endpoint.Path = "/mcp"; break;
            case "ip": endpoint.AllowedIps.Clear(); break;
            case "middleware": spec.Policy.NetworkMiddlewares["identity"].OnError = "fail_open"; break;
            case "host": spec.Policy.NetworkMiddlewares["identity"].Endpoints.Include[0] = "other.example.invalid"; break;
        }
        Assert.Throws<OpenShellStateConflictException>(() => new OpenShellPolicyCompiler().Compile("gateway1", "workspace1", spec, new string('b', 64), Expected(), "https://ingress.example.invalid:17443/identity/ready", "/app/generated/bootstrap.capability", Admission()));
    }

    internal static OpenShellLaunchPlan Plan() => new OpenShellPolicyCompiler().Compile("gateway1", "workspace1", Spec(), new string('b', 64), Expected(), "https://ingress.example.invalid:17443/identity/ready", "/app/generated/bootstrap.capability", Admission());
    internal static OpenShellExpectedProvider[] Expected() => [new("provider1", "provider-id1", 1, 2, 3, new string('a', 64))];
    internal static OpenShellResourceAdmission Admission() => new("owned-approval1",
        "v1:{\"allow_driver_config\":true,\"resource_admission\":{\"enabled\":true,\"required_labels\":{\"openshell.ai/sandbox-attachable\":\"true\",\"openshell.ai/sandbox-attachable-workspace\":\"${workspace}\"}}}",
        "{\"openshell.ai/sandbox-attachable\":\"true\",\"openshell.ai/sandbox-attachable-workspace\":\"workspace1\"}");
    internal static SandboxSpec Spec() => new()
    {
        Template = new() { Image = "example.invalid/agent@sha256:" + new string('a', 64) },
        RestartPolicy = SandboxRestartPolicy.Never, Providers = { "provider1" },
        Command = { "dotnet", "/app/Fleet.Agent.dll", "--OpenShell:Bootstrap=true" },
        Policy = new()
        {
            Version = 1, Process = new() { RunAsUser = "1000", RunAsGroup = "1000" },
            Landlock = new() { Compatibility = "hard_requirement" }, Filesystem = new() { ReadOnly = { "/app" }, ReadWrite = { "/workspace" } },
            NetworkPolicies = { ["identity"] = new()
            {
                Binaries = { new NetworkBinary { Path = "/usr/bin/dotnet" } },
                Endpoints = { new NetworkEndpoint
                {
                    Host = "ingress.example.invalid", Port = 17443, AllowedIps = { "192.0.2.10" }, Protocol = "rest",
                    Path = "/identity/ready", Enforcement = NetworkEnforcementMode.Enforce,
                    Rules = { new L7Rule { Allow = new() { Method = "POST", Path = "/identity/ready" } } },
                } },
            } },
            NetworkMiddlewares = { ["identity"] = new()
            {
                Middleware = "identity", OnError = "fail_closed", Endpoints = new() { Include = { "ingress.example.invalid" } },
                Config = new Struct { Fields = { ["service"] = Value.ForString("orchestrator") } },
            } },
        },
    };
}
