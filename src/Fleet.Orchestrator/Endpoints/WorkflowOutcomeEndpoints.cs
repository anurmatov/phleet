using System.Data.Common;
using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/workflow-outcomes</c> surface (#476): failure views for the operator, the failure
/// inbox/receipt/ack/decision routes for the configured coordinator, and late-result capture and
/// recovery dispatch for the bridge.
/// </summary>
/// <remarks>
/// <para>
/// Authorization is not here: <see cref="WorkflowOutcomeAuthorizationMiddleware.UseWorkflowOutcomeAuthorization"/>
/// runs before routing and refuses every unauthenticated method on every sub-path before any
/// handler runs. Handlers re-check the principal kind they need, so a route can never be reached
/// with a credential of the wrong scope even if the classification and the mapping drift apart.
/// </para>
/// <para>
/// A store failure is 503 with no partial claim: every multi-row write is one transaction.
/// There is no route that starts, signals or cancels a workflow, sends a notification or runs a
/// model turn, and no route deletes a row.
/// </para>
/// </remarks>
public static class WorkflowOutcomeEndpoints
{
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>A late result may be 256 KiB of text; JSON escaping can grow it, so the envelope is bounded at 2 MiB.</summary>
    public const int MaxLateResultBodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Options, authorization, the scoped services, the read-only recovery probe and the batcher.
    /// The payload protector is registered by the host (it depends on the keyring).
    /// </summary>
    public static IServiceCollection AddWorkflowOutcomeApi(this IServiceCollection services, IConfiguration configuration, bool runBatcher = true)
    {
        services.Configure<WorkflowOutcomeOptions>(configuration.GetSection(WorkflowOutcomeOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<WorkflowOutcomeAuthorization>();
        services.AddScoped<WorkflowOutcomeInboxService>();
        services.AddScoped<WorkflowRecoveryService>();
        services.TryAddSingleton<IWorkflowRecoveryProbe, TemporalWorkflowRecoveryProbe>();
        services.AddSingleton<WorkflowOutcomeBatcher>();
        if (runBatcher) services.AddHostedService(sp => sp.GetRequiredService<WorkflowOutcomeBatcher>());
        return services;
    }

    public static IEndpointRouteBuilder MapWorkflowOutcomeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(WorkflowOutcomeAuthorization.ApiRoot);
        const string op = WorkflowOutcomePrincipal.OperatorKind, coord = WorkflowOutcomePrincipal.CoordinatorKind, bridge = WorkflowOutcomePrincipal.BridgeKind;

        // ── Operator views ───────────────────────────────────────────────────
        g.MapGet("", (HttpContext ctx, string? @namespace, string? state, string? cursor, int? limit) =>
            Run(ctx, [op], (s, _, ct) => Inbox(s).ListAsync(@namespace, state, cursor, limit, ct)));

        // HttpContext-only handlers are typed Func<HttpContext, Task<IResult>>: an untyped lambda of
        // that shape binds to the RequestDelegate overload and its result would never be written.
        g.MapGet("/coverage", Handler(ctx => Run(ctx, [op], (s, _, ct) => Inbox(s).CoverageAsync(ct))));

        g.MapGet("/health", Handler(ctx => Run(ctx, [op], (s, _, ct) => Inbox(s).HealthAsync(ct))));

        // Anonymous: one boolean only. A store failure reads as unhealthy, never as healthy.
        g.MapGet("/health/public", async (HttpContext ctx) =>
        {
            var result = await Run(ctx, [WorkflowOutcomePrincipal.AnonymousKind], (s, _, ct) => Inbox(s).PublicHealthAsync(ct));
            return result is IStatusCodeHttpResult { StatusCode: 200 } ? result : Results.Json(new { healthy = false }, statusCode: 503);
        });

        g.MapGet("/{id:guid}", (HttpContext ctx, Guid id, string? detail) =>
            Run(ctx, [op], (s, _, ct) => Inbox(s).DetailAsync(id, detail == "payload", ct)));

        g.MapPost("/{id:guid}/classification", (HttpContext ctx, Guid id) =>
            RunWithBody<WorkflowOutcomeClassificationRequest>(ctx, [op], MaxBodyBytes, (s, p, body, ct) => Inbox(s).ClassifyAsync(p, id, body, ct)));

        // ── Coordinator inbox ────────────────────────────────────────────────
        g.MapGet("/inbox", (HttpContext ctx, long? generation, string? cursor, int? limit) =>
            Run(ctx, [coord], (s, p, ct) => Inbox(s).GetInboxAsync(p, generation, cursor, limit, ct)));

        g.MapPost("/batches/{id:guid}/delivered", (HttpContext ctx, Guid id) =>
            RunWithBody<WorkflowOutcomeDeliveredRequest>(ctx, [coord], MaxBodyBytes, (s, p, body, ct) => Inbox(s).DeliveredAsync(p, id, body, ct)));

        g.MapGet("/batches/{id:guid}/receipt", (HttpContext ctx, Guid id, string? nonce) =>
            Run(ctx, [coord, op], (s, p, ct) => Inbox(s).GetReceiptAsync(p, id, nonce, ct)));

        g.MapPost("/batches/{id:guid}/ack", (HttpContext ctx, Guid id) =>
            RunWithBody<WorkflowOutcomeAckRequest>(ctx, [coord, op], MaxBodyBytes, (s, p, body, ct) => Inbox(s).AckAsync(p, id, body, ct)));

        g.MapPost("/{id:guid}/decisions", (HttpContext ctx, Guid id) =>
            RunWithBody<WorkflowOutcomeDecisionRequest>(ctx, [coord, op], MaxBodyBytes, (s, p, body, ct) => Inbox(s).DecideAsync(p, id, body, ct)));

        // ── Bridge capture ───────────────────────────────────────────────────
        g.MapPost("/late-results", Handler(ctx =>
            RunWithBody<WorkflowOutcomeLateResultRequest>(ctx, [bridge], MaxLateResultBodyBytes, (s, p, body, ct) => Inbox(s).StoreLateResultAsync(p, body, ct))));

        // ── Recovery ─────────────────────────────────────────────────────────
        g.MapPost("/{id:guid}/recovery-plan", (HttpContext ctx, Guid id) =>
            RunWithBody<WorkflowRecoveryPlanRequest>(ctx, [op], MaxBodyBytes, (s, p, body, ct) => Recovery(s).PlanAsync(p, id, body, ct)));

        g.MapPost("/recovery/{operationId:guid}/reserve", (HttpContext ctx, Guid operationId) =>
            RunWithBody<WorkflowRecoveryReserveRequest>(ctx, [op], MaxBodyBytes, (s, p, body, ct) => Recovery(s).ReserveAsync(p, operationId, body, ct)));

        g.MapGet("/recovery/{operationId:guid}", (HttpContext ctx, Guid operationId) =>
            Run(ctx, [op, bridge], (s, p, ct) => Recovery(s).GetAsync(p, operationId, ct)));

        g.MapPost("/recovery/{operationId:guid}/authorize-dispatch", (HttpContext ctx, Guid operationId) =>
            RunWithBody<WorkflowRecoveryDispatchRequest>(ctx, [bridge], MaxBodyBytes, (s, p, body, ct) => Recovery(s).AuthorizeDispatchAsync(p, operationId, body, ct)));

        g.MapPost("/recovery/{operationId:guid}/receipt", (HttpContext ctx, Guid operationId) =>
            RunWithBody<WorkflowRecoveryReceiptRequest>(ctx, [bridge], MaxBodyBytes, (s, p, body, ct) => Recovery(s).ReceiptAsync(p, operationId, body, ct)));

        return app;
    }

    private static Func<HttpContext, Task<IResult>> Handler(Func<HttpContext, Task<IResult>> handler) => handler;

    private static WorkflowOutcomeInboxService Inbox(IServiceProvider s) => s.GetRequiredService<WorkflowOutcomeInboxService>();
    private static WorkflowRecoveryService Recovery(IServiceProvider s) => s.GetRequiredService<WorkflowRecoveryService>();

    private static async Task<IResult> Run(
        HttpContext ctx, string[] kinds,
        Func<IServiceProvider, WorkflowOutcomePrincipal, CancellationToken, Task<WorkflowOutcomeResult>> work)
    {
        // The middleware stored the principal; without it (or with the wrong kind) nothing runs.
        if (ctx.GetWorkflowOutcomePrincipal() is not { } principal) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        if (!kinds.Contains(principal.Kind)) return Results.Json(new { error = "wrong_scope" }, statusCode: 403);
        if (ctx.RequestServices.GetService<OrchestratorDbContext>() is null)
            return Results.Json(new { error = "store_unconfigured" }, statusCode: 503);
        try
        {
            var result = await work(ctx.RequestServices, principal, ctx.RequestAborted);
            return Results.Json(result.Body, WorkflowOutcomeApi.Json, statusCode: result.Status);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            // Type only: provider messages can quote row values.
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkflowOutcomeEndpoints))
                .LogWarning("workflow_outcome_store_unavailable type={Type}", ex.GetType().Name);
            return Results.Json(new { error = "store_unavailable" }, statusCode: 503);
        }
    }

    private static async Task<IResult> RunWithBody<T>(
        HttpContext ctx, string[] kinds, int maxBytes,
        Func<IServiceProvider, WorkflowOutcomePrincipal, T?, CancellationToken, Task<WorkflowOutcomeResult>> work) where T : class
    {
        if (ctx.GetWorkflowOutcomePrincipal() is not { } principal) return Results.Json(new { error = "unauthorized" }, statusCode: 401);
        if (!kinds.Contains(principal.Kind)) return Results.Json(new { error = "wrong_scope" }, statusCode: 403);

        var (body, status) = await ReadAsync<T>(ctx.Request, maxBytes, ctx.RequestAborted);
        if (status != 0) return Results.Json(new { error = status == 413 ? "body_too_large" : "malformed_json" }, statusCode: status);
        return await Run(ctx, kinds, (s, p, ct) => work(s, p, body, ct));
    }

    /// <summary>The JSON body, or 413 when it exceeds <paramref name="maxBytes"/>, or 400 when it is not a JSON object of the shape.</summary>
    private static async Task<(T? Body, int Status)> ReadAsync<T>(HttpRequest request, int maxBytes, CancellationToken ct) where T : class
    {
        if (request.ContentLength > maxBytes) return (null, 413);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return (null, 413);
            buffer.Write(chunk, 0, read);
        }
        try
        {
            var bytes = buffer.ToArray();
            using (var doc = JsonDocument.Parse(bytes))
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, 400);
            return (JsonSerializer.Deserialize<T>(bytes, WorkflowOutcomeApi.Json), 0);
        }
        catch (JsonException)
        {
            return (null, 400);
        }
    }

    private static bool IsStoreFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is DbException or DbUpdateException or RetryLimitExceededException or TimeoutException) return true;
        return false;
    }
}
