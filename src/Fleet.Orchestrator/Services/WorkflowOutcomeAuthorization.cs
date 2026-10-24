using System.Security.Cryptography;
using System.Text;
using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services;

/// <summary>Which credentials a <c>/api/workflow-outcomes</c> route accepts.</summary>
[Flags]
public enum WorkflowOutcomeAccess
{
    None = 0,
    Anonymous = 1,
    Operator = 2,
    Coordinator = 4,
    Bridge = 8,
}

/// <summary>
/// The authenticated caller. <see cref="Kind"/> is <c>operator</c>, <c>bridge</c>, <c>coordinator</c>
/// or <c>anonymous</c>; the operator and the bridge present the same admin bearer, so the route
/// decides which of the two it is.
/// </summary>
public sealed record WorkflowOutcomePrincipal(string Kind, string Name, int CredentialVersion)
{
    public const string OperatorKind = "operator";
    public const string BridgeKind = "bridge";
    public const string CoordinatorKind = "coordinator";
    public const string AnonymousKind = "anonymous";

    public bool IsCoordinator => Kind == CoordinatorKind;
    public bool IsOperator => Kind == OperatorKind;
    public bool IsBridge => Kind == BridgeKind;
}

/// <summary>
/// Authentication for the whole <c>/api/workflow-outcomes</c> surface (#476). Runs as middleware
/// in front of routing so every method, including GET/HEAD/OPTIONS and unmapped sub-paths, is
/// authorized before any handler (and so before any database access) runs.
/// </summary>
/// <remarks>
/// <para>
/// Fail closed: the feature switch off, or no configured credential for the route, is 503. A
/// missing or unknown bearer is 401; a valid credential of the wrong kind is 403. There is no
/// development bypass, no query-string identity and no fallback to the global admin middleware
/// (which exempts this prefix, see <see cref="OrchestratorAuth"/>).
/// </para>
/// <para>
/// Only SHA-256 verifiers of the two tokens are held. The coordinator token is read once, at
/// startup, from the configuration key named by <c>WorkflowOutcomes:CoordinatorTokenKey</c>; a
/// rotation (new value + <c>CoordinatorCredentialVersion</c>) takes effect on restart and the old
/// value stops authenticating. Nothing here logs a token, header or body.
/// </para>
/// </remarks>
public sealed class WorkflowOutcomeAuthorization
{
    public const string ApiRoot = "/api/workflow-outcomes";
    public const string PrincipalItemKey = "fleet.workflow-outcome.principal";
    public const string CoordinatorConfigKey = "FLEET_CTO_AGENT";

    private readonly byte[]? _adminVerifier;
    private readonly byte[]? _coordinatorVerifier;
    private readonly ILogger<WorkflowOutcomeAuthorization> _logger;

    public WorkflowOutcomeAuthorization(
        IOptions<WorkflowOutcomeOptions> options,
        IConfiguration configuration,
        ILogger<WorkflowOutcomeAuthorization> logger)
    {
        _logger = logger;
        var opts = options.Value;
        Enabled = opts.Enabled;
        CredentialVersion = opts.CoordinatorCredentialVersion;
        Generation = opts.CoordinatorGeneration;

        var admin = configuration["Orchestrator:AuthToken"];
        if (!string.IsNullOrWhiteSpace(admin)) _adminVerifier = Verifier(admin.Trim());

        var coordinator = configuration[CoordinatorConfigKey];
        Coordinator = string.IsNullOrWhiteSpace(coordinator) ? null : coordinator.Trim();

        var token = ResolveCoordinatorToken(opts, configuration);
        if (token is not null && Coordinator is not null)
        {
            var verifier = Verifier(token);
            // One credential for two scopes would let the coordinator act as the operator.
            if (_adminVerifier is not null && CryptographicOperations.FixedTimeEquals(verifier, _adminVerifier))
                logger.LogError("workflow_outcomes_auth_misconfigured: the coordinator token equals the admin token; coordinator routes are unavailable");
            else
                _coordinatorVerifier = verifier;
        }
    }

