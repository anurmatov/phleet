using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #492 thin adapter: synthetic stock CLI, broker management API, host view and store. These are
/// source checks of dispatch, ordering and failure handling, not runtime qualification (#493).
/// </summary>
public sealed class ManagedStockRuntimeAdapterTests : IDisposable
{
    private const string Key = "sk-synthetic-value-never-in-argv";
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-stock-").FullName;
    private readonly FakeCli _cli = new();
    private readonly FakeStore _store = new();
    private readonly FakeHost _host = new();
    private readonly FakeBroker _broker = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── apply ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Docker_to_openshell_apply_stops_docker_then_revoked_allocation_uuid_activation_and_one_start()
    {
        var agent = _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        var dockerStops = 0;
        var runtime = Runtime(stopDocker: (_, _) => { dockerStops++; return Task.FromResult<string?>(null); });

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("applied_openshell", result.Code);
        Assert.Equal(1, dockerStops);
        var stored = _store.Get("agent1");
        Assert.Equal("openshell", stored.AppliedRuntimeKind);
        Assert.Equal(agent.SandboxPolicyVersionId, stored.AppliedSandboxPolicyVersionId);
        var record = ManagedRecord.Parse(stored.AppliedRuntimeBinding);
        Assert.Equal("applied", record.State);
        Assert.Null(record.Candidate);
        Assert.Equal(_cli.Sandboxes[record.Applied!.SandboxName].Id, record.Applied.SandboxId);

        var verbs = _cli.Calls.Select(c => string.Join(' ', c.Args.Take(2))).ToList();
        Assert.True(verbs.IndexOf("sandbox create") < verbs.IndexOf("sandbox upload"));
        Assert.True(verbs.IndexOf("sandbox upload") < verbs.IndexOf("sandbox start"));
        Assert.Single(verbs, v => v == "sandbox start");
        var create = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Contains("--no-auto-providers", create.Args);
        Assert.Equal("never", create.Args[create.Args.IndexOf("--restart-policy") + 1]);
        // The real key travels only in the CLI's environment, never in any argument list.
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Any(a => a.Contains(Key, StringComparison.Ordinal)));
        Assert.Contains(_cli.Calls, c => c.SecretEnv?.GetValueOrDefault("OPENAI_API_KEY") == Key);

