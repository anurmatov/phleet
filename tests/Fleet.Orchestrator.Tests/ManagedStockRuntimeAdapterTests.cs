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
        File.WriteAllText(guestFile, _cli.Uploads.Last(u => u.Remote == "/trust/enrollment.json").Content!);
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
    [InlineData("unrouted", "managed_route_unsupported:mcp:fleet-temporal")]
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
        File.WriteAllText(guestFile, _cli.Uploads.Last(u => u.Remote == "/trust/enrollment.json").Content!);
        Assert.Equal(["temporal_start_workflow"], Fleet.Shared.Security.StockEnrollmentAuthority.Load(guestFile).ServiceTools("temporal"));
        // The workload's MCP config names each routed service like memory: exact HTTPS host, no query identity.
        var staged = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && c.Args[4] == "/workspace").Args[3];
        Assert.Equal("{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"},"
            + "\"fleet-telegram\":{\"url\":\"https://telegram.example.test/mcp\"},\"fleet-temporal\":{\"url\":\"https://temporal.example.test/mcp\"}}}",
            File.ReadAllText(Path.Combine(staged, ".mcp.json")));

        // Reprovision keeps the workspace but refreshes its routes from the current grants.
        agent.McpEndpoints.RemoveAll(e => e.McpName == "fleet-telegram");
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
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
        Assert.StartsWith("fleet-agent1-", record.Candidate.SandboxName);
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
        var staged = Assert.Single(_cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && c.Args[4] == "/workspace").Args[3];
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

    // ── fixtures ───────────────────────────────────────────────────────────

    private readonly List<string> _dockerProvisions = [];
    private (bool Ok, string Message) _dockerResult = (true, "provisioned");
    private JsonObject? _serviceHosts = new() { ["temporal"] = "temporal.example.test", ["telegram"] = "telegram.example.test" };
    private string DockerWorkspaceDir => Path.Combine(_dir, "docker-workspace");

    private StockOpenShellRuntime Runtime(Func<string, Task<bool>>? dockerStop = null)
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_dir, "managed-authority.json"), new JsonObject
        {
            ["GatewayIssuer"] = "synthetic-gateway", ["GatewayAudience"] = "synthetic-hook",
            ["GatewayJwksPath"] = "/trust/gateway.jwks", ["AssertionPrivateKeyPath"] = "/outside/private.pem",
            ["AssertionPublicKeyPath"] = "/outside/public.pem", ["MemoryHost"] = "memory.example.test", ["BrokerHost"] = "broker.example.test",
            ["ServiceHosts"] = _serviceHosts?.DeepClone(),
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
            DockerStop = dockerStop ?? (_ => Task.FromResult(true)),
            ProvisionDocker = (name, _) =>
            {
                _dockerProvisions.Add(name + ":" + _store.Get(name).AppliedRuntimeKind);
                return Task.FromResult(_dockerResult);
            },
            DockerWorkspace = _ => DockerWorkspaceDir,
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
        public bool CreateTimesOut;
        public string ProfileYaml = "profile: synthetic\n";

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
                ("logs", _) => SwapOnLogs ? Swap(Name(1)) : new(0, "line\n"),
                ("sandbox", "create") => Create(args),
                ("sandbox", "get") => Sandboxes.TryGetValue(Name(2), out var s)
                    ? new(0, JsonSerializer.Serialize(new { id = s.Id, name = Name(2), phase = s.Phase })) : new(1, ""),
                ("sandbox", "start") => Phase(Name(2), "Ready"),
                ("sandbox", "stop") => Phase(Name(2), "Stopped"),
                ("sandbox", "download") => Transfer(Name(2), () => Download(args[4])),
                ("sandbox", "upload") => Transfer(Name(2), () => { Uploads.Add((args[4], File.Exists(args[3]) ? File.ReadAllText(args[3]) : null)); return new(0, ""); }),
                ("sandbox", "exec") => Transfer(args[3], () => new(0, "")),
                ("sandbox", "list") => ListFails ? new(1, "") : new(0, "[]"),
                ("profile", "export") => new(0, ProfileYaml),
                _ => new(0, ""),
            };
        }

        private StockCliResult Create(IReadOnlyList<string> args)
        {
            // Pinned contract: --upload cannot be combined with a trailing command.
            if (args.Contains("--upload") && args.Contains("--")) return new(2, "");
            OnCreate?.Invoke();
            var name = args[args.ToList().IndexOf("--name") + 1];
            Sandboxes[name] = (Guid.NewGuid().ToString(), "Ready"); // the explicit launcher waits, staged, without a daemon
            return CreateTimesOut ? new(-1, "", TimedOut: true) : new(0, "");
        }

        public bool ListFails;
        public readonly List<(string Remote, string? Content)> Uploads = [];
        public bool SwapOnLogs;
        public Action? OnCreate;

        // Pinned SSH admission accepts transfers only for a running (Ready) sandbox.
        private StockCliResult Transfer(string name, Func<StockCliResult> action) =>
            Sandboxes.TryGetValue(name, out var s) && s.Phase == "Ready" ? action() : new(1, "");

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
        public readonly List<string> History = [];

        public Agent Add(Agent agent) { agent.Id = _agents.Count + 1; _agents.Add(agent); return agent; }
        public Agent Get(string name) => _agents.Single(a => a.Name == name);
        public Task<Agent?> LoadAsync(string name, CancellationToken ct) => Task.FromResult(_agents.FirstOrDefault(a => a.Name == name));
        public Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Agent>>(_agents);
        public string ProfileHashes = "{}";
        public Task<SandboxPolicyVersion?> LoadPolicyAsync(int id, CancellationToken ct) => Task.FromResult<SandboxPolicyVersion?>(
            id is 1 or 2 ? new SandboxPolicyVersion { Id = id, Version = id, Yaml = ManagedStockRuntimeTests.PolicyYaml, Sha256 = new string('b', 64), ProviderProfileHashes = ProfileHashes } : null);

        public Task<bool> SwapAsync(int agentId, string? expected, string next, string? appliedKind, int? appliedPolicyVersionId, CancellationToken ct)
        {
            var agent = _agents.Single(a => a.Id == agentId);
            if (RejectNextSwap) { RejectNextSwap = false; return Task.FromResult(false); }
            if (agent.AppliedRuntimeBinding != expected) return Task.FromResult(false);
            agent.AppliedRuntimeBinding = next;
            History.Add(next);
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
        public IReadOnlyList<string>? DockerStates = [];
        public Task<IReadOnlyList<string>?> DockerContainerStatesAsync(string containerName, CancellationToken ct) => Task.FromResult(DockerStates);
    }

    private sealed class FakeBroker : HttpMessageHandler
    {
        public readonly List<string> Puts = [];
        public readonly List<string> Deletes = [];
        public readonly HashSet<string> Existing = [];
        public bool FailDeletes;
        public string? FailPutContaining;
        public HttpStatusCode WhoAmI = HttpStatusCode.OK;
        public readonly FakeCredentials Credentials = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpStatusCode status;
            if (request.Method == HttpMethod.Get && path.EndsWith("/api/whoami", StringComparison.Ordinal))
                status = WhoAmI;
            else if (request.Method == HttpMethod.Get)
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
