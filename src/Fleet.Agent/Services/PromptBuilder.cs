using System.Text;
using Fleet.Agent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Shared service that builds the system prompt from role files and project contexts.
/// Injected by both ClaudeExecutor and CodexExecutor so both providers use identical prompts.
/// </summary>
public sealed class PromptBuilder(IOptions<AgentOptions> config, ILogger<PromptBuilder> logger)
{
    private readonly AgentOptions _config = config.Value;

    public string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        // Load base prompt (shared across all roles)
        var basePath = Path.Combine(AppContext.BaseDirectory, "roles", "_base", "system.md");
        if (File.Exists(basePath))
        {
            sb.AppendLine(File.ReadAllText(basePath));
            sb.AppendLine();
        }

        // Load role prompt
        var rolePath = Path.Combine(AppContext.BaseDirectory, "roles", _config.Role, "system.md");
        if (File.Exists(rolePath))
        {
            sb.AppendLine(File.ReadAllText(rolePath));
            sb.AppendLine();
        }

        // Load project context
        foreach (var project in _config.Projects)
        {
            var projectPath = Path.Combine(AppContext.BaseDirectory, "projects", project, "context.md");
            if (File.Exists(projectPath))
            {
                sb.AppendLine($"## Project Context: {project}");
                sb.AppendLine();
                sb.AppendLine(File.ReadAllText(projectPath));
                sb.AppendLine();
            }
        }

        // Inject memory instructions based on agent's tool access
        var hasMemoryStore = _config.AllowedTools.Any(t => t.Contains("memory_store"));
        var hasAnyMemoryTools = _config.AllowedTools.Any(t => t.Contains("memory_"));

        if (hasMemoryStore)
        {
            // Full memory access (co-CTO / memory gatekeeper)
            sb.AppendLine("## Memory Scoping");
            sb.AppendLine();
            if (_config.Projects.Count == 1)
            {
                sb.AppendLine($"Your project is **{_config.Projects[0]}**.");
                sb.AppendLine($"- When calling `memory_search` or `memory_list`, set `project` to `{_config.Projects[0]}` unless explicitly asked to search across all projects.");
            }
            else if (_config.Projects.Count > 1)
            {
                sb.AppendLine($"Your projects are **{string.Join(", ", _config.Projects)}**.");
                sb.AppendLine($"- When calling `memory_search` or `memory_list`, do not auto-scope to a single project — search across all projects unless the task specifies otherwise.");
            }
            sb.AppendLine($"- When calling `memory_store`, set `agent` to `{_config.Name}`.");
            sb.AppendLine($"- When calling `memory_store`, set `project` to the relevant project name (or leave empty for cross-project knowledge).");
        }
        else if (hasAnyMemoryTools)
        {
            // Read-only memory access
            sb.AppendLine("## Memory Access (Read-Only)");
            sb.AppendLine();
            sb.AppendLine("You have **read-only** access to the shared memory system. You can search,");
            sb.AppendLine("list, and read memories, but you CANNOT store, update, or delete them.");
            sb.AppendLine();
            if (_config.Projects.Count == 1)
            {
                sb.AppendLine($"Your project is **{_config.Projects[0]}**.");
                sb.AppendLine($"- When calling `memory_search` or `memory_list`, set `project` to `{_config.Projects[0]}` unless explicitly asked to search across all projects.");
                sb.AppendLine();
            }
            else if (_config.Projects.Count > 1)
            {
                sb.AppendLine($"Your projects are **{string.Join(", ", _config.Projects)}**.");
                sb.AppendLine($"- When calling `memory_search` or `memory_list`, do not auto-scope to a single project — search across all projects unless the task specifies otherwise.");
                sb.AppendLine();
            }
            sb.AppendLine("If you discover knowledge worth persisting (decisions, lessons learned,");
            sb.AppendLine("architecture notes, incident postmortems), output a structured block for the");
            sb.AppendLine("CEO to relay to the co-CTO:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("MEMORY_REQUEST");
            sb.AppendLine($"From: {_config.Name}");
            sb.AppendLine("Action: store | update | delete");
            sb.AppendLine("Title: <descriptive, searchable title>");
            sb.AppendLine($"Project: {(_config.Projects.Count == 0 ? "<project>" : _config.Projects[0])}");
            sb.AppendLine("Content:");
            sb.AppendLine("<structured content>");
            sb.AppendLine("```");
        }

        if (hasAnyMemoryTools)
        {
            sb.AppendLine();
            sb.AppendLine("## Session Start: Knowledge Loading");
            sb.AppendLine();
            sb.AppendLine("At the beginning of every session, search memory for relevant context,");
            sb.AppendLine("recent decisions, and open questions before starting work on the task.");
            sb.AppendLine("When memory returns runbooks or procedures, read them fully and follow");
            sb.AppendLine("the exact commands and paths — do not rely on assumptions or defaults.");
            sb.AppendLine();
            sb.AppendLine("## Following Related Memory Links");
            sb.AppendLine();
            sb.AppendLine("Memories may contain a `## Related` section at the bottom with links to");
            sb.AppendLine("other memories (memory ID + title). When you read a memory and it has");
            sb.AppendLine("related links, check them with `memory_get` if they look relevant to your");
            sb.AppendLine("current task. This helps you discover runbooks, context, and decisions");
            sb.AppendLine("that search alone might miss.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Assembles the system prompt and writes it atomically to
    /// <c>{WorkDir}/system-prompt.md</c> (via tmp + rename). Returns the absolute
    /// path to the written file. Always writes — even when the prompt is empty —
    /// so the caller can unconditionally pass <c>--append-system-prompt-file</c>.
    ///
    /// Throws on any write failure. Never falls back to inline delivery; a write
    /// failure must be loud and fatal to prevent silent re-exposure of E2BIG risk.
    /// </summary>
    public string WriteSystemPromptFile()
    {
        var content = BuildSystemPrompt();
        var filePath = Path.Combine(_config.WorkDir, "system-prompt.md");
        var tmpPath = filePath + ".tmp";

        try
        {
            // Write with UTF-8 without BOM, matching .generated/ file conventions.
            File.WriteAllText(tmpPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // Atomic rename — prevents Claude from reading a partially-written file.
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError("[PromptBuilder] Failed to write system-prompt.md: {Message}", ex.Message);
            throw;
        }

        if (!File.Exists(filePath))
            throw new InvalidOperationException(
                $"[PromptBuilder] system-prompt.md not found at expected path after write: {filePath}");

        var byteCount = new FileInfo(filePath).Length;
        logger.LogDebug("[PromptBuilder] Wrote system-prompt.md: {Bytes} bytes at {Path}", byteCount, filePath);

        return filePath;
    }
}
