using Fleet.Temporal.Activities;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Services;
using Fleet.Temporal.Workflows.Fleet;
using RabbitMQ.Client;
using System.Net.Http.Headers;
using System.Text.Json;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.Configure<TemporalBridgeOptions>(builder.Configuration.GetSection(TemporalBridgeOptions.Section));
builder.Services.Configure<FleetMemoryOptions>(builder.Configuration.GetSection(FleetMemoryOptions.Section));
builder.Services.Configure<FleetWorkflowOptions>(builder.Configuration.GetSection(FleetWorkflowOptions.Section));
builder.Services.PostConfigure<FleetWorkflowOptions>(o => o.OrchestratorUrl = builder.Configuration["TemporalBridge:OrchestratorUrl"] ?? "");
builder.Services.Configure<AuthTokenRefreshOptions>(builder.Configuration.GetSection(AuthTokenRefreshOptions.Section));

// RabbitMQ connection factory used by DelegateToAgentActivity for outbound publishes
builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RabbitMqOptions>>().Value;
    return new ConnectionFactory
    {
        HostName = opts.Host,
        ClientProvidedName = "fleet-temporal-bridge-publisher",
    };
});

// HTTP client for fleet-memory MCP calls
builder.Services.AddHttpClient("fleet-memory", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// HTTP client for OAuth token refresh
builder.Services.AddHttpClient("token-refresh", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// HTTP client for targeted task cancellation via orchestrator REST API
builder.Services.AddHttpClient("orchestrator-cancel", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// HTTP client for universal workflow engine (workflow definitions + instruction templates)
builder.Services.AddHttpClient("orchestrator", (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TemporalBridgeOptions>>().Value;
    if (!string.IsNullOrEmpty(opts.OrchestratorUrl))
    {
        client.BaseAddress = new Uri(opts.OrchestratorUrl.TrimEnd('/') + '/');
        if (!string.IsNullOrEmpty(opts.OrchestratorAuthToken))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.OrchestratorAuthToken);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
});

// #436: the epic grant decision forward — same base address and bearer as "orchestrator", with a
// timeout above the orchestrator's bounded decision path, so a slow but successful decision is not
// reported to the caller as "delivery unknown".
builder.Services.AddHttpClient(OrchestratorEpicGrantForwarder.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TemporalBridgeOptions>>().Value;
    if (!string.IsNullOrEmpty(opts.OrchestratorUrl))
    {
        client.BaseAddress = new Uri(opts.OrchestratorUrl.TrimEnd('/') + '/');
        if (!string.IsNullOrEmpty(opts.OrchestratorAuthToken))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.OrchestratorAuthToken);
    }
    client.Timeout = OrchestratorEpicGrantForwarder.Timeout;
});

// #476: the late-result store and the guarded recovery dispatch — registered after the existing
// orchestrator credential wiring, with the same base address and admin bearer. The bridge never
// holds the coordinator's scoped inbox token.
builder.Services.AddHttpClient(WorkflowOutcomeLateResultConsumer.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TemporalBridgeOptions>>().Value;
    if (!string.IsNullOrEmpty(opts.OrchestratorUrl))
    {
        client.BaseAddress = new Uri(opts.OrchestratorUrl.TrimEnd('/') + '/');
        if (!string.IsNullOrEmpty(opts.OrchestratorAuthToken))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.OrchestratorAuthToken);
    }
    client.Timeout = WorkflowOutcomeLateResultConsumer.HttpTimeout;
});
builder.Services.AddHttpClient(WorkflowRecoveryForwarder.HttpClientName, (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TemporalBridgeOptions>>().Value;
    if (!string.IsNullOrEmpty(opts.OrchestratorUrl))
    {
        client.BaseAddress = new Uri(opts.OrchestratorUrl.TrimEnd('/') + '/');
        if (!string.IsNullOrEmpty(opts.OrchestratorAuthToken))
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", opts.OrchestratorAuthToken);
    }
    client.Timeout = WorkflowRecoveryForwarder.Timeout;
});

// HTTP context accessor — needed by NotifyCtoTool to read the ?agent= query param
builder.Services.AddHttpContextAccessor();

// Core services
builder.Services.AddSingleton<TaskCompletionRegistry>();
builder.Services.AddSingleton<WorkflowTypeRegistry>();
builder.Services.AddSingleton<CtoAgentConfigService>();
builder.Services.AddSingleton<IWorkflowGateStateReader, WorkflowGateStateReader>();
// #436: forwards CTO delegated gate approvals to the orchestrator on the "orchestrator" client.
builder.Services.AddSingleton<IEpicGrantDecisionForwarder, OrchestratorEpicGrantForwarder>();
builder.Services.AddSingleton<IWorkflowDispatcher, TemporalWorkflowDispatcher>();
// #476: authorize-dispatch + receipt for the existing tools' optional recovery_operation_id.
builder.Services.AddSingleton<IWorkflowRecoveryForwarder, WorkflowRecoveryForwarder>();

// Peer config — fetches FLEET_CTO_AGENT and other keys from orchestrator on startup
builder.Services.AddHostedService<PeerConfigHostedService>();

