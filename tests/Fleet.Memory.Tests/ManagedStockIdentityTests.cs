using System.Text;
using System.Text.Json;
using Fleet.Shared.Security;

namespace Fleet.Memory.Tests;

/// <summary>#492 multi-agent authority lookup: the sandbox hint selects, the record still decides.</summary>
public sealed class ManagedStockIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("managed-authority-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string agent, string sandbox, string state = "active", int days = 1)
    {
        var path = Path.Combine(_dir, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            State = state, Agent = agent, SandboxId = sandbox, Epoch = Guid.NewGuid(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days), GatewayIssuer = "synthetic-gateway", GatewayAudience = "synthetic-hook",
            GatewayJwksPath = "/trust/gateway.jwks", AssertionPrivateKeyPath = "/outside/private.pem",
            AssertionPublicKeyPath = "/outside/public.pem", MemoryHost = "memory.example.test", BrokerHost = "broker.example.test",
            PolicySha256 = new string('b', 64), StockCommit = "bdb956f2cd65a635f1294fccf1d138d263bf650f",
        }));
        return path;
    }

    [Fact]
    public void Two_agents_resolve_to_their_own_records_by_sandbox()
    {
        Write("agent1", "agent1", "sandbox-1");
        Write("agent2", "agent2", "sandbox-2");
        Assert.Equal("agent1", StockAuthoritySource.Resolve("/unused", _dir, "sandbox-1").Agent);
        Assert.Equal("agent2", StockAuthoritySource.Resolve("/unused", _dir, "sandbox-2").Agent);
    }

    [Fact]
    public void Missing_ambiguous_revoked_expired_and_linked_records_never_match()
    {
        Write("revoked", "agent1", "sandbox-r", state: "revoked");
        Write("expired", "agent1", "sandbox-e", days: -1);
        Assert.Equal("authority_mapping_missing", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-r")).Message);
        Assert.Equal("authority_mapping_missing", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-e")).Message);

        var outside = Directory.CreateTempSubdirectory("outside-").FullName;
        try
        {
            var target = Path.Combine(outside, "x.json");
            File.Copy(Write("tmp", "agent3", "sandbox-l"), target);
            File.Delete(Path.Combine(_dir, "tmp.json"));
            File.CreateSymbolicLink(Path.Combine(_dir, "linked.json"), target);
            Assert.Equal("authority_mapping_missing", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-l")).Message);
        }
        finally { Directory.Delete(outside, recursive: true); }

        Write("a", "agent4", "sandbox-dup");
        Write("b", "agent5", "sandbox-dup");
        Assert.Equal("authority_mapping_ambiguous", Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.Resolve("/unused", _dir, "sandbox-dup")).Message);
    }

    [Fact]
    public void Without_a_directory_the_single_file_path_is_unchanged()
    {
        var file = Write("single", "agent1", "sandbox-1");
        Assert.Equal("agent1", StockAuthoritySource.Resolve(file, null, "any-hint").Agent);
    }

    [Fact]
    public void Assertion_sandbox_hint_is_read_but_never_trusted_alone()
    {
        static string Part(object value) => StockRequestProof.Encode(JsonSerializer.SerializeToUtf8Bytes(value));
        var token = Part(new { alg = "ES256", typ = "stock-memory-v1" }) + "." + Part(new { Sandbox = "sandbox-1" }) + ".sig";
        Assert.Equal("sandbox-1", StockAuthoritySource.SandboxHint(token));
        Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.SandboxHint("not-a-token"));
        Assert.Throws<StockIdentityDenied>(() => StockAuthoritySource.SandboxHint(Part(new { }) + "." + Part(new { Agent = "x" }) + ".sig"));
    }
}