    public bool Enabled { get; }

    /// <summary>The configured coordinator (<c>FLEET_CTO_AGENT</c>), or null when unassigned.</summary>
    public string? Coordinator { get; }

    public int CredentialVersion { get; }

    /// <summary>The current ownership generation; a batch of another generation is superseded.</summary>
    public long Generation { get; }

    public bool OperatorConfigured => _adminVerifier is not null;
    public bool CoordinatorConfigured => _coordinatorVerifier is not null;

    /// <summary>The coordinator token value, or null when the key is blank, forbidden or unset.</summary>
    public static string? ResolveCoordinatorToken(WorkflowOutcomeOptions options, IConfiguration configuration)
    {
        var key = options.CoordinatorTokenKey?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        if (WorkflowOutcomeOptions.ForbiddenTokenKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) return null;
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>True for the API root and everything below it, case-insensitively (routing is too).</summary>
    public static bool IsApiPath(string path) =>
        path.Equals(ApiRoot, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(ApiRoot + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The credentials a path accepts, independent of the method. Anything not listed is
    /// operator-only, so an unmapped or misspelled sub-path is never more open than the root.
    /// </summary>
    public static WorkflowOutcomeAccess Classify(string path)
    {
        var rest = path.Length > ApiRoot.Length ? path[ApiRoot.Length..] : "";
        var s = rest.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        return s switch
        {
            ["health", "public"] => WorkflowOutcomeAccess.Anonymous,
            ["inbox"] => WorkflowOutcomeAccess.Coordinator,
            ["batches", _, "delivered"] => WorkflowOutcomeAccess.Coordinator,
            ["batches", _, "receipt"] or ["batches", _, "ack"] => WorkflowOutcomeAccess.Coordinator | WorkflowOutcomeAccess.Operator,
            ["late-results"] => WorkflowOutcomeAccess.Bridge,
            ["recovery", _, "authorize-dispatch"] or ["recovery", _, "receipt"] => WorkflowOutcomeAccess.Bridge,
            ["recovery", _] => WorkflowOutcomeAccess.Operator | WorkflowOutcomeAccess.Bridge,
            ["recovery", ..] or ["batches", ..] => WorkflowOutcomeAccess.Operator,
            [_, "decisions"] => WorkflowOutcomeAccess.Coordinator | WorkflowOutcomeAccess.Operator,
            _ => WorkflowOutcomeAccess.Operator,
        };
    }

    /// <summary>The principal, or the refusal status and error code. Never touches the database.</summary>
    public (WorkflowOutcomePrincipal? Principal, int Status, string? Error) Authorize(string path, string? authorizationHeader)
    {
        if (!Enabled) return (null, StatusCodes.Status503ServiceUnavailable, "workflow_outcomes_disabled");

        var access = Classify(path);
        if (access == WorkflowOutcomeAccess.Anonymous)
            return (new WorkflowOutcomePrincipal(WorkflowOutcomePrincipal.AnonymousKind, "anonymous", 0), 0, null);

        var acceptsAdmin = (access & (WorkflowOutcomeAccess.Operator | WorkflowOutcomeAccess.Bridge)) != 0;
        var acceptsCoordinator = (access & WorkflowOutcomeAccess.Coordinator) != 0;
        if (!(acceptsAdmin && OperatorConfigured) && !(acceptsCoordinator && CoordinatorConfigured))
            return (null, StatusCodes.Status503ServiceUnavailable, "workflow_outcomes_auth_unconfigured");

        var token = authorizationHeader is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        if (string.IsNullOrEmpty(token)) return (null, StatusCodes.Status401Unauthorized, "unauthorized");

        var presented = Verifier(token);
        if (_adminVerifier is not null && CryptographicOperations.FixedTimeEquals(presented, _adminVerifier))
        {
            if (!acceptsAdmin) return (null, StatusCodes.Status403Forbidden, "wrong_scope");
            var kind = (access & WorkflowOutcomeAccess.Operator) != 0
                ? WorkflowOutcomePrincipal.OperatorKind
                : WorkflowOutcomePrincipal.BridgeKind;
            return (new WorkflowOutcomePrincipal(kind, kind, 0), 0, null);
        }

        if (_coordinatorVerifier is not null && CryptographicOperations.FixedTimeEquals(presented, _coordinatorVerifier))
        {
            if (!acceptsCoordinator) return (null, StatusCodes.Status403Forbidden, "wrong_scope");
            return (new WorkflowOutcomePrincipal(WorkflowOutcomePrincipal.CoordinatorKind, Coordinator!, CredentialVersion), 0, null);
        }

        return (null, StatusCodes.Status401Unauthorized, "unauthorized");
    }

    /// <summary>Logs a refusal with the route class and status only: no path ids, header or body.</summary>
    internal void LogRefusal(string method, WorkflowOutcomeAccess access, int status, string error) =>
        _logger.LogInformation("workflow_outcome_auth_refused method={Method} access={Access} status={Status} error={Error}",
            method, access, status, error);

    private static byte[] Verifier(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

public static class WorkflowOutcomeAuthorizationMiddleware
{
    /// <summary>
    /// Authorizes every request under <see cref="WorkflowOutcomeAuthorization.ApiRoot"/> and stores
    /// the principal for the handlers. Requests elsewhere pass through untouched.
    /// </summary>
    public static IApplicationBuilder UseWorkflowOutcomeAuthorization(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (!WorkflowOutcomeAuthorization.IsApiPath(path))
            {
                await next(context);
                return;
            }

            var auth = context.RequestServices.GetService<WorkflowOutcomeAuthorization>();
            var (principal, status, error) = auth is null
                ? (null, StatusCodes.Status503ServiceUnavailable, "workflow_outcomes_auth_unconfigured")
                : auth.Authorize(path, context.Request.Headers.Authorization.FirstOrDefault());

            if (principal is null)
            {
                auth?.LogRefusal(context.Request.Method, WorkflowOutcomeAuthorization.Classify(path), status, error!);
                context.Response.StatusCode = status;
                if (!HttpMethods.IsHead(context.Request.Method))
                    await context.Response.WriteAsJsonAsync(new { error });
                return;
            }

            context.Items[WorkflowOutcomeAuthorization.PrincipalItemKey] = principal;
            await next(context);
        });

    /// <summary>The principal the middleware stored, or null (which handlers treat as 403).</summary>
    public static WorkflowOutcomePrincipal? GetWorkflowOutcomePrincipal(this HttpContext context) =>
        context.Items.TryGetValue(WorkflowOutcomeAuthorization.PrincipalItemKey, out var p) ? p as WorkflowOutcomePrincipal : null;
}

/// <summary>
/// Startup checks for an enabled feature. Any fault ends the process before <c>app.Run()</c>;
/// messages name keys and paths only, never a token value.
/// </summary>
public static class WorkflowOutcomePreflight
{
    public static IReadOnlyList<string> Check(WorkflowOutcomeOptions options, IConfiguration configuration)
    {
        if (!options.Enabled) return [];
        var errors = new List<string>(options.Validate());

        if (!string.IsNullOrWhiteSpace(options.KeyringDir))
        {
            if (!Directory.Exists(options.KeyringDir))
                errors.Add("WorkflowOutcomes:KeyringDir does not exist");
            else if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(options.KeyringDir);
                if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                    errors.Add("WorkflowOutcomes:KeyringDir must have mode 0700");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CoordinatorTokenKey)
            && WorkflowOutcomeAuthorization.ResolveCoordinatorToken(options, configuration) is null
            && !WorkflowOutcomeOptions.ForbiddenTokenKeys.Contains(options.CoordinatorTokenKey.Trim(), StringComparer.OrdinalIgnoreCase))
            errors.Add("WorkflowOutcomes:CoordinatorTokenKey names a variable with no value");

        return errors;
    }
}
