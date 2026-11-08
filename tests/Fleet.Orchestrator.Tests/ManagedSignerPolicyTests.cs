using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>#503 C1: the saved signer subset and the pure managed route check (no runtime, gateway or signer).</summary>
public sealed class ManagedSignerPolicyTests
{
    private static readonly Dictionary<string, string> MemoryHosts = new() { ["memory"] = "memory.example.test" };
    private static readonly Dictionary<string, string> AllHosts = new()
    {
        ["memory"] = "memory.example.test", ["temporal"] = "temporal.example.test", ["telegram"] = "telegram.example.test",
    };

    /// <summary>The memory-only signed fixture with one textual replacement applied.</summary>
    private static string Memory(string from, string to)
    {
        Assert.Contains(from, SignedPolicy.MemoryOnly);
        return SignedPolicy.MemoryOnly.Replace(from, to);
    }

    private const string Binding = "  stock-memory:\n    middleware: stock-memory\n    order: 0\n    on_error: fail_closed\n"
        + "    config:\n      service: memory\n    endpoints:\n      include:\n        - memory.example.test\n";

    [Fact]
    public void One_and_three_entry_policies_and_old_storage_only_policies_save()
    {
        Assert.Null(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly, new()));
        Assert.Null(SandboxPolicyValidation.DescribeFault(SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal, SignedPolicy.Telegram), new()));
        // A save without bindings is still accepted for storage/manual use.
        Assert.Null(SandboxPolicyValidation.DescribeFault(ManagedStockRuntimeTests.PolicyYaml, new()));
        Assert.Null(SandboxPolicyValidation.DescribeFault("version: 1\nnetwork_middlewares: {}\n", new()));
    }

    [Theory]
    [InlineData("    middleware: stock-memory\n", "    middleware: other\n")]
    [InlineData("      service: memory\n", "      service: temporal\n")]
    [InlineData("    order: 0\n", "    order: 10\n")]
    [InlineData("    order: 0\n", "    order: '0'\n")]
    [InlineData("    order: 0\n", "    order: 00\n")]
    [InlineData("    on_error: fail_closed\n", "    on_error: fail_open\n")]
    [InlineData("        - memory.example.test\n", "        - Memory.example.test\n")]
    [InlineData("        - memory.example.test\n", "        - memory.example.test.\n")]
    [InlineData("        - memory.example.test\n", "        - '*.example.test'\n")]
    [InlineData("        - memory.example.test\n", "        - 192.0.2.10\n")]
    [InlineData("        - memory.example.test\n", "        - https://memory.example.test\n")]
    [InlineData("        - memory.example.test\n", "        - memory.example.test:443\n")]
    [InlineData("        - memory.example.test\n", "        - memory.example.test/mcp\n")]
    [InlineData("        - memory.example.test\n", "        - memory.example.test?x=1\n")]
    [InlineData("        - memory.example.test\n", "        - ''\n")]
    [InlineData("        - memory.example.test\n", "        - memory.example.test\n        - other.example.test\n")]
    [InlineData("      include:\n", "      exclude: [other.example.test]\n      include:\n")]
    [InlineData("  stock-memory:\n", "  memory:\n")]
    [InlineData("    config:\n      service: memory\n", "    config:\n      service: memory\n      extra: x\n")]
    [InlineData("    on_error: fail_closed\n", "")]
    [InlineData("    on_error: fail_closed\n", "    on_error: fail_closed\n    unknown: 1\n")]
    [InlineData("      service: memory\n", "      service: null\n")]
    [InlineData("      service: memory\n", "      service: !!str memory\n")]
    public void Wrong_registration_service_order_type_host_or_selector_is_refused(string from, string to) =>
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Memory(from, to), new()));

    [Fact]
    public void Duplicate_aliased_oversized_or_extra_entries_are_refused()
    {
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly + Binding, new())); // duplicate key
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly.Replace("network_middlewares:\n", "network_middlewares: &m\n"), new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly
            + Binding.Replace("stock-memory:\n", "stock-temporal:\n").Replace("order: 0", "order: 10").Replace("service: memory", "service: temporal"), new())); // same host twice
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly + Binding.Replace("  stock-memory:\n", "  stock-other:\n"), new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault("version: 1\nnetwork_middlewares: [stock-memory]\n", new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault("version: 1\nnetwork_middlewares:\n", new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly + new string('#', 70000), new()));
    }

    // #503 W5, pinned OpenShell bdb956f2 l7_validate.rs: :268–271 makes rules and access exclusive, :273–281 rejects every
    // MCP access preset and :302–307 refuses an MCP endpoint without rules or allow_all_known_mcp_methods. The accepted form
    // is the pinned lib.rs:4630–4678 shape: allow.method with the slash-separated wire names and one scalar allow.tool.
    [Fact]
    public void The_pinned_exact_mcp_rule_shape_saves_for_every_receiver()
    {
        Assert.Contains("        rules:\n          - allow:\n              method: initialize\n", SignedPolicy.MemoryOnly);
        Assert.Contains("              method: tools/call\n              tool: memory_get\n", SignedPolicy.MemoryOnly);
        Assert.Null(SandboxPolicyValidation.DescribeFault(SignedPolicy.MemoryOnly, new()));
        // Quoted scalars are the same stock strings; rule order is not significant.
        Assert.Null(SandboxPolicyValidation.DescribeFault(Memory("method: tools/list", "method: 'tools/list'"), new()));
        Assert.Null(SandboxPolicyValidation.DescribeFault(Memory(SignedPolicy.Rules("memory_get"),
            "        rules:\n          - allow: {method: tools/call, tool: memory_get}\n          - allow: {method: tools/list}\n"
            + "          - allow: {method: notifications/initialized}\n          - allow: {method: initialize}\n"), new()));
    }

    [Theory]
    [InlineData("        enforcement: enforce\n", "        enforcement: enforce\n        access: read-only\n")] // rules + access
    [InlineData("tools/call\n              tool: memory_get\n", "tools/call\n              tool: memory_get\n        access: full\n")]
    [InlineData("method: tools/list", "method: '*'")]
    [InlineData("method: tools/list", "method: tools/*")]
    [InlineData("method: tools/list", "method: tools-list")]
    [InlineData("method: tools/list", "method: resources/list")]
    [InlineData("method: tools/list", "method: null")]
    [InlineData("method: tools/list", "method: ''")]
    [InlineData("tool: memory_get", "tool: memory_*")]
    [InlineData("tool: memory_get", "tool: '*'")]
    [InlineData("tool: memory_get", "tool: Memory_get")]
    [InlineData("tool: memory_get", "tool: {any: [memory_get]}")]
    [InlineData("tool: memory_get", "tool: [memory_get]")]
    [InlineData("              tool: memory_get\n", "")] // tools/call without a tool
    [InlineData("              method: initialize\n", "              method: initialize\n              tool: memory_get\n")]
    [InlineData("              method: initialize\n", "              method: initialize\n              path: /mcp\n")]
    [InlineData("              tool: memory_get\n", "              tool: memory_get\n              params: {name: memory_get}\n")]
    [InlineData("          - allow:\n              method: initialize\n", "          - allow:\n              method: initialize\n          - allow:\n              method: initialize\n")]
    [InlineData("          - allow:\n              method: initialize\n", "          - allow: {}\n")]
    [InlineData("          - allow:\n              method: initialize\n", "          - deny:\n              method: initialize\n")]
    [InlineData("          - allow:\n              method: notifications/initialized\n", "")]
    [InlineData("        enforcement: enforce\n", "        enforcement: enforce\n        deny_rules: [{method: tools/call, tool: memory_store}]\n")]
    [InlineData("        enforcement: enforce\n", "        enforcement: enforce\n        mcp: {allow_all_known_mcp_methods: true}\n")]
    public void Access_presets_wildcards_and_any_other_mcp_rule_form_are_refused_at_save(string from, string to) =>
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Memory(from, to), new()));

    // #503 W13: the revision pinned Codex 0.164.0 negotiates and then sends as MCP-Protocol-Version, in the pinned stock block.
    private const string NativeVersions = "        mcp:\n          versions:\n            - '2025-06-18'\n";

    [Fact]
    public void The_native_revision_saves_and_admits_in_the_bounded_mcp_versions_list()
    {
        var native = Memory("        enforcement: enforce\n", "        enforcement: enforce\n" + NativeVersions);
        Assert.Null(SandboxPolicyValidation.DescribeFault(native, new()));
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(native, MemoryHosts));
        // Plain and flow scalars are the same stock strings; the pinned default may be listed beside it.
        Assert.Null(SandboxPolicyValidation.DescribeFault(Memory("        enforcement: enforce\n",
            "        enforcement: enforce\n        mcp: {versions: [2025-11-25, 2025-06-18]}\n"), new()));
        // The exact method and tool rules still decide: versions never widen them.
        Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(
            native.Replace("tool: memory_get", "tool: memory_store"), MemoryHosts));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(native.Replace("method: tools/list", "method: '*'"), new()));
        // A stored version without the block keeps its meaning (the stock default revision only).
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(SignedPolicy.MemoryOnly, MemoryHosts));
    }

    [Theory]
    [InlineData("        mcp: {versions: ['2026-07-28']}\n")]
    [InlineData("        mcp: {versions: ['2025-03-26']}\n")]
    [InlineData("        mcp: {versions: [latest]}\n")]
    [InlineData("        mcp: {versions: ['2025-06-18 ']}\n")]
    [InlineData("        mcp: {versions: []}\n")]
    [InlineData("        mcp: {versions: ['2025-06-18', '2025-06-18']}\n")]
    [InlineData("        mcp: {versions: '2025-06-18'}\n")]
    [InlineData("        mcp: {versions: null}\n")]
    [InlineData("        mcp: {}\n")]
    [InlineData("        mcp: {versions: ['2025-06-18'], allow_all_known_mcp_methods: true}\n")]
    [InlineData("        mcp: {versions: ['2025-06-18'], strict_tool_names: false}\n")]
    [InlineData("        mcp: {versions: ['2025-06-18'], max_body_bytes: 1024}\n")]
    public void Any_other_mcp_block_is_refused_at_save_and_admission(string block)
    {
        var yaml = Memory("        enforcement: enforce\n", "        enforcement: enforce\n" + block);
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(yaml, new()));
        Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(yaml, MemoryHosts));
    }

    [Fact]
    public void The_mcp_block_is_refused_on_any_other_protocol() =>
        Assert.NotNull(SandboxPolicyValidation.DescribeFault("version: 1\nnetwork_policies: {api: {endpoints: [{host: api.example.com, port: 443, "
            + "protocol: rest, enforcement: enforce, access: read-only, mcp: {versions: ['2025-06-18']}}]}}", new()));

    [Fact]
    public void An_mcp_endpoint_needs_rules_and_rules_need_mcp()
    {
        // The retained pre-W5 memory shape, and an MCP endpoint with no rules at all.
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Memory(SignedPolicy.Rules("memory_get"), "        access: read-only\n"), new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Memory(SignedPolicy.Rules("memory_get"), ""), new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Memory("        protocol: mcp\n", "        protocol: rest\n"), new()));
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Memory("        protocol: mcp\n", "        protocol: json-rpc\n"), new()));
        // Other inspected protocols keep their existing access presets.
        Assert.Null(SandboxPolicyValidation.DescribeFault(
            "version: 1\nnetwork_policies: {api: {endpoints: [{host: api.example.com, port: 443, protocol: rest, enforcement: enforce, access: read-only}]}}", new()));
    }

    [Fact]
    public void Exact_selected_routes_are_admitted()
    {
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(SignedPolicy.MemoryOnly, MemoryHosts));
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(
            SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal, SignedPolicy.Telegram), AllHosts));
        // Unrelated explicit destinations are unchanged.
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(SignedPolicy.MemoryOnly.Replace("network_middlewares:\n",
            "  api:\n    endpoints:\n      - {host: api.example.com, port: 443, protocol: rest, enforcement: enforce, access: read-only}\n"
            + "    binaries: [{path: /usr/bin/codex}]\nnetwork_middlewares:\n"), MemoryHosts));
    }

    [Theory]
    // Legacy unbound policy and a selected route without its binding or endpoint.
    [InlineData("unbound", StockManagedSignerPolicy.Missing)]
    [InlineData("no_endpoint", StockManagedSignerPolicy.Missing)]
    [InlineData("missing_temporal", StockManagedSignerPolicy.Missing)]
    // Extra/unselected bindings, wildcard hosts, duplicate or alternate-port receiver endpoints.
    [InlineData("extra_binding", StockManagedSignerPolicy.Scope)]
    [InlineData("wildcard", StockManagedSignerPolicy.Scope)]
    [InlineData("duplicate", StockManagedSignerPolicy.Scope)]
    [InlineData("alternate_port", StockManagedSignerPolicy.Scope)]
    [InlineData("plaintext_variant", StockManagedSignerPolicy.Scope)]
    [InlineData("trailing_dot_variant", StockManagedSignerPolicy.Scope)]
    [InlineData("port_80_only", StockManagedSignerPolicy.Scope)]
    // A different bound host, or a receiver endpoint that is not inspected/enforced/automatic-TLS/read-only/binary-scoped.
    [InlineData("other_host", StockManagedSignerPolicy.Mismatch)]
    [InlineData("tcp", StockManagedSignerPolicy.Mismatch)]
    [InlineData("rest", StockManagedSignerPolicy.Mismatch)]
    [InlineData("audit", StockManagedSignerPolicy.Mismatch)]
    [InlineData("memory_access_preset", StockManagedSignerPolicy.Mismatch)]
    [InlineData("rules_and_access", StockManagedSignerPolicy.Mismatch)]
    [InlineData("memory_write_tool", StockManagedSignerPolicy.Mismatch)]
    [InlineData("memory_extra_tool", StockManagedSignerPolicy.Mismatch)]
    [InlineData("memory_tool_glob", StockManagedSignerPolicy.Mismatch)]
    [InlineData("memory_any_methods", StockManagedSignerPolicy.Mismatch)]
    [InlineData("memory_no_initialized", StockManagedSignerPolicy.Mismatch)]
    [InlineData("temporal_access_preset", StockManagedSignerPolicy.Mismatch)]
    [InlineData("allow_all_known_methods", StockManagedSignerPolicy.Mismatch)]
    [InlineData("deny_rules", StockManagedSignerPolicy.Mismatch)]
    [InlineData("no_binaries", StockManagedSignerPolicy.Mismatch)]
    [InlineData("case_variant", StockManagedSignerPolicy.Mismatch)]
    [InlineData("unparsable", StockManagedSignerPolicy.Mismatch)]
    public void Missing_mismatched_or_ambiguous_bindings_are_named_faults(string fault, string code)
    {
        var hosts = MemoryHosts;
        var yaml = fault switch
        {
            "unbound" => ManagedStockRuntimeTests.PolicyYaml,
            "no_endpoint" => SignedPolicy.Yaml([], [SignedPolicy.Memory]),
            "missing_temporal" => SignedPolicy.Yaml([SignedPolicy.Memory, SignedPolicy.Temporal], [SignedPolicy.Memory]),
            "extra_binding" => SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal),
            "wildcard" => SignedPolicy.MemoryOnly.Replace("network_middlewares:\n", "  any:\n    endpoints:\n      - {host: '*.example.test', port: 443}\nnetwork_middlewares:\n"),
            "duplicate" => SignedPolicy.MemoryOnly.Replace("network_middlewares:\n",
                "  again:\n    endpoints:\n      - {host: memory.example.test, port: 443, protocol: mcp, enforcement: enforce}\n"
                + "    binaries: [{path: /usr/bin/codex}]\nnetwork_middlewares:\n"),
            "alternate_port" => SignedPolicy.MemoryOnly.Replace("network_middlewares:\n", "  alt:\n    endpoints:\n      - {host: memory.example.test, port: 8443}\nnetwork_middlewares:\n"),
            "plaintext_variant" => SignedPolicy.MemoryOnly.Replace("network_middlewares:\n", "  raw:\n    endpoints:\n      - {host: memory.example.test, port: 443, protocol: tcp}\nnetwork_middlewares:\n"),
            "trailing_dot_variant" => SignedPolicy.MemoryOnly.Replace("network_middlewares:\n", "  dot:\n    endpoints:\n      - {host: memory.example.test., port: 443}\nnetwork_middlewares:\n"),
            "port_80_only" => Memory("        port: 443\n", "        port: 80\n"),
            "other_host" => Memory("        - memory.example.test\n", "        - other.example.test\n"),
            "tcp" => Memory("        protocol: mcp\n        enforcement: enforce\n", "        protocol: tcp\n"),
            "rest" => Memory("        protocol: mcp\n", "        protocol: rest\n"),
            "audit" => Memory("        enforcement: enforce\n", "        enforcement: audit\n"),
            // #503 W5: the retained pre-W5 shape (a REST access preset instead of rules) and every widening of memory_get.
            "memory_access_preset" => Memory(SignedPolicy.Rules("memory_get"), "        access: read-only\n"),
            "rules_and_access" => Memory("        enforcement: enforce\n", "        enforcement: enforce\n        access: read-only\n"),
            "memory_write_tool" => Memory("tool: memory_get", "tool: memory_store"),
            // #512: a fixed read beside a write is still a widening.
            "memory_extra_tool" => Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_get", "memory_search", "memory_store")),
            "memory_tool_glob" => Memory("tool: memory_get", "tool: memory_*"),
            "memory_any_methods" => Memory("method: tools/list", "method: '*'"),
            "memory_no_initialized" => Memory("          - allow:\n              method: notifications/initialized\n", ""),
            // Bytes the saver never accepts are still refused at admission, never trusted to widen the exact rules.
            "allow_all_known_methods" => Memory("        enforcement: enforce\n", "        enforcement: enforce\n        mcp: {allow_all_known_mcp_methods: true}\n"),
            "deny_rules" => Memory("        enforcement: enforce\n", "        enforcement: enforce\n        deny_rules: [{method: tools/call, tool: memory_store}]\n"),
            "no_binaries" => Memory("    binaries:\n      - path: /usr/bin/codex\n", ""),
            "case_variant" => Memory("      - host: memory.example.test\n", "      - host: MEMORY.example.test\n"),
            "unparsable" => "version: 1\nnetwork_middlewares: [",
            "temporal_access_preset" => SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal)
                .Replace(SignedPolicy.Rules("temporal_start_workflow"), "        access: read-write\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        if (fault is "missing_temporal" or "temporal_access_preset") hosts = new() { ["memory"] = "memory.example.test", ["temporal"] = "temporal.example.test" };

        Assert.Equal(code, StockManagedSignerPolicy.DescribeBindingFault(yaml, hosts));
    }

    // #512 A1 (#518: four fixed reads): each fixed read alone, any pair and all explicit tools/call rules save and admit,
    // and AdmittedTools reports exactly the selected set.
    [Theory]
    [InlineData("memory_get")]
    [InlineData("memory_list")]
    [InlineData("memory_search")]
    [InlineData("memory_stats")]
    [InlineData("memory_list", "memory_search")]
    [InlineData("memory_get", "memory_stats")]
    [InlineData("memory_get", "memory_search", "memory_list")]
    [InlineData("memory_get", "memory_search", "memory_list", "memory_stats")]
    public void ManagedMemoryReads_each_fixed_read_alone_or_together_saves_and_admits(params string[] reads)
    {
        var yaml = Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules(reads));
        Assert.Null(SandboxPolicyValidation.DescribeFault(yaml, new()));
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(yaml, MemoryHosts));
        Assert.True(StockManagedSignerPolicy.AdmittedTools(yaml, SignedPolicy.Memory.Host)!.SetEquals(reads));
    }

    // #512 A1: writes, unknown names, case variants and globs never admit, beside a valid read or alone; an empty or
    // duplicated read list and a wildcard/extra method stay refused at save and admission.
    [Theory]
    [InlineData("memory_store")]
    [InlineData("memory_update")]
    [InlineData("memory_delete")]
    [InlineData("memory_unsupported")]
    [InlineData("memory_list", "memory_store")]
    [InlineData("memory_search", "memory_delete")]
    [InlineData("memory_stats", "memory_unsupported")]
    [InlineData("memory_get", "memory_search", "memory_list", "memory_stats", "memory_store")]
    public void ManagedMemoryReads_a_write_or_unknown_tool_never_admits(params string[] tools)
    {
        var yaml = Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules(tools));
        Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(yaml, MemoryHosts));
    }

    [Theory]
    [InlineData("tool: memory_list", "tool: Memory_list")]
    [InlineData("tool: memory_list", "tool: memory_*")]
    [InlineData("tool: memory_list", "tool: [memory_list, memory_search]")]
    [InlineData("              tool: memory_list\n", "")]
    [InlineData("tool: memory_list", "tool: Memory_stats")]
    [InlineData("tool: memory_list", "tool: memory_stat*")]
    [InlineData("tool: memory_list", "tool: '*'")]
    public void ManagedMemoryReads_case_glob_list_or_missing_tool_refuses_at_save_and_admission(string from, string to)
    {
        var yaml = Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_list")).Replace(from, to);
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(yaml, new()));
        Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(yaml, MemoryHosts));
    }

    [Fact]
    public void ManagedMemoryReads_empty_or_duplicate_read_lists_and_extra_methods_stay_refused()
    {
        foreach (var yaml in new[]
        {
            Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules()), // control methods only, no read
            Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_list", "memory_list")),
            Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_stats", "memory_stats")),
            Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_stats") + "          - allow:\n              method: resources/list\n"),
            Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_list") + "          - allow:\n              method: ping\n"),
            Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_search").Replace("method: tools/list", "method: '*'")),
        })
        {
            Assert.NotNull(SandboxPolicyValidation.DescribeFault(yaml, new()));
            Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(yaml, MemoryHosts));
        }
    }

    // #520: the optional endpoint allowed_ips list (1–32 distinct canonical IPv4 /32 host routes), shared by save and admission.
    private static string Api(string host, string? allowedIps) =>
        $"version: 1\nnetwork_policies:\n  api:\n    endpoints:\n      - host: {host}\n        port: 443\n"
        + (allowedIps is null ? "" : $"        allowed_ips: {allowedIps}\n") + "    binaries:\n      - path: /usr/bin/codex\n";

    /// <summary>The signed memory fixture whose receiver endpoint also carries <paramref name="allowedIps"/>.</summary>
    private static string ReceiverWith(string policy, string host, string allowedIps)
    {
        var port = $"      - host: {host}\n        port: 443\n";
        Assert.Contains(port, policy);
        return policy.Replace(port, port + $"        allowed_ips: {allowedIps}\n");
    }

    [Theory]
    [InlineData("[\"192.0.2.10/32\"]")]
    [InlineData("['192.0.2.10/32', 192.0.2.11/32, \"198.51.100.0/32\"]")]
    [InlineData("\n          - 192.0.2.10/32\n          - 255.255.255.255/32")]
    public void Allowed_ips_canonical_host_routes_save_in_block_and_flow_yaml(string list) =>
        Assert.Null(SandboxPolicyValidation.DescribeFault(Api("api.example.test", list), new()));

    [Fact]
    public void Allowed_ips_saves_in_json_policy_text()
    {
        const string json = "{\"version\": 1, \"network_policies\": {\"api\": {\"endpoints\": [{\"host\": \"api.example.test\", \"port\": 443, "
            + "\"allowed_ips\": [\"192.0.2.10/32\", \"192.0.2.11/32\"]}], \"binaries\": [{\"path\": \"/usr/bin/codex\"}]}}}\n";
        Assert.Null(SandboxPolicyValidation.DescribeFault(json, new()));
        Assert.Null(SandboxPolicyValidation.DescribeFault(json.Replace(", \"allowed_ips\": [\"192.0.2.10/32\", \"192.0.2.11/32\"]", ""), new()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("192.0.2.10/32")] // a scalar, not a list
    [InlineData("{a: 192.0.2.10/32}")]
    [InlineData("[[192.0.2.10/32]]")]
    [InlineData("[{ip: 192.0.2.10/32}]")]
    [InlineData("[example.com]")]
    [InlineData("[192.0.2.10]")] // bare address
    [InlineData("[192.0.2.0/24]")]
    [InlineData("[192.0.2.10/31]")]
    [InlineData("[2001:db8::1/128]")]
    [InlineData("['::ffff:192.0.2.10/128']")]
    [InlineData("[192.000.2.10/32]")]
    [InlineData("[192.0.2.010/32]")]
    [InlineData("[' 192.0.2.10/32']")]
    [InlineData("['192.0.2.10/32 ']")]
    [InlineData("[256.0.0.1/32]")]
    [InlineData("[192.0.2/32]")]
    [InlineData("[192.0.2.10/032]")]
    [InlineData("[192.0.2.10/32, 192.0.2.10/32]")]
    [InlineData("[null]")]
    [InlineData("[~]")]
    [InlineData("[1]")]
    public void Allowed_ips_other_shapes_addresses_and_spellings_are_refused(string list)
    {
        var fault = SandboxPolicyValidation.DescribeFault(Api("api.example.test", list), new());
        Assert.NotNull(fault);
        Assert.Contains("allowed_ips", fault);
    }

    [Fact]
    public void Allowed_ips_over_32_entries_is_refused_and_32_saves()
    {
        string List(int n) => "[" + string.Join(", ", Enumerable.Range(1, n).Select(i => $"192.0.2.{i}/32")) + "]";
        Assert.Null(SandboxPolicyValidation.DescribeFault(Api("api.example.test", List(32)), new()));
        Assert.Contains("allowed_ips", SandboxPolicyValidation.DescribeFault(Api("api.example.test", List(33)), new()));
    }

    [Fact]
    public void A_misspelled_allowed_ips_key_is_still_an_unknown_field() =>
        Assert.NotNull(SandboxPolicyValidation.DescribeFault(Api("api.example.test", "[192.0.2.10/32]").Replace("allowed_ips:", "allowed_ip:"), new()));

    // Pinned stock picks its host-gateway modes before allowed_ips (supervisor-network proxy/destination.rs:101–109); its
    // alias test lowercases, strips one trailing dot and compares to proxy.rs:215–219. A wildcard host could match an alias.
    [Theory]
    [InlineData("host.openshell.internal")]
    [InlineData("host.containers.internal")]
    [InlineData("host.docker.internal")]
    [InlineData("Host.Docker.Internal")]
    [InlineData("host.docker.internal.")]
    [InlineData("HOST.OPENSHELL.INTERNAL.")]
    [InlineData("'*.docker.internal'")] // quoted: a plain scalar starting with * is a YAML alias
    [InlineData("'*'")]
    public void Allowed_ips_on_gateway_alias_or_wildcard_hosts_is_refused_and_absence_is_unchanged(string host)
    {
        var fault = SandboxPolicyValidation.DescribeFault(Api(host, "[\"192.0.2.10/32\"]"), new());
        Assert.NotNull(fault);
        Assert.Contains("allowed_ips is not enforced for gateway-alias or wildcard hosts", fault);
        Assert.Null(SandboxPolicyValidation.DescribeFault(Api(host, null), new()));
    }

    [Fact]
    public void Receiver_allowed_ips_is_admitted_when_valid_and_a_mismatch_when_not()
    {
        var valid = ReceiverWith(SignedPolicy.MemoryOnly, SignedPolicy.Memory.Host, "[\"192.0.2.10/32\"]");
        Assert.Null(SandboxPolicyValidation.DescribeFault(valid, new()));
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(valid, MemoryHosts));
        var all = SignedPolicy.Yaml(SignedPolicy.Memory, SignedPolicy.Temporal, SignedPolicy.Telegram);
        foreach (var (_, host) in new[] { SignedPolicy.Memory, SignedPolicy.Temporal, SignedPolicy.Telegram })
            all = ReceiverWith(all, host, "['192.0.2.10/32']");
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(all, AllHosts));
        foreach (var bad in new[] { "[]", "[192.0.2.0/24]", "[192.0.2.10]", "[192.0.2.10/32, 192.0.2.10/32]" })
            Assert.Equal(StockManagedSignerPolicy.Mismatch,
                StockManagedSignerPolicy.DescribeBindingFault(ReceiverWith(SignedPolicy.MemoryOnly, SignedPolicy.Memory.Host, bad), MemoryHosts));
    }

    [Fact]
    public void Admission_refuses_allowed_ips_only_on_an_otherwise_admissible_gateway_alias_receiver()
    {
        // One lowercase alias with no trailing dot in the expected map, the signer binding and the :443 MCP endpoint passes
        // ValidateShape's IsExactHost, so the only difference between the two calls below is the added allowed_ips.
        const string alias = "host.docker.internal";
        var hosts = new Dictionary<string, string> { ["memory"] = alias };
        var policy = SignedPolicy.Yaml(("memory", alias));
        Assert.Null(StockManagedSignerPolicy.DescribeBindingFault(policy, hosts));
        Assert.Equal(StockManagedSignerPolicy.Mismatch,
            StockManagedSignerPolicy.DescribeBindingFault(ReceiverWith(policy, alias, "[\"192.0.2.10/32\"]"), hosts));
    }

    [Fact]
    public void A_route_map_without_memory_never_admits_an_unsigned_policy()
    {
        Assert.Equal(StockManagedSignerPolicy.Missing, StockManagedSignerPolicy.DescribeBindingFault(ManagedStockRuntimeTests.PolicyYaml, new Dictionary<string, string>()));
        Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(SignedPolicy.MemoryOnly,
            new Dictionary<string, string> { ["memory"] = "other.example.test" }));
    }
}
