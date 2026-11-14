using System.Text.Json.Serialization;

namespace Fleet.Contracts;

/// <summary>
/// A newly allowed user in a <see cref="ConfigUpdateMessage"/>, with the name the welcome DM can
/// address them by without a Telegram lookup.
/// </summary>
public sealed class AddedUserInfo
{
    [JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }
}

/// <summary>
/// The text of a <see cref="RelayMessageType.ConfigUpdate"/> message: what changed in an agent's
/// AllowedUserIds and AllowedGroupIds, published by the orchestrator so it applies without a
/// reprovision.
/// </summary>
public sealed class ConfigUpdateMessage
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("target_agent")]
    public string TargetAgent { get; init; } = "";

    [JsonPropertyName("added_users")]
    public IReadOnlyList<AddedUserInfo> AddedUsers { get; init; } = [];

    [JsonPropertyName("removed_user_ids")]
    public IReadOnlyList<long> RemovedUserIds { get; init; } = [];

    [JsonPropertyName("added_group_ids")]
    public IReadOnlyList<long> AddedGroupIds { get; init; } = [];

    [JsonPropertyName("removed_group_ids")]
    public IReadOnlyList<long> RemovedGroupIds { get; init; } = [];
}

/// <summary>
/// What an agent publishes on <see cref="OrchestratorBroker.Exchange"/> under
/// <see cref="OrchestratorBroker.AccessRequest"/> when an unknown user DMs a bot that takes chat
/// requests. The orchestrator forwards it to the CTO agent as a directive.
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

    [JsonPropertyName("message_text")]
    public string MessageText { get; init; } = "";
}
