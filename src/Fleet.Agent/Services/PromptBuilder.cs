using System.Text;
using Fleet.Agent.Configuration;
using Fleet.Shared;
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

    /// <summary>The <c>roles/</c> directory name the base instruction is written to.</summary>
    internal const string BaseDirectoryName = "_base";

    /// <summary>
    /// Root the generated <c>roles/</c> and <c>projects/</c> trees are read from. Overridable for
    /// tests only — production always reads the files the orchestrator wrote beside the binary.
    /// </summary>
    internal string ContentRoot { get; init; } = AppContext.BaseDirectory;

    public string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        // Every assigned instruction, in order. Before #309 this was two fixed paths, so an
        // operator could assign a review checklist or a chat-restraint rule, see it reported as
        // assigned by the config API and the dashboard, and the agent would never have read it.
        foreach (var directory in InstructionDirectories())
        {
            var path = Path.Combine(ContentRoot, "roles", directory, "system.md");
            if (File.Exists(path))
            {
                sb.AppendLine(File.ReadAllText(path));
                sb.AppendLine();
            }
            else
            {
                // The orchestrator writes a file for every name it puts in InstructionOrder, so a
                // miss means generation failed for that one. Silence here is what #309 is about.
                logger.LogWarning(
                    "[PromptBuilder] Assigned instruction '{Instruction}' has no system.md at {Path} "
                    + "— it is NOT in the assembled prompt", directory, path);
            }
        }

        // Load project context
        foreach (var project in _config.Projects)
        {
            var projectPath = Path.Combine(ContentRoot, "projects", project, "context.md");
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

        // The output style, for providers that have no output-style mechanism (#314). Claude reads
        // the identical text as a style file, so the orchestrator leaves this empty for it.
        //
        // Placed BEFORE the formatting block on purpose: the style carries register and length, and
        // the per-agent formatting mode still decides structure. Whatever the block below says about
        // markup is the last word on it.
        if (!string.IsNullOrWhiteSpace(_config.OutputStyleBody))
        {
            sb.AppendLine();
            sb.AppendLine(_config.OutputStyleBody);
        }

        // Inject Rich-mode formatting guidance. PlainText and LegacyHtml keep
        // the base instruction's guidance verbatim — Rich overrides the caution
        // against headers, lists, and tables that is correct for the other tiers
        // but wrong for this one.
        if (_config.FormattingMode == FormattingMode.Rich)
        {
            sb.AppendLine();
            sb.AppendLine("## Output Formatting");
            sb.AppendLine();
            sb.AppendLine("Your messages are sent via `sendRichMessage` (Bot API 10.1). The full");
            sb.AppendLine("Markdown-like subset below is supported and **renders as formatted content** —");
            sb.AppendLine("not as literal characters:");
            sb.AppendLine();
            sb.AppendLine("- `# Heading` / `## Subheading` — section headings");
            sb.AppendLine("- `- item` / `* item` — unordered list");
            sb.AppendLine("- `1. item` — ordered list");
            sb.AppendLine("- GFM table: `| col |` header row + `|---|` separator + `| val |` data rows");
            sb.AppendLine("- `**bold**`, `` `inline code` ``, fenced code blocks, `[label](url)` links");
            sb.AppendLine();
            sb.AppendLine("The base-instruction caution about `# headers` and `- bullets` rendering as");
            sb.AppendLine("literal characters does NOT apply here — use them when they aid clarity.");
            sb.AppendLine("The ~35-character line-length guideline inside fenced blocks still applies");
            sb.AppendLine("(mobile fences wrap rather than scroll, so long lines collapse into noise).");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The <c>roles/</c> directories to inline, in the order they are inlined (#309).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>_base</c> is pinned first, whatever its load order.</b> It is the shared preamble every
    /// role builds on, and its position relative to the role instruction is behaviour this change
    /// is not allowed to alter. Everything else follows in the order the orchestrator supplied, so
    /// the <c>load_order</c> an operator sets is the order they get — including, deliberately, an
    /// extra instruction placed ahead of the role one.
    /// </para>
    /// <para>
    /// <b>Driven by the supplied list, never by a directory listing.</b> The generator only ever
    /// writes files, so unassigning an instruction leaves its directory behind; enumerating
    /// <c>roles/</c> would quietly resurrect a rule the operator had removed. The list is the set
    /// of assignments that actually exist.
    /// </para>
    /// <para>
    /// An empty list is pre-#309 generated config. The fallback is exactly the two paths this used
    /// to read, so an agent that has not been reprovisioned assembles byte for byte what it did
    /// before.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<string> InstructionDirectories()
    {
        if (_config.InstructionOrder.Count == 0)
            return [BaseDirectoryName, _config.Role];

        var ordered = new List<string>(_config.InstructionOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Hoisted, not skipped. The loop below still walks past _base's own entry, and that
        // encounter is the hoist catching up with itself — NOT a repeat. Treating it as one made
        // every prompt build log a duplicate warning, which is how a real duplicate would have
        // gone unnoticed.
        var baseHoisted = _config.InstructionOrder.Contains(BaseDirectoryName, StringComparer.Ordinal);
        if (baseHoisted)
        {
            ordered.Add(BaseDirectoryName);
            seen.Add(BaseDirectoryName);
        }

        var basePending = baseHoisted;

        foreach (var name in _config.InstructionOrder)
        {
            if (basePending && string.Equals(name, BaseDirectoryName, StringComparison.Ordinal))
            {
                // The hoisted entry. A SECOND _base after this one is a genuine repeat and falls
                // through to the warning below.
                basePending = false;
                continue;
            }

            // Not a silent drop: a repeat would inline the same bytes twice, which for a safety
            // rule reads as emphasis and for a long one wastes context. Reported either way.
            if (!seen.Add(name))
            {
                logger.LogWarning(
                    "[PromptBuilder] Instruction '{Instruction}' appears more than once in "
                    + "InstructionOrder — inlining it once", name);
                continue;
            }

            ordered.Add(name);
        }

        return ordered;
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