        // Outside record ends active and bound to the actual UUID; the workload never gets the private key path's key.
        var authority = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "managed", "authority", "agent1.json")))!;
        Assert.Equal("active", authority["State"]!.GetValue<string>());
        Assert.Equal(record.Applied.SandboxId, authority["SandboxId"]!.GetValue<string>());
        // Exactly the epoch vhost, three users and three permissions; never the default vhost.
        Assert.Equal(7, _broker.Puts.Count);
        Assert.DoesNotContain(_broker.Puts, p => p.Contains("/api/vhosts/%2F") || p.Contains("/api/permissions/%2F/"));
    }

    [Fact]
    public async Task Pending_docker_desire_still_dispatches_start_to_the_applied_openshell_binding()
    {
        var binding = Binding("fleet-agent1-aaaaaaaa", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _store.Add(ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));

        var tool = await Runtime().DispatchToolAsync("agent1", "start");

        Assert.Equal("started", tool);
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "start", binding.SandboxName]));
    }

    [Fact]
    public async Task OpenShell_to_docker_apply_revokes_every_leg_then_hands_back_to_docker()
    {
        var binding = Binding("fleet-agent1-bbbbbbbb", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        WriteAuthority("active");

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("applied_docker", result.Code);
        Assert.Equal("docker", _store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("revoked", JsonNode.Parse(File.ReadAllText(AuthorityPath()))!["State"]!.GetValue<string>());
        Assert.Contains(_cli.Calls, c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "detach"]));
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Equal(6, _broker.Deletes.Count); // permission and user for each principal; no live connections
    }

    [Fact]
    public async Task Failed_broker_and_hung_detach_never_suppress_stop_and_hold_replacement()
    {
        var binding = Binding("fleet-agent1-cccccccc", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _cli.Hang.Add("detach");
        _broker.FailDeletes = true;
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = Runtime();

        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.False(revoke.Ok);
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Contains(revoke.Errors, e => e.StartsWith("broker:", StringComparison.Ordinal));
        Assert.Contains(revoke.Errors, e => e.StartsWith("detach:", StringComparison.Ordinal) && e.EndsWith("timeout", StringComparison.Ordinal));
        Assert.Equal("needs_attention", ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).State);

        var apply = await runtime.ApplyAsync("agent1", default);
        Assert.Equal("needs_attention", apply.Code);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
    }

    [Fact]
    public async Task Concurrent_writer_gets_409_before_any_external_action()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        _store.RejectNextSwap = true;

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal((409, "managed_operation_conflict"), (result.Status, result.Code));
        Assert.Empty(_cli.Calls);
        Assert.Empty(_broker.Puts);
    }

    [Fact]
    public async Task Operation_left_by_an_earlier_process_becomes_needs_attention_without_replay()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker", record: new ManagedRecord { State = "applying", Operation = "apply:other-process" }));

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal("needs_attention", result.Code);
        Assert.Contains("interrupted_apply", result.Errors);
        Assert.Empty(_cli.Calls);
    }

    [Fact]
    public async Task Changed_gateway_generation_holds_start_and_stops_the_owned_binding()
    {
        var binding = Binding("fleet-agent1-dddddddd", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _host.Generation = "gateway-2@later";

        var result = await Runtime().StartAsync("agent1", default);

        Assert.Equal("gateway_generation_changed", result.Code);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "start"]));
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
    }

    [Theory]
    [InlineData("telegram", "managed_route_unsupported:telegram")]
    [InlineData("journal", "managed_route_unsupported:journal")]
    [InlineData("mcp", "managed_route_unsupported:mcp:other")]
    [InlineData("version", "stock_cli_version_mismatch")]
    [InlineData("socket", "openshell_requires_codex_api_key_policy_and_no_socket")]
    public async Task Unsupported_or_missing_prerequisites_refuse_before_any_mutation(string fault, string code)
    {
        var agent = ManagedAgent(desired: "openshell", applied: "docker");
        switch (fault)
        {
            case "telegram": agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" }); break;
            case "journal": agent.JournalEnabled = true; break;
            case "mcp": agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "other", Url = "http://other/mcp", TransportType = "http" }); break;
            case "version": _cli.Version = "openshell 0.2.0"; break;
            case "socket": agent.MountDockerSock = true; break;
        }
        _store.Add(agent);

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal(code, result.Code);
        Assert.Empty(_broker.Puts);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Count > 1 && c.Args[0] == "sandbox");
    }

    [Fact]
    public async Task Cancel_execs_loopback_post_with_escaped_task_id_and_refuses_odd_ids()
    {
        var binding = Binding("fleet-agent1-eeeeeeee", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = Runtime();

        Assert.True((await runtime.CancelAsync("agent1", "task 1", default)).Ok);
        var exec = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "exec"]));
        Assert.Equal("/cancel/task%201", exec.Args[^1]);
        Assert.Contains("127.0.0.1/8080", exec.Args[^3]);

        _cli.Sandboxes[binding.SandboxName] = (Guid.NewGuid().ToString(), "Ready");
        Assert.Equal("sandbox_identity_changed", (await runtime.CancelAsync("agent1", null, default)).Code);
    }

    [Fact]
    public async Task Rollback_preflight_counts_desired_applied_and_unresolved_bindings()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        var other = ManagedStockRuntimeTests.Agent();
        other.Name = other.ShortName = other.ContainerName = "agent2";
        other.AppliedRuntimeBinding = new ManagedRecord { State = "needs_attention" }.Serialize();
        _store.Add(other);

        var json = JsonSerializer.Serialize(await Runtime().RollbackPreflightAsync(default));

        Assert.Equal("{\"desiredOpenShell\":1,\"appliedOpenShell\":0,\"unresolvedBindings\":1}", json);
    }

    // ── broker provisioner ─────────────────────────────────────────────────

    [Fact]
    public void Broker_permissions_are_exact_anchored_and_role_separated()
    {
        var (configure, write, read) = StockBrokerProvisioner.Permissions("workload", "fleet.agent.agent1");
        Assert.Equal(@"^(fleet\.relay|fleet\.group|fleet\.orchestrator|fleet\.agent\.agent1)$", configure);
        Assert.Equal(@"^(fleet\.relay|fleet\.agent\.agent1)$", read);
        Assert.Equal(configure, write);
        var observer = StockBrokerProvisioner.Permissions("observer", "fleet.agent.agent1");
        Assert.Equal(@"^(fleet\.orchestrator\.heartbeats)$", observer.Write);
        Assert.DoesNotContain(".*", configure + write + read + observer.Configure + observer.Read);
    }

    [Fact]
    public async Task Broker_conflict_refuses_without_writes_and_partials_are_reported()
    {
        _broker.Existing.Add("/api/vhosts/");
        var conflict = await Provisioner().CreateEpochAsync(Guid.NewGuid(), "agent1", default);
        Assert.Equal("broker_resource_conflict", conflict.Code);
        Assert.Empty(_broker.Puts);

        _broker.Existing.Clear();
        _broker.FailPutContaining = "/api/permissions/";
        var partial = await Provisioner().CreateEpochAsync(Guid.NewGuid(), "agent1", default);
        Assert.Equal("broker_management_permission_denied", partial.Code);
        Assert.Equal(2, partial.OwnedPartials.Count); // vhost and the first user, never activated
    }

    [Fact]
    public async Task Missing_https_trust_blocks_broker_setup()
    {
        var provisioner = new StockBrokerProvisioner(Config(new() { ["StockOpenShell:BrokerManagementUrl"] = "http://broker.test/" }), _broker.Credentials);
        Assert.Equal("broker_management_https_required", provisioner.DescribePreflightFault());
        Assert.False((await provisioner.CreateEpochAsync(Guid.NewGuid(), "agent1", default)).Ok);
    }

    // ── fixtures ───────────────────────────────────────────────────────────

    private StockOpenShellRuntime Runtime(Func<Agent, CancellationToken, Task<string?>>? stopDocker = null)
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_dir, "managed-authority.json"), new JsonObject
        {
            ["GatewayIssuer"] = "synthetic-gateway", ["GatewayAudience"] = "synthetic-hook",
            ["GatewayJwksPath"] = "/trust/gateway.jwks", ["AssertionPrivateKeyPath"] = "/outside/private.pem",
            ["AssertionPublicKeyPath"] = "/outside/public.pem", ["MemoryHost"] = "memory.example.test", ["BrokerHost"] = "broker.example.test",
        }.ToJsonString());
        var generated = Directory.CreateDirectory(Path.Combine(_dir, "generated")).FullName;
        File.WriteAllText(Path.Combine(generated, "appsettings.json"), "{\"Agent\":{\"Name\":\"agent1\"}}");
        File.WriteAllText(Path.Combine(generated, Fleet.Shared.HarnessDefaults.CodexRootTomlFileName), "model = \"synthetic\"\n");
        return new StockOpenShellRuntime(Config(), _cli, Provisioner(), _store, _host, _broker.Credentials,
            new ServiceCollection().BuildServiceProvider(), NullLogger<StockOpenShellRuntime>.Instance)
        {
            LegTimeout = TimeSpan.FromMilliseconds(400),
            FanoutTimeout = TimeSpan.FromSeconds(5),
            StopSettle = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(10),
            GenerateInputs = (_, _) => Task.FromResult(generated),
            StopDocker = stopDocker ?? ((_, _) => Task.FromResult<string?>(null)),
        };
    }

    private StockBrokerProvisioner Provisioner() => new(Config(), _broker.Credentials) { HandlerFactory = () => _broker };

    private IConfiguration Config(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["StockOpenShell:ManagedEnabled"] = "true",
            ["StockOpenShell:ConfigDir"] = _dir,
            ["StockOpenShell:AgentImage"] = "stock-agent:synthetic",
            ["StockOpenShell:BrokerManagementUrl"] = "https://broker.test:15671/",
            ["StockOpenShell:BrokerCaFile"] = _broker.CaFile(_dir),
            ["StockOpenShell:BrokerAdminUserRef"] = "BROKER_ADMIN_USER",
            ["StockOpenShell:BrokerAdminPasswordRef"] = "BROKER_ADMIN_PASSWORD",
        };
        foreach (var (key, value) in overrides ?? []) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private Agent ManagedAgent(string desired, string applied, ManagedRecord? record = null)
    {
        var agent = ManagedStockRuntimeTests.Agent();
        agent.ShortName = "agent1";
        agent.RuntimeKind = desired;
        agent.AppliedRuntimeKind = applied;
        agent.NativeAuthMode = "api_key";
        agent.ProviderApiKeyEnvRef = "SYNTHETIC_API_KEY";
        agent.SandboxPolicyVersionId = desired == "openshell" ? 1 : null;
        agent.AppliedSandboxPolicyVersionId = applied == "openshell" ? 1 : null;
        agent.AppliedRuntimeBinding = record?.Serialize();
        agent.Tools.Add(new AgentTool { ToolName = "mcp__memory__memory_get" });
        agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "memory", Url = "https://memory.example.test/mcp", TransportType = "http" });
        return agent;
    }

    private ManagedBinding Binding(string name, Guid epoch) =>
        new(name, Guid.NewGuid().ToString(), epoch, "stock-agent:synthetic", 1, new string('b', 64), "api_key",
            "SYNTHETIC_API_KEY", "fleet-agent1-openai", _host.Generation);

    private string AuthorityPath() => Path.Combine(_dir, "managed", "authority", "agent1.json");

    private void WriteAuthority(string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuthorityPath())!);
        File.WriteAllText(AuthorityPath(), new JsonObject { ["State"] = state, ["Agent"] = "agent1" }.ToJsonString());
    }

    internal sealed record Call(List<string> Args, IReadOnlyDictionary<string, string>? SecretEnv);

    private sealed class FakeCli : IStockCli
    {
        public readonly List<Call> Calls = [];
        public readonly Dictionary<string, (string Id, string Phase)> Sandboxes = new();
        public readonly HashSet<string> Hang = [];
        public string Version = "openshell 0.1.5";

        public async Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) Calls.Add(new([.. args], secretEnv));
            if (args.Count > 2 && Hang.Contains(args[2])) { await Task.Delay(Timeout.Infinite, ct); }
            string Name(int index) => args[index];
            return (args[0], args.Count > 1 ? args[1] : "") switch
            {
                ("--version", _) => new(0, Version + "\n"),
                ("provider", "get") => new(1, ""),
                ("provider", _) => new(0, ""),
                ("logs", _) => new(0, "line\n"),
                ("sandbox", "create") => Create(args),
                ("sandbox", "get") => Sandboxes.TryGetValue(Name(2), out var s)
                    ? new(0, JsonSerializer.Serialize(new { id = s.Id, name = Name(2), phase = s.Phase })) : new(1, ""),
                ("sandbox", "start") => Phase(Name(2), "Ready"),
                ("sandbox", "stop") => Phase(Name(2), "Stopped"),
                _ => new(0, ""),
            };
        }

        private StockCliResult Create(IReadOnlyList<string> args)
        {
            var name = args[args.ToList().IndexOf("--name") + 1];
            Sandboxes[name] = (Guid.NewGuid().ToString(), "Error"); // the revoked allocation refused before intake
            return new(0, "");
        }

        private StockCliResult Phase(string name, string phase)
        {
            if (!Sandboxes.TryGetValue(name, out var s)) return new(1, "");
            Sandboxes[name] = (s.Id, phase);
            return new(0, "");
        }
    }

    private sealed class FakeStore : IManagedBindingStore
    {
        private readonly List<Agent> _agents = [];
        public bool RejectNextSwap;

        public Agent Add(Agent agent) { agent.Id = _agents.Count + 1; _agents.Add(agent); return agent; }
        public Agent Get(string name) => _agents.Single(a => a.Name == name);
        public Task<Agent?> LoadAsync(string name, CancellationToken ct) => Task.FromResult(_agents.FirstOrDefault(a => a.Name == name));
        public Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Agent>>(_agents);
        public Task<SandboxPolicyVersion?> LoadPolicyAsync(int id, CancellationToken ct) => Task.FromResult<SandboxPolicyVersion?>(
            id == 1 ? new SandboxPolicyVersion { Id = 1, Version = 1, Yaml = ManagedStockRuntimeTests.PolicyYaml, Sha256 = new string('b', 64) } : null);

        public Task<bool> SwapAsync(int agentId, string? expected, string next, string? appliedKind, int? appliedPolicyVersionId, CancellationToken ct)
        {
            var agent = _agents.Single(a => a.Id == agentId);
            if (RejectNextSwap) { RejectNextSwap = false; return Task.FromResult(false); }
            if (agent.AppliedRuntimeBinding != expected) return Task.FromResult(false);
            agent.AppliedRuntimeBinding = next;
            if (appliedKind is not null) { agent.AppliedRuntimeKind = appliedKind; agent.AppliedSandboxPolicyVersionId = appliedPolicyVersionId; }
            return Task.FromResult(true);
        }
    }

    private sealed class FakeHost : IStockHostView
    {
        public string Generation = "gateway-1@start";
        public Task<string?> GatewayGenerationAsync(CancellationToken ct) => Task.FromResult<string?>(Generation);
        public Task<IReadOnlyList<string>?> RunningSandboxContainersAsync(string sandboxId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private sealed class FakeBroker : HttpMessageHandler
    {
        public readonly List<string> Puts = [];
        public readonly List<string> Deletes = [];
        public readonly HashSet<string> Existing = [];
        public bool FailDeletes;
        public string? FailPutContaining;
        public readonly FakeCredentials Credentials = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpStatusCode status;
            if (request.Method == HttpMethod.Get)
                status = Existing.Any(path.StartsWith) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            else if (request.Method == HttpMethod.Put)
            {
                if (FailPutContaining is not null && path.Contains(FailPutContaining)) status = HttpStatusCode.Forbidden;
                else { lock (Puts) Puts.Add(path); status = HttpStatusCode.Created; }
            }
            else { lock (Deletes) Deletes.Add(path); status = FailDeletes ? HttpStatusCode.InternalServerError : HttpStatusCode.NoContent; }
            return Task.FromResult(new HttpResponseMessage(status));
        }

        public string CaFile(string dir)
        {
            var path = Path.Combine(dir, "broker-ca.pem");
            if (File.Exists(path)) return path;
            using var key = ECDsa.Create();
            var request = new CertificateRequest("CN=synthetic-broker-ca", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(path, cert.ExportCertificatePem());
            return path;
        }
    }

    private sealed class FakeCredentials : ICredentialsReader
    {
        public string? Get(string key) => key switch
        {
            "SYNTHETIC_API_KEY" => Key,
            "BROKER_ADMIN_USER" => "synthetic-admin",
            "BROKER_ADMIN_PASSWORD" => "synthetic-password",
            _ => null,
        };
        public void InvalidateCache() { }
    }
}
