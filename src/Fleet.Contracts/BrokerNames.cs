namespace Fleet.Contracts;

/// <summary>
/// The relay exchange and the queues bound to it. Each is declared by more than one service, and a
/// second declaration with a different name, type or argument is a broker channel error.
/// </summary>
public static class RelayBroker
{
    /// <summary>
    /// The direct exchange every relay message goes through, durable. The default of each service's
    /// <c>RabbitMq:Exchange</c>; the orchestrator publishes to it under this name whatever is
    /// configured, so an override has to be the same in every service.
    /// </summary>
    public const string Exchange = "fleet.group";

    /// <summary>
    /// The durable fanout exchange for OAuth token broadcasts. Every agent that holds a provider
    /// credential binds its queue here; a local-model agent never does (#340, #386).
    /// </summary>
    public const string TokenBroadcastExchange = "fleet.relay";

    /// <summary>The temporal bridge's routing key on <see cref="Exchange"/>, and its queue.</summary>
    public const string TemporalBridgeKey = "temporal-bridge";
    public const string TemporalBridgeQueue = "fleet.agent.temporal-bridge";

    /// <summary>fleet-bridge's routing key on <see cref="Exchange"/>, and its queue.</summary>
    public const string BridgeKey = "bridge";
    public const string BridgeQueue = "fleet.agent.bridge";

    /// <summary>
    /// An agent's durable queue. <paramref name="lowercasedShortName"/> is also its routing key on
    /// <see cref="Exchange"/>.
    /// </summary>
    public static string AgentQueue(string lowercasedShortName) => $"fleet.agent.{lowercasedShortName}";
}

/// <summary>The orchestrator's topic exchange: what agents report and what peers subscribe to.</summary>
public static class OrchestratorBroker
{
    /// <summary>The durable topic exchange. The default of the orchestrator's <c>RabbitMq:Exchange</c>.</summary>
    public const string Exchange = "fleet.orchestrator";

    /// <summary>The orchestrator's durable queue, bound with <c>#</c>.</summary>
    public const string HeartbeatQueue = "fleet.orchestrator.heartbeats";

    /// <summary>Routing key prefixes, each followed by <c>.{lowercased short name}</c>.</summary>
    public const string Registration = "registration";
    public const string Heartbeat = "heartbeat";
    public const string AccessRequest = "access-request";

    /// <summary>The routing key of a <see cref="ConfigChangedEvent"/>.</summary>
    public const string ConfigChanged = "config.changed";

    /// <summary>An agent's routing key for <paramref name="kind"/>, one of the prefixes above.</summary>
    public static string AgentKey(string kind, string lowercasedShortName) => $"{kind}.{lowercasedShortName}";

    /// <summary>
    /// How long a registration or heartbeat may wait unconsumed, as the AMQP <c>expiration</c>
    /// property: after a minute it describes a state that has passed.
    /// </summary>
    public const string HeartbeatExpirationMilliseconds = "60000";
}

/// <summary>
/// The broker topology between the chat service and an agent, as the conversations design's
/// topology table fixes it.
/// </summary>
public static class ConversationBroker
{
    /// <summary>Commands to the agent. Direct; the routing key is the agent name.</summary>
    public const string CommandExchange = "fleet.conversations";

    /// <summary>Committed events. Direct; the routing key is the conversation id.</summary>
    public const string EventExchange = "fleet.conversations.events";

    /// <summary>
    /// An agent's durable inbound queue, bound to <see cref="CommandExchange"/> under the agent's
    /// name. The agent's name is its unique orchestrator name (#530 D9).
    /// </summary>
    public static string CommandQueue(string agentName) =>
        $"fleet.conversations.inbound.{agentName}";

    /// <summary>
    /// The command queue's arguments. The chat service and the agent both declare the queue, so
    /// whichever comes first the other's declaration is a no-op; with different arguments it is a
    /// broker error.
    /// </summary>
    public static Dictionary<string, object?> CommandQueueArguments() => new() { ["x-queue-type"] = "quorum" };
}
