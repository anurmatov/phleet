using System.Text.Json.Serialization;

namespace Fleet.Agent.Models;

/// <summary>
/// Payload for a config.update relay message published by the orchestrator
/// when an agent's AllowedUserIds or AllowedGroupIds are changed at runtime.
/// </summary>
public sealed class ConfigUpdateMessage
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("target_agent")]
    public string TargetAgent { get; init; } = "";

    [JsonPropertyName("added_user_ids")]
    public IReadOnlyList<long> AddedUserIds { get; init; } = [];

    [JsonPropertyName("removed_user_ids")]
    public IReadOnlyList<long> RemovedUserIds { get; init; } = [];

    [JsonPropertyName("added_group_ids")]
    public IReadOnlyList<long> AddedGroupIds { get; init; } = [];

    [JsonPropertyName("removed_group_ids")]
    public IReadOnlyList<long> RemovedGroupIds { get; init; } = [];
}

/// <summary>
/// Payload for an access.request relay message published to a control-plane agent
/// when an unknown user DMs a bot with CanReceiveChatRequests=true.
/// </summary>
public sealed class AccessRequestPayload
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("request_id")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("target_agent")]
    public string TargetAgent { get; init; } = "";

    [JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; init; }

    [JsonPropertyName("message_text")]
    public string MessageText { get; init; } = "";
}
