using System.Globalization;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Endpoints;
using Fleet.Shared.Security;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// #503: the one supported <c>network_middlewares</c> subset of the pinned stock schema (OpenShell bdb956f2), attaching the
/// fixed <c>stock-memory</c> signer registration (served by the existing <c>StockSupervisorMiddleware</c>) to exact
/// first-party receiving hosts, plus the managed admission check that a saved policy's bindings and receiver endpoints
/// match one agent's selected routes. Pure: no module state, network client or user-defined implementation registry.
/// The saved YAML is never rewritten; managed apply passes its exact bytes.
/// </summary>
internal static class StockManagedSignerPolicy
{
    /// <summary>The one gateway registration name and stock middleware this subset may attach.</summary>
    internal const string Registration = "stock-memory";

    internal const string Missing = "managed_signer_policy_missing";
    internal const string Mismatch = "managed_signer_policy_mismatch";
    internal const string Scope = "managed_signer_policy_scope";
    internal const string AuthorityUnavailable = "managed_signer_authority_unavailable";

    /// <summary>The one tool a memory receiver endpoint's rules may allow.</summary>
    internal const string MemoryTool = "memory_get";

    /// <summary>Entry key → (receiving service, fixed unique order).</summary>
    internal static readonly IReadOnlyDictionary<string, (string Service, int Order)> Entries =
        new Dictionary<string, (string, int)>(StringComparer.Ordinal)
        {
            ["stock-memory"] = (StockServices.Memory, 0),
            ["stock-temporal"] = ("temporal", 10),
            ["stock-telegram"] = ("telegram", 20),
        };

    // Lowercase DNS labels, no terminal dot; the final label must contain a letter, so no IPv4 literal passes.
    private static readonly Regex HostPattern = new(
        @"\A(?=.{1,253}\z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant);

    internal static bool IsExactHost(string host) => HostPattern.IsMatch(host) && host.Split('.')[^1].Any(char.IsAsciiLetterLower);

    /// <summary>
    /// Save-time shape of the parsed <c>network_middlewares</c> node: at most the three fixed entries, each exactly
    /// <c>middleware</c>/<c>order</c>/<c>on_error</c>/<c>config</c>/<c>endpoints</c>. Returns service → bound host.
    /// Throws <see cref="ArgumentException"/> with an operator-facing reason for any other form.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ValidateShape(YamlNode node)
    {
        var map = SandboxPolicyValidation.Map(node);
        Require(map.Children.Count <= Entries.Count, "network_middlewares holds at most the stock-memory, stock-temporal and stock-telegram bindings.");
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (keyNode, value) in map.Children)
        {
            var key = SandboxPolicyValidation.Value(keyNode);
            Require(Entries.TryGetValue(key, out var expected), "Signer binding names are stock-memory, stock-temporal or stock-telegram.");
            var entry = SandboxPolicyValidation.Map(value, "middleware", "order", "on_error", "config", "endpoints");
            Require(entry.Children.Count == 5, "Each signer binding needs exactly middleware, order, on_error, config and endpoints.");
            Require(SandboxPolicyValidation.Scalar(entry, "middleware") == Registration, "middleware must be the fixed stock-memory registration.");
            Require(SandboxPolicyValidation.PlainScalar(entry, "order") == expected.Order.ToString(CultureInfo.InvariantCulture),
                $"{key} order must be the unquoted integer {expected.Order}.");
            Require(SandboxPolicyValidation.Scalar(entry, "on_error") == "fail_closed", "on_error must be fail_closed.");
            var config = SandboxPolicyValidation.Map(SandboxPolicyValidation.Get(entry, "config")!, "service");
            Require(config.Children.Count == 1 && SandboxPolicyValidation.Scalar(config, "service") == expected.Service,
                $"{key} config must be exactly service: {expected.Service}.");
            var endpoints = SandboxPolicyValidation.Map(SandboxPolicyValidation.Get(entry, "endpoints")!, "include");
            Require(endpoints.Children.Count == 1, "Signer endpoints must be exactly one include list (no exclude).");
            var include = SandboxPolicyValidation.List(SandboxPolicyValidation.Get(endpoints, "include")!).ToList();
            Require(include.Count == 1, "Signer endpoints include exactly one host.");
            var host = SandboxPolicyValidation.Value(include[0]);
            Require(IsExactHost(host), "Signer endpoint must be one exact lowercase DNS name (no wildcard, IP, scheme, port, path, query or terminal dot).");
            Require(!bound.Values.Contains(host, StringComparer.Ordinal), "Each signer binding names a distinct host.");
            bound[expected.Service] = host;
        }
        return bound;
    }

