using Fleet.Orchestrator.Data;
using Fleet.Shared;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Writes what <see cref="AgentFileRenderer"/> rendered into the agent's <c>.generated/</c>
/// directory, before the container is created. Decides nothing.
/// </summary>
internal sealed class AgentFileWriter(ILogger logger)
{
    /// <summary>
    /// Writes appsettings.json, .mcp.json, and settings.json into the agent's
    /// .generated/ workspace directory before the container is started.
    /// </summary>
    internal async Task WriteConfigFilesAsync(Agent agent, string baseDir, GeneratedFiles files)
    {
        var generatedDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated");
        Directory.CreateDirectory(generatedDir);

        // The style file is written BEFORE settings.json, so "settings.json names a style that
        // exists on disk" is a fact about the order rather than a hope. GenerateSettingsJson
        // refuses the reference outright when the row is missing.
        await WriteOutputStyleFileAsync(agent, generatedDir, files.Style);

        await File.WriteAllTextAsync(Path.Combine(generatedDir, "appsettings.json"), files.Appsettings);
        await File.WriteAllTextAsync(Path.Combine(generatedDir, ".mcp.json"),        files.McpJson);
        await File.WriteAllTextAsync(Path.Combine(generatedDir, "settings.json"),    files.SettingsJson);

        if (files.CodexRootToml is not null)
            await File.WriteAllTextAsync(Path.Combine(generatedDir, HarnessDefaults.CodexRootTomlFileName), files.CodexRootToml);

        logger.LogInformation(
            "Generated config files for '{Agent}' in {Dir}",
            agent.Name, generatedDir);
    }

    private async Task WriteOutputStyleFileAsync(Agent agent, string generatedDir, OutputStyle? style)
    {
        var dir = Path.Combine(generatedDir, "output-styles");

        if (!AgentConfigFiles.HasStyleFile(agent) || style is null)
        {
            // A stale file from a previous provision would still be mounted and discoverable, so
            // clearing the style has to remove it rather than merely stop naming it.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return;
        }

        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, $"{style.Name}.md"), OutputStyleRenderer.ForStyleFile(style));

        logger.LogInformation(
            "Wrote output style '{Style}' for '{Agent}' to {Dir}", style.Name, agent.Name, dir);
    }

    /// <summary>
    /// Writes the rendered instruction files into the agent's .generated/roles/ workspace directory
    /// before the container is started.
    /// </summary>
    internal async Task WriteInstructionFilesAsync(Agent agent, string baseDir, GeneratedFiles files)
    {
        if (files.Instructions is null)
            return;

        var rolesDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated", "roles");

        foreach (var instruction in files.Instructions)
        {
            var dir = Path.Combine(rolesDir, instruction.Directory);
            Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(Path.Combine(dir, "system.md"), instruction.Content);
        }

        logger.LogInformation(
            "Generated instruction files for '{Agent}' in {Dir} ({Count} instruction(s))",
            agent.Name, rolesDir, agent.Instructions.Count);
    }

    /// <summary>
    /// Writes <c>projects/{name}/context.md</c> for every assignment into the agent's
    /// <c>.generated/projects/</c> directory before the container is started.
    /// </summary>
    internal async Task WriteProjectContextFilesAsync(Agent agent, string baseDir, ProjectContextPlan plan)
    {
        var projectsDir = Path.Combine(baseDir, "workspaces", agent.ContainerName, ".generated", "projects");
        Directory.CreateDirectory(projectsDir);

        // A full.md is left over from a card-mode provision before cards were removed (#346). Nothing
        // reads it any more; delete every one so a formerly-card agent ends up identical to any other.
        foreach (var dir in Directory.EnumerateDirectories(projectsDir))
        {
            var fullMd = Path.Combine(dir, "full.md");
            if (File.Exists(fullMd))
            {
                File.Delete(fullMd);
                logger.LogInformation(
                    "Removed stale full.md for project '{Project}' of '{Agent}'", Path.GetFileName(dir), agent.Name);
            }
        }

        if (agent.Projects.Count == 0)
        {
            logger.LogInformation("No projects configured for '{Agent}' — skipping project context file generation", agent.Name);
            return;
        }

        foreach (var assignment in plan.Assignments)
        {
            var dir = Path.Combine(projectsDir, assignment.ProjectName);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "context.md"), assignment.ContextMd);
        }

        logger.LogInformation(
            "Generated project context files for '{Agent}' in {Dir} ({Count} project(s))",
            agent.Name, projectsDir, agent.Projects.Count);
    }
}
