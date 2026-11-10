using Fleet.Shared;

namespace Fleet.SecretProxy.Tests;

/// <summary>The formats the orchestrator and the forwarder share.</summary>
public sealed class SharedContractTests
{
    [Fact]
    public void Placeholder_HasTheFixedShape_IsRandom_AndHashesToLowercaseHex()
    {
        var a = SecretPlaceholders.Create();
        var b = SecretPlaceholders.Create();

        Assert.NotEqual(a, b);
        Assert.Equal(SecretPlaceholders.Length, a.Length);
        Assert.Matches(@"\Afleet_placeholder_[A-Za-z0-9_-]{43}\z", a);
        Assert.True(SecretPlaceholders.IsPlaceholder(a));
        Assert.Matches(@"\A[0-9a-f]{64}\z", SecretPlaceholders.Hash(a));
        Assert.NotEqual(SecretPlaceholders.Hash(a), SecretPlaceholders.Hash(b));
    }

    [Fact]
    public void Placeholder_SaysWhatItIs_AndHasNoVendorKeyShape()
    {
        var placeholder = SecretPlaceholders.Create();

        Assert.StartsWith("fleet_placeholder_", placeholder);
        Assert.Equal(SecretPlaceholders.Prefix.Length + SecretPlaceholders.RandomLength, SecretPlaceholders.Length);
        // Prefixes that secret scanners key on. A placeholder must never look like one of these: it is
        // committed, logged and pasted freely, and a real key of that kind must never be taken for one.
        foreach (var prefix in new[]
                 {
                     "phs_", "phc_", "phx_", "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_", "glpat-",
                     "sk_", "pk_", "rk_", "sk-", "xox", "AKIA", "ASIA", "AIza", "ya29.", "eyJ", "npm_", "pypi-", "dop_v1_",
                 })
            Assert.False(placeholder.StartsWith(prefix, StringComparison.Ordinal), prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<placeholder>")]
    [InlineData("fleet_placeholder_short")]
    [InlineData("ghs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void IsPlaceholder_RejectsEverythingElse(string? value) => Assert.False(SecretPlaceholders.IsPlaceholder(value));

    [Fact]
    public void FindAll_FindsPlaceholdersInsideText_ButNotALongerToken()
    {
        var a = SecretPlaceholders.Create();
        var b = SecretPlaceholders.Create();

        Assert.Equal([a, b, a], SecretPlaceholders.FindAll($"Bearer {a}, x-access-token:{b};{a}.owner"));
        Assert.Empty(SecretPlaceholders.FindAll(a + "A"));
        Assert.Empty(SecretPlaceholders.FindAll("no placeholder here"));
        Assert.Empty(SecretPlaceholders.FindAll(null));
    }

    [Theory]
    [InlineData("api.example.test")]
    [InlineData("a-b.c1.example.test")]
    public void HostName_AcceptsOneExactLowercaseDnsName(string host) => Assert.True(SecretBindingRules.IsHostName(host));

    [Theory]
    [InlineData("*.example.test")]
    [InlineData("API.example.test")]
    [InlineData("api.example.test:443")]
    [InlineData("https://api.example.test")]
    [InlineData("api.example.test/path")]
    [InlineData("api.example.test.")]
    [InlineData("localhost")]
    [InlineData("10.0.0.1")]
    [InlineData("user@api.example.test")]
    [InlineData("api..example.test")]
    [InlineData("-api.example.test")]
    [InlineData("")]
    [InlineData(null)]
    public void HostName_RejectsWildcardsPortsSchemesAddressesAndSingleLabels(string? host) =>
        Assert.False(SecretBindingRules.IsHostName(host));

    [Theory]
    [InlineData("EXAMPLE_API_KEY", "static", "api.example.test", "Authorization", null)]
    [InlineData("EXAMPLE_API_KEY", "static", "api.example.test", "X-Api-Key", null)]
    [InlineData("GITHUB_APP_PEM", "github_app", "api.github.com,github.com", "Authorization", null)]
    [InlineData("1BAD", "static", "api.example.test", "Authorization", "secret_binding_key_invalid")]
    [InlineData("HAS-DASH", "static", "api.example.test", "Authorization", "secret_binding_key_invalid")]
    [InlineData("K", "dynamic", "api.example.test", "Authorization", "secret_binding_kind_invalid")]
    [InlineData("K", "static", "", "Authorization", "secret_binding_hosts_missing")]
    [InlineData("K", "static", "*.example.test", "Authorization", "secret_binding_hosts_invalid")]
    [InlineData("K", "static", "a.example.test,a.example.test", "Authorization", "secret_binding_hosts_invalid")]
    [InlineData("K", "static", "a.example.test", "Host", "secret_binding_header_invalid")]
    [InlineData("K", "static", "a.example.test", "Cookie", "secret_binding_header_invalid")]
    [InlineData("K", "static", "a.example.test", "Bad Header", "secret_binding_header_invalid")]
    [InlineData("K", "static", "a.example.test", "", "secret_binding_header_invalid")]
    [InlineData("K", "github_app", "api.github.com", "X-Api-Key", "secret_binding_header_invalid")]
    public void BindingRules_NameTheFault(string key, string kind, string hosts, string header, string? fault)
    {
        var actual = SecretBindingRules.DescribeFault(
            key, kind, hosts.Split(',', StringSplitOptions.RemoveEmptyEntries), header);

        if (fault is null) Assert.Null(actual);
        else Assert.StartsWith(fault, actual);
    }

    [Fact]
    public void BindingRules_RefuseMoreThanTheHostLimit()
    {
        var hosts = Enumerable.Range(0, SecretBindingRules.MaxHosts + 1).Select(i => $"h{i}.example.test").ToArray();
        Assert.StartsWith("secret_binding_hosts_invalid", SecretBindingRules.DescribeFault("K", "static", hosts, "Authorization"));
    }

    [Fact]
    public void State_RoundTrips_AndCarriesNoPlaceholder()
    {
        var placeholder = SecretPlaceholders.Create();
        var state = new SecretProxyState
        {
            Version = SecretProxyState.CurrentVersion,
            GeneratedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Bindings = [new SecretProxyBinding("K", SecretBindingRules.KindStatic, ["api.example.test"], "Authorization", "synthetic")],
            Grants = [new SecretProxyGrant("agent1", "10.213.96.20", "K", SecretPlaceholders.Hash(placeholder))],
        };

        var json = state.Serialize();
        var parsed = SecretProxyState.TryParse(json, out var fault);

        Assert.Null(fault);
        Assert.DoesNotContain(placeholder, json);
        Assert.Equal("synthetic", parsed!.Bindings.Single().Value);
        Assert.Equal("10.213.96.20", parsed.Grants.Single().SourceIp);
    }

    [Theory]
    [InlineData("""{"version":1,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[],"grants":[{"agent":"a","sourceIp":"10.0.0.5","key":"MISSING","placeholderSha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""", "state_grant_invalid")]
    [InlineData("""{"version":1,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[{"key":"K","kind":"static","hosts":["a.example.test"],"header":"Authorization","value":"v","appId":null}],"grants":[{"agent":"a","sourceIp":"not-an-ip","key":"K","placeholderSha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""", "state_grant_invalid")]
    [InlineData("""{"version":1,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[{"key":"K","kind":"static","hosts":["a.example.test"],"header":"Authorization","value":"v","appId":null}],"grants":[{"agent":"a","sourceIp":"10.0.0.5","key":"K","placeholderSha256":"ABC"}]}""", "state_grant_invalid")]
    [InlineData("""{"version":1,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[{"key":"K","kind":"static","hosts":["a.example.test"],"header":"Authorization","value":"v","appId":null},{"key":"K","kind":"static","hosts":["b.example.test"],"header":"Authorization","value":"v","appId":null}],"grants":[]}""", "state_binding_invalid")]
    [InlineData("""{"version":1,"bindings":[],"grants":[]}""", "state_unreadable")]
    [InlineData("", "state_empty")]
    public void State_WithAnInvalidEntry_IsRefusedWhole(string json, string fault)
    {
        Assert.Null(SecretProxyState.TryParse(json, out var actual));
        Assert.Equal(fault, actual);
    }

    [Fact]
    public void Network_DerivesOneFixedAddressPerAgent()
    {
        Assert.Null(SecretProxyNetwork.TryParse(SecretProxyNetwork.DefaultSubnet, SecretProxyNetwork.DefaultAddress, out var network));

        Assert.Equal("10.213.96.2", network!.ProxyAddress.ToString());
        Assert.Equal("10.213.96.17", network.AgentAddress(1)!.ToString());
        Assert.Equal("10.213.97.16", network.AgentAddress(256)!.ToString());
        Assert.NotEqual(network.AgentAddress(7), network.AgentAddress(8));
        Assert.Null(network.AgentAddress(0));
        // 10.213.96.0/22 ends at 10.213.99.255 (broadcast): the last usable agent id is 1006.
        Assert.Equal("10.213.99.254", network.AgentAddress(1006)!.ToString());
        Assert.Null(network.AgentAddress(1007));
    }

    [Theory]
    [InlineData("10.213.96.0/22", "10.213.96.1", "secret_proxy_address_invalid")]
    [InlineData("10.213.96.0/22", "10.213.96.16", "secret_proxy_address_invalid")]
    [InlineData("10.213.96.0/22", "10.213.100.2", "secret_proxy_address_invalid")]
    [InlineData("10.213.96.0/22", "", "secret_proxy_address_invalid")]
    [InlineData("10.213.96.1/22", "10.213.96.2", "secret_proxy_subnet_invalid")]
    [InlineData("10.213.96.0/30", "10.213.96.2", "secret_proxy_subnet_invalid")]
    [InlineData("10.213.96.0/8", "10.213.96.2", "secret_proxy_subnet_invalid")]
    [InlineData("fd00::/64", "fd00::2", "secret_proxy_subnet_invalid")]
    [InlineData("not-a-subnet", "10.213.96.2", "secret_proxy_subnet_invalid")]
    [InlineData(null, null, "secret_proxy_subnet_invalid")]
    public void Network_NamesAnInvalidSubnetOrAddress(string? subnet, string? address, string fault)
    {
        Assert.StartsWith(fault, SecretProxyNetwork.TryParse(subnet, address, out var network));
        Assert.Null(network);
    }
}