// #476 late-result capture: only when enabled does anything new connect or consume. Off keeps the
// legacy orphan discard (and logs one warning at startup, below).
var lateCapture = builder.Configuration
    .GetSection(TemporalBridgeOptions.Section)
    .Get<TemporalBridgeOptions>() ?? new TemporalBridgeOptions();
if (lateCapture.ValidateWorkflowOutcomeLateCapture() is { } lateCaptureError)
{
    // Invalid values fail startup instead of being replaced. An explicit exit, not a throw: a throw
    // alone is not a reliable way to stop a .NET host on every platform this image runs on.
    Console.Error.WriteLine($"Invalid late-result capture configuration: {lateCaptureError}");
    Environment.Exit(1);
}
if (lateCapture.WorkflowOutcomeLateCaptureEnabled)
{
    builder.Services.AddSingleton<IWorkflowOutcomeBroker, RabbitMqWorkflowOutcomeBroker>();
    builder.Services.AddHostedService<WorkflowOutcomeLateResultConsumer>();
}

// RabbitMQ listener — feeds agent responses into the registry
builder.Services.AddHostedService<TemporalRelayListener>();

// Namespace initializer — creates configured Temporal namespaces on startup if they don't exist
builder.Services.AddHostedService<NamespaceInitializer>();

// Search attribute initializer — registers required search attributes before workers start polling
builder.Services.AddHostedService<SearchAttributeInitializer>();

// Temporal client factory — used by MCP tools to resolve per-namespace clients on demand
builder.Services.AddSingleton<TemporalClientFactory>();
builder.Services.AddSingleton<ITemporalClientFactory>(sp => sp.GetRequiredService<TemporalClientFactory>());

var temporalOpts = builder.Configuration
    .GetSection(TemporalBridgeOptions.Section)
    .Get<TemporalBridgeOptions>() ?? new TemporalBridgeOptions();
var configuredNamespaces = temporalOpts.Namespaces
    .Where(ns => !string.IsNullOrWhiteSpace(ns))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

// Case-insensitive data converter — ensures camelCase JSON from agents maps correctly to PascalCase C# records
var caseInsensitiveDataConverter = DataConverter.Default with
{
    PayloadConverter = new DefaultPayloadConverter(new JsonSerializerOptions(JsonSerializerDefaults.Web))
};

// Workflow failure accountability (#476): the image's source commit becomes the worker build id,
// which Temporal records on every workflow task — the provenance compiled classifier rules match.
var workerSourceCommit = Environment.GetEnvironmentVariable(WorkerSourceCommit.EnvironmentVariable);

foreach (var @namespace in configuredNamespaces)
{
    // Register the same generic workflows/activities on every namespace worker.
    // Unused registrations are harmless and keep startup fully config-driven.
    builder.Services
        .AddHostedTemporalWorker(temporalOpts.TemporalAddress, @namespace, @namespace)
        .ConfigureOptions(opt =>
        {
            opt.ClientOptions!.DataConverter = caseInsensitiveDataConverter;
            WorkerSourceCommit.Apply(opt, workerSourceCommit);
        })
        .AddScopedActivities<DelegateToAgentActivity>()
        .AddScopedActivities<HttpRequestActivity>()
        .AddScopedActivities<BroadcastTokenUpdateActivity>()
        .AddScopedActivities<RefreshAuthTokenActivity>()
        .AddScopedActivities<StartCrossNamespaceWorkflowActivity>()
        .AddScopedActivities<LoadWorkflowDefinitionActivity>()
        .AddScopedActivities<LoadWorkflowConfigActivity>()
        .AddScopedActivities<LoadAgentBudgetActivity>()
        .AddWorkflow<UniversalWorkflow>()
        .AddWorkflow<ConsensusReviewWorkflow>()
        .AddWorkflow<AuthTokenRefreshWorkflow>()
        .AddWorkflow<NotifyCtoWorkflow>();
}

// MCP Server
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

// Bind to port 3001 for MCP
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(3001);
});

var app = builder.Build();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.Temporal.Startup");

if (!lateCapture.WorkflowOutcomeLateCaptureEnabled)
{
    startupLogger.LogWarning(
        "Workflow outcome late-result capture is OFF (TemporalBridge:WorkflowOutcomeLateCaptureEnabled=false): agent replies whose waiter is gone are NOT captured and are discarded as before");
}

if (WorkerSourceCommit.Resolve(workerSourceCommit) is null)
{
    startupLogger.LogWarning(
        "{Variable} is not a 40-hex commit: Temporal workers keep the SDK default build id, so compiled workflow runs cannot be source-verified by workflow outcome classifier rules",
        WorkerSourceCommit.EnvironmentVariable);
}

if (configuredNamespaces.Length == 0)
{
    startupLogger.LogWarning("No namespaces configured — no Temporal workers will be started");
}
else
{
    startupLogger.LogInformation(
        "Configured Temporal workers for namespaces: {Namespaces}",
        string.Join(", ", configuredNamespaces));
}

// Initialize static config singleton for determinism-safe access in Temporal workflows.
// CtoAgent may be empty at startup — it will be set by PeerConfigHostedService on bootstrap.
var fleetWorkflowOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FleetWorkflowOptions>>().Value;
FleetWorkflowConfig.Initialize(fleetWorkflowOptions);

app.MapMcp();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fleet-temporal-bridge" }));

app.Run();
