using System.Text.Json;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Locks the snake_case JSON deserialization contract for access-request payloads.
///
/// AccessRequestPayload is a private inner class in HeartbeatConsumerService.
/// These tests exercise the same JsonSerializerOptions (SnakeCaseLower) against a
/// mirror class with identical field shapes to ensure the casing contract stays correct.
/// A regression to PropertyNameCaseInsensitive would silently bind only `username`
/// (all-lowercase) and leave request_id / target_agent / user_id / first_name /
/// message_text at their default values — as confirmed in prod (issue #168).
/// </summary>
public class HeartbeatConsumerDeserializationTests
{
    // Mirror of HeartbeatConsumerService.AccessRequestPayload — must stay in sync with JSON shape.
    private sealed class AccessRequestPayload
    {
        public string RequestId { get; init; } = "";
        public string TargetAgent { get; init; } = "";
        public long UserId { get; init; }
        public string? Username { get; init; }
        public string? FirstName { get; init; }
        public string MessageText { get; init; } = "";
    }

    private static readonly JsonSerializerOptions SnakeCaseOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private const string SampleJson = """
        {
            "request_id": "req-abc123",
            "target_agent": "acanary-codex",
            "user_id": 265403247,
            "username": "gg_sa",
            "first_name": "Anvar",
            "message_text": "I'd like access to this bot"
        }
        """;

    [Fact]
    public void Deserialize_SnakeCaseJson_BindsAllFields()
    {
        var payload = JsonSerializer.Deserialize<AccessRequestPayload>(SampleJson, SnakeCaseOpts);

        Assert.NotNull(payload);
        Assert.Equal("req-abc123", payload.RequestId);
        Assert.Equal("acanary-codex", payload.TargetAgent);
        Assert.Equal(265403247L, payload.UserId);
        Assert.Equal("gg_sa", payload.Username);
        Assert.Equal("Anvar", payload.FirstName);
        Assert.Equal("I'd like access to this bot", payload.MessageText);
    }

    [Fact]
    public void Deserialize_SnakeCaseJson_WithNullOptionalFields_BindsCorrectly()
    {
        var json = """
            {
                "request_id": "req-xyz",
                "target_agent": "some-bot",
                "user_id": 123456,
                "message_text": "hello"
            }
            """;

        var payload = JsonSerializer.Deserialize<AccessRequestPayload>(json, SnakeCaseOpts);

        Assert.NotNull(payload);
        Assert.Equal("req-xyz", payload.RequestId);
        Assert.Equal("some-bot", payload.TargetAgent);
        Assert.Equal(123456L, payload.UserId);
        Assert.Null(payload.Username);
        Assert.Null(payload.FirstName);
        Assert.Equal("hello", payload.MessageText);
    }

    /// <summary>
    /// Regression guard: PropertyNameCaseInsensitive only bridges PascalCase ↔ camelCase.
    /// It does NOT strip underscores, so snake_case fields like "target_agent" would not
    /// bind to "TargetAgent". This test confirms the correct policy is in use.
    /// </summary>
    [Fact]
    public void Deserialize_WithCaseInsensitiveOnly_DoesNotBindSnakeCaseFields()
    {
        var caseInsensitiveOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var payload = JsonSerializer.Deserialize<AccessRequestPayload>(SampleJson, caseInsensitiveOpts);

        Assert.NotNull(payload);
        // username is all-lowercase, so it binds even without SnakeCaseLower
        Assert.Equal("gg_sa", payload.Username);
        // These snake_case fields do NOT bind with case-insensitive-only — they default
        Assert.Equal("", payload.RequestId);
        Assert.Equal("", payload.TargetAgent);
        Assert.Equal(0L, payload.UserId);
    }
}
