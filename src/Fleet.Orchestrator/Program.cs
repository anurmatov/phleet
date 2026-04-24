using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection(TemporalOptions.Section));

// Database
var connectionString = builder.Configuration.GetConnectionString("OrchestratorDb");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<OrchestratorDbContext>(opts =>
        opts.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)),
            mysql => mysql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null)));
}

// Core services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TaskHistoryStore>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<ContainerProvisioningService>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<ICredentialsReader, EnvFileCredentialsReader>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<MemoryProxyService>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

// HeartbeatConsumerService registered as singleton so tools can inject IRabbitMqStatus
builder.Services.AddSingleton<HeartbeatConsumerService>();
builder.Services.AddSingleton<IRabbitMqStatus>(sp => sp.GetRequiredService<HeartbeatConsumerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatConsumerService>());

builder.Services.AddHostedService<AgentStateMonitorService>();

// Rate limiting — fixed-window 10 req/min per bearer token (or IP if unauthenticated)
builder.Services.AddRateLimiter(rl =>
{
    rl.AddPolicy("reveal-rate-limit", ctx =>
    {
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault() ?? "";
        var partitionKey = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
    rl.RejectionStatusCode = 429;
});

// DB-backed agent config preloader (disabled if DB is not configured)
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddHostedService<AgentConfigService>();
}

// Temporal workflow poller (disabled if Temporal:Address is empty)
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddSingleton<TemporalClientRegistry>();
builder.Services.AddHostedService<TemporalPollerService>();

// MCP server (HTTP transport — same port as REST, path /mcp)
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Auto-migrate and seed DB on startup
if (!string.IsNullOrEmpty(connectionString))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var rolesDir      = app.Configuration["Provisioning:RolesDir"]     ?? "/app/roles";
    var projectsDir   = app.Configuration["Provisioning:ProjectsDir"]  ?? "/app/projects";
    var seedFilePath  = app.Configuration["Provisioning:SeedFilePath"];
    var temporalAddr  = app.Configuration["Temporal:Address"] ?? "";
    var temporalReg   = string.IsNullOrWhiteSpace(temporalAddr)
        ? null
        : app.Services.GetRequiredService<TemporalClientRegistry>();
    await DbSeeder.SeedAsync(db, rolesDir, seedFilePath, startupLogger, temporalReg, projectsDir);
}

// WebSocket support
app.UseWebSockets();

// Rate limiting middleware
app.UseRateLimiter();

// Bearer token auth on mutating endpoints (non-GET).
// If Orchestrator:AuthToken is empty, auth is disabled (dev/backward-compat mode).
var orchestratorAuthToken = app.Configuration["Orchestrator:AuthToken"] ?? "";
if (!string.IsNullOrWhiteSpace(orchestratorAuthToken))
{
    app.Use(async (context, next) =>
    {
        var method = context.Request.Method;
        var path   = context.Request.Path.Value ?? "";

        // Only protect mutating HTTP methods; skip GETs, WebSocket upgrades, /health, /mcp
        var isReadOnly   = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var isExemptPath = path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("/api/config", StringComparison.OrdinalIgnoreCase)
                        || path.Equals("/health", StringComparison.OrdinalIgnoreCase);

        if (isReadOnly || isExemptPath)
        {
            await next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        var token      = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        if (token != orchestratorAuthToken)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        await next(context);
    });
}

// Config API auth — ORCHESTRATOR_CONFIG_TOKEN gates /api/config/* (all methods).
// Separate from ORCHESTRATOR_AUTH_TOKEN so peers can read config without full admin access.
// Fails CLOSED: if ORCHESTRATOR_CONFIG_TOKEN is not configured all /api/config/* requests
// return 503 so the endpoint is never accidentally open on a misconfigured deployment.
var orchestratorConfigToken = app.Configuration["Orchestrator:ConfigToken"] ?? "";
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/config", StringComparison.OrdinalIgnoreCase))
    {
        await next(context);
        return;
    }

    if (string.IsNullOrWhiteSpace(orchestratorConfigToken))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Config API unavailable: ORCHESTRATOR_CONFIG_TOKEN is not configured on this orchestrator"
        });
        return;
    }

    var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
    var token = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
        ? authHeader["Bearer ".Length..].Trim()
        : null;

    if (token != orchestratorConfigToken)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    await next(context);
});

// MCP endpoint — explicitly mapped to /mcp so the auth middleware exemption matches
app.MapMcp("/mcp");

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-orchestrator" }));

// ── Config API ────────────────────────────────────────────────────────────────

// GET /api/config/values?keys=KEY1,KEY2,...
// Returns literal and agent-derived values for the requested keys.
// Rate-limited to 10 req/min per bearer token to limit secret enumeration surface.
app.MapGet("/api/config/values", async (HttpRequest request, ConfigService configService, CancellationToken ct) =>
{
    var keysParam = request.Query["keys"].FirstOrDefault() ?? "";
    var keys = keysParam
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var result = await configService.GetValuesAsync(keys, ct);
    return Results.Ok(new { literals = result.Literals, agentDerived = result.AgentDerived });
}).RequireRateLimiting("reveal-rate-limit");

// GET /api/config/all — returns all non-denylisted .env keys (for dashboard credentials table)
// Rate-limited to 10 req/min per bearer token to limit secret enumeration surface.
app.MapGet("/api/config/all", (ConfigService configService) =>
{
    var all = configService.GetAll();
    return Results.Ok(all);
}).RequireRateLimiting("reveal-rate-limit");

// POST /api/config/reload — re-reads .env, diffs, publishes config.changed
app.MapPost("/api/config/reload", async (ConfigService configService, CancellationToken ct) =>
{
    var changed = await configService.ReloadAsync(ct);
    return Results.Ok(new { changedKeys = changed, message = $"{changed.Count} key(s) changed, peers notified" });
});

// PUT /api/config/values — atomically writes key-value pairs to .env + broadcasts config.changed
app.MapPut("/api/config/values", async (HttpRequest request, ConfigService configService, CancellationToken ct) =>
{
    Dictionary<string, string>? kvs;
    try { kvs = await request.ReadFromJsonAsync<Dictionary<string, string>>(ct); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }

    if (kvs is null || kvs.Count == 0)
        return Results.BadRequest(new { error = "empty_payload" });

    try
    {
        var changed = await configService.PutValuesAsync(kvs, ct);
        return Results.Ok(new { changedKeys = changed, message = $"{changed.Count} key(s) changed, peers notified" });
    }
    catch (DenylistedException ex)
    {
        return Results.Json(new { error = "denylisted", detail = ex.Message }, statusCode: 403);
    }
    catch (TimeoutException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Write failed: {ex.Message}", statusCode: 500);
    }
});

// REST: list all known agents
app.MapGet("/api/agents", async (AgentRegistry registry, IServiceScopeFactory scopeFactory) =>
{
    // Merge DB agent records with in-memory registry so DB-only (not yet provisioned) agents are visible.
    try
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is not null)
        {
            var dbAgents = await db.Agents.AsNoTracking().ToListAsync();
            foreach (var a in dbAgents)
                registry.PreloadFromDbConfig(a); // no-op if agent already in registry
        }
    }
    catch { /* DB unavailable — return what we have */ }
    return Results.Ok(registry.GetAll());
});

// REST: get a specific agent by name
app.MapGet("/api/agents/{name}", (string name, AgentRegistry registry) =>
{
    var agent = registry.Get(name);
    return agent is not null ? Results.Ok(agent) : Results.NotFound();
});

// REST: task history for a specific agent
app.MapGet("/api/agents/{name}/history", (string name, TaskHistoryStore taskHistory) =>
    Results.Ok(taskHistory.GetHistory(name)));

// REST: get full DB config for an agent (all fields + related tables)
app.MapGet("/api/agents/{name}/config", async (string name, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var agent = await db.Agents
        .Include(a => a.Tools.OrderBy(t => t.ToolName))
        .Include(a => a.Projects.OrderBy(p => p.ProjectName))
        .Include(a => a.McpEndpoints.OrderBy(e => e.McpName))
        .Include(a => a.Networks.OrderBy(n => n.NetworkName))
        .Include(a => a.EnvRefs.OrderBy(r => r.EnvKeyName))
        .Include(a => a.TelegramUsers.OrderBy(u => u.UserId))
        .Include(a => a.TelegramGroups.OrderBy(g => g.GroupId))
        .Include(a => a.Instructions.OrderBy(i => i.LoadOrder))
            .ThenInclude(ai => ai.Instruction)
        .AsSplitQuery()
        .AsNoTracking()
        .FirstOrDefaultAsync(a => a.Name == name);

    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

    return Results.Ok(new
    {
        agent.Name,
        agent.Model,
        agent.MemoryLimitMb,
        agent.IsEnabled,
        agent.Image,
        agent.PermissionMode,
        agent.MaxTurns,
        agent.WorkDir,
        agent.ProactiveIntervalMinutes,
        agent.GroupListenMode,
        agent.GroupDebounceSeconds,
        agent.ShortName,
        agent.ShowStats,
        agent.PrefixMessages,
        agent.SuppressToolMessages,
        agent.TelegramSendOnly,
        agent.Effort,
        agent.JsonSchema,
        agent.AgentsJson,
        agent.HostPort,
        agent.AutoMemoryEnabled,
        agent.Provider,
        agent.CodexSandboxMode,
        Tools = agent.Tools.Select(t => new { t.ToolName, t.IsEnabled }),
        Projects = agent.Projects.Select(p => p.ProjectName),
        McpEndpoints = agent.McpEndpoints.Select(e => new { e.McpName, e.Url, e.TransportType }),
        Networks = agent.Networks.Select(n => n.NetworkName),
        EnvRefs = agent.EnvRefs.Select(r => r.EnvKeyName),
        TelegramUsers = agent.TelegramUsers.Select(u => u.UserId),
        TelegramGroups = agent.TelegramGroups.Select(g => g.GroupId),
        Instructions = agent.Instructions.Select(i => new { i.Instruction.Name, i.LoadOrder }),
    });
});

