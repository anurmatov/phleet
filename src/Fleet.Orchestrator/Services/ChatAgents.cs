using System.Text.RegularExpressions;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>What provisioning writes into a chat-enabled agent's <c>Conversations</c> section.</summary>
/// <param name="PrimaryHumanPrincipalId">
/// The chat principal that is the deployment's primary human, or null when the deployment names
/// none. The agent gives that principal's messages the priority its Telegram messages get.
/// </param>
internal sealed record ChatProvisioning(string SouthBaseUrl, string SouthToken, string? PrimaryHumanPrincipalId);

/// <summary>
/// The rules for which agents a person can chat with (#530), in one place: the config routes, the
/// agent list the clients read, the chat service's question and provisioning all ask here.
/// </summary>
/// <remarks>
/// <para>
/// <b>An agent's chat identity is its <see cref="Agent.Name"/>.</b> That one value is what a client
/// names when it opens a conversation, the routing key the chat service publishes the agent's
/// commands with, the segment of the queue the agent consumes, and the subject of the token the
/// agent presents back. It is unique, which a short name is not.
/// </para>
/// <para>
/// <b>The key is the one the orchestrator and the chat service already share</b>
/// (<c>Journal:TokenKey</c>, <c>FLEET_COMMS_JOURNAL_KEY</c>). It began with the journal and now
/// also mints each agent's chat token and verifies the chat service's identity question, so chat
/// needs it whether or not the journal is on.
/// </para>
/// </remarks>
public static partial class ChatAgents
{
    private static bool ReportsReply(Models.AgentState? agent) =>
        agent is { IsDbOnly: false }
        && agent.EffectiveStatus is "idle" or "busy"
        && agent.Capabilities?.Contains(SignIn.ChatReplyCapability, StringComparer.Ordinal) == true;

    /// <summary>Telemetry proves support only for the configured, currently running Docker instance.</summary>
    public static async Task<bool> SupportsReplyAsync(
        Models.AgentState? agent, string? configuredContainer, DockerService docker, CancellationToken ct)
    {
        if (!ReportsReply(agent) || string.IsNullOrWhiteSpace(configuredContainer)) return false;
        // A stuck Docker socket must withdraw Reply, not exhaust the chat issuer's request budget.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        // Never let a heartbeat choose the container inspected with the orchestrator's authority.
        return SupportsReply(agent, await docker.InspectContainerAsync(configuredContainer, deadline.Token));
    }

    internal static bool SupportsReply(Models.AgentState? agent, string? inspectedRuntime)
    {
        if (!ReportsReply(agent) || inspectedRuntime is null) return false;
        try
        {
            using var document = JsonDocument.Parse(inspectedRuntime);
            var root = document.RootElement;
            var runtime = root.GetProperty("State");
            var id = root.GetProperty("Id").GetString();
            var reported = agent!.ContainerName;
            return runtime.GetProperty("Running").ValueKind == JsonValueKind.True
                && id is { Length: 64 } && id.All(Uri.IsHexDigit)
                && reported is { Length: >= 12 and <= 64 } && reported.All(Uri.IsHexDigit)
                && id.StartsWith(reported, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(runtime.GetProperty("StartedAt").GetString(), out var started)
                && started.Year > 1 && agent.LastSeen >= started;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Where a chat-enabled agent reaches the chat service's agent-facing listener.</summary>
    public const string DefaultSouthUrl = "http://fleet-comms:8082";

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,128}\z")]
    private static partial Regex NameShape();

    /// <summary>Whether a name can be a routing key, a queue segment and a token subject.</summary>
    public static bool IsUsableName(string? name) => name is not null && NameShape().IsMatch(name);

    /// <summary>
    /// Whether this deployment can offer chat at all: the shared key is what every part of it needs.
    /// </summary>
    public static bool IsAvailable(JournalTokenService tokens) => tokens.DescribeKeyFault() is null;

    /// <summary>
    /// Whether chat applies to this agent: its switch is on (the default), the deployment can offer
    /// chat, and its name can be a routing key.
    /// </summary>
    /// <remarks>
    /// Never a reason to refuse anything. Chat is on by default for every agent, so a deployment
    /// without the chat service — or an agent whose name cannot be a queue segment — simply has no
    /// chat with it, and provisions exactly as it did before.
    /// </remarks>
    public static bool IsEffective(Agent agent, JournalTokenService tokens) =>
        agent.ChatEnabled && IsUsableName(agent.Name) && IsAvailable(tokens);

    /// <summary>
    /// The <c>Conversations</c> section for an agent chat applies to, or null for any other.
    /// </summary>
    internal static ChatProvisioning? BuildProvisioning(
        Agent agent, JournalTokenService tokens, string? southUrl, long? primaryHumanUserId)
    {
        if (!IsEffective(agent, tokens)) return null;

        return new ChatProvisioning(
            string.IsNullOrWhiteSpace(southUrl) ? DefaultSouthUrl : southUrl.Trim().TrimEnd('/'),
            tokens.Mint(SignIn.AgentTokenPurpose, agent.Name),
            // Sign-in's owner follows the deployment's primary human (AuthService.EnsureOwnerAsync),
            // so when one is named, the owner principal IS that person.
            primaryHumanUserId is null ? null : Fleet.Orchestrator.Auth.UserRecord.OwnerId);
    }
}
