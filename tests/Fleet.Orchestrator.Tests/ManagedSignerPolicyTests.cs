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
            "memory_extra_tool" => Memory(SignedPolicy.Rules("memory_get"), SignedPolicy.Rules("memory_get", "memory_search")),
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

    [Fact]
    public void A_route_map_without_memory_never_admits_an_unsigned_policy()
    {
        Assert.Equal(StockManagedSignerPolicy.Missing, StockManagedSignerPolicy.DescribeBindingFault(ManagedStockRuntimeTests.PolicyYaml, new Dictionary<string, string>()));
        Assert.Equal(StockManagedSignerPolicy.Mismatch, StockManagedSignerPolicy.DescribeBindingFault(SignedPolicy.MemoryOnly,
            new Dictionary<string, string> { ["memory"] = "other.example.test" }));
    }
}