// REST: update agent DB config (all scalar fields + replace-all for related tables)
app.MapPut("/api/agents/{name}/config", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory, AgentRegistry registry, SetupService setupService) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<AgentConfigUpdateRequest>();
    if (body is null)
        return Results.BadRequest(new { error = "Invalid request body" });

    var agent = await db.Agents
        .Include(a => a.Tools)
        .Include(a => a.Projects)
        .Include(a => a.McpEndpoints)
        .Include(a => a.Networks)
        .Include(a => a.EnvRefs)
        .Include(a => a.TelegramUsers)
        .Include(a => a.TelegramGroups)
        .Include(a => a.Instructions)
        .FirstOrDefaultAsync(a => a.Name == name);

    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

    // Scalar fields
    if (body.Model is not null) agent.Model = body.Model;
    if (body.MemoryLimitMb is not null) agent.MemoryLimitMb = body.MemoryLimitMb.Value;
    if (body.IsEnabled is not null) agent.IsEnabled = body.IsEnabled.Value;
    if (body.Image is not null) agent.Image = body.Image == "" ? null : body.Image;
    if (body.PermissionMode is not null)
    {
        if (string.Equals(body.PermissionMode, "bypassPermissions", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new
            {
                error = "PermissionMode 'bypassPermissions' is not supported — fleet containers run as root and Claude CLI " +
                        "rejects --dangerously-skip-permissions for root processes. Use 'acceptEdits' with an explicit tools list."
            });
        agent.PermissionMode = body.PermissionMode;
    }
    if (body.MaxTurns is not null) agent.MaxTurns = body.MaxTurns.Value;
    if (body.WorkDir is not null) agent.WorkDir = body.WorkDir;
    if (body.ProactiveIntervalMinutes is not null) agent.ProactiveIntervalMinutes = body.ProactiveIntervalMinutes.Value;
    if (body.GroupListenMode is not null) agent.GroupListenMode = body.GroupListenMode;
    if (body.GroupDebounceSeconds is not null) agent.GroupDebounceSeconds = body.GroupDebounceSeconds.Value;
    if (body.ShortName is not null) agent.ShortName = string.IsNullOrWhiteSpace(body.ShortName) ? agent.Name : body.ShortName.Trim();
    if (body.ShowStats is not null) agent.ShowStats = body.ShowStats.Value;
    if (body.PrefixMessages is not null) agent.PrefixMessages = body.PrefixMessages.Value;
    if (body.SuppressToolMessages is not null) agent.SuppressToolMessages = body.SuppressToolMessages.Value;
    if (body.TelegramSendOnly is not null) agent.TelegramSendOnly = body.TelegramSendOnly.Value;
    if (body.Effort is not null) agent.Effort = body.Effort == "" ? null : body.Effort;
    if (body.JsonSchema is not null) agent.JsonSchema = body.JsonSchema == "" ? null : body.JsonSchema;
    if (body.AgentsJson is not null) agent.AgentsJson = body.AgentsJson == "" ? null : body.AgentsJson;
    if (body.AutoMemoryEnabled is not null) agent.AutoMemoryEnabled = body.AutoMemoryEnabled.Value;
    if (body.Provider is not null) agent.Provider = body.Provider;
    if (body.CodexSandboxMode is not null)
    {
        var validModes = new[] { "read-only", "workspace-write", "danger-full-access" };
        if (body.CodexSandboxMode != "" && !Array.Exists(validModes, m => m == body.CodexSandboxMode))
            return Results.BadRequest(new
            {
                error = $"Invalid CodexSandboxMode '{body.CodexSandboxMode}'. Valid values: read-only, workspace-write, danger-full-access."
            });
        agent.CodexSandboxMode = body.CodexSandboxMode == "" ? null : body.CodexSandboxMode;
    }

    // Replace-all for related tables (omit field = keep current)
    if (body.Tools is not null)
    {
        var existingEnabled = agent.Tools
            .ToDictionary(t => t.ToolName, t => t.IsEnabled, StringComparer.OrdinalIgnoreCase);
        db.AgentTools.RemoveRange(agent.Tools);
        agent.Tools = body.Tools
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new AgentTool
            {
                AgentId = agent.Id,
                ToolName = t,
                IsEnabled = existingEnabled.TryGetValue(t, out var was) ? was : true,
            })
            .ToList();
    }

    if (body.Projects is not null)
    {
        db.AgentProjects.RemoveRange(agent.Projects);
        agent.Projects = body.Projects
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new AgentProject { AgentId = agent.Id, ProjectName = p })
            .ToList();
    }

    if (body.McpEndpoints is not null)
    {
        db.AgentMcpEndpoints.RemoveRange(agent.McpEndpoints);
        agent.McpEndpoints = body.McpEndpoints
            .DistinctBy(e => e.McpName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new AgentMcpEndpoint { AgentId = agent.Id, McpName = e.McpName, Url = e.Url, TransportType = e.TransportType })
            .ToList();
    }

    if (body.Networks is not null)
    {
        db.AgentNetworks.RemoveRange(agent.Networks);
        agent.Networks = body.Networks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new AgentNetwork { AgentId = agent.Id, NetworkName = n })
            .ToList();
    }

    if (body.EnvRefs is not null)
    {
        db.AgentEnvRefs.RemoveRange(agent.EnvRefs);
        agent.EnvRefs = body.EnvRefs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(r => new AgentEnvRef { AgentId = agent.Id, EnvKeyName = r })
            .ToList();
    }

    if (body.TelegramUsers is not null)
    {
        db.AgentTelegramUsers.RemoveRange(agent.TelegramUsers);
        var tuSet = new HashSet<long>(body.TelegramUsers.Distinct());
        var ownerId = setupService.GetTelegramUserId();
        if (ownerId.HasValue) tuSet.Add(ownerId.Value);
        agent.TelegramUsers = tuSet
            .Select(u => new AgentTelegramUser { AgentId = agent.Id, UserId = u })
            .ToList();
    }

    if (body.TelegramGroups is not null)
    {
        db.AgentTelegramGroups.RemoveRange(agent.TelegramGroups);
        agent.TelegramGroups = body.TelegramGroups
            .Distinct()
            .Select(g => new AgentTelegramGroup { AgentId = agent.Id, GroupId = g })
            .ToList();
    }

    if (body.Instructions is not null)
    {
        // Resolve instruction names to IDs, then upsert the join table
        var instructionNames = body.Instructions
            .Select(i => i.InstructionName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var instructions = await db.Instructions
            .Where(i => instructionNames.Contains(i.Name))
            .ToListAsync();

        var missing = instructionNames
            .Where(n => instructions.All(i => !i.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count > 0)
            return Results.BadRequest(new { error = $"Instruction(s) not found: {string.Join(", ", missing)}" });

        db.AgentInstructions.RemoveRange(agent.Instructions);
        agent.Instructions = body.Instructions
            .DistinctBy(i => i.InstructionName, StringComparer.OrdinalIgnoreCase)
            .Select(i =>
            {
                var instr = instructions.First(x => x.Name.Equals(i.InstructionName, StringComparison.OrdinalIgnoreCase));
                return new AgentInstruction { AgentId = agent.Id, InstructionId = instr.Id, LoadOrder = i.LoadOrder };
            })
            .ToList();
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"Agent '{name}' config updated" });
});

// REST: list all instructions with version summary
app.MapGet("/api/instructions", async (IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var instructions = await db.Instructions
        .Include(i => i.Versions)
        .Include(i => i.AgentInstructions)
            .ThenInclude(ai => ai.Agent)
        .AsSplitQuery()
        .AsNoTracking()
        .OrderBy(i => i.Name)
        .ToListAsync();

    return Results.Ok(instructions.Select(i => new
    {
        i.Name,
        i.CurrentVersion,
        i.IsActive,
        TotalVersions = i.Versions.Count,
        Agents = i.AgentInstructions.Select(ai => ai.Agent.Name).OrderBy(n => n),
    }));
});

// REST: get full instruction with all versions and content
app.MapGet("/api/instructions/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var instruction = await db.Instructions
        .Include(i => i.Versions.OrderByDescending(v => v.VersionNumber))
        .AsNoTracking()
        .FirstOrDefaultAsync(i => i.Name == name);

    if (instruction is null)
        return Results.NotFound(new { error = $"Instruction '{name}' not found" });

    return Results.Ok(new
    {
        instruction.Name,
        instruction.CurrentVersion,
        Versions = instruction.Versions.Select(v => new
        {
            v.VersionNumber,
            v.Content,
            CreatedAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            v.CreatedBy,
            v.Reason,
        }),
    });
});

// REST: create new version of an instruction
app.MapPost("/api/instructions/{name}/versions", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<InstructionUpdateRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { error = "content is required" });

    const int MaxVersions = 20;

    var instruction = await db.Instructions
        .Include(i => i.Versions.OrderBy(v => v.VersionNumber))
        .FirstOrDefaultAsync(i => i.Name == name);

    if (instruction is null)
        return Results.NotFound(new { error = $"Instruction '{name}' not found" });

    var newVersion = instruction.CurrentVersion + 1;
    instruction.Versions.Add(new InstructionVersion
    {
        InstructionId = instruction.Id,
        VersionNumber = newVersion,
        Content       = body.Content,
        CreatedAt     = DateTime.UtcNow,
        CreatedBy     = body.CreatedBy ?? "dashboard",
        Reason        = body.Reason,
    });
    instruction.CurrentVersion = newVersion;

    var excess = instruction.Versions.Count - MaxVersions;
    if (excess > 0)
        db.InstructionVersions.RemoveRange(instruction.Versions.OrderBy(v => v.VersionNumber).Take(excess));

    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"Instruction '{name}' updated to v{newVersion}", version = newVersion });
});

// REST: rollback instruction to a prior version (creates new version with old content)
app.MapPost("/api/instructions/{name}/rollback/{targetVersion:int}", async (string name, int targetVersion, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    const int MaxVersions = 20;

    var instruction = await db.Instructions
        .Include(i => i.Versions.OrderBy(v => v.VersionNumber))
        .FirstOrDefaultAsync(i => i.Name == name);

    if (instruction is null)
        return Results.NotFound(new { error = $"Instruction '{name}' not found" });

    var target = instruction.Versions.FirstOrDefault(v => v.VersionNumber == targetVersion);
    if (target is null)
        return Results.NotFound(new { error = $"Version {targetVersion} not found" });

    var newVersion = instruction.CurrentVersion + 1;
    instruction.Versions.Add(new InstructionVersion
    {
        InstructionId = instruction.Id,
        VersionNumber = newVersion,
        Content       = target.Content,
        CreatedAt     = DateTime.UtcNow,
        CreatedBy     = "rollback",
        Reason        = $"rollback to v{targetVersion}",
    });
    instruction.CurrentVersion = newVersion;

    var excess = instruction.Versions.Count - MaxVersions;
    if (excess > 0)
        db.InstructionVersions.RemoveRange(instruction.Versions.OrderBy(v => v.VersionNumber).Take(excess));

    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"Rolled back '{name}' to v{targetVersion} content — saved as v{newVersion}", version = newVersion });
});

