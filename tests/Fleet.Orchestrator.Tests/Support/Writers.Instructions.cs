using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Support;

/// <summary>The instruction tools, each over the writer it adapts.</summary>
internal static partial class Writers
{
    public static CreateInstructionTool CreateInstructionTool(IServiceScopeFactory scopes, PromptSizePolicy policy) =>
        new(new InstructionWriter(scopes, policy));

    public static UpdateInstructionTool UpdateInstructionTool(IServiceScopeFactory scopes, PromptSizePolicy policy) =>
        new(new InstructionWriter(scopes, policy));

    public static RollbackInstructionTool RollbackInstructionTool(IServiceScopeFactory scopes, PromptSizePolicy policy) =>
        new(new InstructionWriter(scopes, policy));
}
