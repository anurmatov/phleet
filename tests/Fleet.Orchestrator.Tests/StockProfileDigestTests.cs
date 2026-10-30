using System.Security.Cryptography;
using System.Text;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #503 W2: one canonical full-document digest of a stock profile export, shared by the managed admission checker and
/// the operator producer (<c>scripts/stock-profile-digest.sh</c>). The fixture digest below is asserted by both.
/// </summary>
public sealed class StockProfileDigestTests
{
    /// <summary>Also asserted by tests/scripts/test_stock_profile_digest.py for the producer script.</summary>
    internal const string FixtureDigest = "bc2fa12d386f87e19cab2e7faccee65a771c07eed68a0a483476a97ba2faf84c";

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Fleet.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, "tests", "fixtures", "stock-profile", name));
        throw new InvalidOperationException("Fleet.sln not found above the test output");
    }

    private static string Raw(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Map_order_whitespace_and_string_escaping_never_change_the_digest()
    {
        var export = Fixture("export.json");
        var reordered = Fixture("export-reordered.json");
        Assert.NotEqual(Raw(export), Raw(reordered)); // what the raw-stdout pin compared
        Assert.Equal(FixtureDigest, StockProfileDigest.Compute(export));
        Assert.Equal(FixtureDigest, StockProfileDigest.Compute(reordered));
        Assert.Equal(StockProfileDigest.Compute(SyntheticProfile.Export), StockProfileDigest.Compute(SyntheticProfile.Reordered(SyntheticProfile.Export)));
    }

    [Theory]
    [InlineData("\"UNQUALIFIED\"", "\"QUALIFIED\"")]                                // an annotation value
    [InlineData("\"z-last\"", "\"z-other\"")]                                       // an annotation key
    [InlineData("\"host\": \"api.example.test\", \"port\": 443, \"path\": \"/v1/models\"", "\"host\": \"api.example.test\", \"port\": 8443, \"path\": \"/v1/models\"")]
    [InlineData("\"path\": \"/v1/responses\", \"protocol\"", "\"path\": \"/v1/other\", \"protocol\"")]
    [InlineData("\"api.example.test\", \"port\": 443, \"path\": \"/v1/models\"", "\"evil.example.test\", \"port\": 443, \"path\": \"/v1/models\"")]
    [InlineData("[\"/usr/bin/codex\", \"/usr/lib/synthetic/bin/codex\"]", "[\"/usr/lib/synthetic/bin/codex\", \"/usr/bin/codex\"]")] // array order is meaningful
    [InlineData("[\"/usr/bin/codex\", \"/usr/lib/synthetic/bin/codex\"]", "[\"/usr/bin/codex\", \"/usr/lib/synthetic/bin/codex\", \"/bin/sh\"]")]
    [InlineData("[\"SYNTHETIC_ACCESS_TOKEN\"]", "[\"SYNTHETIC_ACCESS_TOKEN\", \"OTHER_TOKEN\"]")]
    [InlineData("\"required\": true", "\"required\": false")]
    [InlineData("\"refresh_before_seconds\": 300", "\"refresh_before_seconds\": \"300\"")] // a type change
    [InlineData("\"deprecated\": null", "\"deprecated\": \"null\"")]
    [InlineData(",\n  \"deprecated\": null", "")]                                     // a removed field
    [InlineData("\"deprecated\": null", "\"deprecated\": null, \"extra\": 1")]       // an added field
    public void Every_real_field_change_changes_the_digest(string from, string to)
    {
        var export = Fixture("export.json");
        Assert.Contains(from, export);
        var changed = StockProfileDigest.Compute(export.Replace(from, to));
        Assert.NotNull(changed);
        Assert.NotEqual(FixtureDigest, changed);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":1}")]                  // duplicate key
    [InlineData("{\"a\":1.5}")]                       // non-integer number
    [InlineData("{\"a\":1e3}")]
    [InlineData("{\"a\":9223372036854775808}")]       // outside signed 64-bit
    [InlineData("{\"a\":NaN}")]
    [InlineData("[{\"a\":1}]")]                       // not an object
    [InlineData("profile: synthetic\n")]              // the old YAML export
    [InlineData("{\"a\":\"\\ud800\"}")]               // a lone surrogate
    [InlineData("{\"a\":1} {\"b\":2}")]
    public void Exports_that_cannot_be_canonicalized_are_never_a_digest(string export) =>
        Assert.Null(StockProfileDigest.Compute(export));

    [Fact]
    public void Nesting_beyond_sixty_four_containers_refuses()
    {
        string Nested(int depth) => "{\"a\":" + string.Concat(Enumerable.Repeat("[", depth - 1)) + "1" + string.Concat(Enumerable.Repeat("]", depth - 1)) + "}";
        Assert.NotNull(StockProfileDigest.Compute(Nested(64)));
        Assert.Null(StockProfileDigest.Compute(Nested(65)));
    }
}

/// <summary>#503 W2: managed admission uses the canonical digest for pins, with strict refusal unchanged.</summary>
public sealed class ManagedProfileDigestAdmissionTests : IDisposable
{
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Identical_exports_in_varying_map_order_pass_the_pin()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];

        var first = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(first.Ok, first.Message);
        // The fixture CLI alternates map order on every export; reprovision exports again in the other order.
        Assert.True((await _t.Runtime().ApplyAsync("agent1", default)).Ok);
        Assert.True(_t._cli.Calls.Count(c => c.Args.Take(2).SequenceEqual(["profile", "export"])) >= 2);
        Assert.All(_t._cli.Calls.Where(c => c.Args.Take(2).SequenceEqual(["profile", "export"])), c => Assert.Equal(["-o", "json"], c.Args.TakeLast(2)));
    }

    [Theory]
    [InlineData("mutated", "provider_profile_hash_mismatch:openai")]
    [InlineData("yaml", "provider_profile_unverifiable:openai")]
    [InlineData("duplicate", "provider_profile_unverifiable:openai")]
    public async Task A_changed_or_uncanonicalizable_export_refuses_before_the_old_runtime_stops(string export, string code)
    {
        _t._cli.ProfileExport = export switch
        {
            "mutated" => SyntheticProfile.Mutated,
            "yaml" => "profile: synthetic\n",
            _ => SyntheticProfile.Export.TrimEnd('}') + ",\"id\":\"other\"}",
        };
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var stops = 0;
        _t._host.DockerStates = ["running"];

        var result = await _t.Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal((409, code), (result.Status, result.Code));
        Assert.Equal(0, stops);
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.Empty(_t._broker.Puts);
    }
}