// REST: create a new instruction with initial v1 content
app.MapPost("/api/instructions", async (HttpRequest request, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<InstructionCreateRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { error = "name and content are required" });

    if (!System.Text.RegularExpressions.Regex.IsMatch(body.Name, @"^[a-zA-Z0-9_-]+$"))
        return Results.BadRequest(new { error = "name must contain only letters, digits, hyphens, or underscores" });

    var exists = await db.Instructions.AnyAsync(i => i.Name == body.Name);
    if (exists)
        return Results.Conflict(new { error = $"Instruction '{body.Name}' already exists" });

    var instr = new Instruction { Name = body.Name, CurrentVersion = 1 };
    db.Instructions.Add(instr);
    await db.SaveChangesAsync();

    db.InstructionVersions.Add(new InstructionVersion
    {
        InstructionId = instr.Id,
        VersionNumber = 1,
        Content       = body.Content,
        CreatedAt     = DateTime.UtcNow,
        CreatedBy     = body.CreatedBy ?? "dashboard",
        Reason        = body.Reason ?? "Initial creation",
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { message = $"Instruction '{body.Name}' created at v1" });
});

// REST: toggle active/inactive on an instruction
app.MapPost("/api/instructions/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    var instr = await db.Instructions.FirstOrDefaultAsync(i => i.Name == name);
    if (instr is null) return Results.NotFound(new { error = $"Instruction '{name}' not found" });
    instr.IsActive = req.IsActive;
    await db.SaveChangesAsync();
    return Results.Ok(new { instr.Name, instr.IsActive });
});

// REST: list all project contexts with version summary + agent assignments
app.MapGet("/api/project-contexts", async (IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var contexts = await db.ProjectContexts
        .AsNoTracking()
        .OrderBy(p => p.Name)
        .Select(p => new
        {
            p.Name,
            p.CurrentVersion,
            p.IsActive,
            TotalVersions = db.ProjectContextVersions.Count(v => v.ProjectContextId == p.Id),
        })
        .ToListAsync();

    // Agent assignments via agent_projects (name-keyed join)
    var agents = await db.Agents
        .Include(a => a.Projects)
        .AsNoTracking()
        .ToListAsync();

    return Results.Ok(contexts.Select(p => new
    {
        p.Name,
        p.CurrentVersion,
        p.IsActive,
        p.TotalVersions,
        Agents = agents
            .Where(a => a.Projects.Any(pr => pr.ProjectName.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Name)
            .OrderBy(n => n),
    }));
});

// REST: get full project context with all versions and content
app.MapGet("/api/project-contexts/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var ctx = await db.ProjectContexts
        .Include(p => p.Versions.OrderByDescending(v => v.VersionNumber))
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.Name == name);

    if (ctx is null)
        return Results.NotFound(new { error = $"Project context '{name}' not found" });

    return Results.Ok(new
    {
        ctx.Name,
        ctx.CurrentVersion,
        Versions = ctx.Versions.Select(v => new
        {
            v.VersionNumber,
            v.Content,
            CreatedAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            v.CreatedBy,
            v.Reason,
        }),
    });
});

// REST: create new project context with initial v1 content
app.MapPost("/api/project-contexts", async (HttpRequest request, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<ProjectContextCreateRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { error = "name and content are required" });

    if (!System.Text.RegularExpressions.Regex.IsMatch(body.Name, @"^[a-zA-Z0-9_-]+$"))
        return Results.BadRequest(new { error = "name must contain only letters, digits, hyphens, or underscores" });

    var exists = await db.ProjectContexts.AnyAsync(p => p.Name == body.Name);
    if (exists)
        return Results.Conflict(new { error = $"Project context '{body.Name}' already exists" });

    var ctx = new ProjectContext { Name = body.Name, CurrentVersion = 1 };
    db.ProjectContexts.Add(ctx);
    await db.SaveChangesAsync();

    db.ProjectContextVersions.Add(new ProjectContextVersion
    {
        ProjectContextId = ctx.Id,
        VersionNumber    = 1,
        Content          = body.Content,
        CreatedAt        = DateTime.UtcNow,
        CreatedBy        = body.CreatedBy ?? "dashboard",
        Reason           = "Initial creation",
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { message = $"Project context '{body.Name}' created at v1" });
});

// REST: create new version of a project context
app.MapPost("/api/project-contexts/{name}/versions", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<ProjectContextUpdateRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Content))
        return Results.BadRequest(new { error = "content is required" });

    const int MaxVersions = 20;

    var ctx = await db.ProjectContexts
        .Include(p => p.Versions.OrderBy(v => v.VersionNumber))
        .FirstOrDefaultAsync(p => p.Name == name);

    if (ctx is null)
        return Results.NotFound(new { error = $"Project context '{name}' not found" });

    var newVersion = ctx.CurrentVersion + 1;
    ctx.Versions.Add(new ProjectContextVersion
    {
        ProjectContextId = ctx.Id,
        VersionNumber    = newVersion,
        Content          = body.Content,
        CreatedAt        = DateTime.UtcNow,
        CreatedBy        = body.CreatedBy ?? "dashboard",
        Reason           = body.Reason,
    });
    ctx.CurrentVersion = newVersion;
    ctx.UpdatedAt = DateTime.UtcNow;

    var excess = ctx.Versions.Count - MaxVersions;
    if (excess > 0)
        db.ProjectContextVersions.RemoveRange(ctx.Versions.OrderBy(v => v.VersionNumber).Take(excess));

    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"Project context '{name}' updated to v{newVersion}", version = newVersion });
});

// REST: rollback project context to a prior version (creates new version with old content)
app.MapPost("/api/project-contexts/{name}/rollback/{targetVersion:int}", async (string name, int targetVersion, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    const int MaxVersions = 20;

    var ctx = await db.ProjectContexts
        .Include(p => p.Versions.OrderBy(v => v.VersionNumber))
        .FirstOrDefaultAsync(p => p.Name == name);

    if (ctx is null)
        return Results.NotFound(new { error = $"Project context '{name}' not found" });

    var target = ctx.Versions.FirstOrDefault(v => v.VersionNumber == targetVersion);
    if (target is null)
        return Results.NotFound(new { error = $"Version {targetVersion} not found" });

    var newVersion = ctx.CurrentVersion + 1;
    ctx.Versions.Add(new ProjectContextVersion
    {
        ProjectContextId = ctx.Id,
        VersionNumber    = newVersion,
        Content          = target.Content,
        CreatedAt        = DateTime.UtcNow,
        CreatedBy        = "rollback",
        Reason           = $"rollback to v{targetVersion}",
    });
    ctx.CurrentVersion = newVersion;
    ctx.UpdatedAt = DateTime.UtcNow;

    var excess = ctx.Versions.Count - MaxVersions;
    if (excess > 0)
        db.ProjectContextVersions.RemoveRange(ctx.Versions.OrderBy(v => v.VersionNumber).Take(excess));

    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"Rolled back '{name}' to v{targetVersion} content — saved as v{newVersion}", version = newVersion });
});

// REST: toggle active/inactive on a project context
app.MapPost("/api/project-contexts/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    var ctx = await db.ProjectContexts.FirstOrDefaultAsync(p => p.Name == name);
    if (ctx is null) return Results.NotFound(new { error = $"Project context '{name}' not found" });
    ctx.IsActive = req.IsActive;
    ctx.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { ctx.Name, ctx.IsActive });
});

// REST: active workflows across all Temporal namespaces
app.MapGet("/api/workflows", (WorkflowStore workflows) =>
    Results.Ok(workflows.GetAll()));

// REST: restart an agent's container via Docker API
app.MapPost("/api/agents/{name}/restart", async (string name, AgentRegistry registry, DockerService docker, IServiceScopeFactory scopeFactory) =>
{
    // Prefer registry (live state), fall back to DB for container name (handles stopped/crashed agents)
    var containerName = registry.Get(name)?.ContainerName;
    if (containerName is null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        var dbAgent = db is null ? null : await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
        containerName = dbAgent?.ContainerName;
    }

    if (containerName is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in registry or DB" });

    var ok = await docker.RestartContainerAsync(containerName);

    return ok
        ? Results.Ok(new { message = $"Container '{containerName}' restart requested" })
        : Results.Problem($"Failed to restart container '{containerName}' — check orchestrator logs");
});


// REST: cancel all running tasks on an agent by proxying to the agent's HTTP /cancel endpoint.
// Resolves the agent URL via container name on the Docker network (fleet-orchestrator is containerized).
// Falls back to host-port routing when container name is unavailable (not yet provisioned agents).
app.MapPost("/api/agents/{name}/cancel", async (string name, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ContainerProvisioningService provisioning) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found" });

    var agentUrl = $"{provisioning.GetAgentBaseUrl(agent)}/cancel";

    try
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var response = await client.PostAsync(agentUrl, null);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { message = $"Cancel request sent to agent '{name}'" })
            : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to reach agent '{name}' at {agentUrl}: {ex.Message}");
    }
});

// REST: cancel a specific background task on an agent by proxying to the agent's HTTP /cancel_bg/{taskId} endpoint.
// Uses container-name routing — see /cancel above for rationale.
app.MapPost("/api/agents/{name}/cancel_bg/{taskId}", async (string name, string taskId, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ContainerProvisioningService provisioning) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found" });

    var agentUrl = $"{provisioning.GetAgentBaseUrl(agent)}/cancel_bg/{Uri.EscapeDataString(taskId)}";

    try
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var response = await client.PostAsync(agentUrl, null);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { message = $"Cancel requested for background task '{taskId}' on agent '{name}'" })
            : response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? Results.NotFound(new { error = $"Background task '{taskId}' not found on agent '{name}'" })
                : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to reach agent '{name}' at {agentUrl}: {ex.Message}");
    }
});

// REST: cancel a specific foreground task on an agent by proxying to the agent's HTTP /cancel/{taskId} endpoint.
// Targets only the task with the given bridge taskId, leaving all other tasks unaffected.
// Uses container-name routing — see /cancel above for rationale.
app.MapPost("/api/agents/{name}/cancel/{**taskId}", async (string name, string taskId, IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, ContainerProvisioningService provisioning) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found" });

    var decodedTaskId = Uri.UnescapeDataString(taskId);
    var agentUrl = $"{provisioning.GetAgentBaseUrl(agent)}/cancel/{Uri.EscapeDataString(decodedTaskId)}";

    try
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var response = await client.PostAsync(agentUrl, null);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { message = $"Cancel requested for task '{decodedTaskId}' on agent '{name}'" })
            : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to reach agent '{name}' at {agentUrl}: {ex.Message}");
    }
});

