using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Data;

public static class DbSeeder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task SeedAsync(
        OrchestratorDbContext db,
        string rolesDir,
        string? seedFilePath = null,
        ILogger? logger = null,
        TemporalClientRegistry? temporal = null,
        string? projectsDir = null)
    {
        logger?.LogInformation("Running database migrations...");
        await db.Database.MigrateAsync();

        logger?.LogInformation("Seeding instructions from {RolesDir}...", rolesDir);
        await SeedInstructionsAsync(db, rolesDir, logger);

        if (projectsDir != null)
        {
            logger?.LogInformation("Seeding project contexts from {ProjectsDir}...", projectsDir);
            await SeedProjectContextsAsync(db, projectsDir, logger);
        }

        if (string.IsNullOrWhiteSpace(seedFilePath))
        {
            logger?.LogInformation("Provisioning:SeedFilePath not set — skipping agent seed");
            return;
        }

        if (!File.Exists(seedFilePath))
        {
            logger?.LogWarning("Seed file not found at {Path} — skipping agent seed", seedFilePath);
            return;
        }

        logger?.LogInformation("Seeding agents from {Path}...", seedFilePath);
        await SeedFromFileAsync(db, seedFilePath, logger, temporal);
    }

    private static async Task SeedFromFileAsync(
        OrchestratorDbContext db, string seedFilePath, ILogger? logger,
        TemporalClientRegistry? temporal = null)
    {
        var json = await File.ReadAllTextAsync(seedFilePath);
        SeedFile? seedFile;
        try
        {
            seedFile = JsonSerializer.Deserialize<SeedFile>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning("Seed file at {Path} is malformed — skipping agent seed: {Error}", seedFilePath, ex.Message);
            return;
        }

        if (seedFile is null)
        {
            logger?.LogWarning("Seed file is empty or invalid — skipping seed");
            return;
        }

        await SeedWorkflowDefinitionsAsync(db, seedFile, logger);
        await SeedRepositoriesAsync(db, seedFile, logger);

        if (temporal is not null)
            await SeedSchedulesAsync(seedFile, temporal, logger);
        else if (seedFile.Schedules.Count > 0)
            logger?.LogInformation("Temporal not configured — skipping schedule seed ({Count} schedules defined in seed file)", seedFile.Schedules.Count);

        if (seedFile.Agents.Count == 0)
        {
            logger?.LogInformation("No agents defined in seed file — skipping agent seed");
            return;
        }

        foreach (var def in seedFile.Agents)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                logger?.LogWarning("Skipping seed entry with empty name");
                continue;
            }

            var exists = await db.Agents.AnyAsync(a => a.Name == def.Name);
            if (exists)
            {
                logger?.LogDebug("Agent '{Name}' already exists — skipping", def.Name);
                continue;
            }

            var agent = new Agent
            {
                Name                     = def.Name,
                DisplayName              = def.DisplayName,
                ContainerName            = def.ContainerName,
                Role                     = def.Role,
                Model                    = def.Model,
                MemoryLimitMb            = def.MemoryLimitMb,
                IsEnabled                = def.IsEnabled,
                PermissionMode           = def.PermissionMode,
                MaxTurns                 = def.MaxTurns,
                WorkDir                  = def.WorkDir,
                ProactiveIntervalMinutes = def.ProactiveIntervalMinutes,
                GroupListenMode          = def.GroupListenMode,
                GroupDebounceSeconds     = def.GroupDebounceSeconds,
                ShortName                = def.ShortName,
                ShowStats                = def.ShowStats,
                TelegramSendOnly         = def.TelegramSendOnly,
                PrefixMessages           = def.PrefixMessages,
            };
            db.Agents.Add(agent);
            await db.SaveChangesAsync();

            foreach (var tool in def.Tools)
                db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolName = tool, IsEnabled = true });

            foreach (var project in def.Projects)
                db.AgentProjects.Add(new AgentProject { AgentId = agent.Id, ProjectName = project });

            foreach (var mcp in def.McpEndpoints)
                db.AgentMcpEndpoints.Add(new AgentMcpEndpoint
                {
                    AgentId = agent.Id, McpName = mcp.Name, Url = mcp.Url, TransportType = mcp.Transport,
                });

            foreach (var key in def.EnvRefs)
                db.AgentEnvRefs.Add(new AgentEnvRef { AgentId = agent.Id, EnvKeyName = key });

            foreach (var network in def.Networks)
                db.AgentNetworks.Add(new AgentNetwork { AgentId = agent.Id, NetworkName = network });

            foreach (var userId in def.TelegramUsers)
                db.AgentTelegramUsers.Add(new AgentTelegramUser { AgentId = agent.Id, UserId = userId });

            foreach (var groupId in def.TelegramGroups)
                db.AgentTelegramGroups.Add(new AgentTelegramGroup { AgentId = agent.Id, GroupId = groupId });

            await db.SaveChangesAsync();
            await LinkInstructionsAsync(db, agent, def.Role, logger);

            logger?.LogInformation("Seeded agent '{Name}'", def.Name);
        }
    }

    private static async Task SeedRepositoriesAsync(
        OrchestratorDbContext db, SeedFile seedFile, ILogger? logger)
    {
        foreach (var def in seedFile.Repositories)
        {
            if (string.IsNullOrWhiteSpace(def.Name) || string.IsNullOrWhiteSpace(def.FullName))
            {
                logger?.LogWarning("Skipping repository with empty name or fullName");
                continue;
            }

            var exists = await db.Repositories.AnyAsync(r => r.Name == def.Name);
            if (exists)
            {
                logger?.LogDebug("Repository '{Name}' already exists — skipping", def.Name);
                continue;
            }

            db.Repositories.Add(new Repository { Name = def.Name, FullName = def.FullName });
            await db.SaveChangesAsync();
            logger?.LogInformation("Seeded repository '{Name}'", def.Name);
        }
    }

    private static async Task SeedWorkflowDefinitionsAsync(
        OrchestratorDbContext db, SeedFile seedFile, ILogger? logger)
    {
        foreach (var def in seedFile.WorkflowDefinitions)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                logger?.LogWarning("Skipping workflow definition with empty name");
                continue;
            }

            var exists = await db.WorkflowDefinitions.AnyAsync(w => w.Name == def.Name);
            if (exists)
            {
                logger?.LogDebug("Workflow definition '{Name}' already exists — skipping", def.Name);
                continue;
            }

            var definitionJson = def.Definition.GetRawText();
            db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Name        = def.Name,
                Namespace   = def.Namespace,
                TaskQueue   = def.TaskQueue,
                Description = def.Description,
                Definition  = definitionJson,
                Version     = 1,
                IsActive    = true,
                CreatedBy   = "seed",
            });
            await db.SaveChangesAsync();
            logger?.LogInformation("Seeded workflow definition '{Name}'", def.Name);
        }
    }

    private static async Task SeedSchedulesAsync(
        SeedFile seedFile, TemporalClientRegistry temporal, ILogger? logger)
    {
        foreach (var def in seedFile.Schedules)
        {
            if (string.IsNullOrWhiteSpace(def.ScheduleId) || string.IsNullOrWhiteSpace(def.CronExpression))
            {
                logger?.LogWarning("Skipping schedule with empty scheduleId or cronExpression");
                continue;
            }

            try
            {
                object? input = def.Input.HasValue
                    ? JsonSerializer.Deserialize<object>(def.Input.Value.GetRawText(), JsonOpts)
                    : null;

                await temporal.CreateScheduleAsync(
                    def.Namespace, def.ScheduleId, def.WorkflowType, def.TaskQueue,
                    def.CronExpression, input, def.Memo, paused: false);

                logger?.LogInformation("Seeded Temporal schedule '{ScheduleId}'", def.ScheduleId);
            }
            catch (Exception ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogDebug("Schedule '{ScheduleId}' already exists — skipping", def.ScheduleId);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("Failed to seed schedule '{ScheduleId}': {Error}", def.ScheduleId, ex.Message);
            }
        }
    }

    private static async Task SeedProjectContextsAsync(OrchestratorDbContext db, string projectsDir, ILogger? logger)
    {
        if (!Directory.Exists(projectsDir))
        {
            logger?.LogWarning("Projects directory not found: {Dir}", projectsDir);
            return;
        }

        foreach (var projectDir in Directory.GetDirectories(projectsDir).OrderBy(d => d))
        {
            var projectName = Path.GetFileName(projectDir);
            var contextPath = Path.Combine(projectDir, "context.md");
            if (!File.Exists(contextPath))
            {
                logger?.LogWarning("No context.md found for project {Project}", projectName);
                continue;
            }

            await UpsertProjectContextAsync(db, projectName, await File.ReadAllTextAsync(contextPath), logger);
        }
    }

    private static async Task UpsertProjectContextAsync(
        OrchestratorDbContext db, string name, string content, ILogger? logger)
    {
        var existing = await db.ProjectContexts
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Name == name);

        if (existing != null)
        {
            logger?.LogDebug("Project context '{Name}' already exists, skipping", name);
            return;
        }

        var context = new ProjectContext { Name = name, CurrentVersion = 1 };
        db.ProjectContexts.Add(context);
        await db.SaveChangesAsync();

        db.ProjectContextVersions.Add(new ProjectContextVersion
        {
            ProjectContextId = context.Id,
            VersionNumber    = 1,
            Content          = content,
            CreatedBy        = "seed",
            Reason           = "Initial seed from projects directory",
        });
        await db.SaveChangesAsync();

        logger?.LogInformation("Seeded project context '{Name}'", name);
    }

    private static async Task SeedInstructionsAsync(OrchestratorDbContext db, string rolesDir, ILogger? logger)
    {
        // Seed _base first
        var basePath = Path.Combine(rolesDir, "_base", "system.md");
        if (File.Exists(basePath))
            await UpsertInstructionAsync(db, "base", await File.ReadAllTextAsync(basePath), logger);
        else
            logger?.LogWarning("Base instruction not found at {Path}", basePath);

        // Seed each role directory
        if (!Directory.Exists(rolesDir))
        {
            logger?.LogWarning("Roles directory not found: {Dir}", rolesDir);
            return;
        }

        foreach (var roleDir in Directory.GetDirectories(rolesDir).OrderBy(d => d))
        {
            var roleName = Path.GetFileName(roleDir);
            if (roleName == "_base") continue;

            var mdPath = Path.Combine(roleDir, "system.md");
            if (!File.Exists(mdPath))
            {
                logger?.LogWarning("No system.md found for role {Role}", roleName);
                continue;
            }

            await UpsertInstructionAsync(db, roleName, await File.ReadAllTextAsync(mdPath), logger);
        }
    }

    private static async Task UpsertInstructionAsync(
        OrchestratorDbContext db, string name, string content, ILogger? logger)
    {
        var existing = await db.Instructions
            .Include(i => i.Versions)
            .FirstOrDefaultAsync(i => i.Name == name);

        if (existing != null)
        {
            logger?.LogDebug("Instruction '{Name}' already exists, skipping", name);
            return;
        }

        var instruction = new Instruction { Name = name, CurrentVersion = 1 };
        db.Instructions.Add(instruction);
        await db.SaveChangesAsync();

        db.InstructionVersions.Add(new InstructionVersion
        {
            InstructionId = instruction.Id,
            VersionNumber = 1,
            Content = content,
            CreatedBy = "seed",
            Reason = "Initial seed from roles directory",
        });
        await db.SaveChangesAsync();

        logger?.LogInformation("Seeded instruction '{Name}'", name);
    }

    private static async Task LinkInstructionsAsync(
        OrchestratorDbContext db, Agent agent, string role, ILogger? logger)
    {
        var existing = await db.AgentInstructions
            .Where(ai => ai.AgentId == agent.Id)
            .ToListAsync();

        var baseInstruction = await db.Instructions.FirstOrDefaultAsync(i => i.Name == "base");
        if (baseInstruction != null && existing.All(ai => ai.InstructionId != baseInstruction.Id))
        {
            db.AgentInstructions.Add(new AgentInstruction
            {
                AgentId = agent.Id,
                InstructionId = baseInstruction.Id,
                LoadOrder = 1,
            });
        }

        var roleInstruction = await db.Instructions.FirstOrDefaultAsync(i => i.Name == role);
        if (roleInstruction != null && existing.All(ai => ai.InstructionId != roleInstruction.Id))
        {
            db.AgentInstructions.Add(new AgentInstruction
            {
                AgentId = agent.Id,
                InstructionId = roleInstruction.Id,
                LoadOrder = 2,
            });
        }
        else if (roleInstruction == null)
        {
            logger?.LogWarning("No instruction found for role '{Role}', skipping role instruction link", role);
        }

        await db.SaveChangesAsync();
    }
}
