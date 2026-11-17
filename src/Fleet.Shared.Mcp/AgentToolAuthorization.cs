using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Fleet.Shared.Mcp;

/// <summary>What an agent has been granted, as the server learns it.</summary>
/// <param name="Known">False when the grants could not be read: nothing can be concluded from <paramref name="Grants"/>.</param>
/// <param name="Grants">The agent's granted tool names (<c>mcp__server__tool</c>). Empty for an agent with none, and for an unknown one.</param>
public readonly record struct AgentGrantAnswer(bool Known, IReadOnlyCollection<string> Grants)
{
    public static readonly AgentGrantAnswer Unknown = new(false, []);
}

/// <summary>Where a server reads an agent's granted tools from.</summary>
public interface IAgentGrantSource
{
    ValueTask<AgentGrantAnswer> GrantsOfAsync(string agent, CancellationToken ct);
}

/// <summary>
/// Holds an agent to the tools it was granted, on the server. Until this, the grant list was
/// enforced only by the provider's CLI inside the agent's own container, which the agent's shell
/// can simply go around.
/// </summary>
public static class AgentToolAuthorization
{
    public const string NotGrantedCode = "tool_not_granted";
    public const string GrantsUnavailableCode = "tool_grants_unavailable";

    /// <param name="serverName">The name agents know this server by in their grants: <c>fleet-memory</c>, <c>fleet-orchestrator</c>, …</param>
    public static IMcpServerBuilder AddAgentToolAuthorization(this IMcpServerBuilder builder, string serverName)
    {
        builder.AddCallToolFilter(next => async (context, ct) =>
        {
            var tool = context.Params?.Name ?? "";
            var verdict = await DecideAsync(context.Services, serverName, tool, ct);
            if (verdict is null) return await next(context, ct);
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = verdict }] };
        });

        builder.AddListToolsFilter(next => async (context, ct) =>
        {
            var result = await next(context, ct);
            var services = context.Services;
            var caller = AgentCallerHttp.Of(services?.GetService<IHttpContextAccessor>());
            if (caller.Kind == AgentCallerKind.Service) return result;
            var source = services?.GetService<IAgentGrantSource>();
            var answer = caller.Kind == AgentCallerKind.Agent && source is not null
                ? await source.GrantsOfAsync(caller.Name, ct) : AgentGrantAnswer.Unknown;
            if (!answer.Known)
            {
                // Missing or unreadable authorization never lists tools.
                result.Tools = [];
                return result;
            }
            // An agent is shown what it may call. The rest exists, and is not its to see.
            result.Tools = [.. result.Tools.Where(t => AgentToolGrants.Allows(answer.Grants, serverName, t.Name))];
            return result;
        });

        return builder;
    }

    /// <summary>Null to let the call through, or the refusal's text.</summary>
    internal static async ValueTask<string?> DecideAsync(IServiceProvider? services, string serverName, string tool, CancellationToken ct)
    {
        var caller = AgentCallerHttp.Of(services?.GetService<IHttpContextAccessor>());
        if (caller.Kind == AgentCallerKind.Service) return null;
        if (caller.Kind != AgentCallerKind.Agent) return AgentIdentityApp.RequiredCode;

        var source = services?.GetService<IAgentGrantSource>();
        var answer = source is null ? AgentGrantAnswer.Unknown : await source.GrantsOfAsync(caller.Name, ct);
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger("Fleet.AgentIdentity");

        if (!answer.Known)
        {
            logger?.LogWarning("{Code}: agent={Agent} server={Server} tool={Tool}", GrantsUnavailableCode, caller.Name, serverName, tool);
            return $"{GrantsUnavailableCode}: this server cannot read which tools '{caller.Name}' is granted right now. Try again shortly.";
        }

        if (AgentToolGrants.Allows(answer.Grants, serverName, tool)) return null;

        logger?.LogWarning("{Code}: agent={Agent} server={Server} tool={Tool}", NotGrantedCode, caller.Name, serverName, tool);
        return $"{NotGrantedCode}: '{caller.Name}' is not granted {AgentToolGrants.GrantName(serverName, tool)}. "
            + "An operator grants tools in the agent's config; a change applies on reprovision.";
    }
}