// REST: restart an agent using a specific instruction version (temporary or pinned)
app.MapPost("/api/agents/{name}/restart-with-version", async (string name, HttpRequest request, IServiceScopeFactory scopeFactory, ContainerProvisioningService provisioning) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<RestartWithVersionRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.InstructionName))
        return Results.BadRequest(new { error = "instructionName is required" });

    var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

    var instruction = await db.Instructions
        .Include(i => i.Versions)
        .FirstOrDefaultAsync(i => i.Name == body.InstructionName);

    if (instruction is null)
        return Results.NotFound(new { error = $"Instruction '{body.InstructionName}' not found" });

    var version = instruction.Versions.FirstOrDefault(v => v.VersionNumber == body.VersionNumber);
    if (version is null)
        return Results.NotFound(new { error = $"Version {body.VersionNumber} not found for instruction '{body.InstructionName}'" });

    if (body.Pin == true)
    {
        instruction.CurrentVersion = body.VersionNumber;
        await db.SaveChangesAsync();
        var pinnedResult = await provisioning.ReprovisionAsync(name);
        return pinnedResult.Success
            ? Results.Ok(new { message = $"Pinned '{body.InstructionName}' to v{body.VersionNumber} and reprovisioned '{name}'" })
            : Results.Problem(pinnedResult.Message);
    }
    else
    {
        var overrides = new Dictionary<string, int> { [body.InstructionName] = body.VersionNumber };
        var result = await provisioning.ReprovisionAsync(name, instructionVersionOverrides: overrides);
        return result.Success
            ? Results.Ok(new { message = $"Reprovisioned '{name}' with '{body.InstructionName}' v{body.VersionNumber} (temporary)" })
            : Results.Problem(result.Message);
    }
});

// REST: reprovision an agent's container (deprovision + provision from DB config)
// Optional ?image= query param overrides the DB-configured image (used by CI for PR-specific tags)
app.MapPost("/api/agents/{name}/reprovision", async (string name, HttpRequest request, ContainerProvisioningService provisioning) =>
{
    var imageOverride = request.Query["image"].FirstOrDefault();
    var result = await provisioning.ReprovisionAsync(name, imageOverride);
    return result.Success
        ? Results.Ok(new { message = result.Message })
        : Results.Problem(result.Message);
});

// REST: bulk reprovision all currently running agents
// Optional ?image= query param overrides the image for all agents (e.g. bulk image upgrade)
app.MapPost("/api/agents/reprovision-running", async (HttpRequest request, ContainerProvisioningService provisioning) =>
{
    var imageOverride = request.Query["image"].FirstOrDefault();
    var results = await provisioning.ReprovisionRunningAsync(imageOverride);

    var failed = results.Where(r => !r.Success).ToList();
    return failed.Count == 0
        ? Results.Ok(new { message = $"Reprovisioned {results.Count} running agent(s)", results })
        : Results.Json(
            new { message = $"{failed.Count}/{results.Count} agent(s) failed to reprovision", results },
            statusCode: 500);
});

// REST: create a new agent (insert into DB + provision container)
app.MapPost("/api/agents", async (HttpRequest request, IServiceScopeFactory scopeFactory, ContainerProvisioningService provisioning, AgentRegistry registry, SetupService setupService, ConfigService configService, TemporalClientRegistry temporal, ILogger<Program> logger) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var body = await request.ReadFromJsonAsync<CreateAgentRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Name))
        return Results.BadRequest(new { error = "name is required" });
    if (string.IsNullOrWhiteSpace(body.Model))
        return Results.BadRequest(new { error = "model is required" });
    if (string.IsNullOrWhiteSpace(body.Role))
        return Results.BadRequest(new { error = "role is required" });

    // Telegram is a hard prerequisite — agents without a valid bot token are headless.
    var setupStatus = setupService.GetStatus();
    if (!setupStatus.Telegram.Configured)
        return Results.Conflict(new
        {
            error = "telegram_not_configured",
            message = "Cannot provision agent: Telegram bot token is not configured. Configure Telegram first using the setup banner."
        });

    // First agent must be a co-cto — specialists are provisioned by it on demand.
    var agentCount = await db.Agents.CountAsync();
    if (agentCount == 0 && !string.Equals(body.Role.Trim(), "co-cto", StringComparison.OrdinalIgnoreCase))
        return Results.Conflict(new
        {
            error = "cto_required_first",
            message = "Your first agent must be a co-cto."
        });

    var name = body.Name.Trim().ToLowerInvariant();

    if (await db.Agents.AnyAsync(a => a.Name == name))
        return Results.Conflict(new { error = $"Agent '{name}' already exists" });

    var containerName = string.IsNullOrWhiteSpace(body.ContainerName) ? $"fleet-{name}" : body.ContainerName.Trim();
    var displayName   = string.IsNullOrWhiteSpace(body.DisplayName)   ? name               : body.DisplayName.Trim();

    var agent = new Agent
    {
        Name          = name,
        DisplayName   = displayName,
        Role          = body.Role.Trim(),
        Model         = body.Model.Trim(),
        ContainerName = containerName,
        MemoryLimitMb = body.MemoryLimitMb ?? 4096,
        IsEnabled     = body.IsEnabled ?? true,
        Image         = string.IsNullOrEmpty(body.Image) ? null : body.Image,
        PermissionMode            = body.PermissionMode            ?? "acceptEdits",
        MaxTurns                  = body.MaxTurns                  ?? 50,
        WorkDir                   = body.WorkDir                   ?? "/workspace",
        ProactiveIntervalMinutes  = body.ProactiveIntervalMinutes  ?? 0,
        GroupListenMode           = body.GroupListenMode           ?? "mention",
        GroupDebounceSeconds      = body.GroupDebounceSeconds      ?? 15,
        ShortName                 = string.IsNullOrWhiteSpace(body.ShortName) ? name : body.ShortName.Trim(),
        ShowStats                 = body.ShowStats                 ?? true,
        PrefixMessages            = body.PrefixMessages            ?? false,
        SuppressToolMessages      = body.SuppressToolMessages      ?? false,
        TelegramSendOnly          = body.TelegramSendOnly          ?? false,
        Effort                    = string.IsNullOrEmpty(body.Effort) ? null : body.Effort,
        JsonSchema                = string.IsNullOrEmpty(body.JsonSchema) ? null : body.JsonSchema,
        AgentsJson                = string.IsNullOrEmpty(body.AgentsJson) ? null : body.AgentsJson,
        AutoMemoryEnabled         = body.AutoMemoryEnabled ?? true,
        Provider                  = body.Provider          ?? "claude",
        CodexSandboxMode          = string.IsNullOrEmpty(body.CodexSandboxMode) ? null : body.CodexSandboxMode,
    };

    db.Agents.Add(agent);
    await db.SaveChangesAsync();

    if (body.Tools is not null)
        foreach (var t in body.Tools.Distinct(StringComparer.OrdinalIgnoreCase))
            db.AgentTools.Add(new AgentTool { AgentId = agent.Id, ToolName = t, IsEnabled = true });

    if (body.Projects is not null)
        foreach (var p in body.Projects.Distinct(StringComparer.OrdinalIgnoreCase))
            db.AgentProjects.Add(new AgentProject { AgentId = agent.Id, ProjectName = p });

    if (body.McpEndpoints is not null)
        foreach (var e in body.McpEndpoints.DistinctBy(x => x.McpName, StringComparer.OrdinalIgnoreCase))
            db.AgentMcpEndpoints.Add(new AgentMcpEndpoint { AgentId = agent.Id, McpName = e.McpName, Url = e.Url, TransportType = e.TransportType });

    if (body.Networks is not null)
        foreach (var n in body.Networks.Distinct(StringComparer.OrdinalIgnoreCase))
            db.AgentNetworks.Add(new AgentNetwork { AgentId = agent.Id, NetworkName = n });

    var envRefSet = new HashSet<string>(
        body.EnvRefs?.Distinct(StringComparer.OrdinalIgnoreCase) ?? [],
        StringComparer.OrdinalIgnoreCase);
    // Every agent needs some Telegram bot token ref to start its transport (without one the
    // RabbitMQ consumers never get wired up). If the caller didn't pass any *_BOT_TOKEN-shaped
    // key, seed TELEGRAM_NOTIFIER_BOT_TOKEN so the agent boots with a working default.
    var hasBotTokenRef = envRefSet.Any(k =>
        k.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase) &&
        k.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase));
    if (!hasBotTokenRef)
        envRefSet.Add("TELEGRAM_NOTIFIER_BOT_TOKEN");
    foreach (var r in envRefSet)
        db.AgentEnvRefs.Add(new AgentEnvRef { AgentId = agent.Id, EnvKeyName = r });

    // Build the final Telegram user set: explicit payload + installer's ID (deduped)
    var telegramUserSet = new HashSet<long>(body.TelegramUsers?.Distinct() ?? []);
    var ownerTgId = setupService.GetTelegramUserId();
    if (ownerTgId.HasValue) telegramUserSet.Add(ownerTgId.Value);
    foreach (var u in telegramUserSet)
        db.AgentTelegramUsers.Add(new AgentTelegramUser { AgentId = agent.Id, UserId = u });

    if (body.TelegramGroups is not null)
        foreach (var g in body.TelegramGroups.Distinct())
            db.AgentTelegramGroups.Add(new AgentTelegramGroup { AgentId = agent.Id, GroupId = g });

    await db.SaveChangesAsync();

    // Immediately register the new agent in the in-memory registry so it's visible in GET /api/agents
    // and list_agents even before provisioning or first heartbeat.
    registry.PreloadFromDbConfig(agent);

    // When the first co-cto agent is created, write FLEET_CTO_AGENT to .env and broadcast
    // config.changed so peers (fleet-bridge, fleet-temporal-bridge) self-update their routing.
    // Only fires when no prior co-cto existed (agentCount == 0 ensures this is the first agent overall).
    if (string.Equals(agent.Role, "co-cto", StringComparison.OrdinalIgnoreCase)
        && !await db.Agents.AnyAsync(a => a.Role.ToLower() == "co-cto" && a.Id != agent.Id))
    {
        try
        {
            await configService.PutValuesAsync(new Dictionary<string, string>
            {
                ["FLEET_CTO_AGENT"] = name
            }, CancellationToken.None);
            logger.LogInformation("FLEET_CTO_AGENT set to '{Agent}' and config.changed broadcast", name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write FLEET_CTO_AGENT for new co-cto agent '{Agent}'", name);
        }

    }

    if (body.Provision != false)
    {
        var result = await provisioning.ProvisionAsync(name);
        if (!result.Success)
            return Results.Json(
                new
                {
                    message = $"Agent '{name}' created in DB but provisioning failed: {result.Message}",
                    agentName = name,
                },
                statusCode: 207);

        // Welcome DM — fire-and-forget workflow so there's no waiting for a result.
        // 15s delay gives the agent process time to start consuming RabbitMQ.
        if (string.Equals(agent.Role, "co-cto", StringComparison.OrdinalIgnoreCase))
        {
            var ceoUserId = setupService.GetTelegramUserId();
            if (WelcomeDmHelper.ShouldTrigger(agent, true, ceoUserId))
            {
                var welcomeInput = JsonSerializer.Serialize(new
                {
                    TargetAgent = name,
                    TaskDescription = WelcomeDmHelper.BuildWelcomeDirective(name, ceoUserId!.Value),
                    TimeoutMinutes = 1
                });
                await WelcomeDmHelper.TriggerAsync(
                    agent,
                    saveWelcomeSentAt: async () => await db.SaveChangesAsync(CancellationToken.None),
                    startWorkflow: async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        await temporal.StartWorkflowAsync(
                            "FireAndForgetTaskWorkflow",
                            $"welcome-{agent.Id}",
                            "fleet",
                            "fleet",
                            welcomeInput);
                    },
                    logger);
            }
            else if (agent.WelcomeSentAt is null)
            {
                logger.LogWarning("Welcome DM skipped: CEO Telegram user ID not configured.");
            }
        }

        return Results.Created($"/api/agents/{name}", new { message = $"Agent '{name}' created and provisioned", agentName = name });
    }

    return Results.Created($"/api/agents/{name}", new { message = $"Agent '{name}' created (not provisioned — use POST /api/agents/{name}/reprovision when ready)", agentName = name });
});

