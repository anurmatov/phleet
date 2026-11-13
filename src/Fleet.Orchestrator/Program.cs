using System.Threading.RateLimiting;
using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection(TemporalOptions.Section));
builder.Services.Configure<EpicGrantOptions>(builder.Configuration.GetSection(EpicGrantOptions.Section));

// Database
var connectionString = builder.Configuration.GetConnectionString("OrchestratorDb");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<OrchestratorDbContext>((sp, opts) =>
        opts.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 0)),
            mysql => mysql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null))
            // A saved binding or Env Ref change reaches the fleet-secrets state file at once.
            .AddInterceptors(sp.GetRequiredService<SecretStateSaveInterceptor>()));
}

// Secret placeholders: bound .env keys reach agents only as placeholders that fleet-secrets resolves.
// Default off (FLEET_SECRETS_ENABLED); settings are read once, here.
builder.Services.AddSingleton(sp => SecretProxySettings.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SecretProxyStateWriter>();
builder.Services.AddSingleton<SecretStateSaveInterceptor>();
builder.Services.AddHostedService<SecretProxyStateService>();

// Core services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TaskHistoryStore>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<ContainerProvisioningService>();
builder.Services.AddSingleton<JournalTokenService>();
builder.Services.AddSingleton(_ => new TelegramMembershipClient());
builder.Services.AddSingleton<CommsJournalStatusProxy>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<ICredentialsReader, EnvFileCredentialsReader>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<IConfigWriter>(sp => sp.GetRequiredService<ConfigService>());
builder.Services.AddSingleton<IAclChangeNotifier>(sp => sp.GetRequiredService<ConfigService>());
builder.Services.AddSingleton<MemoryProxyService>();
// Advisory instruction / project-context size limits (#346): parsed once from the raw config
// strings, never through the options binder, so a bad value is a Warning rather than a crash.
builder.Services.AddSingleton(sp => PromptSizePolicy.FromConfiguration(
    sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<PromptSizePolicy>>()));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AgentConfigPublisherService>();

// Sign-in (docs/sign-in.md): users, their devices, and the credentials each device holds. Durable
// when a database is configured; in memory otherwise, where a restart signs everyone out.
builder.Services.AddFleetSignIn(durable: !string.IsNullOrEmpty(connectionString));

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
    rl.AddSignInPolicy();
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
builder.Services.AddSingleton<IWorkflowSignalClient>(sp => sp.GetRequiredService<TemporalClientRegistry>());
builder.Services.AddHostedService<TemporalPollerService>();

// Epic grants (#436): opt-in, run-scoped delegated gate approvals. Off unless EpicGrants:Enabled.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEpicGrantTemporal, TemporalEpicGrantClient>();
builder.Services.AddHttpClient<RepoVisibilityReader>(c => c.Timeout = RepoVisibilityReader.Timeout);
builder.Services.AddScoped<EpicGrantService>();
builder.Services.Configure<EpicDriverWatcherOptions>(builder.Configuration.GetSection(EpicDriverWatcherOptions.Section));
builder.Services.AddSingleton<IEpicDriverTemporal>(sp => sp.GetRequiredService<TemporalClientRegistry>());
builder.Services.AddSingleton<EpicRunLinkage>();
builder.Services.AddSingleton<EpicDriverStateService>();
builder.Services.AddHostedService<EpicDriverWatcherService>();

// Push notifications to the first-party iOS app (off by default).
builder.Services.AddPushNotifications(builder.Configuration);

// The writers REST routes and MCP tools both call (Application/): one implementation per write.
builder.Services.AddAgentWrites();

// MCP server (HTTP transport — same port as REST, path /mcp)
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

var epicGrantOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EpicGrantOptions>>().Value;
if (epicGrantOptions.DisableMisconfiguredAutonomy())
    app.Logger.LogWarning("epic_autonomy_misconfigured");

// Resolve the size policy now so an invalid value warns at startup, then log the active limits.
var promptSizePolicy = app.Services.GetRequiredService<PromptSizePolicy>();
promptSizePolicy.LogStartup();

// The Provisioning section. With a database configured this service provisions agents, so a
// section it cannot provision from ends the process here with exit code 1, as the preflights
// below do, and not at the first provision.
var provisioningOptions = ProvisioningOptions.From(app.Configuration);
if (!string.IsNullOrEmpty(connectionString) && provisioningOptions.DescribeFault() is { } provisioningFault)
{
    Console.Error.WriteLine($"Fleet.Orchestrator startup aborted, exiting 1: {provisioningFault}");
    Environment.Exit(1);
}

// Auto-migrate and seed DB on startup
if (!string.IsNullOrEmpty(connectionString))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var rolesDir      = provisioningOptions.RolesDir;
    var projectsDir   = provisioningOptions.ProjectsDir;
    var seedFilePath  = provisioningOptions.SeedFilePath;
    var temporalAddr  = app.Configuration["Temporal:Address"] ?? "";
    var temporalReg   = string.IsNullOrWhiteSpace(temporalAddr)
        ? null
        : app.Services.GetRequiredService<TemporalClientRegistry>();
    // Every SeedAsync failure — a blocked #346 context-removal preflight, a migration or a seeding
    // error — ends the process with exit code 1 before app.Run(), so no listener, hosted service or
    // reprovisioning ever exists. An unhandled exception after Build() does not exit reliably (it has
    // wedged on arm64 and dumped core on x86), hence the explicit exit, as in Fleet.Agent/Program.cs.
    // Console.Error, not the logger: this path must not resolve anything from DI.
    try
    {
        await DbSeeder.SeedAsync(db, rolesDir, seedFilePath, startupLogger, temporalReg, projectsDir,
            projectContextLimitBytes: promptSizePolicy.ProjectContextLimitBytes);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fleet.Orchestrator startup aborted, exiting 1: {ex.Message}");
        Environment.Exit(1);
    }

    // Epic grants (#436): report the switch once at startup, so `epic_grants_misconfigured`
    // (Enabled with MaxDays <= 0) is visible before the first decision is refused.
    using var epicGrantScope = app.Services.CreateScope();
    var epicGrants = epicGrantScope.ServiceProvider.GetRequiredService<EpicGrantService>();
    app.Logger.LogInformation("Epic grants are {State}", epicGrants.IsEnabled ? "enabled" : "disabled");
}

