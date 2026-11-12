using System.Text.RegularExpressions;
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
    /// <summary>Where a chat-enabled agent reaches the chat service's agent-facing listener.</summary>
    public const string DefaultSouthUrl = "http://fleet-comms:8082";

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,128}\z")]
    private static partial Regex NameShape();

    /// <summary>Whether a name can be a routing key, a queue segment and a token subject.</summary>
    public static bool IsUsableName(string? name) => name is not null && NameShape().IsMatch(name);

    /// <summary>
    /// Why chat cannot be turned on for this agent, or null. A fixed code, never a value.
    /// </summary>
    public static string? DescribeEnableFault(Agent agent, JournalTokenService tokens)
    {
        if (!IsUsableName(agent.Name)) return "chat_agent_name_invalid";

        return tokens.DescribeKeyFault() switch
        {
            null => null,
            "journal_key_missing" => "chat_key_missing",
            _ => "chat_key_invalid",
        };
    }

    /// <summary>
    /// Why a chat-enabled agent cannot be provisioned, or null. Null for every agent with chat off,
    /// so an agent that never opted in is never refused for it.
    /// </summary>
    public static string? DescribeProvisioningFault(Agent agent, JournalTokenService tokens) =>
        agent.ChatEnabled ? DescribeEnableFault(agent, tokens) : null;

    /// <summary>
    /// Whether this deployment can offer chat at all: the shared key is what every part of it needs.
    /// </summary>
    public static bool IsAvailable(JournalTokenService tokens) => tokens.DescribeKeyFault() is null;

    /// <summary>
    /// The <c>Conversations</c> section for a chat-enabled agent, or null for any other.
    /// </summary>
    internal static ChatProvisioning? BuildProvisioning(
        Agent agent, JournalTokenService tokens, string? southUrl, long? primaryHumanUserId)
    {
        if (!agent.ChatEnabled) return null;

        return new ChatProvisioning(
            string.IsNullOrWhiteSpace(southUrl) ? DefaultSouthUrl : southUrl.Trim().TrimEnd('/'),
            tokens.Mint(SignIn.AgentTokenPurpose, agent.Name),
            // Sign-in's owner follows the deployment's primary human (AuthService.EnsureOwnerAsync),
            // so when one is named, the owner principal IS that person.
            primaryHumanUserId is null ? null : Fleet.Orchestrator.Auth.UserRecord.OwnerId);
    }
}
