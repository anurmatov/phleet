using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Support;

/// <summary>The allowlist and project-access tools, each over the writer it adapts.</summary>
internal static partial class Writers
{
    public static AgentAllowlist Allowlist(
        IServiceScopeFactory scopes, AgentConfigPublisherService publisher, ITelegramSetup? telegram = null) =>
        new(scopes, publisher, telegram ?? new TelegramSetup());

    public static ManageAgentTelegramUsersTool TelegramUsersTool(IServiceScopeFactory scopes, AgentConfigPublisherService publisher) =>
        new(Allowlist(scopes, publisher));

    public static ManageAgentTelegramGroupsTool TelegramGroupsTool(IServiceScopeFactory scopes, AgentConfigPublisherService publisher) =>
        new(Allowlist(scopes, publisher));

    public static ManageAgentProjectAccessTool ProjectAccessTool(IServiceScopeFactory scopes, ConfigService configService) =>
        new(new AgentProjectAccessWriter(scopes, configService));
}
