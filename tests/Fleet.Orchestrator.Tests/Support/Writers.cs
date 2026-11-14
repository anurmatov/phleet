using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Support;

/// <summary>
/// Builds the MCP tools the way the orchestrator's container does: a tool over the writer it
/// adapts. The tools used to take a scope factory and write to the database themselves; the tests
/// that constructed them that way construct them here, with the same two arguments, and assert
/// exactly what they asserted before.
/// </summary>
/// <remarks>
/// Collaborators a test does not name are inert: a publisher with no broker configured (which
/// reports "nothing to deliver" as delivered), Telegram set up with no owner, and no Docker or
/// Temporal — none of which an MCP write reaches.
/// </remarks>
internal static partial class Writers
{
    /// <summary>The operator's Telegram setup as a test states it.</summary>
    public sealed record TelegramSetup(bool IsConfigured = true, long? OwnerUserId = null) : ITelegramSetup;

    /// <summary>Records what would have been written to .env.</summary>
    public sealed class RecordingConfigWriter : IConfigWriter
    {
        public Dictionary<string, string> Written { get; } = new(StringComparer.Ordinal);
        public HashSet<string> GetExistingKeys(IEnumerable<string> keys) => [.. keys.Where(Written.ContainsKey)];
        public Task<List<string>> PutValuesAsync(Dictionary<string, string> kvs, CancellationToken ct = default)
        {
            foreach (var (key, value) in kvs) Written[key] = value;
            return Task.FromResult(kvs.Keys.ToList());
        }
    }

    public static AgentConfigPublisherService InertPublisher() =>
        new(Options.Create(new RabbitMqOptions()), NullLogger<AgentConfigPublisherService>.Instance);

    public static AgentConfigWriter ConfigWriter(
        IServiceScopeFactory scopes, IAclChangeNotifier acl,
        AgentConfigPublisherService? publisher = null, ITelegramSetup? telegram = null) =>
        new(scopes, acl, publisher ?? InertPublisher(), telegram ?? new TelegramSetup(), new AgentHostPorts(), NullLogger<AgentConfigWriter>.Instance);

    public static AgentCreator Creator(
        IServiceScopeFactory scopes, IConfiguration configuration,
        ITelegramSetup? telegram = null, IAclChangeNotifier? acl = null,
        IConfigWriter? configWriter = null, AgentRegistry? registry = null) =>
        new(scopes, telegram ?? new TelegramSetup(), acl ?? new SilentAclNotifier(),
            configWriter ?? new RecordingConfigWriter(),
            registry ?? new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance),
            provisioning: null!, temporal: null!, configuration, new AgentHostPorts(), NullLogger<AgentCreator>.Instance);

    public static UpdateAgentConfigTool UpdateConfigTool(IServiceScopeFactory scopes, IAclChangeNotifier acl) =>
        new(ConfigWriter(scopes, acl));

    public static ManageAgentEnvRefsTool EnvRefsTool(IServiceScopeFactory scopes) =>
        new(ConfigWriter(scopes, new SilentAclNotifier()));

    public static CreateAgentTool CreateAgentTool(IServiceScopeFactory scopes, IConfiguration configuration) =>
        new(Creator(scopes, configuration));

    private sealed class SilentAclNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
