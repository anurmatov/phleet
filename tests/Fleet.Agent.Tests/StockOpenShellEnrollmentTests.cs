using System.Text.Json;
using Fleet.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Agent.Tests;

public sealed class StockOpenShellEnrollmentTests
{
    [Fact]
    public void Disabled_DoesNotReadEnrollmentOrChangeRegistration()
    {
        var absent = new ServiceCollection();
        var empty = new ServiceCollection();
        absent.AddAgentCoreServices(new ConfigurationBuilder().Build());
        empty.AddAgentCoreServices(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [StockOpenShellEnrollment.ConfigurationKey] = "" }).Build());
        Assert.NotEmpty(absent);
        Assert.Equal(absent.Select(s => (s.ServiceType, s.Lifetime)), empty.Select(s => (s.ServiceType, s.Lifetime)));
    }

    [Theory]
    [InlineData("true", null, "stock_enrollment_file_missing")]
    [InlineData("perhaps", null, "stock_enrollment_enabled_invalid")]
    [InlineData("false", "/synthetic/enrollment.json", "stock_enrollment_disabled_with_file")]
    public void MissingOrInconsistentOptIn_Refuses(string enabled, string? path, string reason)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StockOpenShell:Enabled"] = enabled, [StockOpenShellEnrollment.ConfigurationKey] = path
        }).Build();
        var services = new ServiceCollection();
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => services.AddAgentCoreServices(configuration)).Message);
        Assert.Empty(services);
    }

    [Fact]
    public void MissingFile_RefusesBeforeCoreRegistration()
    {
        var services = new ServiceCollection();
        var configuration = Configuration("/missing/enrollment.json");
        var error = Assert.Throws<InvalidOperationException>(() => services.AddAgentCoreServices(configuration));
        Assert.Equal("stock_enrollment_unreadable", error.Message);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("revoked", "agent1", "stock_enrollment_revoked")]
    [InlineData("unknown", "agent1", "stock_enrollment_state_unknown")]
    [InlineData("active", "agent2", "stock_enrollment_agent_mismatch")]
    public void InvalidEnrollment_RefusesWithoutEchoingValues(string state, string agent, string reason)
    {
        WithRecord(record => { record["state"] = state; record["agent"] = agent; }, reason);
    }

    [Fact]
    public void Expired_Refuses() => WithRecord(record => record["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1), "stock_enrollment_expired");

    [Fact]
    public void ForkCommit_Refuses() => WithRecord(record => record["stockCommit"] = new string('a', 40), "stock_enrollment_stock_commit");

    [Fact]
    public void MissingSandbox_Refuses() => WithRecord(record => record["sandboxId"] = "", "stock_enrollment_sandbox_missing");

    [Fact]
    public void MissingRolePolicy_Refuses() => WithRecord(record => record["policySha256"] = "", "stock_enrollment_policy_missing");

    [Fact]
    public void UnknownField_Refuses() => WithRecord(record => record["privateValue"] = "do-not-log", "stock_enrollment_invalid");

    [Fact]
    public void ActiveRecord_CannotSelfAttestMissingReceiver()
        => WithRecord(_ => { }, "stock_enrollment_required_route_unverified:memory");

    [Fact]
    public void DuplicateField_Refuses()
    {
        WithFile("{\"state\":\"active\",\"state\":\"revoked\"}", "stock_enrollment_invalid");
    }

    private static IConfiguration Configuration(string path) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["StockOpenShell:Enabled"] = "true", ["StockOpenShell:EnrollmentFile"] = path, ["Agent:Name"] = "agent1" }).Build();

    private static void WithRecord(Action<Dictionary<string, object>> change, string reason)
    {
        var record = new Dictionary<string, object>
        {
            ["version"] = 1, ["state"] = "active", ["agent"] = "agent1", ["sandboxId"] = "synthetic-sandbox",
            ["stockCommit"] = "bdb956f2cd65a635f1294fccf1d138d263bf650f", ["policySha256"] = new string('b', 64),
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1)
        };
        change(record);
        WithFile(JsonSerializer.Serialize(record), reason);
    }

    private static void WithFile(string json, string reason)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stock-enrollment-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            var services = new ServiceCollection();
            var error = Assert.Throws<InvalidOperationException>(() => services.AddAgentCoreServices(Configuration(path)));
            Assert.Equal(reason, error.Message);
            Assert.Empty(services); // No executor, transport, poller or heartbeat registration on refusal.
        }
        finally { File.Delete(path); }
    }
}
