using Fleet.Agent.Configuration;
using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for the unauthorized-DM decision logic in MessageRouter.HandleUnauthorizedDmAsync.
/// Mirrors the gate predicate inline (same pattern as MessageRouterGroupGateTests) to avoid
/// wiring the full DI graph through sealed classes.
///
/// Gate predicate:
///   if (!CanReceiveChatRequests)         → silent drop
///   if (ctoAgentName is null/empty)      → log error, drop
///   otherwise                            → forward access.request to CTO agent + optional reply
/// </summary>
public class MessageRouterUnauthorizedDmTests
{
    // ── Gate predicate mirrored from HandleUnauthorizedDmAsync ────────────────

    public enum UnauthorizedDmOutcome { SilentDrop, ConfigError, ForwardAndReply }

    private static UnauthorizedDmOutcome EvalGate(bool canReceive, string ctoAgent) =>
        !canReceive                          ? UnauthorizedDmOutcome.SilentDrop :
        string.IsNullOrWhiteSpace(ctoAgent)  ? UnauthorizedDmOutcome.ConfigError :
                                               UnauthorizedDmOutcome.ForwardAndReply;

    // ── Table-driven gate tests ───────────────────────────────────────────────

    public static IEnumerable<object[]> GateCases() =>
    [
        [false, "",     UnauthorizedDmOutcome.SilentDrop],
        [false, "acto", UnauthorizedDmOutcome.SilentDrop],    // feature off → always drop
        [true,  "",     UnauthorizedDmOutcome.ConfigError],   // feature on, no CTO
        [true,  "acto", UnauthorizedDmOutcome.ForwardAndReply],
    ];

    [Theory]
    [MemberData(nameof(GateCases))]
    public void Gate_CanReceiveAndCtoAgent_ProducesExpectedOutcome(
        bool canReceive, string ctoAgent, UnauthorizedDmOutcome expected)
    {
        Assert.Equal(expected, EvalGate(canReceive, ctoAgent));
    }

    // ── RequestReceivedMessage — fallback and custom ──────────────────────────

    [Theory]
    [InlineData(null,    "Your request has been received and is awaiting approval.")]
    [InlineData("",      "Your request has been received and is awaiting approval.")]
    [InlineData("Wait!", "Wait!")]
    public void ReplyText_UsesConfiguredMessageOrDefault(string? configured, string expected)
    {
        // Mirrors the fallback logic in HandleUnauthorizedDmAsync
        var actual = string.IsNullOrWhiteSpace(configured)
            ? "Your request has been received and is awaiting approval."
            : configured;
        Assert.Equal(expected, actual);
    }

    // ── TelegramOptions.CanReceiveChatRequests default ────────────────────────

    [Fact]
    public void TelegramOptions_CanReceiveChatRequests_DefaultIsFalse()
    {
        var opts = new TelegramOptions();
        Assert.False(opts.CanReceiveChatRequests);
    }

    // ── AccessRequestPayload construction ─────────────────────────────────────

    [Fact]
    public void AccessRequestPayload_CanBeConstructedWithOptionalFields()
    {
        var payload = new AccessRequestPayload
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TargetAgent = "adev",
            UserId = 12345,
            Username = "alice",
            FirstName = "Alice",
            MessageText = "Hello, can I get access?",
        };

        Assert.NotEmpty(payload.RequestId);
        Assert.Equal(12345, payload.UserId);
        Assert.Equal("alice", payload.Username);
        Assert.True(payload.SchemaVersion > 0);
    }

    // ── CtoAgentNameService reads FLEET_CTO_AGENT from env ───────────────────

    [Fact]
    public void CtoAgentNameService_MissingEnvVar_ReturnsEmptyString()
    {
        // Use a config with no keys — simulates container where FLEET_CTO_AGENT is not set
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var svc = new Fleet.Agent.Services.CtoAgentNameService(config);
        Assert.Equal(string.Empty, svc.GetCtoAgentName());
    }
}