// REST: delete an agent (deprovision container + remove DB records)
app.MapDelete("/api/agents/{name}", async (string name, IServiceScopeFactory scopeFactory, ContainerProvisioningService provisioning, AgentRegistry registry) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null)
        return Results.Problem("Database is not configured on this orchestrator");

    var agent = await db.Agents
        .Include(a => a.Tools)
        .Include(a => a.Projects)
        .Include(a => a.McpEndpoints)
        .Include(a => a.Networks)
        .Include(a => a.EnvRefs)
        .Include(a => a.TelegramUsers)
        .Include(a => a.TelegramGroups)
        .Include(a => a.Instructions)
        .FirstOrDefaultAsync(a => a.Name == name);

    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });

    // Best-effort deprovision — if container doesn't exist that's fine, proceed with DB delete
    var deprovision = await provisioning.DeprovisionAsync(name);

    db.Agents.Remove(agent);
    await db.SaveChangesAsync();

    // Remove from in-memory registry so the agent doesn't persist as a ghost in the dashboard.
    registry.Remove(name);

    return Results.Ok(new
    {
        message = deprovision.Success
            ? $"Agent '{name}' deleted and container removed"
            : $"Agent '{name}' deleted (container was not running)"
    });
});

// REST: stop an agent's container via Docker API
app.MapPost("/api/agents/{name}/stop", async (string name, AgentRegistry registry, DockerService docker) =>
{
    var agent = registry.Get(name);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in registry" });

    var containerName = agent.ContainerName ?? name;
    var ok = await docker.StopContainerAsync(containerName);

    return ok
        ? Results.Ok(new { message = $"Container '{containerName}' stop requested" })
        : Results.Problem($"Failed to stop container '{containerName}' — check orchestrator logs");
});

// REST: start an agent's container via Docker API
app.MapPost("/api/agents/{name}/start", async (string name, AgentRegistry registry, DockerService docker) =>
{
    var agent = registry.Get(name);
    if (agent is null)
        return Results.NotFound(new { error = $"Agent '{name}' not found in registry" });

    var containerName = agent.ContainerName ?? name;
    var ok = await docker.StartContainerAsync(containerName);

    return ok
        ? Results.Ok(new { message = $"Container '{containerName}' start requested" })
        : Results.Problem($"Failed to start container '{containerName}' — check orchestrator logs");
});

// REST: cancel a running Temporal workflow (graceful shutdown)
app.MapPost("/api/workflows/{ns}/cancel/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

    var runId = request.Query["runId"].FirstOrDefault();
    var workflowId = Uri.UnescapeDataString(id);

    try
    {
        var ok = await temporal.CancelWorkflowAsync(ns, workflowId, runId);
        return ok
            ? Results.Ok(new { message = $"Workflow '{workflowId}' cancel requested" })
            : Results.NotFound(new { error = $"No client for namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to cancel workflow: {ex.Message}" }, statusCode: 500);
    }
});

// REST: terminate a running Temporal workflow (force kill)
app.MapPost("/api/workflows/{ns}/terminate/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

    var runId = request.Query["runId"].FirstOrDefault();
    var workflowId = Uri.UnescapeDataString(id);

    try
    {
        var ok = await temporal.TerminateWorkflowAsync(ns, workflowId, runId);
        return ok
            ? Results.Ok(new { message = $"Workflow '{workflowId}' terminated" })
            : Results.NotFound(new { error = $"No client for namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to terminate workflow: {ex.Message}" }, statusCode: 500);
    }
});

// REST: restart a Temporal workflow — reads original type + input from history, starts fresh execution
app.MapPost("/api/workflows/{ns}/restart/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

    var runId = request.Query["runId"].FirstOrDefault();
    var terminateExisting = request.Query["terminateExisting"].FirstOrDefault() == "true";
    var workflowId = Uri.UnescapeDataString(id);

    try
    {
        var (newWorkflowId, newRunId) = await temporal.RestartWorkflowAsync(ns, workflowId, runId, terminateExisting);
        return Results.Ok(new { message = $"Workflow restarted as '{newWorkflowId}'", newWorkflowId, newRunId });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to restart workflow: {ex.Message}" }, statusCode: 500);
    }
});

// REST: workflow execution history for detail view — GET /api/workflows/{ns}/history/{**id}
// Uses /history/ prefix before the catch-all so ASP.NET Core routing resolves correctly.
app.MapGet("/api/workflows/{ns}/history/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Problem("Temporal is not configured on this orchestrator");

    var workflowId = Uri.UnescapeDataString(id);
    try
    {
        var events = await temporal.FetchWorkflowEventsAsync(ns, workflowId, runId: null, ct);
        return Results.Ok(events);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error fetching workflow history: {ex.Message}");
    }
});

// REST: recently failed/terminated/canceled workflows (last 1 hour by default)
app.MapGet("/api/workflows/failures", async (TemporalClientRegistry temporal) =>
{
    if (!temporal.IsConfigured)
        return Results.Ok(Array.Empty<object>());

    var failures = await temporal.ListRecentFailuresAsync(TimeSpan.FromHours(1));
    return Results.Ok(failures);
});

// REST: recently closed workflows — GET /api/workflows/completed?hours=24
app.MapGet("/api/workflows/completed", async (TemporalClientRegistry temporal, int? hours) =>
{
    if (!temporal.IsConfigured)
        return Results.Ok(Array.Empty<object>());

    var h = Math.Min(hours ?? 24, 72);
    var closed = await temporal.ListRecentlyClosedAsync(TimeSpan.FromHours(h));
    return Results.Ok(closed);
});

// REST: workflow type registry — hardcoded C# types merged with active UWE DB types
app.MapGet("/api/workflow-types", async (IServiceScopeFactory scopeFactory) =>
{
    var types = new List<object>(WorkflowTypeRegistry.HardcodedTypes
        .Select(t => new { t.Name, t.Description, t.Namespace, t.TaskQueue, t.InputSchema }));

    try
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<Fleet.Orchestrator.Data.OrchestratorDbContext>();
        if (db is not null)
        {
            var hardcodedNames = new HashSet<string>(WorkflowTypeRegistry.HardcodedTypes.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            var uweTypes = await db.WorkflowDefinitions
                .Where(d => d.IsActive)
                .AsNoTracking()
                .Select(d => new { d.Name, Description = "UWE: " + d.Name, d.Namespace, d.TaskQueue, d.Definition })
                .ToListAsync();
            var inputExprRegex = new System.Text.RegularExpressions.Regex(@"\{\{input\.([A-Za-z0-9_]+)\}\}");
            var filtered = uweTypes.Where(t => !hardcodedNames.Contains(t.Name));
            foreach (var t in filtered)
            {
                var matches = inputExprRegex.Matches(t.Definition);
                var fields = matches.Select(m => m.Groups[1].Value).Distinct().ToList();
                string? inputSchema = null;
                if (fields.Count > 0)
                {
                    var props = string.Join(",", fields.Select(f => $"\"{f}\":{{\"type\":\"string\"}}"));
                    inputSchema = $"{{\"type\":\"object\",\"properties\":{{{props}}}}}";
                }
                types.Add(new { t.Name, t.Description, t.Namespace, t.TaskQueue, InputSchema = inputSchema });
            }
        }
    }
    catch
    {
        // DB unavailable — return hardcoded only
    }

    return Results.Ok(types);
});

// REST: start a new Temporal workflow
app.MapPost("/api/workflows/start", async (HttpRequest request, TemporalClientRegistry temporal, IServiceScopeFactory scopeFactory) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

    using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
    var root = body.RootElement;

    if (!root.TryGetProperty("workflowType", out var wfTypeEl) || wfTypeEl.GetString() is not string workflowType || string.IsNullOrWhiteSpace(workflowType))
        return Results.BadRequest(new { error = "workflowType is required" });

    if (!root.TryGetProperty("namespace", out var nsEl) || nsEl.GetString() is not string @namespace || string.IsNullOrWhiteSpace(@namespace))
        return Results.BadRequest(new { error = "namespace is required" });

    var taskQueue = root.TryGetProperty("taskQueue", out var tqEl) ? tqEl.GetString() : null;
    if (string.IsNullOrWhiteSpace(taskQueue)) taskQueue = @namespace;

    var customId    = root.TryGetProperty("workflowId", out var idEl) ? idEl.GetString() : null;
    var workflowId  = string.IsNullOrWhiteSpace(customId)
        ? $"{workflowType}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        : customId!;

    string? inputJson = null;
    if (root.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        inputJson = inputEl.GetRawText();
        // Treat {} as no input
        if (inputJson.Replace(" ", "") == "{}") inputJson = null;
    }

    try
    {
        var newId = await temporal.StartWorkflowAsync(workflowType, workflowId, @namespace, taskQueue, inputJson);
        return Results.Ok(new { workflowId = newId, workflowNamespace = @namespace });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to start workflow: {ex.Message}" }, statusCode: 500);
    }
});

