using Fleet.Temporal.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Temporal.Extensibility;

/// <summary>
/// Contract for project-specific workflow assemblies. Implementations register their
/// workflows, activities, and type metadata without modifying core code.
/// </summary>
public interface IWorkflowPlugin
{
    /// <summary>
    /// Register plugin-specific services (HTTP clients, options, etc.) into the DI container.
    /// Called during host builder setup, before workers start.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Register workflows and activities on namespace-specific workers.
    /// Called after ConfigureServices, during worker builder setup.
    /// </summary>
    void ConfigureWorkers(WorkerRegistrationContext context);

    /// <summary>
    /// Return workflow type metadata for the MCP tool registry.
    /// Used by WorkflowTypeRegistry to build the catalog dynamically.
    /// </summary>
    IReadOnlyList<WorkflowTypeInfo> GetWorkflowTypes();
}
