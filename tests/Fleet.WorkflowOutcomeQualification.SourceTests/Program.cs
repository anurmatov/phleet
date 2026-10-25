using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.WorkflowOutcomeQualification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

var passed = 0;
var failed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Require(bool condition) { if (!condition) throw new InvalidOperationException("source assertion failed"); }
var defaults = new Dictionary<string, string> {
    ["Docker__SocketPath"]="/run/synthetic.sock",
    ["TemporalBridge__TemporalAddress"]="127.0.0.1:7233",
    ["TemporalBridge__Namespaces__0"]="synthetic",
    ["TemporalBridge__OrchestratorUrl"]="http://127.0.0.1:4567",
    ["RabbitMq__Host"]="amqp://127.0.0.1:5672/",
    ["Agent__Provider"]="codex",
    ["Agent__Model"]="ollama/synthetic",
    ["Agent__CodexOssBaseUrl"]="http://127.0.0.1:4321/v1"
};
foreach (var key in defaults.Keys)
    Test("provider_canonical_alias_" + key, () => {
        var values = new Dictionary<string,string>(defaults);
        var value = values[key]; values.Remove(key);
        var alias = key.ToLowerInvariant().Replace("__", ":");
        values.Add(alias,value);
        using var config = Configuration(values);
        Require(config[key.Replace("__", ":")] == value);
        var observed = Parse(values);
        var hashes = Hashes(observed);
        var selected = key.Replace("__", ":");
        Require(hashes.TryGetValue(selected,out var actual));
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        Require(actual == expected);
    });
foreach (var alias in new[] {
    "temporalbridge__namespaces__1", "TEMPORALBRIDGE:NAMESPACES:1",
    "TemporalBridge:Namespaces__1", "temporalbridge__namespaces:0:extra",
    "TemporalBridge__Namespaces__00"
})
    Test("provider_foreign_namespace_refused_" + alias, () => {
        var values = new Dictionary<string,string>(defaults) { [alias]="foreign-synthetic" };
        using var config = Configuration(values);
        Require(config[alias.Replace("__", ":")] == "foreign-synthetic");
        if (alias.EndsWith("1", StringComparison.Ordinal))
            Require(config.GetSection("TemporalBridge:Namespaces").Get<string[]>()!.Contains("foreign-synthetic"));
        Refuses(values);
    });
foreach (var alias in new[] {
    "agent__provider", "Agent:Provider", "AGENT:PROVIDER"
})
    Test("provider_alias_collision_refused_" + alias, () => {
        var values = new Dictionary<string,string>(defaults) { [alias]="foreign-synthetic" };
        using var config = Configuration(values);
        Require(config.GetSection("Agent").GetChildren().Count(s => s.Key.Equals("Provider",StringComparison.OrdinalIgnoreCase)) == 1);
        // Provider enumeration order may choose either alias; the guard must never choose.
        Refuses(values);
    });
Test("provider_default_configuration_accepted", () => {
    using var config = Configuration(defaults);
    Require(config.GetSection("TemporalBridge:Namespaces").GetChildren().Single().Key == "0");
    Require(config.GetSection("TemporalBridge:Namespaces").Get<string[]>()!.SequenceEqual(["synthetic"]));
    Require(Hashes(Parse(defaults)).Count == defaults.Count);
});
Console.WriteLine(JsonSerializer.Serialize(new { total=passed+failed, passed, failed, skipped=0 }));
return failed == 0 ? 0 : 1;

static ConfigurationRoot Configuration(Dictionary<string,string> values)
{
    var provider = new SyntheticEnvironmentProvider(values);
    return new ConfigurationRoot([provider]);
}
static ContainerBindingObservation Parse(Dictionary<string,string> values)
{
    var root = JsonSerializer.SerializeToElement(new {
        Id=new string('1',64), Image="sha256:"+new string('9',64),
        Config=new { Env=values.Select(p => p.Key+"="+p.Value).ToArray(),
            Labels=new Dictionary<string,string> { ["org.phleet.qualification.owner"]="synthetic-owner" } },
        HostConfig=new { Privileged=false, NetworkMode="synthetic", PidMode="", IpcMode="" },
        State=new { Running=true, Pid=42, StartedAt="2026-01-01T00:00:00Z" },
        Mounts=Array.Empty<object>(), NetworkSettings=new { Networks=new {} }
    });
    // Exercise the actual production parser without making its internal entrypoint public.
    try {
        return (ContainerBindingObservation)typeof(ContainerBindingObservation)
            .GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[root])!;
    } catch (TargetInvocationException e) when (e.InnerException is QualificationException) {
        throw e.InnerException;
    }
}
static IReadOnlyDictionary<string,string> Hashes(ContainerBindingObservation value) =>
    (IReadOnlyDictionary<string,string>)typeof(ContainerBindingObservation)
        .GetProperty("EnvironmentHashes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
static void Refuses(Dictionary<string,string> values)
{
    try { Parse(values); }
    catch (QualificationException) { return; }
    throw new InvalidOperationException("ambiguous provider configuration accepted");
}
sealed class SyntheticEnvironmentProvider(Dictionary<string,string> values) : EnvironmentVariablesConfigurationProvider()
{
    public override void Load()
    {
        var environment = new Hashtable(StringComparer.Ordinal);
        foreach (var (key,value) in values) environment.Add(key,value);
        // Invoke the real provider's IDictionary loader, without mutating global environment.
        var loader = typeof(EnvironmentVariablesConfigurationProvider).GetMethod("Load",
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(IDictionary)], null)
            ?? throw new InvalidOperationException("platform provider loader unavailable");
        loader.Invoke(this, [environment]);
    }
}
