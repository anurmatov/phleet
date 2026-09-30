using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The journal's read tools are granted per agent, never by a template (#394 MUST NOT 10).
/// </summary>
/// <remarks>
/// A template is applied to every agent created from it, so a journal endpoint or read tool listed
/// in one would hand the journal to agents nobody chose to give it to. Asserted twice: on the
/// source, which catches the name arriving in any form (a constant, a comment that becomes a
/// string), and on what the registry actually serves.
/// </remarks>
public sealed class AgentTemplateRegistryJournalTests
{
    private const string JournalEndpoint = "fleet-comms-journal";

    [Fact]
    public void The_registry_source_never_names_the_journal_endpoint()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Fleet.Orchestrator", "Services", "AgentTemplateRegistry.cs"));

        Assert.Contains("AgentTemplateRegistry", source, StringComparison.Ordinal);
        Assert.DoesNotContain(JournalEndpoint, source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JournalEndpoint, ContainerProvisioningService.JournalMcpServerName);
    }

    [Fact]
    public void No_template_grants_the_journal_endpoint_or_its_tools()
    {
        var templates = AgentTemplateRegistry.GetAll();
        Assert.NotEmpty(templates);

        foreach (var summary in templates)
        {
            var template = AgentTemplateRegistry.TryGet(summary.Name);
            Assert.NotNull(template);

            Assert.DoesNotContain(template!.Config.McpEndpoints,
                endpoint => endpoint.McpName.Equals(JournalEndpoint, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(template.Config.Tools,
                tool => tool.ToolName.Contains(JournalEndpoint, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, "could not locate the repository root");
        return directory!.FullName;
    }
}