// REST: signal registry — static mapping of workflow type → available signals
app.MapGet("/api/signals", () => Results.Ok(SignalRegistry.All));

// REST: send a signal to a running Temporal workflow
app.MapPost("/api/workflows/{ns}/signal/{**id}", async (string ns, string id, HttpRequest request, TemporalClientRegistry temporal) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured on this orchestrator" }, statusCode: 503);

    // Body: { signalName, workflowType, payload, runId? }
    using var body = await System.Text.Json.JsonDocument.ParseAsync(request.Body);
    var root = body.RootElement;

    if (!root.TryGetProperty("signalName", out var signalNameEl) || signalNameEl.GetString() is not string signalName || string.IsNullOrWhiteSpace(signalName))
        return Results.BadRequest(new { error = "signalName is required" });

    // Validate signal name against registry if workflowType is provided
    if (root.TryGetProperty("workflowType", out var wfTypeEl) && wfTypeEl.GetString() is string wfType && !string.IsNullOrWhiteSpace(wfType))
    {
        var knownSignals = SignalRegistry.Get(wfType);
        if (knownSignals is not null && !knownSignals.Any(s => s.Name.Equals(signalName, StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { error = $"Signal '{signalName}' is not registered for workflow type '{wfType}'" });
    }

    var payload    = root.TryGetProperty("payload", out var payloadEl) ? (payloadEl.GetString() ?? "{}") : "{}";
    var runId      = root.TryGetProperty("runId",   out var runIdEl)   ? runIdEl.GetString() : null;
    var workflowId = Uri.UnescapeDataString(id);

    try
    {
        var ok = await temporal.SignalWorkflowAsync(ns, workflowId, runId, signalName, payload);
        return ok
            ? Results.Ok(new { message = $"Signal '{signalName}' sent to workflow '{workflowId}'" })
            : Results.NotFound(new { error = $"No client for namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to send signal: {ex.Message}" }, statusCode: 500);
    }
});

// WebSocket: real-time agent state updates
app.Map("/ws", async (HttpContext context, AgentRegistry registry) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    registry.AddSocket(socket);
    await registry.BroadcastAllToSocket(socket);

    // Keep socket alive until client disconnects
    var buffer = new byte[256];
    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
        if (result.MessageType == WebSocketMessageType.Close)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
    }
});

// WebSocket: real-time container log streaming per agent
app.Map("/ws/logs/{agentName}", async (string agentName, HttpContext context, AgentRegistry registry, DockerService docker) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var agent = registry.Get(agentName);
    var containerName = agent?.ContainerName ?? agentName;

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

    // Receive loop — detect client-initiated close
    _ = Task.Run(async () =>
    {
        var buf = new byte[64];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buf, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch { /* disconnect */ }
        finally { cts.Cancel(); }
    }, CancellationToken.None);

    try
    {
        await foreach (var line in docker.StreamContainerLogsAsync(containerName, 200, cts.Token))
        {
            if (socket.State != WebSocketState.Open) break;
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogDebug(ex, "Log stream for {Agent} ended", agentName);
    }

    if (socket.State == WebSocketState.Open)
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream ended", CancellationToken.None);
});

// ─── Workflow Definitions ────────────────────────────────────────────────────

app.MapGet("/api/workflow-definitions", async (bool? includeInactive, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    var query = db.WorkflowDefinitions.AsQueryable();
    if (includeInactive != true) query = query.Where(d => d.IsActive);
    var defs = await query
        .OrderBy(d => d.Name)
        .AsNoTracking()
        .Select(d => new { d.Name, d.Namespace, d.TaskQueue, d.Description, d.Version, d.IsActive, d.CreatedAt, d.UpdatedAt })
        .ToListAsync();
    return Results.Ok(defs);
});

app.MapGet("/api/workflow-definitions/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    var def = await db.WorkflowDefinitions
        .Include(d => d.Versions)
        .AsNoTracking()
        .FirstOrDefaultAsync(d => d.Name == name);
    if (def is null) return Results.NotFound(new { error = $"Workflow definition '{name}' not found" });
    return Results.Ok(new {
        def.Name, def.Namespace, def.TaskQueue, def.Description,
        def.Version, def.IsActive, def.Definition,
        def.CreatedAt, def.UpdatedAt, def.CreatedBy,
        Versions = def.Versions.OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.Definition, v.Reason, v.CreatedAt, v.CreatedBy })
            .ToList()
    });
});

app.MapPost("/api/workflow-definitions", async (WorkflowDefinitionRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required" });
    if (string.IsNullOrWhiteSpace(req.Namespace)) return Results.BadRequest(new { error = "Namespace is required" });
    if (string.IsNullOrWhiteSpace(req.TaskQueue)) return Results.BadRequest(new { error = "TaskQueue is required" });
    if (string.IsNullOrWhiteSpace(req.Definition)) return Results.BadRequest(new { error = "Definition is required" });
    var def = new WorkflowDefinition
    {
        Name        = req.Name,
        Namespace   = req.Namespace,
        TaskQueue   = req.TaskQueue,
        Description = req.Description,
        Definition  = req.Definition,
        Version     = 1,
        IsActive    = true,
        CreatedBy   = req.CreatedBy,
        CreatedAt   = DateTime.UtcNow,
        UpdatedAt   = DateTime.UtcNow,
    };
    db.WorkflowDefinitions.Add(def);
    await db.SaveChangesAsync();
    return Results.Created($"/api/workflow-definitions/{def.Name}", def);
});

app.MapPut("/api/workflow-definitions/{name}", async (string name, WorkflowDefinitionRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    var def = await db.WorkflowDefinitions.FirstOrDefaultAsync(d => d.Name == name);
    if (def is null) return Results.NotFound(new { error = $"Workflow definition '{name}' not found" });

    if (req.Definition is not null)
    {
        // Save previous version to history before overwriting
        db.WorkflowDefinitionVersions.Add(new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = def.Id,
            Version              = def.Version,
            Definition           = def.Definition,
            Reason               = req.Reason,
            CreatedBy            = req.CreatedBy,
            CreatedAt            = DateTime.UtcNow,
        });
        def.Definition = req.Definition;
        def.Version   += 1;
    }

    def.Namespace   = req.Namespace   ?? def.Namespace;
    def.TaskQueue   = req.TaskQueue   ?? def.TaskQueue;
    def.Description = req.Description ?? def.Description;
    def.UpdatedAt   = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new {
        def.Name, def.Namespace, def.TaskQueue, def.Description,
        def.Version, def.IsActive, def.UpdatedAt
    });
});

app.MapPost("/api/workflow-definitions/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    var def = await db.WorkflowDefinitions.FirstOrDefaultAsync(d => d.Name == name);
    if (def is null) return Results.NotFound(new { error = $"Workflow definition '{name}' not found" });
    def.IsActive  = req.IsActive;
    def.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { def.Name, def.Namespace, def.TaskQueue, def.Description, def.Version, def.IsActive, def.UpdatedAt });
});

// ─── Schedules ───────────────────────────────────────────────────────────────

// GET /api/schedules — list all schedules across all configured namespaces (cheap fields only, no N+1)
app.MapGet("/api/schedules", async (TemporalClientRegistry temporal, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Ok(Array.Empty<object>());

    try
    {
        var clients = await temporal.GetClientsAsync(ct);
        var results = new List<object>();
        foreach (var (ns, _) in clients)
        {
            try
            {
                var schedules = await temporal.ListSchedulesAsync(ns, ct);
                results.AddRange(schedules);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error listing schedules in namespace '{Namespace}'", ns);
            }
        }
        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to list schedules: {ex.Message}" }, statusCode: 500);
    }
});

// GET /api/schedules/{ns}/{**id} — describe a single schedule (full detail including run-time fields)
app.MapGet("/api/schedules/{ns}/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

    var scheduleId = Uri.UnescapeDataString(id);
    try
    {
        var desc = await temporal.DescribeScheduleAsync(ns, scheduleId, ct);
        return Results.Ok(desc);
    }
    catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
    {
        return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to describe schedule: {ex.Message}" }, statusCode: 500);
    }
});

// POST /api/schedules — create a new schedule
app.MapPost("/api/schedules", async (HttpRequest request, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

    CreateScheduleRequest? req;
    try { req = await request.ReadFromJsonAsync<CreateScheduleRequest>(); }
    catch { return Results.BadRequest(new { error = "Invalid request body" }); }

    if (req is null)                                    return Results.BadRequest(new { error = "Request body is required" });
    if (string.IsNullOrWhiteSpace(req.Namespace))       return Results.BadRequest(new { error = "namespace is required" });
    if (string.IsNullOrWhiteSpace(req.WorkflowType))    return Results.BadRequest(new { error = "workflowType is required" });
    if (string.IsNullOrWhiteSpace(req.CronExpression))  return Results.BadRequest(new { error = "cronExpression is required" });

    // Auto-generate scheduleId: {workflowType}-{8-char-guid} lowercased with dashes
    var scheduleId = string.IsNullOrWhiteSpace(req.ScheduleId)
        ? $"{req.WorkflowType!.ToLowerInvariant().Replace('_', '-')}-{Guid.NewGuid().ToString("N")[..8]}"
        : req.ScheduleId!;
    var taskQueue = string.IsNullOrWhiteSpace(req.TaskQueue) ? req.Namespace : req.TaskQueue;

    // Parse optional input JSON
    object? inputObj = null;
    if (!string.IsNullOrWhiteSpace(req.InputJson))
    {
        try { inputObj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(req.InputJson); }
        catch { return Results.BadRequest(new { error = "input is not valid JSON" }); }
    }

    try
    {
        var created = await temporal.CreateScheduleAsync(
            req.Namespace!, scheduleId, req.WorkflowType!, taskQueue!,
            req.CronExpression!, inputObj, req.Memo, req.Paused ?? false, ct);
        return Results.Created($"/api/schedules/{req.Namespace}/{created}",
            new { scheduleId = created, @namespace = req.Namespace, status = "created" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to create schedule: {ex.Message}" }, statusCode: 500);
    }
});

// POST /api/schedules/{ns}/pause/{**id} — pause a schedule
app.MapPost("/api/schedules/{ns}/pause/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

    var scheduleId = Uri.UnescapeDataString(id);
    try
    {
        await temporal.PauseScheduleAsync(ns, scheduleId, ct);
        return Results.Ok(new { scheduleId, status = "paused" });
    }
    catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
    {
        return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to pause schedule: {ex.Message}" }, statusCode: 500);
    }
});

// POST /api/schedules/{ns}/unpause/{**id} — unpause a schedule
app.MapPost("/api/schedules/{ns}/unpause/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

    var scheduleId = Uri.UnescapeDataString(id);
    try
    {
        await temporal.UnpauseScheduleAsync(ns, scheduleId, ct);
        return Results.Ok(new { scheduleId, status = "active" });
    }
    catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
    {
        return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to unpause schedule: {ex.Message}" }, statusCode: 500);
    }
});