    /// <summary>
    /// Managed admission of one saved policy for an exact service → receiving-host map (memory always present). Null, or
    /// <see cref="Missing"/> (no binding or receiver endpoint for a selected service), <see cref="Scope"/> (an extra or
    /// unselected binding, a wildcard endpoint host, a duplicate or alternate-port endpoint for a receiving host) or
    /// <see cref="Mismatch"/> (a different bound host or receiver endpoint form, or an unparsable policy).
    /// </summary>
    internal static string? DescribeBindingFault(string yaml, IReadOnlyDictionary<string, string> expectedHosts)
    {
        if (!expectedHosts.ContainsKey(StockServices.Memory)) return Missing;
        YamlMappingNode root;
        IReadOnlyDictionary<string, string> bound;
        try
        {
            if (SandboxPolicyValidation.ParseRoot(yaml, out _) is not { } parsed) return Mismatch;
            root = parsed;
            bound = SandboxPolicyValidation.Get(root, "network_middlewares") is { } node ? ValidateShape(node) : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException) { return Mismatch; }
        if (expectedHosts.Keys.Any(service => !bound.ContainsKey(service))) return Missing;
        if (bound.Keys.Any(service => !expectedHosts.ContainsKey(service))) return Scope;
        if (expectedHosts.Any(p => !string.Equals(bound[p.Key], p.Value, StringComparison.Ordinal))) return Mismatch;
        try { return DescribeEndpointFault(root, expectedHosts); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Mismatch; }
    }

    /// <summary>
    /// Exactly one inspected, enforced <c>host:443</c> MCP endpoint per receiving host, in a rule with binaries, stating its
    /// traffic as the exact MCP rules (memory: <see cref="MemoryTool"/> only).
    /// </summary>
    private static string? DescribeEndpointFault(YamlMappingNode root, IReadOnlyDictionary<string, string> expectedHosts)
    {
        var endpoints = new List<(YamlMappingNode Endpoint, YamlMappingNode Rule)>();
        if (SandboxPolicyValidation.Get(root, "network_policies") is { } rules)
            foreach (var pair in SandboxPolicyValidation.Map(rules).Children)
            {
                var rule = SandboxPolicyValidation.Map(pair.Value);
                if (SandboxPolicyValidation.Get(rule, "endpoints") is { } list)
                    foreach (var endpoint in SandboxPolicyValidation.List(list)) endpoints.Add((SandboxPolicyValidation.Map(endpoint), rule));
            }
        // A wildcard could match a receiving host outside the one signed endpoint.
        if (endpoints.Any(e => SandboxPolicyValidation.Scalar(e.Endpoint, "host").Contains('*'))) return Scope;
        foreach (var (service, host) in expectedHosts)
        {
            var matching = endpoints.Where(e => Comparable(SandboxPolicyValidation.Scalar(e.Endpoint, "host")) == Comparable(host)).ToList();
            if (matching.Count == 0) return Missing;
            if (matching.Count > 1) return Scope; // duplicate, alternate-port, plaintext or TCP variants of a receiving host
            var (endpoint, rule) = matching[0];
            if (SandboxPolicyValidation.Scalar(endpoint, "host") != host) return Mismatch;
            if (SandboxPolicyValidation.Scalar(endpoint, "port") != "443") return Scope;
            if (SandboxPolicyValidation.Scalar(endpoint, "protocol") != "mcp" || SandboxPolicyValidation.Scalar(endpoint, "enforcement") != "enforce"
                || SandboxPolicyValidation.Scalar(endpoint, "tls") != "") return Mismatch;
            // #503 W5: exact MCP rules, never an access preset (pinned stock rejects them) or a stock field outside the saved
            // subset such as mcp.allow_all_known_mcp_methods; memory stays read-only as memory_get alone.
            SandboxPolicyValidation.Map(endpoint, "host", "port", "protocol", "tls", "enforcement", "rules");
            var tools = SandboxPolicyValidation.McpRules(endpoint);
            if (service == StockServices.Memory && !tools.SetEquals([MemoryTool])) return Mismatch;
            if (SandboxPolicyValidation.Get(rule, "binaries") is not { } binaries || !SandboxPolicyValidation.List(binaries).Any()) return Mismatch;
        }
        return null;
    }

    private static string Comparable(string host) => host.TrimEnd('.').ToLowerInvariant();

    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
}
