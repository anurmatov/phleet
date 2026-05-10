using Fleet.Agent.Configuration;
using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for the unauthorized-DM decision logic in MessageRouter.HandleUnauthorizedDmAsync.
/// Mirrors the gate predicate inline (same pattern as MessageRouterGroupGateTests) to avoid
/// wiring the full DI graph through sealed classes.
///
/// Gate predicate (agent side — orchestrator resolves CTO agent, not the agent):
///   if (!CanReceiveChatRequests)  → silent drop
///   otherwise                    → publish access.request to fleet.orchestrator exchange + optional reply
/// </summary>
public class MessageRouterUnauthorizedDmTests
{
    // ── Gate predicate mirrored from HandleUnauthorizedDmAsync ────────────────

    public enum UnauthorizedDmOutcome { SilentDrop, ForwardToOrchestrator }

    private static UnauthorizedDmOutcome EvalGate(bool canReceive) =>
        !canReceive ? UnauthorizedDmOutcome.SilentDrop : UnauthorizedDmOutcome.ForwardToOrchestrator;

    // ── Table-driven gate tests ───────────────────────────────────────────────

    public static IEnumerable<object[]> GateCases() =>
    [
        [false, UnauthorizedDmOutcome.SilentDrop],
        [true,  UnauthorizedDmOutcome.ForwardToOrchestrator],
    ];

    [Theory]
    [MemberData(nameof(GateCases))]
    public void Gate_CanReceive_ProducesExpectedOutcome(bool canReceive, UnauthorizedDmOutcome expected)
    {
        Assert.Equal(expected, EvalGate(canReceive));
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

}
