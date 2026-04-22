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

// Core services
builder.Services.AddSingleton<TaskCompletionRegistry>();
builder.Services.AddSingleton<WorkflowTypeRegistry>();

// Peer config — fetches FLEET_CTO_AGENT and other keys from orchestrator on startup
builder.Services.AddHostedService<PeerConfigHostedService>();

// RabbitMQ listener — feeds agent responses into the registry
builder.Services.AddHostedService<TemporalRelayListener>();

// Namespace initializer — creates configured Temporal namespaces on startup if they don't exist
builder.Services.AddHostedService<NamespaceInitializer>();

// Search attribute initializer — registers required search attributes before workers start polling
builder.Services.AddHostedService<SearchAttributeInitializer>();

// Temporal client factory — used by MCP tools to resolve per-namespace clients on demand
builder.Services.AddSingleton<TemporalClientFactory>();

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

foreach (var @namespace in configuredNamespaces)
{
    // Register the same generic workflows/activities on every namespace worker.
    // Unused registrations are harmless and keep startup fully config-driven.
    builder.Services
        .AddHostedTemporalWorker(temporalOpts.TemporalAddress, @namespace, @namespace)
        .ConfigureOptions(opt => opt.ClientOptions!.DataConverter = caseInsensitiveDataConverter)
        .AddScopedActivities<DelegateToAgentActivity>()
        .AddScopedActivities<HttpRequestActivity>()
        .AddScopedActivities<BroadcastTokenUpdateActivity>()
        .AddScopedActivities<RefreshAuthTokenActivity>()
        .AddScopedActivities<StartCrossNamespaceWorkflowActivity>()
        .AddScopedActivities<LoadWorkflowDefinitionActivity>()
        .AddScopedActivities<LoadWorkflowConfigActivity>()
        .AddWorkflow<UniversalWorkflow>()
        .AddWorkflow<ConsensusReviewWorkflow>()
        .AddWorkflow<AuthTokenRefreshWorkflow>();
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