// POST /api/schedules/{ns}/trigger/{**id} — trigger an immediate run
app.MapPost("/api/schedules/{ns}/trigger/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

    var scheduleId = Uri.UnescapeDataString(id);
    try
    {
        await temporal.TriggerScheduleAsync(ns, scheduleId, ct);
        return Results.Ok(new { scheduleId, status = "triggered" });
    }
    catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
    {
        return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to trigger schedule: {ex.Message}" }, statusCode: 500);
    }
});

// DELETE /api/schedules/{ns}/{**id} — delete a schedule
app.MapDelete("/api/schedules/{ns}/{**id}", async (string ns, string id, TemporalClientRegistry temporal, CancellationToken ct) =>
{
    if (!temporal.IsConfigured)
        return Results.Json(new { error = "Temporal is not configured" }, statusCode: 503);

    var scheduleId = Uri.UnescapeDataString(id);
    try
    {
        await temporal.DeleteScheduleAsync(ns, scheduleId, ct);
        return Results.Ok(new { scheduleId, status = "deleted" });
    }
    catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("NOT_FOUND", StringComparison.Ordinal))
    {
        return Results.NotFound(new { error = $"Schedule '{scheduleId}' not found in namespace '{ns}'" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Failed to delete schedule: {ex.Message}" }, statusCode: 500);
    }
});

app.MapGet("/api/namespaces", (TemporalClientRegistry temporal) =>
{
    if (!temporal.IsConfigured) return Results.Ok(Array.Empty<string>());
    return Results.Ok(temporal.GetNamespaces());
});

app.MapGet("/api/search-attributes", () =>
    Results.Ok(Fleet.Shared.SearchAttributeTypeRegistry.GetNames()));

// ─── Repositories ─────────────────────────────────────────────────────────────

app.MapGet("/api/repositories", async (bool? includeInactive, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured on this orchestrator");

    var query = db.Repositories.AsQueryable();
    if (includeInactive != true) query = query.Where(r => r.IsActive);

    var repos = await query.OrderBy(r => r.Name).AsNoTracking().ToListAsync();
    return Results.Ok(repos.Select(r => new { r.Name, r.FullName, r.IsActive, r.CreatedAt, r.UpdatedAt }));
});

app.MapPost("/api/repositories", async (RepositoryRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required" });
    if (string.IsNullOrWhiteSpace(req.FullName)) return Results.BadRequest(new { error = "FullName is required" });

    var repo = new Repository
    {
        Name = req.Name.Trim(),
        FullName = req.FullName.Trim(),
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.Repositories.Add(repo);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException ex) { return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message }); }
    return Results.Created($"/api/repositories/{repo.Name}", new { repo.Name, repo.FullName, repo.IsActive });
});

app.MapPut("/api/repositories/{name}", async (string name, RepositoryRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");

    var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
    if (repo is null) return Results.NotFound(new { error = $"Repository '{name}' not found" });

    if (!string.IsNullOrWhiteSpace(req.FullName)) repo.FullName = req.FullName.Trim();
    if (!string.IsNullOrWhiteSpace(req.Name) && req.Name != name) repo.Name = req.Name.Trim();
    repo.UpdatedAt = DateTime.UtcNow;

    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException ex) { return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message }); }
    return Results.Ok(new { repo.Name, repo.FullName, repo.IsActive, repo.UpdatedAt });
});

app.MapPost("/api/repositories/{name}/toggle-active", async (string name, ToggleActiveRequest req, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");

    var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
    if (repo is null) return Results.NotFound(new { error = $"Repository '{name}' not found" });

    repo.IsActive = req.IsActive;
    repo.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(new { repo.Name, repo.IsActive, repo.UpdatedAt });
});

app.MapDelete("/api/repositories/{name}", async (string name, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database is not configured");

    var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Name == name);
    if (repo is null) return Results.NotFound(new { error = $"Repository '{name}' not found" });

    db.Repositories.Remove(repo);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = name });
});

// ── Setup endpoints ───────────────────────────────────────────────────────────

app.MapGet("/api/setup/status", (SetupService setup) =>
    Results.Ok(setup.GetStatus()));

app.MapPost("/api/setup/telegram/validate", async (
    HttpRequest request, SetupService setup, CancellationToken ct) =>
{
    TelegramSetupRequest? req;
    try { req = await request.ReadFromJsonAsync<TelegramSetupRequest>(ct); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }
    if (req is null) return Results.BadRequest(new { error = "empty_body" });

    var result = await setup.ValidateTelegramAsync(req, ct);
    if (!result.Valid)
        return Results.BadRequest(new { error = result.ErrorCode, detail = result.ErrorDetail });
    await setup.UpdateLastValidatedAsync("telegram");
    return Results.Ok(new { valid = true, ctoBot = result.CtoBot, notifierBot = result.NotifierBot, groupChat = result.GroupChat, warnings = result.Warnings });
});


app.MapPost("/api/setup/github/validate", async (
    HttpRequest request, SetupService setup, CancellationToken ct) =>
{
    GitHubSetupRequest? req;
    try { req = await request.ReadFromJsonAsync<GitHubSetupRequest>(ct); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }
    if (req is null) return Results.BadRequest(new { error = "empty_body" });

    var result = await setup.ValidateGitHubAsync(req, ct);
    if (!result.Valid)
        return Results.BadRequest(new { error = result.ErrorCode, detail = result.ErrorDetail });
    await setup.UpdateLastValidatedAsync("github");
    return Results.Ok(new { valid = true, appId = result.AppId, appName = result.AppName, warnings = result.Warnings });
});

// POST /api/setup/github/save — writes GITHUB_APP_ID + GITHUB_APP_PEM to .env.
// GITHUB_APP_PEM is on the config-API denylist (prevents general agents from reading it back),
// so it cannot go through PUT /api/config/values. This dedicated setup endpoint bypasses the
// denylist for the two GitHub App keys only. Auth-gated by ORCHESTRATOR_AUTH_TOKEN.
app.MapPost("/api/setup/github/save", async (
    HttpRequest request, SetupService setup, ConfigService configService, CancellationToken ct) =>
{
    GitHubSetupRequest? req;
    try { req = await request.ReadFromJsonAsync<GitHubSetupRequest>(ct); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }
    if (req is null) return Results.BadRequest(new { error = "empty_body" });

    try
    {
        await setup.SaveGitHubAsync(req.AppId, req.PrivateKeyPem);
        // Reload to invalidate cache and broadcast config.changed for non-denylisted changes
        // (GITHUB_APP_ID will be broadcast; GITHUB_APP_PEM is denylisted so it is never broadcast).
        var changed = await configService.ReloadAsync(ct);
        return Results.Ok(new { saved = true, changedKeys = changed });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Save failed: {ex.Message}", statusCode: 500);
    }
});


// ── Credentials / connections view ─────────────────────────────────────────────

// GET /api/credentials/connections — rich status with masked values + last-validated
app.MapGet("/api/credentials/connections", (SetupService setupService) =>
    Results.Ok(setupService.GetRichConnectionsStatus()));

// POST /api/services/restart
app.MapPost("/api/services/restart", async (HttpRequest request, DockerService docker, CancellationToken ct) =>
{
    var body = await request.ReadFromJsonAsync<RestartServicesRequest>();
    var errors = new Dictionary<string, string>();
    var restarted = new List<string>();
    foreach (var svc in body?.Services ?? [])
    {
        try
        {
            await docker.StopContainerAsync(svc);
            await docker.StartContainerAsync(svc);
            restarted.Add(svc);
        }
        catch (Exception ex)
        {
            errors[svc] = ex.Message;
        }
    }
    return errors.Count > 0
        ? Results.Json(new { restarted, errors }, statusCode: 207)
        : Results.Ok(new { restarted });
});

// GET /api/credential-files
app.MapGet("/api/credential-files", async (IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database not configured");

    var files = await db.CredentialFiles
        .Include(f => f.Mounts).ThenInclude(m => m.Agent)
        .OrderBy(f => f.Name)
        .Select(f => new
        {
            f.Id, f.Name, f.Type, f.FileName, f.SizeBytes, f.CreatedAt,
            mounts = f.Mounts.Select(m => new { m.Id, agentName = m.Agent.Name, m.MountPath, m.Mode })
        })
        .ToListAsync();

    return Results.Ok(files);
});

// POST /api/credential-files — multipart upload
app.MapPost("/api/credential-files", async (HttpRequest request, IServiceScopeFactory scopeFactory, IConfiguration config) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database not configured");

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var name = form["name"].FirstOrDefault()?.Trim();
    var type = form["type"].FirstOrDefault()?.Trim() ?? "generic";

    if (file is null) return Results.BadRequest(new { error = "file is required" });
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "name is required" });

    if (await db.CredentialFiles.AnyAsync(f => f.Name == name))
        return Results.Conflict(new { error = $"Credential file '{name}' already exists" });

    var baseDir = config["Provisioning:BaseDir"]
        ?? throw new InvalidOperationException("Provisioning:BaseDir not configured");
    var credDir = Path.Combine(baseDir, "credentials");
    Directory.CreateDirectory(credDir);

    var safeFileName = Path.GetFileName(file.FileName).Replace("..", "_");
    // Ensure unique filename
    var finalFileName = safeFileName;
    var counter = 1;
    while (File.Exists(Path.Combine(credDir, finalFileName)))
        finalFileName = $"{Path.GetFileNameWithoutExtension(safeFileName)}_{counter++}{Path.GetExtension(safeFileName)}";

    var filePath = Path.Combine(credDir, finalFileName);
    await using var stream = File.Create(filePath);
    await file.CopyToAsync(stream);

    // chmod 600 for SSH keys
    if (type == "ssh-private-key")
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    var credFile = new CredentialFile
    {
        Name = name,
        Type = type,
        FileName = finalFileName,
        FilePath = filePath,
        SizeBytes = file.Length,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.CredentialFiles.Add(credFile);
    await db.SaveChangesAsync();

    return Results.Created($"/api/credential-files/{credFile.Id}", new { credFile.Id, credFile.Name, credFile.Type, credFile.FileName, credFile.SizeBytes });
});