// Sign-in needs its one user before the first request. The owner's Telegram id follows the
// deployment's primary human, so a chat message and a Telegram message from the same person are
// known to be the same person. A failure here exits like any other startup failure above.
try
{
    await app.Services.GetRequiredService<AuthService>().EnsureOwnerAsync(
        app.Services.GetRequiredService<ContainerProvisioningService>().PrimaryHumanUserIdOrNull());
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fleet.Orchestrator startup aborted, exiting 1: sign-in store: {ex.Message}");
    Environment.Exit(1);
}

// WebSocket support
app.UseWebSockets();

// A committed change that could not be published to fleet-secrets is a 503, never a 200.
app.UseSecretStateFaults();

// Rate limiting middleware
app.UseRateLimiter();

// Who is calling, and whether this request may proceed (docs/sign-in.md): the caller is resolved
// from its credential, OrchestratorAuth.Decide gates the request, and /api/config keeps its own
// token. With no Orchestrator:AuthToken nothing is gated, as before sign-in existed.
app.UseFleetAccess();

// MCP endpoint — explicitly mapped to /mcp so the auth middleware exemption matches
app.MapMcp("/mcp");

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-orchestrator" }));

// REST: /api/config — read, write and reload the deployment's .env values.
app.MapConfigEndpoints();

// REST: the agent registry — list, one agent, task history.
app.MapAgentListEndpoints();

// REST: agent config read/write lives in Endpoints/AgentConfigEndpoints.cs so the
// production handlers can be exercised over real HTTP by the endpoint tests (#357).
app.MapAgentConfigEndpoints();
// REST: GET /api/agents/{name}/effective-harness — the computed harness plan and drift (#460).
app.MapAgentHarnessEndpoints();
app.MapJournalCrossChatAuthorization();
app.MapCommsJournalEndpoints();

// REST: the /api/output-styles surface (#317) — list, read, create, update and delete.
// Mapped from Endpoints/OutputStyleEndpoints.cs so the endpoint tests exercise the same
// handlers a live orchestrator serves rather than a copy of them.
app.MapOutputStyleEndpoints();

// REST: /api/push — device registration for push notifications to the iOS app.
app.MapPushEndpoints();

// Secret bindings: which .env keys agents hold only as placeholders, and the hosts each may reach.
app.MapSecretBindingEndpoints();

// REST: the /api/instructions surface — list, detail, new version, rollback, create and
// toggle-active. Mapped from Endpoints/InstructionEndpoints.cs for the same reason (#346).
app.MapInstructionEndpoints();

// REST: GET /api/prompt-size-policy — the active instruction and project-context soft limits (#346).
app.MapPromptSizePolicyEndpoints();

// REST: GET /api/operator-token — whether the presented bearer is the operator token. The one
// read that checks it, so a client can verify a stored token without making a write.
app.MapOperatorTokenEndpoints();

// Sign-in: browsers, enrolled apps, devices, and the chat service's identity check.
app.MapAuthEndpoints();

// REST: the /api/project-contexts surface — list, detail, create, new version, rollback and
// toggle-active. Mapped from Endpoints/ so the endpoint tests exercise the same handlers a live
// orchestrator serves rather than a copy of them.
app.MapProjectContextEndpoints();

// REST: the /api/epic-grants surface (#436) — list, detail, validate, create, revoke, and the
// bridge-only decisions route. Mapped from Endpoints/EpicGrantEndpoints.cs.
app.MapEpicGrantEndpoints();
app.MapEpicDriverEndpoints();

// REST: Temporal workflows — list, cancel, terminate, restart, history, types and start.
app.MapWorkflowEndpoints();

// REST: cancel all tasks, one background task or one task on an agent.
app.MapAgentTaskCancelEndpoints();

// REST: create a new agent — Endpoints/AgentCreateEndpoints.cs
app.MapAgentCreateEndpoints();

// REST: restart, stop, start, restart-with-version, reprovision and delete — Endpoints/AgentLifecycleEndpoints.cs
app.MapAgentLifecycleEndpoints();

// REST: exact-run signal/gate identity checks share the tested production handler.
app.MapWorkflowSignalEndpoints();

// WebSockets: agent state updates and per-agent container logs.
app.MapLiveSocketEndpoints();

// Workflow definitions: list, detail, create, update, toggle-active — Endpoints/WorkflowDefinitionEndpoints.cs
app.MapWorkflowDefinitionEndpoints();

// REST: Temporal schedules — list, describe, create, pause, unpause, trigger and delete.
app.MapScheduleEndpoints();

// REST: the repository list.
app.MapRepositoryEndpoints();

// REST: setup status, and validating and saving the Telegram and GitHub App settings.
app.MapSetupEndpoints();

// REST: connection status, service restarts and credential files with their mounts.
app.MapCredentialEndpoints();

// REST: agent templates.
app.MapAgentTemplateEndpoints();

// Telegram allowlist and memory project access of one agent — Endpoints/AgentAccessEndpoints.cs
app.MapAgentAccessEndpoints();

// The Memory page, proxied from fleet-memory, and the project-access map fleet-memory reads.
app.MapMemoryEndpoints();

app.Run();
return 0;
