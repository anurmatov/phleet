using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
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
    private const string BotToken = "424242:synthetic-bot-secret-never-in-argv";
    private const string IngestToken = "cj1.ingest.agent1.synthetic-journal-mac-never-in-argv";
    internal readonly string _dir;

    public ManagedStockRuntimeAdapterTests() : this(null) { }

    /// <summary>#502: subscription fixtures need a ConfigDir whose ancestors carry no group/other write bits.</summary>
    internal ManagedStockRuntimeAdapterTests(string? baseDir) =>
        _dir = baseDir ?? Directory.CreateTempSubdirectory("managed-stock-").FullName;
    internal readonly FakeCli _cli = new();
    internal readonly FakeStore _store = new();
    internal readonly FakeHost _host = new();
    internal readonly FakeBroker _broker = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── apply ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Docker_to_openshell_apply_stops_docker_then_revoked_allocation_uuid_activation_and_one_start()
    {
        var agent = _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        var dockerStops = 0;
        _host.DockerStates = ["running"];
        var putsAtCreate = -1;
        _cli.OnCreate = () => putsAtCreate = _broker.Puts.Count;
        var runtime = Runtime(dockerStop: _ => { dockerStops++; _host.DockerStates = ["exited"]; return Task.FromResult(true); });

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
        Assert.DoesNotContain("sandbox start", verbs); // created straight into the staged launcher
        Assert.True(verbs.IndexOf("sandbox create") < verbs.IndexOf("sandbox upload"));
        Assert.Equal(0, putsAtCreate); // broker users exist only after staging
        Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "exec"]) && c.Args[^1].Contains(".fleet-activate"));
        // N5/F15: the guest gets the active enrollment bound to the exact UUID/epoch, accepted by the real loader.
        var guestFile = Path.Combine(_dir, "guest-enrollment.json");
        File.WriteAllText(guestFile, _cli.Uploads.Last(u => u.Remote == "/home/agent/trust/enrollment.json").Content!);
        var loaded = Fleet.Shared.Security.StockEnrollmentAuthority.Load(guestFile);
        Assert.Equal((record.Applied!.SandboxId, record.Applied.Epoch), (loaded.SandboxId, loaded.Epoch));
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
        // Exactly the epoch vhost, its prepared bridge reply exchange/queue/binding, three users and three
        // permissions; never the default vhost.
        Assert.Equal(9, _broker.Puts.Count);
        Assert.Single(_broker.Posts);
        Assert.DoesNotContain(_broker.Puts, p => p.Contains("/api/vhosts/%2F") || p.Contains("/api/permissions/%2F/"));
    }

    [Fact]
    public async Task Pending_docker_desire_still_dispatches_start_to_the_applied_openshell_binding()
    {
        var binding = Binding("fleet-agent1-aaaaaaaa", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _store.Add(ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        Enroll(binding);

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
        Assert.Equal(Fleet.Shared.Security.StockAuthoritySource.RetiredState, JsonNode.Parse(File.ReadAllText(AuthorityPath()))!["State"]!.GetValue<string>());
        Assert.Contains(_cli.Calls, c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "detach"]));
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        // Permission and user for each principal, then the management user's own vhost grant; no live connections.
        Assert.Equal(7, _broker.Deletes.Count);
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

    // #503 W8: pinned stock Stop returns an exact failed-main sandbox unchanged and its CLI exits nonzero waiting for
    // Stopped. Only that owned, already-quiescent terminal state reconciles the stop; every other hold stays.
    [Theory]
    [InlineData("owned_failed_main", null)]
    [InlineData("running", "stop:")]
    [InlineData("containers_unobservable", "stop:")]
    [InlineData("foreign_after_stop", "stop:")]
    [InlineData("status_unreadable", "stop:")]
    [InlineData("ready_true", "stop:")]
    [InlineData("other_reason", "stop:")]
    [InlineData("no_exit_code", "stop:")]
    [InlineData("stopping_phase", "stop:")]
    [InlineData("restarted_during_check", "stop:")]
    // F1: pinned stock checks the driver operation before its failed-main shortcut; pending or unreadable holds on either read.
    [InlineData("settled_without_record", null)]
    [InlineData("quiescent_operation_pending", "stop:")]
    [InlineData("pending_set_during_stop", "stop:")]
    [InlineData("pending_after_quiescence", "stop:")]
    [InlineData("provisioning_missing", "stop:")]
    [InlineData("provisioning_unreadable", "stop:")]
    [InlineData("pending_field_missing", "stop:")]
    [InlineData("pending_not_boolean", "stop:")]
    [InlineData("broker_fails", "broker:")]
    [InlineData("detach_hangs", "detach:")]
    public async Task Revoke_reconciles_only_an_exact_owned_already_quiescent_failed_main_sandbox(string variant, string? heldBy)
    {
        var binding = Binding("fleet-agent1-f8f8f8f8", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, variant == "stopping_phase" ? "Stopping" : "Error");
        _cli.Status[binding.SandboxName] = variant switch
        {
            "ready_true" => FakeCli.FailedMain(readyStatus: "True"),
            "other_reason" => FakeCli.FailedMain(reason: "ConfigurationInvalid"),
            "no_exit_code" => FakeCli.FailedMain(exitCode: false),
            "status_unreadable" => new JsonObject { ["exit_code"] = 1, ["conditions"] = "unreadable" },
            "settled_without_record" => FakeCli.FailedMain(operationPending: null),
            "quiescent_operation_pending" => FakeCli.FailedMain(operationPending: true),
            _ => FakeCli.FailedMain(),
        };
        var status = _cli.Status[binding.SandboxName];
        void Pending() => status["provisioning"]!["driver_operation_pending"] = true;
        switch (variant)
        {
            case "pending_set_during_stop": _cli.OnStop = _ => Pending(); break;
            case "pending_after_quiescence": _host.OnRunningRead = Pending; break;
            case "provisioning_missing": status.Remove("provisioning"); break;
            case "provisioning_unreadable": status["provisioning"] = "unreadable"; break;
            case "pending_field_missing": status["provisioning"]!.AsObject().Remove("driver_operation_pending"); break;
            case "pending_not_boolean": status["provisioning"]!["driver_operation_pending"] = "false"; break;
        }
        _cli.StopKeepsPhase.Add(binding.SandboxName);
        if (variant == "running") _host.RunningContainers = ["workload"];
        if (variant == "containers_unobservable") _host.RunningContainers = null;
        if (variant == "foreign_after_stop") _cli.OnStop = name => _cli.Sandboxes[name] = (Guid.NewGuid().ToString(), "Error");
        if (variant == "restarted_during_check") _host.OnRunningRead = () => _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Starting");
        if (variant == "broker_fails") _broker.FailDeletes = true;
        if (variant == "detach_hangs") _cli.Hang.Add("detach");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Errors = ["stop_unknown"] }));

        var revoke = await Runtime().RevokeAsync("agent1", default);

        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        var record = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding);
        if (heldBy is null)
        {
            Assert.True(revoke.Ok, revoke.Code + ":" + string.Join(",", revoke.Errors));
            Assert.Equal(("revoked", 0), (record.State, record.Errors.Count));
            Assert.Equal((binding.SandboxId, "Error"), _cli.Sandboxes[binding.SandboxName]); // observed, never rewritten or deleted
            return;
        }
        Assert.False(revoke.Ok);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains(revoke.Errors, e => e.StartsWith(heldBy, StringComparison.Ordinal));
        if (heldBy == "stop:") Assert.Contains($"stop:{binding.SandboxName}:stop_unknown", revoke.Errors);
        else Assert.DoesNotContain(revoke.Errors, e => e.StartsWith("stop:", StringComparison.Ordinal)); // the stop itself reconciled
    }

    // #503 W9: pinned stock gives the main process only the providers attached when it spawns; a revoked reuse must reattach
    // before the staged boot, as create's --provider does, and a stopped restart boots with the attachments it kept.
    [Theory]
    [InlineData("revoked_reuse")]
    [InlineData("stopped_restart")]
    public async Task Staged_boot_carries_the_api_provider_its_preparation_reads(string path)
    {
        var binding = Binding("fleet-agent1-a7a7a7a7", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        var restart = path == "stopped_restart";
        if (restart) _cli.Attached[binding.SandboxName] = [binding.Provider]; // a clean revoke would have detached it
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = restart ? "stopped" : "revoked", Applied = binding }));
        if (restart) Enroll(binding);
        // The pinned preparation reads the OPENAI_API_KEY placeholder from the environment its boot was spawned with.
        _cli.OnExec = script => script == StockOpenShellRuntime.DaemonReadyScript
            && !(_cli.Booted.TryGetValue(binding.SandboxName, out var booted) && booted.Contains(binding.Provider)) ? 1 : 0;

        var result = restart ? await Runtime().StartAsync("agent1", default) : await Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
        Assert.Contains(binding.Provider, _cli.Booted[binding.SandboxName]);
        var start = _cli.Calls.FindIndex(c => c.Args.SequenceEqual(["sandbox", "start", binding.SandboxName]));
        var attach = _cli.Calls.FindIndex(c => c.Args.SequenceEqual(["sandbox", "provider", "attach", binding.SandboxName, binding.Provider]));
        if (restart) Assert.Equal(-1, attach);
        else Assert.InRange(attach, 0, start - 1);
    }

    // #503 W10: a clean revoke of a failed-main sandbox leaves it Error, which pinned Stop never changes. Reapply reaches the
    // staged boot at the same and at a changed gateway generation only with the W8/F1 proof; every unproven state holds.
    [Theory]
    [InlineData("same", "proven", null)]
    [InlineData("restart", "proven", null)]
    [InlineData("same", "running", "held")]
    [InlineData("restart", "running", "gateway_generation_changed")]
    [InlineData("same", "operation_pending", "held")]
    [InlineData("restart", "operation_pending", "gateway_generation_changed")]
    [InlineData("same", "foreign_after_stop", "held")]
    [InlineData("restart", "status_unreadable", "gateway_generation_changed")]
    [InlineData("same", "not_clean_revoke", "held")]
    [InlineData("restart", "not_clean_revoke", "gateway_generation_changed")]
    [InlineData("restart", "failed_main_peer", "gateway_generation_changed")]
    public async Task Clean_revoked_failed_main_sandbox_is_reapplied_only_with_its_proof(string generation, string variant, string? held)
    {
        var binding = Binding("fleet-agent1-a6a6a6a6", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Error");
        _cli.Status[binding.SandboxName] = variant switch
        {
            "operation_pending" => FakeCli.FailedMain(operationPending: true),
            "status_unreadable" => new JsonObject { ["exit_code"] = 1, ["conditions"] = "unreadable" },
            _ => FakeCli.FailedMain(),
        };
        _cli.StopKeepsPhase.Add(binding.SandboxName);
        if (variant == "running") _host.RunningContainers = ["workload"];
        if (variant == "foreign_after_stop") _cli.OnStop = name => _cli.Sandboxes[name] = (Guid.NewGuid().ToString(), "Error");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = variant == "not_clean_revoke" ? "stopped" : "revoked", Applied = binding }));
        if (variant == "failed_main_peer")
        {
            // Peers keep the exact Stopped requirement: a clean-revoked failed-main peer still holds a planned restart.
            var peer = Binding("fleet-agent2-a6a6a6a6", Guid.NewGuid());
            _cli.Sandboxes[peer.SandboxName] = (peer.SandboxId, "Error");
            _cli.Status[peer.SandboxName] = FakeCli.FailedMain();
            var other = ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "revoked", Applied = peer });
            other.Name = other.ShortName = other.ContainerName = "agent2";
            _store.Add(other);
        }
        if (generation == "restart") _host.Generation = "gateway-2@restart";

        var result = await Runtime().ApplyAsync("agent1", default);

        var record = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding);
        var started = _cli.Calls.Any(c => c.Args.SequenceEqual(["sandbox", "start", binding.SandboxName]));
        if (held is null)
        {
            Assert.True(result.Ok, result.Code + ":" + string.Join(",", result.Errors));
            Assert.Equal(("applied", binding.SandboxId, _host.Generation), (record.State, record.Applied!.SandboxId, record.Applied.GatewayGeneration));
            Assert.True(started); // pinned Start accepts a failed-main Error sandbox
            Assert.Contains(binding.Provider, _cli.Booted[binding.SandboxName]); // W9: attached before the staged boot
            return;
        }
        Assert.False(result.Ok);
        Assert.False(started);
        if (held == "gateway_generation_changed")
        {
            Assert.Equal("gateway_generation_changed", result.Code);
            Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "stop"])); // refused before any effect
            Assert.Equal((variant == "not_clean_revoke" ? "stopped" : "revoked", binding.SandboxId, binding.Epoch, binding.GatewayGeneration),
                (record.State, record.Applied!.SandboxId, record.Applied.Epoch, record.Applied.GatewayGeneration)); // never rebound
        }
        else Assert.Equal("needs_attention", record.State);
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
        // Running despite the stopped record: unplanned (a quiescent Stopped fleet rebinds, #492 review F6).
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _host.Generation = "gateway-2@later";

        var result = await Runtime().StartAsync("agent1", default);

        Assert.Equal("gateway_generation_changed", result.Code);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "start"]));
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
    }

    [Theory]
    [InlineData("telegram", "telegram_bot_token_unresolved")]
    [InlineData("telegram_invalid", "telegram_bot_token_invalid")]
    [InlineData("journal", "managed_route_unsupported:journal")]
    [InlineData("journal_mcp", "managed_route_unsupported:mcp:fleet-comms-journal")]
    [InlineData("mcp", "managed_route_unsupported:mcp:other")]
    [InlineData("unrouted", "managed_route_unsupported:mcp:fleet-temporal")]
    [InlineData("version", "stock_cli_version_mismatch")]
    [InlineData("socket", "openshell_requires_codex_api_key_policy_and_no_socket")]
    public async Task Unsupported_or_missing_prerequisites_refuse_before_any_mutation(string fault, string code)
    {
        var agent = ManagedAgent(desired: "openshell", applied: "docker");
        switch (fault)
        {
            case "telegram": agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" }); break;
            case "telegram_invalid":
                agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
                _broker.Credentials.Extra["TELEGRAM_AGENT1_BOT_TOKEN"] = "synthetic-without-bot-id";
                break;
            case "journal": // capture is configured, but this installation names no journal listener host
                agent.JournalEnabled = true;
                agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
                _broker.Credentials.Extra["TELEGRAM_AGENT1_BOT_TOKEN"] = BotToken;
                break;
            case "journal_mcp": // the read MCP is routed, but this installation names no journal listener host
                agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-comms-journal", Url = "http://comms.example.test/journal/v1/mcp", TransportType = "http" });
                break;
            case "mcp": agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "other", Url = "http://other/mcp", TransportType = "http" }); break;
            case "unrouted": // a supported service, but this installation configured no receiving host for it
                agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-temporal", Url = "https://temporal.example.test/mcp", TransportType = "http" });
                _serviceHosts = new() { ["telegram"] = "telegram.example.test" };
                break;
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
    public async Task Supported_first_party_mcp_grants_apply_with_exactly_their_granted_tools()
    {
        var agent = ManagedAgent(desired: "openshell", applied: "docker");
        agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-temporal", Url = "https://temporal.example.test/mcp", TransportType = "http" });
        agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-telegram", Url = "https://telegram.example.test/mcp", TransportType = "http" });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-temporal__temporal_start_workflow" });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-temporal__temporal_terminate_workflow", IsEnabled = false });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-telegram__send_message" });
        agent.FormattingMode = Fleet.Shared.FormattingMode.Rich;
        _store.Add(agent);
        // #503: the saved policy signs exactly the three selected receiving hosts.
        _store.PolicyYaml = SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal, SignedPolicy.Telegram);
        var runtime = Runtime();

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        // Exactly the enabled DB grants per service (no disabled, implicit or cross-service tools), plus the
        // operator's receiving host and Telegram's formatting mode for its receiver.
        var authority = JsonNode.Parse(File.ReadAllText(AuthorityPath()))!;
        Assert.Equal("{\"telegram\":{\"Host\":\"telegram.example.test\",\"Tools\":[\"send_message\"],\"Query\":\"formatting_mode=2\"},"
            + "\"temporal\":{\"Host\":\"temporal.example.test\",\"Tools\":[\"temporal_start_workflow\"],\"Query\":\"\"}}",
            authority["Services"]!.ToJsonString());
        var guestFile = Path.Combine(_dir, "guest-enrollment.json");
        File.WriteAllText(guestFile, _cli.Uploads.Last(u => u.Remote == "/home/agent/trust/enrollment.json").Content!);
        Assert.Equal(["temporal_start_workflow"], Fleet.Shared.Security.StockEnrollmentAuthority.Load(guestFile).ServiceTools("temporal"));
        // The workload's MCP config names each routed service like memory: exact HTTPS host, no query identity.
        var staged = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "workspace").Args[3];
        Assert.Equal("{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"},"
            + "\"fleet-telegram\":{\"url\":\"https://telegram.example.test/mcp\"},\"fleet-temporal\":{\"url\":\"https://temporal.example.test/mcp\"}}}",
            File.ReadAllText(Path.Combine(staged, ".mcp.json")));

        // Reprovision keeps the workspace but refreshes its routes from the current grants. #503: the old artifact still
        // signs the dropped route, so it is refused; an appended version signing exactly memory and temporal applies.
        agent.McpEndpoints.RemoveAll(e => e.McpName == "fleet-telegram");
        Assert.Equal((409, StockManagedSignerPolicy.Scope), ((await runtime.ApplyAsync("agent1", default)) is var refused ? (refused.Status, refused.Code) : default));
        _store.PolicyYaml2 = SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal);
        agent.SandboxPolicyVersionId = 2;
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
        Assert.Contains(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["policy", "set"]));
        Assert.DoesNotContain("fleet-telegram", _cli.Uploads.Last(u => u.Remote == "/workspace/.mcp.json").Content!);
        Assert.Equal(["temporal"], JsonNode.Parse(File.ReadAllText(AuthorityPath()))!["Services"]!.AsObject().Select(p => p.Key));
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

        Assert.Equal("{\"desiredOpenShell\":1,\"appliedOpenShell\":0,\"unresolvedBindings\":1,\"subscriptionBindings\":0}", json);
    }

    // ── broker provisioner ─────────────────────────────────────────────────

    [Fact]
    public void Broker_permissions_are_exact_anchored_and_role_separated()
    {
        var (configure, write, read) = StockBrokerProvisioner.Permissions("workload", "fleet.agent.agent1");
        Assert.Equal(@"^(fleet\.relay|fleet\.group|fleet\.orchestrator|fleet\.agent\.agent1)$", configure);
        Assert.Equal(@"^(fleet\.relay|fleet\.group|fleet\.agent\.agent1)$", read);
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
        Assert.Equal(5, partial.OwnedPartials.Count); // vhost, reply exchange/queue/binding and the first user, never activated
    }

    [Fact]
    public async Task Bridge_reply_queue_and_binding_are_prepared_before_any_principal_with_the_lane_declare_properties()
    {
        var epoch = Guid.NewGuid();
        var created = await Provisioner().CreateEpochAsync(epoch, "agent1", default);

        Assert.True(created.Ok);
        var vhost = StockBrokerProvisioner.VirtualHost(epoch);
        var queue = $"/api/queues/{vhost}/fleet.agent.bridge";
        var binding = $"/api/bindings/{vhost}/e/fleet.group/q/fleet.agent.bridge";
        var exchange = $"/api/exchanges/{vhost}/fleet.group";
        var firstUser = _broker.Writes.FindIndex(p => p.Contains("/api/users/"));
        Assert.True(_broker.Writes.IndexOf(exchange) is var e && e > 0 && e < firstUser);
        Assert.True(_broker.Writes.IndexOf(queue) is var q && q > e && q < firstUser);
        Assert.True(_broker.Writes.IndexOf(binding) is var b && b > q && b < firstUser);
        // Exactly what the Bridge lane redeclares (durable, non-exclusive, non-auto-delete, no arguments).
        Assert.Equal("{\"durable\":true,\"auto_delete\":false,\"arguments\":{}}", _broker.Bodies[queue]);
        Assert.Equal("{\"type\":\"direct\",\"durable\":true,\"auto_delete\":false,\"internal\":false,\"arguments\":{}}", _broker.Bodies[exchange]);
        Assert.Equal("{\"routing_key\":\"bridge\",\"arguments\":{}}", _broker.Bodies[binding]);
        Assert.Contains("queue:" + vhost + "/fleet.agent.bridge", created.OwnedPartials);
        Assert.Contains("binding:" + vhost + "/fleet.group/bridge/fleet.agent.bridge", created.OwnedPartials);

        // Revoke keeps the vhost and its prepared queue for custody.
        await Provisioner().RevokeEpochAsync(epoch, default);
        Assert.DoesNotContain(_broker.Deletes, p => p.Contains("/api/queues/") || p.Contains("/api/bindings/") || p.Contains("/api/vhosts/"));
    }

    [Fact]
    public async Task Revoke_removes_only_the_management_users_own_grant_on_the_epoch_vhost_and_keeps_the_vhost()
    {
        var epoch = Guid.NewGuid();
        var vhost = StockBrokerProvisioner.VirtualHost(epoch);
        Assert.True((await Provisioner().CreateEpochAsync(epoch, "agent1", default)).Ok);
        // Review X3: create writes no grant for the management user (its PUTs rely on the creator's implicit grant).
        Assert.DoesNotContain(_broker.Writes, p => p.EndsWith("/synthetic-admin", StringComparison.Ordinal));

        var revoked = await Provisioner().RevokeEpochAsync(epoch, default);

        Assert.True(revoked.Ok, revoked.Code);
        var admin = $"/api/permissions/{vhost}/synthetic-admin";
        // Exactly one bounded DELETE of that grant, after every principal is gone; nothing else of the admin's is touched.
        Assert.Single(_broker.Deletes, p => p.Contains("synthetic-admin"));
        Assert.Equal(admin, _broker.Deletes[^1]);
        Assert.DoesNotContain(_broker.Deletes, p => p.Contains("/api/vhosts/") || p.Contains("/api/users/synthetic-admin"));
    }

    [Fact]
    public async Task Failed_management_grant_removal_is_reported_without_naming_the_management_user()
    {
        _broker.FailDeletes = true;

        var revoked = await Provisioner().RevokeEpochAsync(Guid.NewGuid(), default);

        Assert.False(revoked.Ok);
        Assert.Contains("permission:management", revoked.OwnedPartials);
        Assert.DoesNotContain(revoked.OwnedPartials, l => l.Contains("synthetic-admin"));
    }

    [Fact]
    public async Task Failed_bridge_reply_queue_creation_holds_the_epoch_without_principals_or_activation()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        _broker.FailPutContaining = "/api/queues/";

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        Assert.NotEqual("openshell", _store.Get("agent1").AppliedRuntimeKind);
        Assert.DoesNotContain(_broker.Writes, p => p.Contains("/api/users/") || p.Contains("/api/bindings/"));
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "exec"]) && c.Args[^1].Contains(".fleet-activate"));
        Assert.NotEqual("applied", ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Missing_https_trust_blocks_broker_setup()
    {
        var provisioner = new StockBrokerProvisioner(Config(new() { ["StockOpenShell:BrokerManagementUrl"] = "http://broker.test/" }), _broker.Credentials);
        Assert.Equal("broker_management_https_required", provisioner.DescribePreflightFault());
        Assert.False((await provisioner.CreateEpochAsync(Guid.NewGuid(), "agent1", default)).Ok);
    }

    // ── #492 review blockers (b09f5f5a) ────────────────────────────────────

    [Fact]
    public async Task Candidate_epoch_and_name_are_persisted_before_broker_side_effects_and_kept_on_failure()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        _broker.FailPutContaining = "/api/vhosts/";

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.False(result.Ok);
        var record = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.NotNull(record.Candidate);
        Assert.NotEqual(Guid.Empty, record.Candidate!.Epoch);
        Assert.Equal(StockOpenShellRuntime.SandboxNameFor("agent1", record.Candidate.Epoch), record.Candidate.SandboxName);
        Assert.Contains(_store.History, h => ManagedRecord.Parse(h) is { Operation: not null, Candidate: not null });
        Assert.Equal("needs_attention", (await Runtime().ApplyAsync("agent1", default)).Code); // held, never replayed
    }

    [Fact]
    public async Task Create_timeout_keeps_the_observed_uuid_so_revoke_can_stop_that_exact_sandbox()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        _cli.CreateTimesOut = true;
        var runtime = Runtime();

        Assert.Equal("sandbox_create_unknown", (await runtime.ApplyAsync("agent1", default)).Code);
        var candidate = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).Candidate!;
        Assert.Equal(_cli.Sandboxes[candidate.SandboxName].Id, candidate.SandboxId);

        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", candidate.SandboxName]));
        Assert.Null(ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).Candidate);
    }

    [Theory]
    [InlineData(null, "docker_observation_failed")]
    [InlineData("running", "docker_quiescence_pending")]
    public async Task Docker_observation_failure_or_survivor_is_never_read_as_absence(string? state, string code)
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        _host.DockerStates = state is null ? null : [state];

        var result = await Runtime(dockerStop: _ => Task.FromResult(true)).ApplyAsync("agent1", default);

        Assert.Equal(code, result.Code);
        Assert.Empty(_broker.Puts);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
    }

    [Fact]
    public async Task Successful_stop_keeps_an_unresolved_hold_and_start_stays_refused()
    {
        var binding = Binding("fleet-agent1-ffffffff", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Errors = ["broker:unresolved"] }));
        var runtime = Runtime();

        Assert.Equal("stopped_held", (await runtime.StopAsync("agent1", default)).Code);
        var record = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("needs_attention", "broker:unresolved"), (record.State, Assert.Single(record.Errors)));
        Assert.Equal("needs_attention", (await runtime.StartAsync("agent1", default)).Code);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "start"]));
    }

    [Fact]
    public async Task Managed_reprovision_reuses_the_same_sandbox_and_workspace_under_a_new_epoch()
    {
        var binding = Binding("fleet-agent1-11111111", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        var agent = _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        agent.SandboxPolicyVersionId = 2;

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        var applied = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).Applied!;
        Assert.Equal((binding.SandboxName, binding.SandboxId), (applied.SandboxName, applied.SandboxId));
        Assert.NotEqual(binding.Epoch, applied.Epoch);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Contains(_cli.Calls, c => c.Args.Take(3).SequenceEqual(["policy", "set", binding.SandboxName]));
        Assert.Single(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "start", binding.SandboxName]));
        var verbs = _cli.Calls.Select(c => string.Join(' ', c.Args.Take(3))).ToList();
        Assert.True(verbs.IndexOf("sandbox stop " + binding.SandboxName) < verbs.IndexOf("sandbox upload " + binding.SandboxName)); // N6: writers quiesced first
        Assert.Contains("sandbox provider attach", verbs); // provider re-attached after the reuse revocation
    }

    [Fact]
    public async Task Docker_workspace_moves_into_the_sandbox_without_control_state_and_back_out()
    {
        Directory.CreateDirectory(Path.Combine(DockerWorkspaceDir, ".generated"));
        File.WriteAllText(Path.Combine(DockerWorkspaceDir, "marker.txt"), "keep");
        File.WriteAllText(Path.Combine(DockerWorkspaceDir, ".generated", "appsettings.json"), "{\"secret\":true}");
        Directory.CreateDirectory(Path.Combine(DockerWorkspaceDir, "codex"));
        File.WriteAllText(Path.Combine(DockerWorkspaceDir, "codex", "auth.json"), "{\"synthetic\":true}");
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = Runtime();

        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
        var create = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        var staged = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "workspace").Args[3];
        Assert.True(File.Exists(Path.Combine(staged, "marker.txt")));
        Assert.False(Directory.Exists(Path.Combine(staged, ".generated")));
        Assert.Equal(["--", "/app/stock-entrypoint.sh"], create.Args.TakeLast(2)); // explicit canonical launcher
        Assert.Contains("FLEET_STOCK_STAGED=1", create.Args);
        Assert.DoesNotContain("--upload", create.Args);
        Assert.False(Directory.Exists(Path.Combine(staged, "codex")));

        _store.Get("agent1").RuntimeKind = "docker";
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
        Assert.Equal("from sandbox", File.ReadAllText(Path.Combine(DockerWorkspaceDir, "marker-back.txt")));
        Assert.True(Directory.Exists(Path.Combine(DockerWorkspaceDir, "only-dir"))); // contents layout kept, not flattened
        Assert.Equal(["agent1:openshell"], _dockerProvisions); // Docker ran while the managed hold was still applied
        Assert.Equal("docker", _store.Get("agent1").AppliedRuntimeKind);
    }

    [Fact]
    public async Task Failed_docker_transition_keeps_the_openshell_binding_held()
    {
        var binding = Binding("fleet-agent1-22222222", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _dockerResult = (false, "image missing");

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal("docker_transition_failed", result.Code);
        Assert.Equal("openshell", _store.Get("agent1").AppliedRuntimeKind);
        Assert.Equal("needs_attention", ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task Changed_generation_refuses_cancel_logs_and_stop_without_touching_the_sandbox_lifecycle()
    {
        var binding = Binding("fleet-agent1-33333333", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _host.Generation = "gateway-9@other";
        var runtime = Runtime();

        Assert.Equal("gateway_generation_changed", (await runtime.CancelAsync("agent1", null, default)).Code);
        Assert.Equal("gateway_generation_changed", (await runtime.LogsAsync("agent1", 10, default)).Result.Code);
        Assert.DoesNotContain(_cli.Calls, c => c.Args[0] is "logs" || c.Args.Take(2).SequenceEqual(["sandbox", "exec"]));
        Assert.Equal("gateway_generation_changed", (await runtime.StopAsync("agent1", default)).Code);
        Assert.Equal("needs_attention", ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Theory]
    [InlineData("auth", "broker_management_auth_failed")]
    [InlineData("profile", "provider_profile_hash_mismatch:openai")]
    [InlineData("symlink", "managed_inputs_invalid")]
    public async Task Broker_auth_profile_hashes_and_native_inputs_are_checked_before_old_runtime_stop(string fault, string code)
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        var stops = 0;
        var runtime = Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); });
        _host.DockerStates = ["running"];
        switch (fault)
        {
            case "auth": _broker.WhoAmI = HttpStatusCode.Unauthorized; break;
            case "profile": _store.ProfileHashes = "{\"openai\":\"" + new string('0', 64) + "\"}"; break;
            case "symlink":
                Directory.CreateSymbolicLink(Path.Combine(_dir, "generated", "roles"), Path.GetTempPath());
                break;
        }

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.Equal(code, result.Code);
        Assert.Equal(0, stops);
        Assert.Empty(_broker.Puts);
    }

    [Fact]
    public async Task Cli_runner_bounds_output_and_settles_within_the_deadline()
    {
        var script = Path.Combine(_dir, "fake-openshell");
        File.WriteAllText(script, "#!/bin/sh\nif [ \"$1\" = big ]; then head -c 2000000 /dev/zero | tr '\\0' x; exit 0; fi\nsleep 30\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_000_000);
        var cli = new ProcessStockCli(Config(new() { ["StockOpenShell:CliPath"] = script }));

        var big = await cli.RunAsync(["big"], null, TimeSpan.FromSeconds(10), default);
        Assert.Equal((-1, ""), (big.ExitCode, big.Stdout));

        var started = DateTime.UtcNow;
        var slow = await cli.RunAsync(["slow"], null, TimeSpan.FromMilliseconds(300), default);
        Assert.True(slow.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    // ── #492 review blockers (2be4e3a0) ────────────────────────────────────

    [Fact]
    public async Task Logs_across_a_uuid_swap_are_discarded_and_held()
    {
        var binding = Binding("fleet-agent1-44444444", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _cli.SwapOnLogs = true;

        var (result, logs) = await Runtime().LogsAsync("agent1", 10, default);

        Assert.Equal("sandbox_identity_changed", result.Code);
        Assert.Null(logs);
        var record = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains("sandbox_identity_changed", record.Errors);
    }

    [Fact]
    public async Task Unverified_gateway_client_trust_refuses_before_old_runtime_stop()
    {
        _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        _cli.ListFails = true;
        var stops = 0;

        var result = await Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal("gateway_client_unverified", result.Code);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task Stopped_sandbox_reprovisions_and_rolls_back_through_staging_without_an_extra_daemon_start()
    {
        var binding = Binding("fleet-agent1-55555555", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        var agent = _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        var runtime = Runtime();

        Assert.Equal("applied_openshell", (await runtime.ApplyAsync("agent1", default)).Code);
        agent.RuntimeKind = "docker";
        Assert.Equal("applied_docker", (await runtime.ApplyAsync("agent1", default)).Code);

        Assert.Contains(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "download"]));
        Assert.Equal("docker", _store.Get("agent1").AppliedRuntimeKind);
        // Only the reprovision activated the daemon; the Docker rollback staged without launching it.
        Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "exec"]) && c.Args[^1].Contains(".fleet-activate"));
        Assert.Equal("Stopped", _cli.Sandboxes[binding.SandboxName].Phase);
    }

    [Fact]
    public async Task Symlinked_workspace_source_or_ancestor_refuses_without_writing_outside()
    {
        var outside = Directory.CreateTempSubdirectory("outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "victim.txt"), "original");
            Directory.CreateDirectory(DockerWorkspaceDir);
            Directory.CreateSymbolicLink(Path.Combine(DockerWorkspaceDir, "escape"), outside);
            _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));

            var result = await Runtime().ApplyAsync("agent1", default);

            Assert.Equal("workspace_stage_failed", result.Code);
            Assert.Equal("original", File.ReadAllText(Path.Combine(outside, "victim.txt")));
            Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
            Assert.Throws<IOException>(() => StockOpenShellRuntime.RefuseLinkedAncestors(Path.Combine(DockerWorkspaceDir, "escape", "child")));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public async Task Inherited_pipe_after_parent_exit_does_not_extend_the_deadline()
    {
        var script = Path.Combine(_dir, "fake-openshell-pipe");
        File.WriteAllText(script, "#!/bin/sh\n(sleep 5 &)\nexit 0\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_000_000);
        var cli = new ProcessStockCli(Config(new() { ["StockOpenShell:CliPath"] = script }));

        var started = DateTime.UtcNow;
        var run = await cli.RunAsync(["x"], null, TimeSpan.FromMilliseconds(300), default);

        Assert.True(run.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5), (DateTime.UtcNow - started).ToString());
    }

    [Fact]
    public async Task Failed_cut_still_attempts_a_bounded_stop_and_keeps_every_error()
    {
        var binding = Binding("fleet-agent1-66666666", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _broker.FailDeletes = true;

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.Equal("openshell_to_docker_unresolved", result.Code);
        Assert.Contains(result.Errors, e => e.StartsWith("broker:", StringComparison.Ordinal));
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Equal("Stopped", _cli.Sandboxes[binding.SandboxName].Phase);
        Assert.Empty(_dockerProvisions);
    }

    [Fact]
    public async Task Status_refresh_holds_and_stops_the_owned_sandbox_on_unexpected_gateway_restart()
    {
        var binding = Binding("fleet-agent1-77777777", Guid.NewGuid());
        _cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _store.Add(ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = Runtime();

        await runtime.CheckGenerationAsync("agent1", default);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "stop"])); // unchanged: nothing to do

        _host.Generation = "gateway-2@unexpected";
        await runtime.CheckGenerationAsync("agent1", default);

        var record = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Contains("gateway_generation_changed", record.Errors);
        Assert.Contains(_cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Equal("Stopped", _cli.Sandboxes[binding.SandboxName].Phase);
    }

    [Fact]
    public async Task Cli_timeout_reaps_reparented_descendants()
    {
        var token = "7." + Random.Shared.Next(100000, 999999);
        var script = Path.Combine(_dir, "fake-openshell-orphan");
        File.WriteAllText(script, $"#!/bin/sh\n(sleep {token} &)\nsleep 30\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_000_000);
        var cli = new ProcessStockCli(Config(new() { ["StockOpenShell:CliPath"] = script }));

        Assert.True((await cli.RunAsync(["x"], null, TimeSpan.FromMilliseconds(300), default)).TimedOut);
        await Task.Delay(300);

        var survivors = Directory.EnumerateDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _))
            .Select(d => { try { return File.ReadAllText(Path.Combine(d, "cmdline")).Replace('\0', ' '); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } })
            .Where(c => c.Contains("sleep " + token, StringComparison.Ordinal)).ToList();
        Assert.Empty(survivors);
    }

    // ── own Telegram bot and journal capture (#492) ────────────────────────

    private Agent BotAndJournalAgent()
    {
        var agent = ManagedAgent(desired: "openshell", applied: "docker");
        agent.JournalEnabled = true;
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
        _broker.Credentials.Extra["TELEGRAM_AGENT1_BOT_TOKEN"] = BotToken;
        _journalHost = "journal.example.test";
        _generatedSettings = new JsonObject
        {
            ["Agent"] = new JsonObject { ["Name"] = "agent1" },
            ["Telegram"] = new JsonObject { ["AllowedUserIds"] = new JsonArray(1001) },
            ["Journal"] = new JsonObject { ["IngestToken"] = IngestToken, ["ExcludedChatIds"] = "" },
        }.ToJsonString();
        return agent;
    }

    [Fact]
    public async Task Own_bot_and_journal_apply_with_values_only_in_outside_providers_and_placeholder_slots_inside()
    {
        _store.Add(BotAndJournalAgent());
        var runtime = Runtime();

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        // Each value reaches only its own stock provider, through the CLI environment under the credential name.
        var telegram = Assert.Single(_cli.Calls, c => c.Args.Take(4).SequenceEqual(["provider", "create", "--name", "fleet-agent1-telegram"]));
        Assert.Equal(["provider", "create", "--name", "fleet-agent1-telegram", "--type", "telegram", "--credential", "TELEGRAM_BOT_TOKEN"], telegram.Args);
        Assert.Equal([("TELEGRAM_BOT_TOKEN", BotToken)], telegram.SecretEnv!.Select(p => (p.Key, p.Value)));
        var journal = Assert.Single(_cli.Calls, c => c.Args.Take(4).SequenceEqual(["provider", "create", "--name", "fleet-agent1-journal"]));
        Assert.Equal(["provider", "create", "--name", "fleet-agent1-journal", "--type", "fleet-journal", "--credential", "FLEET_JOURNAL_INGEST_TOKEN"], journal.Args);
        Assert.Equal([("FLEET_JOURNAL_INGEST_TOKEN", IngestToken)], journal.SecretEnv!.Select(p => (p.Key, p.Value)));
        foreach (var secret in new[] { Key, BotToken, IngestToken, "synthetic-bot-secret" })
            Assert.DoesNotContain(_cli.Calls, c => c.Args.Any(a => a.Contains(secret, StringComparison.Ordinal)));
        var create = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Equal(["fleet-agent1-openai", "fleet-agent1-telegram", "fleet-agent1-journal"],
            create.Args.Select((a, i) => (a, i)).Where(p => p.a == "--provider").Select(p => create.Args[p.i + 1]));
        var applied = ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).Applied!;
        Assert.Equal(["fleet-agent1-telegram", "fleet-agent1-journal"], applied.ServiceProviders);

        // Workload inputs: empty credential slots, the public bot id, the capture switch and the journal route.
        var app = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "app").Args[3];
        var bundle = Directory.GetParent(app)!.FullName;
        foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            foreach (var secret in new[] { Key, BotToken, IngestToken, "synthetic-bot-secret", "synthetic-journal-mac" })
                Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(app, "appsettings.json")))!;
        Assert.Equal("424242", settings["StockOpenShell"]!["TelegramBotId"]!.GetValue<string>());
        Assert.True(settings["StockOpenShell"]!["JournalCapture"]!.GetValue<bool>());
        Assert.Equal("https://journal.example.test", settings["Journal"]!["BaseUrl"]!.GetValue<string>());
        Assert.Null(settings["Journal"]!["IngestToken"]);
        Assert.Null(settings["Telegram"]!["BotToken"]);
        Assert.Equal(1001, settings["Telegram"]!["AllowedUserIds"]![0]!.GetValue<int>()); // ordinary allowlist kept

        // Revoke detaches every attached provider, not only the native one.
        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        Assert.Equal(["fleet-agent1-openai", "fleet-agent1-telegram", "fleet-agent1-journal"],
            _cli.Calls.Where(c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "detach"])).Select(c => c.Args[4]));
    }

    // ── comms-journal read MCP (#492) ───────────────────────────────────────

    private const string ReadToken = "cj1.read.agent1.synthetic-read-mac-never-in-argv";

    private Agent JournalReadAgent()
    {
        var agent = ManagedAgent(desired: "openshell", applied: "docker");
        agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-comms-journal", Url = "http://comms.example.test/journal/v1/mcp?x=1", TransportType = "http" });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-comms-journal__get_message", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-comms-journal__search_messages", IsEnabled = false });
        _journalHost = "journal.example.test";
        // Ordinary provisioning's own output: the derived per-agent read token in the MCP header.
        _generatedMcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["fleet-comms-journal"] = new JsonObject
                {
                    ["type"] = "http", ["url"] = "http://comms.example.test/journal/v1/mcp",
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + ReadToken },
                },
            },
        }.ToJsonString();
        return agent;
    }

    [Fact]
    public async Task Comms_journal_read_mcp_applies_with_the_read_token_only_in_its_outside_provider()
    {
        _store.Add(JournalReadAgent());
        var runtime = Runtime();

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        var read = Assert.Single(_cli.Calls, c => c.Args.Take(4).SequenceEqual(["provider", "create", "--name", "fleet-agent1-journal-read"]));
        Assert.Equal(["provider", "create", "--name", "fleet-agent1-journal-read", "--type", "fleet-journal-read", "--credential", "FLEET_JOURNAL_READ_TOKEN"], read.Args);
        Assert.Equal([("FLEET_JOURNAL_READ_TOKEN", ReadToken)], read.SecretEnv!.Select(p => (p.Key, p.Value)));
        foreach (var secret in new[] { Key, ReadToken, "synthetic-read-mac" })
            Assert.DoesNotContain(_cli.Calls, c => c.Args.Any(a => a.Contains(secret, StringComparison.Ordinal)));
        var create = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Equal(["fleet-agent1-openai", "fleet-agent1-journal-read"],
            create.Args.Select((a, i) => (a, i)).Where(p => p.a == "--provider").Select(p => create.Args[p.i + 1]));

        var app = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "app").Args[3];
        var bundle = Directory.GetParent(app)!.FullName;
        foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            foreach (var secret in new[] { Key, ReadToken, "synthetic-read-mac" })
                Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
        // The workload's MCP route: the journal listener host at the ordinary path, no header and no query.
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "workspace", ".mcp.json")))!["mcpServers"]!;
        Assert.Equal("{\"url\":\"https://journal.example.test/journal/v1/mcp\"}", mcp["fleet-comms-journal"]!.ToJsonString());
        // The outside record names the route and exactly the enabled existing grants.
        var authority = Fleet.Shared.Security.StockEnrollmentAuthority.Load(Path.Combine(bundle, "trust", "enrollment.json"));
        Assert.Equal("journal.example.test", authority.JournalRead!.Host);
        Assert.Equal("/journal/v1/mcp", authority.JournalRead.Path);
        Assert.Equal(["get_message"], authority.JournalRead.Tools);

        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        Assert.Equal(["fleet-agent1-openai", "fleet-agent1-journal-read"],
            _cli.Calls.Where(c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "detach"])).Select(c => c.Args[4]));
    }

    private const string CrossChatToken = "cj1.read-cross-chat.agent1.synthetic-cross-mac-never-in-argv";

    [Fact]
    public async Task Journal_files_send_and_cross_chat_apply_with_derived_tokens_only_in_outside_providers()
    {
        var agent = BotAndJournalAgent();
        agent.Tools.Add(new AgentTool { ToolName = "mcp__fleet-journal-files__fetch_attachment", IsEnabled = true });
        agent.Tools.Add(new AgentTool { ToolName = JournalGrants.SendGrant, IsEnabled = true });
        agent.JournalCrossChatEnabled = true;
        _generatedSettings = new JsonObject
        {
            ["Agent"] = new JsonObject { ["Name"] = "agent1" },
            ["Telegram"] = new JsonObject { ["AllowedUserIds"] = new JsonArray(1001) },
            ["Journal"] = new JsonObject
            {
                ["IngestToken"] = IngestToken, ["ExcludedChatIds"] = "", ["ReadToken"] = ReadToken, ["FilesEnabled"] = true,
                ["SendEnabled"] = true, ["CrossChatEnabled"] = true, ["CrossChatToken"] = CrossChatToken,
            },
        }.ToJsonString();
        _store.Add(agent);

        var result = await Runtime().ApplyAsync("agent1", default);

        Assert.True(result.Ok, result.Message);
        var read = Assert.Single(_cli.Calls, c => c.Args.Take(4).SequenceEqual(["provider", "create", "--name", "fleet-agent1-journal-read"]));
        Assert.Equal(["provider", "create", "--name", "fleet-agent1-journal-read", "--type", "fleet-journal-read", "--credential", "FLEET_JOURNAL_READ_TOKEN"], read.Args);
        Assert.Equal([("FLEET_JOURNAL_READ_TOKEN", ReadToken)], read.SecretEnv!.Select(p => (p.Key, p.Value)));
        var cross = Assert.Single(_cli.Calls, c => c.Args.Take(4).SequenceEqual(["provider", "create", "--name", "fleet-agent1-journal-cross-chat"]));
        Assert.Equal(["provider", "create", "--name", "fleet-agent1-journal-cross-chat", "--type", "fleet-journal-cross-chat", "--credential", "FLEET_JOURNAL_CROSS_CHAT_TOKEN"], cross.Args);
        Assert.Equal([("FLEET_JOURNAL_CROSS_CHAT_TOKEN", CrossChatToken)], cross.SecretEnv!.Select(p => (p.Key, p.Value)));
        foreach (var secret in new[] { Key, BotToken, IngestToken, ReadToken, CrossChatToken })
            Assert.DoesNotContain(_cli.Calls, c => c.Args.Any(a => a.Contains(secret, StringComparison.Ordinal)));
        var create = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Equal(["fleet-agent1-openai", "fleet-agent1-telegram", "fleet-agent1-journal", "fleet-agent1-journal-read", "fleet-agent1-journal-cross-chat"],
            create.Args.Select((a, i) => (a, i)).Where(p => p.a == "--provider").Select(p => create.Args[p.i + 1]));

        var app = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "app").Args[3];
        var bundle = Directory.GetParent(app)!.FullName;
        foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            foreach (var secret in new[] { Key, BotToken, IngestToken, ReadToken, CrossChatToken, "synthetic-read-mac", "synthetic-cross-mac" })
                Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
        var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(app, "appsettings.json")))!["Journal"]!;
        Assert.Null(journal["ReadToken"]);
        Assert.Null(journal["CrossChatToken"]);
        Assert.True(journal["FilesEnabled"]!.GetValue<bool>() && journal["SendEnabled"]!.GetValue<bool>() && journal["CrossChatEnabled"]!.GetValue<bool>());
        var mcp = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "workspace", ".mcp.json")))!["mcpServers"]!;
        Assert.Equal("{\"url\":\"http://127.0.0.1:8091/journal-files/v1/mcp\"}", mcp["fleet-journal-files"]!.ToJsonString());
    }

    [Fact]
    public async Task Journal_files_without_their_derived_read_token_refuse_before_the_old_runtime_stops()
    {
        var agent = BotAndJournalAgent();
        agent.Tools.Add(new AgentTool { ToolName = JournalGrants.SendGrant, IsEnabled = true });
        _store.Add(agent);
        var stops = 0;

        var result = await Runtime(_ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal("journal_token_unresolved", result.Code);
        Assert.Equal(0, stops);
        Assert.DoesNotContain(_cli.Calls, c => c.Args[0] == "provider" || c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
    }

    [Fact]
    public async Task Comms_journal_read_mcp_without_its_derived_token_refuses_before_the_old_runtime_stops()
    {
        _store.Add(JournalReadAgent());
        _generatedMcp = "{\"mcpServers\":{}}";

        var stops = 0;

        var result = await Runtime(_ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal("journal_token_unresolved", result.Code);
        Assert.Equal(0, stops);
        Assert.DoesNotContain(_cli.Calls, c => c.Args[0] == "provider" || c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Equal("docker", _store.Get("agent1").AppliedRuntimeKind);
    }

    [Fact]
    public async Task Reprovision_without_the_bot_attaches_only_the_remaining_providers()
    {
        var agent = _store.Add(BotAndJournalAgent());
        var runtime = Runtime();
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        agent.JournalEnabled = false;
        agent.EnvRefs.Clear();
        _cli.Calls.Clear();
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        // The reuse cut detached all three; only the native provider is attached again.
        Assert.Equal(3, _cli.Calls.Count(c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "detach"])));
        Assert.Equal(["fleet-agent1-openai"], _cli.Calls.Where(c => c.Args.Take(3).SequenceEqual(["sandbox", "provider", "attach"])).Select(c => c.Args[4]));
        Assert.Empty(ManagedRecord.Parse(_store.Get("agent1").AppliedRuntimeBinding).Applied!.ServiceProviders);
    }

    [Theory]
    [InlineData("bot_unresolved", "telegram_bot_token_unresolved")]
    [InlineData("journal_token_missing", "journal_token_unresolved")]
    public async Task Missing_bot_or_journal_credential_refuses_before_the_old_runtime_stops(string fault, string code)
    {
        _store.Add(BotAndJournalAgent());
        if (fault == "bot_unresolved") _broker.Credentials.Extra.Clear();
        else _generatedSettings = "{\"Agent\":{\"Name\":\"agent1\"},\"Journal\":{\"ExcludedChatIds\":\"\"}}";
        _host.DockerStates = ["running"];
        var stops = 0;
        var runtime = Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); });

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.Equal(code, result.Code);
        Assert.Equal(0, stops);
        Assert.Empty(_broker.Puts);
        Assert.DoesNotContain(_cli.Calls, c => c.Args[0] == "provider" || c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        var stored = _store.Get("agent1");
        Assert.Equal("docker", stored.AppliedRuntimeKind);
        Assert.Null(ManagedRecord.Parse(stored.AppliedRuntimeBinding).Candidate);
    }

    [Fact]
    public async Task Journal_enabled_without_a_bot_stays_capture_off_and_needs_no_journal_route()
    {
        var agent = _store.Add(ManagedAgent(desired: "openshell", applied: "docker"));
        agent.JournalEnabled = true; // ordinary provisioning mints no ingest token without the agent's own bot

        Assert.True((await Runtime().ApplyAsync("agent1", default)).Ok);
        Assert.DoesNotContain(_cli.Calls, c => c.Args.Contains("fleet-agent1-journal"));
        var app = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "app").Args[3];
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(app, "appsettings.json")))!["StockOpenShell"]!["JournalCapture"]);
    }

    // ── fixtures ───────────────────────────────────────────────────────────

    internal readonly List<string> _dockerProvisions = [];
    internal (bool Ok, string Message) _dockerResult = (true, "provisioned");
    private JsonObject? _serviceHosts = new() { ["temporal"] = "temporal.example.test", ["telegram"] = "telegram.example.test" };
    private string? _journalHost;
    private string? _generatedMcp;
    private string _generatedSettings = "{\"Agent\":{\"Name\":\"agent1\"}}";
    internal string DockerWorkspaceDir => Path.Combine(_dir, "docker-workspace");

    internal StockOpenShellRuntime Runtime(Func<string, Task<bool>>? dockerStop = null, AgentRegistry? registry = null,
        IStockCli? cli = null, TimeProvider? clock = null, Func<string, bool>? directorySync = null, IManagedBindingStore? store = null)
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_dir, "managed-authority.json"), new JsonObject
        {
            ["GatewayIssuer"] = "synthetic-gateway", ["GatewayAudience"] = "synthetic-hook",
            ["GatewayJwksPath"] = "/trust/gateway.jwks", ["AssertionPrivateKeyPath"] = "/outside/private.pem",
            ["AssertionPublicKeyPath"] = "/outside/public.pem", ["MemoryHost"] = "memory.example.test", ["BrokerHost"] = "broker.example.test",
            ["ServiceHosts"] = _serviceHosts?.DeepClone(),
            ["JournalHost"] = _journalHost,
        }.ToJsonString());
        var generated = Directory.CreateDirectory(Path.Combine(_dir, "generated")).FullName;
        File.WriteAllText(Path.Combine(generated, "appsettings.json"), _generatedSettings);
        if (_generatedMcp is not null) File.WriteAllText(Path.Combine(generated, ".mcp.json"), _generatedMcp);
        File.WriteAllText(Path.Combine(generated, Fleet.Shared.HarnessDefaults.CodexRootTomlFileName), "model = \"synthetic\"\n");
        var services = new ServiceCollection();
        if (registry is not null) services.AddSingleton(registry);
        return new StockOpenShellRuntime(Config(), cli ?? _cli, Provisioner(), store ?? _store, _host, _broker.Credentials,
            services.BuildServiceProvider(), NullLogger<StockOpenShellRuntime>.Instance)
        {
            LegTimeout = TimeSpan.FromMilliseconds(400),
            FanoutTimeout = TimeSpan.FromSeconds(5),
            StopSettle = TimeSpan.FromSeconds(1),
            Poll = TimeSpan.FromMilliseconds(10),
            GenerateInputs = (_, _) => Task.FromResult(generated),
            DockerStop = dockerStop ?? (_ => Task.FromResult(true)),
            ProvisionDocker = (name, _) =>
            {
                _dockerProvisions.Add(name + ":" + _store.Get(name).AppliedRuntimeKind);
                return Task.FromResult(_dockerResult);
            },
            DockerWorkspace = _ => DockerWorkspaceDir,
            Clock = clock ?? TimeProvider.System,
            DirectorySync = directorySync,
        };
    }

    internal StockBrokerProvisioner Provisioner() => new(Config(), _broker.Credentials) { HandlerFactory = () => _broker };

    /// <summary>The recorded host-operator owner (<c>StockOpenShell:ConfigDirOwnerUid</c>); unset by default.</summary>
    internal string? ConfigDirOwnerUid;

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
        if (ConfigDirOwnerUid is not null) values["StockOpenShell:ConfigDirOwnerUid"] = ConfigDirOwnerUid;
        foreach (var (key, value) in overrides ?? []) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    internal Agent ManagedAgent(string desired, string applied, ManagedRecord? record = null)
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

    internal ManagedBinding Binding(string name, Guid epoch) =>
        new(name, Guid.NewGuid().ToString(), epoch, "stock-agent:synthetic", 1, SandboxPolicyValidation.Hash(_store.PolicyYaml), "api_key",
            "SYNTHETIC_API_KEY", "fleet-agent1-openai", _host.Generation);

    /// <summary>
    /// #503: the active outside enrollment an applied binding of this agent carries after a verified apply: exact agent,
    /// UUID, epoch, stock commit and policy hash plus its receiving hosts (memory and any routed services).
    /// </summary>
    internal void Enroll(ManagedBinding binding, string agent = "agent1", JsonObject? services = null)
    {
        var path = Path.Combine(_dir, "managed", "authority", agent + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var record = new JsonObject
        {
            ["State"] = "active", ["Agent"] = agent, ["SandboxId"] = binding.SandboxId, ["Epoch"] = binding.Epoch.ToString(),
            ["ExpiresAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"), ["MemoryHost"] = SignedPolicy.Memory.Host,
            ["BrokerHost"] = "broker.example.test", ["StockCommit"] = StockOpenShellRuntime.StockCommit, ["PolicySha256"] = binding.PolicySha256,
        };
        if (services is not null) record["Services"] = services;
        File.WriteAllText(path, record.ToJsonString());
    }

    internal string AuthorityPath() => Path.Combine(_dir, "managed", "authority", "agent1.json");

    internal void WriteAuthority(string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuthorityPath())!);
        File.WriteAllText(AuthorityPath(), new JsonObject { ["State"] = state, ["Agent"] = "agent1" }.ToJsonString());
    }

    internal sealed record Call(List<string> Args, IReadOnlyDictionary<string, string>? SecretEnv);

    internal sealed class FakeCli : IStockCli
    {
        public readonly List<Call> Calls = [];
        /// <summary>The bound each call was given (review A7: transfer vs ordinary leg).</summary>
        public readonly List<(string[] Args, TimeSpan Timeout)> Timeouts = [];
        public readonly Dictionary<string, (string Id, string Phase)> Sandboxes = new();
        public readonly HashSet<string> Hang = [];
        public string Version = "openshell 0.1.5";
        public bool CreateTimesOut;
        /// <summary>#503 W2: the profile export document; consecutive exports alternate map order like the pinned CLI.</summary>
        public string ProfileExport = SyntheticProfile.Export;
        private int _exports;

        public async Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) { Calls.Add(new([.. args], secretEnv)); Timeouts.Add(([.. args], timeout)); }
            if (args.Count > 2 && Hang.Contains(args[2])) { await Task.Delay(Timeout.Infinite, ct); }
            string Name(int index) => args[index];
            return (args[0], args.Count > 1 ? args[1] : "") switch
            {
                ("--version", _) => new(0, Version + "\n"),
                ("provider", "get") => new(1, ""),
                ("provider", _) => new(0, ""),
                ("logs", _) => SwapOnLogs ? Swap(Name(1)) : new(0, "line\n"),
                ("sandbox", "create") => Create(args),
                ("sandbox", "get") => Sandboxes.TryGetValue(Name(2), out var s)
                    ? new(0, Get(Name(2), s))
                    : NameTaken ? new(0, JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString(), name = Name(2), phase = "Ready" })) : new(1, ""),
                ("sandbox", "start") => Start(Name(2)),
                ("sandbox", "stop") => Stop(Name(2)),
                ("sandbox", "provider") => Attachment(args),
                ("sandbox", "download") => Transfer(Name(2), () => Download(args[4])),
                ("sandbox", "upload") => Transfer(Name(2), () => Upload(args[3], args[4])),
                ("sandbox", "exec") => Transfer(args[3], () => new(OnExec?.Invoke(args[^1]) ?? 0, "")),
                ("sandbox", "list") => ListFails ? new(1, "") : List(args),
                ("profile", "export") => new(0, Interlocked.Increment(ref _exports) % 2 == 0 ? SyntheticProfile.Reordered(ProfileExport) : ProfileExport),
                _ => new(0, ""),
            };
        }

        private StockCliResult Create(IReadOnlyList<string> args)
        {
            // Pinned contract: --upload cannot be combined with a trailing command.
            if (args.Contains("--upload") && args.Contains("--")) return new(2, "");
            OnCreate?.Invoke();
            var name = args[args.ToList().IndexOf("--name") + 1];
            BeforeCreate?.Invoke(name);
            // #503 W3: the pinned server's first spec validation (InvalidArgument, nothing allocated); a taken name refuses too.
            if (CreateRejects || !StockSandboxName(name) || Sandboxes.ContainsKey(name)) return new(1, "");
            Sandboxes[name] = (Guid.NewGuid().ToString(), "Ready"); // the explicit launcher waits, staged, without a daemon
            Attached[name] = [.. args.Select((a, i) => (a, i)).Where(p => p.a == "--provider").Select(p => args[p.i + 1])];
            Booted[name] = [.. Attached[name]];
            // #503 W4: the stock labels create set, as `sandbox get -o json` reports them.
            Labels[name] = LabelsOverride ?? args.Select((a, i) => (a, i)).Where(p => p.a == "--label").Select(p => args[p.i + 1].Split('=', 2))
                .ToDictionary(kv => kv[0], kv => kv[1]);
            if (CreateFailsAfterAllocating) return new(1, "");
            return CreateTimesOut ? new(-1, "", TimedOut: true) : new(0, "");
        }

        public bool ListFails;
        /// <summary>Create fails like the pinned server's InvalidArgument: nonzero, nothing allocated.</summary>
        public bool CreateRejects;
        /// <summary>Every name is already taken by a foreign sandbox (a name collision).</summary>
        public bool NameTaken;
        /// <summary>Labels per sandbox name as the pinned <c>sandbox get -o json</c> reports them (absent: no labels key).</summary>
        public readonly Dictionary<string, Dictionary<string, string>> Labels = new();
        /// <summary>Labels the next created sandbox carries instead of the requested ones (a mismatched identity).</summary>
        public Dictionary<string, string>? LabelsOverride;
        /// <summary>Runs with the requested name just before create validates it (a list/create race).</summary>
        public Action<string>? BeforeCreate;
        /// <summary>Create allocates the requested, labelled sandbox and then reports failure (a genuine owned failure).</summary>
        public bool CreateFailsAfterAllocating;

        /// <summary>Pinned openshell-server validate_sandbox_name: 1–19 chars, a DNS-1123 label without consecutive hyphens.</summary>
        internal static bool StockSandboxName(string name) =>
            name.Length is >= 1 and <= StockOpenShellRuntime.MaxSandboxNameLength
            && System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9]))*\z");

        /// <summary>Listed names per page (page i is served for <c>--page-token p{i}</c>); default one page of Sandboxes.</summary>
        public List<string[]>? ListPages;

        // Pinned 0.1.5 (openshell-cli output.rs paginated_collection_value): -o json prints the
        // {"sandboxes":[…],"next_page_token":"…"} envelope, never a bare array (review F9).
        private StockCliResult List(IReadOnlyList<string> args)
        {
            var at = args.ToList().IndexOf("--page-token");
            var page = at < 0 ? 0 : int.Parse(args[at + 1][1..]);
            var names = ListPages is { } pages ? pages[page] : [.. Sandboxes.Keys];
            var next = ListPages is { } all && page + 1 < all.Count ? "p" + (page + 1) : "";
            return new(0, JsonSerializer.Serialize(new { sandboxes = names.Select(n => new { id = Sandboxes.TryGetValue(n, out var s) ? s.Id : "", name = n }), next_page_token = next }));
        }

        public readonly List<(string Remote, string? Content)> Uploads = [];
        public bool SwapOnLogs;
        public Action? OnCreate;
        public Func<string, int>? OnExec;

        // Pinned SSH admission accepts transfers only for a running (Ready) sandbox.
        private StockCliResult Transfer(string name, Func<StockCliResult> action) =>
            Sandboxes.TryGetValue(name, out var s) && s.Phase == "Ready" ? action() : new(1, "");

        /// <summary>Remote directories created by uploads (the stock image's own dirs are not modelled).</summary>
        public readonly HashSet<string> RemoteDirs = [];

        // Pinned 0.1.5 (openshell-cli ssh.rs sandbox_sync_up/ssh_tar_upload): a directory is archived under its
        // basename and extracted at DEST, landing at DEST/basename/...; a file lands at DEST/basename when DEST is
        // a directory, top-level or ends in '/', else at DEST itself. Uploads records each resolved remote file.
        private StockCliResult Upload(string local, string dest)
        {
            if (Directory.Exists(local))
            {
                var root = dest.TrimEnd('/') + "/" + Path.GetFileName(local.TrimEnd('/'));
                RemoteDirs.Add(root);
                foreach (var dir in Directory.GetDirectories(local, "*", SearchOption.AllDirectories))
                    RemoteDirs.Add(root + "/" + Path.GetRelativePath(local, dir).Replace('\\', '/'));
                foreach (var file in Directory.GetFiles(local, "*", SearchOption.AllDirectories))
                    Uploads.Add((root + "/" + Path.GetRelativePath(local, file).Replace('\\', '/'), File.ReadAllText(file)));
                return new(0, "");
            }
            var target = dest.EndsWith('/') || dest.LastIndexOf('/') == 0 || RemoteDirs.Contains(dest)
                ? dest.TrimEnd('/') + "/" + Path.GetFileName(local) : dest;
            Uploads.Add((target, File.Exists(local) ? File.ReadAllText(local) : null));
            return new(0, "");
        }

        // The pinned CLI extracts the directory's contents directly into the destination.
        private static StockCliResult Download(string staging)
        {
            File.WriteAllText(Path.Combine(staging, "marker-back.txt"), "from sandbox");
            Directory.CreateDirectory(Path.Combine(staging, "only-dir"));
            return new(0, "");
        }

        private StockCliResult Swap(string name)
        {
            Sandboxes[name] = (Guid.NewGuid().ToString(), "Ready");
            return new(0, "line\n");
        }

        /// <summary>
        /// #503 W9 pinned spawn semantics: the providers attached to each sandbox, and the set its main process was spawned
        /// with at its last boot (create or start). Stock captures the provider environment when the supervisor starts and
        /// injects it only at spawn (openshell-supervisor lib.rs:912, openshell-sandbox process.rs:514–523); an attach while
        /// running updates the spec and pushes config (grpc/sandbox.rs:1481–1516) but never the running main process.
        /// </summary>
        public readonly Dictionary<string, HashSet<string>> Attached = new();
        public readonly Dictionary<string, HashSet<string>> Booted = new();

        private StockCliResult Start(string name)
        {
            Booted[name] = Attached.TryGetValue(name, out var attached) ? [.. attached] : [];
            return Phase(name, "Ready");
        }

        private StockCliResult Attachment(IReadOnlyList<string> args)
        {
            if (args.Count != 5 || args[2] is not ("attach" or "detach")) return new(0, "");
            var set = Attached.TryGetValue(args[3], out var current) ? current : Attached[args[3]] = [];
            if (args[2] == "attach") set.Add(args[4]); else set.Remove(args[4]);
            return new(0, "");
        }

        /// <summary>#503 W8: extra `sandbox get -o json` fields per name (the pinned exit_code and conditions).</summary>
        public readonly Dictionary<string, JsonObject> Status = new();
        /// <summary>
        /// Names whose stop answers like the pinned failed-main shortcut: the server returns the Error sandbox unchanged and
        /// the CLI exits nonzero waiting for Stopped. <see cref="OnStop"/> runs first (for example, a racing replacement).
        /// </summary>
        public readonly HashSet<string> StopKeepsPhase = [];
        public Action<string>? OnStop;

        /// <summary>
        /// The pinned failed-main status: Error, exit code 1, Ready=False/MainProcessFailed, with the pinned <c>provisioning</c>
        /// field (settled by default; null is a row without a provisioning record).
        /// </summary>
        internal static JsonObject FailedMain(string readyStatus = "False", string reason = "MainProcessFailed", bool exitCode = true,
            bool? operationPending = false) => new()
        {
            ["exit_code"] = exitCode ? 1 : null,
            ["conditions"] = new JsonArray(
                new JsonObject { ["type"] = "Ready", ["status"] = readyStatus, ["reason"] = reason, ["message"] = "synthetic" },
                new JsonObject { ["type"] = "ConfigurationReady", ["status"] = "True", ["reason"] = "ConfigurationAccepted", ["message"] = "" }),
            ["provisioning"] = operationPending is { } pending
                ? new JsonObject { ["phase"] = "ready", ["driver_operation_pending"] = pending, ["driver_operation_id"] = pending ? "op-synthetic" : "" }
                : null,
        };

        private string Get(string name, (string Id, string Phase) s)
        {
            var json = new JsonObject { ["id"] = s.Id, ["name"] = name, ["phase"] = s.Phase };
            if (Labels.TryGetValue(name, out var labels)) json["labels"] = JsonSerializer.SerializeToNode(labels);
            if (Status.TryGetValue(name, out var status))
                foreach (var (key, value) in status) json[key] = value?.DeepClone();
            return json.ToJsonString();
        }

        private StockCliResult Stop(string name)
        {
            OnStop?.Invoke(name);
            return StopKeepsPhase.Contains(name) ? new(1, "") : Phase(name, "Stopped");
        }

        private StockCliResult Phase(string name, string phase)
        {
            if (!Sandboxes.TryGetValue(name, out var s)) return new(1, "");
            Sandboxes[name] = (s.Id, phase);
            return new(0, "");
        }
    }

    internal sealed class FakeStore : IManagedBindingStore
    {
        // Every selectable provider profile pinned to the synthetic export's SHA-256.
        internal static readonly string PinnedProfiles = new JsonObject
        {
            ["openai"] = SyntheticProfile.Digest, ["telegram"] = SyntheticProfile.Digest, ["fleet-journal"] = SyntheticProfile.Digest,
            ["fleet-journal-read"] = SyntheticProfile.Digest, ["fleet-journal-cross-chat"] = SyntheticProfile.Digest,
        }.ToJsonString();
        private readonly List<Agent> _agents = [];
        public bool RejectNextSwap;
        /// <summary>Rejects (false) or fails (DB exception) any swap that commits this applied kind.</summary>
        public string? RejectKind, ThrowKind;
        public readonly List<string> History = [];

        public Agent Add(Agent agent) { agent.Id = _agents.Count + 1; _agents.Add(agent); return agent; }
        public Agent Get(string name) => _agents.Single(a => a.Name == name);
        public Task<Agent?> LoadAsync(string name, CancellationToken ct) => ThrowOnLoad
            ? throw new InvalidOperationException("synthetic_db_failure")
            : Task.FromResult(_agents.FirstOrDefault(a => a.Name == name));
        public Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Agent>>(_agents);
        public string ProfileHashes = PinnedProfiles;
        /// <summary>#503: the saved artifact (a valid signer-bound policy); its stored hash is the actual content hash.</summary>
        public string PolicyYaml = SignedPolicy.MemoryOnly;
        /// <summary>Version 2's own saved bytes when a test appends a different artifact; otherwise the same as version 1.</summary>
        public string? PolicyYaml2;
        public Task<SandboxPolicyVersion?> LoadPolicyAsync(int id, CancellationToken ct)
        {
            var yaml = id == 2 && PolicyYaml2 is { } second ? second : PolicyYaml;
            return Task.FromResult<SandboxPolicyVersion?>(id is 1 or 2
                ? new SandboxPolicyVersion { Id = id, Version = id, Yaml = yaml, Sha256 = SandboxPolicyValidation.Hash(yaml), ProviderProfileHashes = ProfileHashes } : null);
        }

        /// <summary>Interceptor awaited inside every swap before it commits (applied kind, next binding).</summary>
        public Func<string?, string, Task>? OnSwap;
        /// <summary>Commits a swap of this applied kind, then throws (post-commit ambiguity).</summary>
        public string? ThrowAfterCommitKind;
        /// <summary>Rejects (false) / throws on the n-th swap without an applied kind after a swap of this kind committed.</summary>
        public bool RejectFollowUp, ThrowFollowUp;
        public bool ThrowOnLoad;
        private bool _kindCommitted;

        public async Task<bool> SwapAsync(int agentId, string? expected, string next, string? appliedKind, int? appliedPolicyVersionId, CancellationToken ct)
        {
            if (OnSwap is not null) await OnSwap(appliedKind, next);
            var agent = _agents.Single(a => a.Id == agentId);
            if (RejectNextSwap) { RejectNextSwap = false; return false; }
            if (appliedKind is not null && appliedKind == RejectKind) return false;
            if (appliedKind is not null && appliedKind == ThrowKind) throw new InvalidOperationException("synthetic_db_failure");
            if (appliedKind is null && _kindCommitted && RejectFollowUp) return false;
            if (appliedKind is null && _kindCommitted && ThrowFollowUp) throw new InvalidOperationException("synthetic_db_failure");
            if (agent.AppliedRuntimeBinding != expected) return false;
            agent.AppliedRuntimeBinding = next;
            History.Add(next);
            if (appliedKind is not null) { agent.AppliedRuntimeKind = appliedKind; agent.AppliedSandboxPolicyVersionId = appliedPolicyVersionId; _kindCommitted = true; }
            if (appliedKind is not null && appliedKind == ThrowAfterCommitKind)
            {
                ThrowOnLoad = ThrowOnLoadAfterCommit;
                throw new InvalidOperationException("synthetic_post_commit_failure");
            }
            return true;
        }
        public bool ThrowOnLoadAfterCommit;
    }

    internal sealed class FakeHost : IStockHostView
    {
        public string Generation = "gateway-1@start";
        public Task<string?> GatewayGenerationAsync(CancellationToken ct) => Task.FromResult<string?>(Generation);
        public Task<IReadOnlyList<string>?> RunningSandboxContainersAsync(string sandboxId, CancellationToken ct)
        {
            OnRunningRead?.Invoke();
            return Task.FromResult(RunningContainers);
        }
        /// <summary>Runs on each running-container read (for example, a restart racing the observation).</summary>
        public Action? OnRunningRead;
        /// <summary>Containers running under a sandbox UUID; null is an unobservable read.</summary>
        public IReadOnlyList<string>? RunningContainers = [];
        public IReadOnlyList<string>? DockerStates = [];
        public Task<IReadOnlyList<string>?> DockerContainerStatesAsync(string containerName, CancellationToken ct) => Task.FromResult(DockerStates);
        /// <summary>Labelled workload/supervisor containers; null (default) is an unobservable read.</summary>
        public IReadOnlyList<StockSandboxContainer>? SandboxContainers;
        public Task<IReadOnlyList<StockSandboxContainer>?> SandboxContainersAsync(string sandboxId, CancellationToken ct) => Task.FromResult(SandboxContainers);
    }

    internal sealed class FakeBroker : HttpMessageHandler
    {
        public readonly List<string> Puts = [];
        public readonly List<string> Deletes = [];
        public readonly List<string> Posts = [];
        public readonly List<string> Writes = []; // PUT and POST paths in order
        public readonly Dictionary<string, string> Bodies = [];
        public readonly HashSet<string> Existing = [];
        public bool FailDeletes;
        /// <summary>When set, the body of every per-user connections listing (review A8: malformed listings).</summary>
        public string? ConnectionsBody;
        public string? FailPutContaining;
        public HttpStatusCode WhoAmI = HttpStatusCode.OK;
        public readonly FakeCredentials Credentials = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpStatusCode status;
            if (request.Method == HttpMethod.Get && path.EndsWith("/api/whoami", StringComparison.Ordinal))
                status = WhoAmI;
            else if (request.Method == HttpMethod.Get && ConnectionsBody is not null && path.Contains("/api/connections/username/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ConnectionsBody) });
            else if (request.Method == HttpMethod.Get)
                status = Existing.Any(path.StartsWith) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            else if (request.Method == HttpMethod.Put || request.Method == HttpMethod.Post)
            {
                if (FailPutContaining is not null && path.Contains(FailPutContaining)) status = HttpStatusCode.Forbidden;
                else
                {
                    lock (Puts)
                    {
                        (request.Method == HttpMethod.Put ? Puts : Posts).Add(path);
                        Writes.Add(path);
                        Bodies[path] = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                    }
                    status = HttpStatusCode.Created;
                }
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

    internal sealed class FakeCredentials : ICredentialsReader
    {
        public readonly Dictionary<string, string> Extra = new();
        public string? Get(string key) => key switch
        {
            "SYNTHETIC_API_KEY" => Key,
            "BROKER_ADMIN_USER" => "synthetic-admin",
            "BROKER_ADMIN_PASSWORD" => "synthetic-password",
            _ => Extra.GetValueOrDefault(key),
        };
        public void InvalidateCache() { }
    }
}

/// <summary>
/// #503 shared fixture: a valid saved policy whose <c>network_middlewares</c> bind the fixed <c>stock-memory</c> signer to
/// exactly the given receiving hosts, each with its one inspected, enforced <c>host:443</c> MCP endpoint and exact MCP rules.
/// </summary>
internal static class SignedPolicy
{
    internal static readonly (string Service, string Host) Memory = ("memory", "memory.example.test");
    internal static readonly (string Service, string Host) Temporal = ("temporal", "temporal.example.test");
    internal static readonly (string Service, string Host) Telegram = ("telegram", "telegram.example.test");

    /// <summary>#503 W5: the exact tool each receiver endpoint's rules allow (declared before <see cref="MemoryOnly"/>, which reads it).</summary>
    internal static readonly Dictionary<string, string> Tools = new()
    {
        ["memory"] = "memory_get", ["temporal"] = "temporal_start_workflow", ["telegram"] = "send_message",
    };

    internal static readonly string MemoryOnly = Yaml(Memory);

    /// <summary>The one supported MCP rule form (pinned stock shape): the control methods, then tools/call per exact tool.</summary>
    internal static string Rules(params string[] tools) =>
        "        rules:\n          - allow:\n              method: initialize\n          - allow:\n              method: notifications/initialized\n"
        + "          - allow:\n              method: tools/list\n"
        + string.Concat(tools.Select(t => $"          - allow:\n              method: tools/call\n              tool: {t}\n"));

    internal static string Yaml(params (string Service, string Host)[] routes) => Yaml(routes, routes);

    /// <summary>Endpoints for <paramref name="endpoints"/>, signer bindings for <paramref name="signed"/> (negative cases).</summary>
    internal static string Yaml(IEnumerable<(string Service, string Host)> endpoints, IEnumerable<(string Service, string Host)> signed)
    {
        var text = new System.Text.StringBuilder("version: 1\nfilesystem_policy:\n  include_workdir: true\n  read_only: [/usr]\n  read_write: [/workspace]\n"
            + "process:\n  run_as_user: sandbox\n  run_as_group: sandbox\nlandlock:\n  compatibility: hard_requirement\n");
        text.Append(endpoints.Any() ? "network_policies:\n" : "network_policies: {}\n");
        foreach (var (service, host) in endpoints)
            text.Append($"  fleet-{service}:\n    endpoints:\n      - host: {host}\n        port: 443\n        protocol: mcp\n        enforcement: enforce\n"
                + Rules(Tools[service]) + "    binaries:\n      - path: /usr/bin/codex\n");
        if (signed.Any()) text.Append("network_middlewares:\n");
        foreach (var (service, host) in signed)
        {
            var (_, order) = StockManagedSignerPolicy.Entries["stock-" + service];
            text.Append($"  stock-{service}:\n    middleware: stock-memory\n    order: {order}\n    on_error: fail_closed\n"
                + $"    config:\n      service: {service}\n    endpoints:\n      include:\n        - {host}\n");
        }
        return text.ToString();
    }
}

/// <summary>
/// #503 W2 shared fixture: a synthetic <c>profile export -o json</c> document pinned by its canonical digest. The pinned
/// CLI renders maps from an unordered map, so <see cref="Reordered"/> is the same document as another export prints it.
/// </summary>
internal static class SyntheticProfile
{
    internal const string Export = "{\"id\":\"synthetic\",\"display_name\":\"Synthetic\",\"annotations\":{\"phleet.dev/status\":\"UNQUALIFIED\","
        + "\"phleet.dev/source\":\"synthetic\",\"owner\":\"example\"},\"credentials\":[{\"name\":\"api_key\",\"env_vars\":[\"OPENAI_API_KEY\"]}],"
        + "\"endpoints\":[{\"host\":\"api.example.test\",\"port\":443,\"protocol\":\"rest\",\"enforcement\":\"enforce\"}],\"binaries\":[\"/usr/bin/codex\"]}";

    internal static readonly string Digest = StockProfileDigest.Compute(Export)!;

    /// <summary>A real field change: the export now also allows another binary.</summary>
    internal static readonly string Mutated = Export.Replace("[\"/usr/bin/codex\"]", "[\"/usr/bin/codex\",\"/usr/bin/other\"]");

    /// <summary>The same document with every object's keys in reverse order and different whitespace.</summary>
    internal static string Reordered(string export) =>
        Reverse(JsonNode.Parse(export)!)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    private static JsonNode? Reverse(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Reverse().Select(p => KeyValuePair.Create(p.Key, Reverse(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray([.. a.Select(item => Reverse(item?.DeepClone()))]),
        _ => node?.DeepClone(),
    };
}