// DELETE /api/credential-files/{id}
app.MapDelete("/api/credential-files/{id:int}", async (int id, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database not configured");

    var file = await db.CredentialFiles.FindAsync(id);
    if (file is null) return Results.NotFound();

    try { if (File.Exists(file.FilePath)) File.Delete(file.FilePath); }
    catch { /* best-effort */ }

    db.CredentialFiles.Remove(file);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"Credential file '{file.Name}' deleted" });
});

// GET /api/credential-files/{id}/download (auth-gated)
app.MapGet("/api/credential-files/{id:int}/download", async (int id, IServiceScopeFactory scopeFactory, HttpContext ctx) =>
{
    var token = app.Configuration["Orchestrator:AuthToken"];
    if (!string.IsNullOrEmpty(token))
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault() ?? "";
        if (!auth.Equals($"Bearer {token}", StringComparison.Ordinal))
            return Results.Unauthorized();
    }

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database not configured");

    var file = await db.CredentialFiles.FindAsync(id);
    if (file is null) return Results.NotFound();
    if (!File.Exists(file.FilePath)) return Results.NotFound(new { error = "File not found on disk" });

    var bytes = await File.ReadAllBytesAsync(file.FilePath);
    return Results.File(bytes, "application/octet-stream", file.FileName);
});

// POST /api/credential-files/{id}/mount
app.MapPost("/api/credential-files/{id:int}/mount", async (int id, HttpRequest request, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database not configured");

    var body = await request.ReadFromJsonAsync<MountCredentialRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.AgentName) || string.IsNullOrWhiteSpace(body.MountPath))
        return Results.BadRequest(new { error = "agentName and mountPath are required" });

    var file = await db.CredentialFiles.FindAsync(id);
    if (file is null) return Results.NotFound(new { error = "Credential file not found" });

    var agent = await db.Agents.FirstOrDefaultAsync(a => a.Name == body.AgentName);
    if (agent is null) return Results.NotFound(new { error = $"Agent '{body.AgentName}' not found" });

    // Remove existing mount for same agent+path if any
    var existing = await db.AgentCredentialMounts
        .FirstOrDefaultAsync(m => m.AgentId == agent.Id && m.MountPath == body.MountPath);
    if (existing is not null) db.AgentCredentialMounts.Remove(existing);

    db.AgentCredentialMounts.Add(new AgentCredentialMount
    {
        AgentId = agent.Id,
        CredentialFileId = id,
        MountPath = body.MountPath,
        Mode = body.Mode ?? "ro",
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { message = $"Mounted '{file.Name}' to agent '{body.AgentName}' at '{body.MountPath}'" });
});

// DELETE /api/credential-files/{fileId}/mount/{mountId}
app.MapDelete("/api/credential-files/{fileId:int}/mount/{mountId:int}", async (int fileId, int mountId, IServiceScopeFactory scopeFactory) =>
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
    if (db is null) return Results.Problem("Database not configured");

    var mount = await db.AgentCredentialMounts.FindAsync(mountId);
    if (mount is null || mount.CredentialFileId != fileId) return Results.NotFound();

    db.AgentCredentialMounts.Remove(mount);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Mount removed" });
});

// Agent templates
app.MapGet("/api/agent-templates", () =>
    Results.Ok(AgentTemplateRegistry.GetAll()));

app.MapGet("/api/agent-templates/{name}", (string name) =>
{
    var entry = AgentTemplateRegistry.TryGet(name);
    if (entry is null) return Results.NotFound(new { error = $"Template '{name}' not found" });
    var c = entry.Config;
    return Results.Ok(new
    {
        entry.Name,
        entry.DisplayName,
        entry.Description,
        c.Model,
        c.Role,
        c.Provider,
        c.MemoryLimitMb,
        c.PermissionMode,
        c.MaxTurns,
        c.WorkDir,
        c.ProactiveIntervalMinutes,
        c.GroupListenMode,
        c.GroupDebounceSeconds,
        c.ShowStats,
        c.PrefixMessages,
        c.SuppressToolMessages,
        c.TelegramSendOnly,
        c.AutoMemoryEnabled,
        Tools = c.Tools.Select(t => new { t.ToolName, t.IsEnabled }),
        Projects = c.Projects,
        McpEndpoints = c.McpEndpoints.Select(e => new { e.McpName, e.Url, e.TransportType }),
        Networks = c.Networks,
        EnvRefs = c.EnvRefs,
        Instructions = c.Instructions.Select(i => new { i.Name, i.LoadOrder }),
    });
});

// ── Memory API (proxied from fleet-memory /internal/*) ───────────────────────
// All endpoints require bearer-token auth (checked inline since GETs are normally exempt).

bool CheckMemoryAuth(HttpRequest req)
{
    var token = req.Headers.Authorization.FirstOrDefault();
    if (token?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        token = token["Bearer ".Length..].Trim();
    return !string.IsNullOrWhiteSpace(orchestratorAuthToken) && token == orchestratorAuthToken;
}

app.MapGet("/api/memory", async (HttpRequest req, MemoryProxyService memProxy, CancellationToken ct) =>
{
    if (!CheckMemoryAuth(req)) return Results.Unauthorized();
    var resp = await memProxy.GetListAsync(ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", null, (int)resp.StatusCode);
});

app.MapGet("/api/memory/ids", async (HttpRequest req, MemoryProxyService memProxy, CancellationToken ct) =>
{
    if (!CheckMemoryAuth(req)) return Results.Unauthorized();
    var resp = await memProxy.GetIdsAsync(ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", null, (int)resp.StatusCode);
});

app.MapGet("/api/memory/search", async (HttpRequest req, string? q, MemoryProxyService memProxy, CancellationToken ct) =>
{
    if (!CheckMemoryAuth(req)) return Results.Unauthorized();
    var resp = await memProxy.SearchAsync(q ?? "", ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", null, (int)resp.StatusCode);
});

app.MapGet("/api/memory/stats/reads", async (HttpRequest req, MemoryProxyService memProxy, CancellationToken ct) =>
{
    if (!CheckMemoryAuth(req)) return Results.Unauthorized();
    var resp = await memProxy.GetReadStatsAsync(ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", null, (int)resp.StatusCode);
});

app.MapGet("/api/memory/{id}", async (HttpRequest req, string id, MemoryProxyService memProxy, CancellationToken ct) =>
{
    if (!CheckMemoryAuth(req)) return Results.Unauthorized();
    var resp = await memProxy.GetAsync(id, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", null, (int)resp.StatusCode);
});

app.MapPut("/api/memory/{id}", async (HttpRequest req, string id, MemoryProxyService memProxy, CancellationToken ct) =>
{
    // Auth checked by the existing mutating-method middleware
    object? body;
    try { body = await req.ReadFromJsonAsync<object>(ct); }
    catch { return Results.BadRequest(new { error = "invalid_json" }); }
    if (body is null) return Results.BadRequest(new { error = "empty_payload" });

    var resp = await memProxy.UpdateAsync(id, body, ct);
    var respBody = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(respBody, "application/json", null, (int)resp.StatusCode);
});

app.MapDelete("/api/memory/{id}", async (string id, MemoryProxyService memProxy, CancellationToken ct) =>
{
    // Auth checked by the existing mutating-method middleware
    var resp = await memProxy.DeleteAsync(id, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", null, (int)resp.StatusCode);
});


app.Run();
return 0;

record WorkflowDefinitionRequest
{
    public string? Name { get; init; }
    public string? Namespace { get; init; }
    public string? TaskQueue { get; init; }
    public string? Definition { get; init; }
    public string? Description { get; init; }
    public string? Reason { get; init; }
    public string? CreatedBy { get; init; }
}

record ToggleActiveRequest(bool IsActive);

record AgentConfigUpdateRequest(
    string? Model,
    int? MemoryLimitMb,
    bool? IsEnabled,
    string? Image,
    string? PermissionMode,
    int? MaxTurns,
    string? WorkDir,
    int? ProactiveIntervalMinutes,
    string? GroupListenMode,
    int? GroupDebounceSeconds,
    string? ShortName,
    bool? ShowStats,
    bool? PrefixMessages,
    bool? SuppressToolMessages,
    bool? TelegramSendOnly,
    string? Effort,
    string? JsonSchema,
    string? AgentsJson,
    bool? AutoMemoryEnabled,
    string? Provider,
    string? CodexSandboxMode,
    string[]? Tools,
    string[]? Projects,
    McpEndpointEntry[]? McpEndpoints,
    string[]? Networks,
    string[]? EnvRefs,
    long[]? TelegramUsers,
    long[]? TelegramGroups,
    InstructionAssignmentEntry[]? Instructions);

record McpEndpointEntry(string McpName, string Url, string TransportType);
record InstructionAssignmentEntry(string InstructionName, int LoadOrder);

record InstructionCreateRequest(string Name, string Content, string? Reason, string? CreatedBy);
record InstructionUpdateRequest(string Content, string? Reason, string? CreatedBy);

record ProjectContextCreateRequest(string Name, string Content, string? CreatedBy);
record ProjectContextUpdateRequest(string Content, string? Reason, string? CreatedBy);

record RepositoryRequest(string? Name, string? FullName);

record RestartWithVersionRequest(string InstructionName, int VersionNumber, bool? Pin);

record RestartServicesRequest(string[] Services);
record MountCredentialRequest(string AgentName, string MountPath, string? Mode);

record CreateScheduleRequest(
    string? ScheduleId,
    string? Namespace,
    string? WorkflowType,
    string? TaskQueue,
    string? CronExpression,
    string? InputJson,
    string? Memo,
    bool? Paused);

record CreateAgentRequest(
    string Name,
    string? DisplayName,
    string Role,
    string Model,
    string? ContainerName,
    int? MemoryLimitMb,
    bool? IsEnabled,
    string? Image,
    string? PermissionMode,
    int? MaxTurns,
    string? WorkDir,
    int? ProactiveIntervalMinutes,
    string? GroupListenMode,
    int? GroupDebounceSeconds,
    string? ShortName,
    bool? ShowStats,
    bool? PrefixMessages,
    bool? SuppressToolMessages,
    bool? TelegramSendOnly,
    string? Effort,
    string? JsonSchema,
    string? AgentsJson,
    bool? AutoMemoryEnabled,
    string? Provider,
    string? CodexSandboxMode,
    string[]? Tools,
    string[]? Projects,
    McpEndpointEntry[]? McpEndpoints,
    string[]? Networks,
    string[]? EnvRefs,
    long[]? TelegramUsers,
    long[]? TelegramGroups,
    bool? Provision);
