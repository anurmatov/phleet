using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Comms.Contracts;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Fleet.Comms;

/// <summary>
/// Composition for the north listener.
///
/// <para>Shared byte-for-byte between <c>Program</c> and the tests, so a test exercises the same
/// registration graph production runs. A test host that wires its own services proves things about
/// the test host.</para>
/// </summary>
public static class CommsApp
{
    /// <summary>
    /// Configuration sub-section holding the store's retention, lease and GC values — so the
    /// environment key is <c>Comms__Conversations__DeliveryClaimRetention</c> and the rest.
    /// </summary>
    public const string ConversationStoreSection = "Conversations";

    /// <summary>Register everything the north boundary needs. No south service is registered here.</summary>
    public static IServiceCollection AddNorthBoundary(this IServiceCollection services)
    {
        // TryAdd, not Add. These are defaults: a host that registered a substitute before calling
        // this keeps it. With Add, the last registration wins and the substitute is silently
        // discarded — which would have made every TTL assertion run against the system clock and
        // pass vacuously.
        services.TryAddSingleton<IAuthStore>(provider =>
        {
            // The deployable's default is the durable store. An in-process one is a test fixture
            // and is registered by the test host ahead of this call; it must never be what a
            // deployment gets by omission.
            var path = provider.GetRequiredService<IOptions<CommsOptions>>().Value.AuthStorePath;
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    $"{CommsOptions.SectionName}:{nameof(CommsOptions.AuthStorePath)} is required " +
                    "and has no default. Point it at a path on storage that survives a restart.");
            return new SqliteAuthStore(path);
        });
        // The conversation store, registered LAZILY.
        //
        // A factory rather than an instance, so an install that has not enabled the feature never
        // constructs it and never attempts a connection — which is the property that makes the
        // disabled path byte-identical to the auth slice rather than merely quiet.
        services.TryAddSingleton<IConversationStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CommsOptions>>().Value;

            if (!options.ConversationsEnabled)
            {
                throw new InvalidOperationException(
                    $"{CommsOptions.SectionName}:{nameof(CommsOptions.ConversationConnectionString)} "
                    + "is not configured, so there is no conversation store to resolve. Reaching "
                    + "this means a conversation route was mapped on an install that did not enable "
                    + "the feature.");
            }

            return new MySqlConversationStore(
                options.ConversationConnectionString,
                provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                provider.GetRequiredService<ILogger<MySqlConversationStore>>());
        });

        services.TryAddSingleton<ISecretHasher, Argon2idSecretHasher>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MonotonicClock>();
        services.TryAddSingleton<AuthService>();

        // Registered so the stream slice can take it as a dependency without re-deciding the
        // 30-second bound. Nothing resolves it in a request pipeline yet — this slice ships the
        // component, not the socket.
        services.TryAddSingleton<CredentialRevalidator>();

        services.AddRateLimiter(ConfigureAuthRateLimiter);
        return services;
    }

    /// <summary>
    /// Build the north application.
    ///
    /// <para>The north and south surfaces are <b>separate listeners</b>, not one listener with two
    /// path prefixes (§1, D1). This method builds the north one and maps only
    /// <see cref="NorthEndpoints.MapNorthApi"/>; when the south surface lands it is its own
    /// application on its own listener, so a routing mistake cannot promote a device to a trusted
    /// service caller.</para>
    /// </summary>
    public static WebApplication BuildNorthApp(WebApplicationBuilder builder)
    {
        builder.Services.Configure<CommsOptions>(
            builder.Configuration.GetSection(CommsOptions.SectionName));

        // Bound from configuration rather than defaulted in code. The retention, lease and
        // garbage-collection values are operator settings — the claim retention in particular has to
        // exceed the broker's redelivery horizon, which only the deployment knows — and a value that
        // can only be changed by a rebuild is not an operator setting.
        builder.Services.Configure<ConversationStoreOptions>(
            builder.Configuration.GetSection(
                $"{CommsOptions.SectionName}:{ConversationStoreSection}"));

        builder.Services.AddNorthBoundary();

        // The background sweeps and the outbox drain, registered only when the feature is
        // configured. Read straight from configuration because options are not resolvable until
        // after Build(), and a hosted service has to be registered before it.
        //
        // ⚠️ Two DISTINCT types, deliberately. `AddHostedService` registers through
        // `TryAddEnumerable`, which dedupes on (ServiceType, ImplementationType) — so two
        // registrations of one type through differently-written factories silently keep only the
        // first, with no exception and no log line.
        var conversationSection = builder.Configuration.GetSection(CommsOptions.SectionName);
        var conversationConnection =
            conversationSection[nameof(CommsOptions.ConversationConnectionString)];
        var brokerConnection = conversationSection[nameof(CommsOptions.BrokerConnectionString)];
        var agentName = conversationSection[nameof(CommsOptions.AgentName)];

        if (!string.IsNullOrWhiteSpace(conversationConnection))
        {
            builder.Services.AddHostedService(provider => new ConversationMaintenanceService(
                new Reconciler(
                    conversationConnection,
                    provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                    provider.GetRequiredService<ILogger<Reconciler>>()),
                new GarbageCollector(
                    conversationConnection,
                    provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                    provider.GetRequiredService<ILogger<GarbageCollector>>()),
                provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                provider.GetRequiredService<ILogger<ConversationMaintenanceService>>()));

            // The drain needs a broker. Without one the outboxes still accumulate correctly and the
            // client is unaffected — its submission is already durable — so a missing broker is a
            // logged degradation rather than a refusal to start.
            if (!string.IsNullOrWhiteSpace(brokerConnection) && !string.IsNullOrWhiteSpace(agentName))
            {
                builder.Services.AddHostedService(provider => new OutboxDrainService(
                    new RabbitMqOutboxTransport(
                        brokerConnection, ConversationBroker.CommandExchange,
                        provider.GetRequiredService<ILogger<OutboxDrainService>>()),
                    new RabbitMqOutboxTransport(
                        brokerConnection, ConversationBroker.EventExchange,
                        provider.GetRequiredService<ILogger<OutboxDrainService>>()),
                    conversationConnection,
                    agentName,
                    provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                    provider.GetRequiredService<ILogger<OutboxDrainService>>()));
            }
        }

        var app = builder.Build();

        // Resolve the store NOW, during construction.
        //
        // Configuration that is missing is not the same failure as a store that is unreachable, and
        // they must not arrive the same way. A missing or blank path is an operator mistake that
        // cannot resolve itself, so it is a process that refuses to start; `503` is reserved for a
        // store that was configured and is temporarily not answering, which is what a client is
        // told to retry (§5.2, §16). Deferring this to the first request would present the first
        // one as the second, and the deployment would look healthy until someone tried to enroll.
        //
        // A host that registered its own IAuthStore keeps it — this resolves whatever is wired.
        _ = app.Services.GetRequiredService<IAuthStore>();

        // Fail closed, at the edge. An auth-store outage is a 503 with the fixed Internal body and
        // is NEVER a pass (§16, MUST NOT 18); anything else unhandled is a 500 with the same body,
        // so no exception message can reach a client (§13). Both statuses carry identical bytes —
        // the distinction is the status line, and a client must not parse the body to tell them
        // apart (§5.2).
        //
        // Outermost, so it also covers a fault inside the limiter below.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (AuthStoreUnavailableException)
            {
                await WriteFixedError(context, StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception)
            {
                await WriteFixedError(context, StatusCodes.Status500InternalServerError);
            }
        });

        // Before the limiter, because the limiter partitions on the address this rewrites. After
        // it, the header would be read too late to matter and the switch would look like it worked.
        //
        // Opt-in: see CommsOptions.TrustForwardedHeaders for why the default is off. One hop only —
        // each extra hop is another position a caller can forge from.
        if (app.Services.GetRequiredService<IOptions<CommsOptions>>().Value.TrustForwardedHeaders)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
            };

            // CLEARED, with calls. `KnownNetworks = { }` in an object initialiser is a collection
            // initialiser adding nothing — it leaves the defaults (loopback) in place, so a proxy
            // reaching the container over a bridge address was silently untrusted and every client
            // still shared one budget. The switch looked like it worked and did not.
            //
            // `KnownIPNetworks`, not the obsolete `KnownNetworks`: the latter emits ASPDEPR005.
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();

            // Trusting any peer is the deliberate consequence of opting in: the operator's proxy
            // address is unknown to this repository, and pinning a guess would fail closed in a way
            // they could not diagnose. What bounds it is the one-hop limit plus the proxy REPLACING
            // the header — both stated in docs/comms-deployment.md, in the paragraph that explains
            // when to turn this on.
            app.UseForwardedHeaders(forwarded);
        }

        // Before the endpoints, and that placement is the whole point: a throttled request must be
        // refused without spending an Argon2id evaluation or taking a store transaction.
        app.UseRateLimiter();

        app.MapNorthApi();

        // The conversation surface, only when it is configured.
        //
        // An install that has not configured it is byte-identical to the one the auth slice ships:
        // no conversation route is mapped, every conversation path is a 404 from the router rather
        // than a handler that decided to refuse, no WebSocket middleware is in the pipeline, and no
        // database connection is attempted. That is what makes this an opt-in feature rather than
        // one that merely defaults to off.
        if (app.Services.GetRequiredService<IOptions<CommsOptions>>().Value.ConversationsEnabled)
        {
            // Only reached on the enabled path, so a disabled install adds no middleware at all.
            app.UseWebSockets();
            app.MapConversationApi();
        }

        return app;
    }

    /// <summary>
    /// The operations application: `/health` and `/ready`, on their own listener.
    ///
    /// <para><b>A second <c>WebApplication</c>, not a filtered route on the north one.</b> The
    /// alternative — one application whose ops endpoints are gated on <c>RequireHost</c> or the
    /// <c>Host</c> header — looks equivalent and is not: <c>Host</c> is client-supplied, so a caller
    /// on the public port can reach the readiness oracle by sending the ops listener's host and
    /// port. A test that asks the north listener for `/ready` without that header passes under both
    /// designs, which is exactly why the design has to be the safe one rather than the tested
    /// one.</para>
    ///
    /// <para>The north application's addresses come from <c>ASPNETCORE_URLS</c> alone; this one's
    /// come from <see cref="CommsOptions.OpsUrl"/>. Neither inherits the other's.</para>
    /// </summary>
    public static WebApplication BuildOpsApp(
        WebApplicationBuilder builder, IAuthStore store,
        IConversationStore? conversations = null, string? migrationStatusConnectionString = null)
    {
        builder.Services.AddSingleton(store);
        var app = builder.Build();

        // Liveness: the process is running and can answer. Deliberately says nothing about the
        // store — that is /ready's job, and conflating them makes a restart loop out of a
        // recoverable dependency failure.
        app.MapGet("/health", () => Results.Json(new { status = "ok" }, FleetProtocolJson.Options));

        // Readiness: one trivial transaction against the real store.
        //
        // This is the probe that separates "started and permanently broken" from "healthy". The
        // store initialises lazily and a bad path — unwritable, wrong owner, read-only mount, no
        // volume mounted — is not detected at construction by design, so a probe that did not
        // touch the store would report a service that 503s every request as ready.
        //
        // The transaction is a read, but SqliteAuthStore opens every transaction BEGIN IMMEDIATE,
        // so this acquires the write lock and surfaces a read-only mount too, not only a missing
        // file.
        app.MapGet("/ready", async (IAuthStore authStore, CancellationToken ct) =>
        {
            try
            {
                await authStore.InTransactionAsync(
                    (tx, token) => tx.CountActiveDevicesAsync("", token), ct);

                // The conversation store too, when the feature is enabled — and the SCHEMA VERSION
                // as well as reachability.
                //
                // A wrong credential or a database that is up but carrying the wrong schema would
                // otherwise be reported ready, and every conversation route would then 503 while the
                // probe said the service was healthy. An applied version AHEAD of this binary is as
                // unhealthy as one behind it: that is the rollback-after-migration case, where the
                // binary would write rows a newer schema wrote differently.
                if (conversations is not null)
                {
                    try
                    {
                        await conversations.ReadAsync(new ReadConversationRequest
                        {
                            ConversationId = ReadinessProbeConversationId,
                            AfterSeq = 0,
                            Limit = 1,
                            PrincipalId = ReadinessProbePrincipalId,
                        }, ct);
                    }
                    catch (ConversationNotFoundException)
                    {
                        // The SUCCESSFUL outcome. The probe names no real conversation, so
                        // not-found means the query reached the database, ran and answered — which
                        // is the whole question. A transport or credential failure throws something
                        // else and is caught below.
                    }

                    if (migrationStatusConnectionString is { Length: > 0 })
                    {
                        var schema = await new MigrationRunner(migrationStatusConnectionString)
                            .GetStatusAsync(ct);

                        if (!schema.Matches)
                        {
                            return Results.Json(
                                new { status = "unhealthy", schema = schema.Describe() },
                                FleetProtocolJson.Options,
                                statusCode: StatusCodes.Status503ServiceUnavailable);
                        }
                    }
                }

                return Results.Json(new { status = "ready" }, FleetProtocolJson.Options);
            }
            catch (AuthStoreUnavailableException)
            {
                // The same fixed body every other store failure produces. A readiness endpoint is
                // not a debugging surface, and on a misconfigured deployment it is the one thing
                // reachable before anything else works.
                return Results.Json(ErrorResponse.For(ProtocolErrorCode.Internal),
                    FleetProtocolJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception)
            {
                // The conversation database is unreachable, mid-failover, or answering with a
                // rejected credential. Same fixed body: a readiness endpoint is not a debugging
                // surface, and on a misconfigured deployment it is the one thing reachable before
                // anything else works.
                return Results.Json(ErrorResponse.For(ProtocolErrorCode.Internal),
                    FleetProtocolJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }

    /// <summary>
    /// Identifiers the readiness probe reads with. They belong to no principal and name no
    /// conversation, so the probe is a real query that can only ever answer not-found.
    /// </summary>
    private const string ReadinessProbeConversationId = "00000000000000000000000000";

    private const string ReadinessProbePrincipalId = "p_readiness_probe";

    /// <summary>
    /// Build the south application: the agent-facing store surface, on its own listener.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A THIRD application, for the same reason the ops listener is a second one. The north surface
    /// is a client contract and this is an administrative one; a client that could reach
    /// <c>/turns:commit</c> could write a terminal for someone else's turn, so the two are separated
    /// by address rather than by a path prefix or a header check.
    /// </para>
    /// <para>
    /// Extracted from <c>Program</c> so a test drives the composition production runs rather than a
    /// hand-wired approximation of it — the same reason <see cref="BuildNorthApp"/> exists. A test
    /// host that maps the endpoints itself would prove the endpoints can be mapped, which is not
    /// the question.
    /// </para>
    /// <para>
    /// ⚠️ It never migrates. The store is handed in already constructed and nothing here touches the
    /// migration runner: a process that advanced the schema on boot would turn a deployment mistake
    /// into an irreversible change, and the runtime account has no DDL grant to do it with.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The conversation limiter's partition key: the hashed bearer when one is presented, and the
    /// caller's address otherwise.
    /// </summary>
    /// <remarks>
    /// The address fallback matters: an upgrade or a route call arriving with no credential at all
    /// must not land in one shared "anonymous" partition, because that turns every unauthenticated
    /// caller into a denial of service against every other one.
    /// </remarks>
    private static string ConversationPartitionKey(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();

        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            return $"addr:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(header["Bearer ".Length..]));

        return $"cred:{Convert.ToHexStringLower(hash)}";
    }

    public static WebApplication BuildSouthApp(
        WebApplicationBuilder builder, IConversationStore store, CommsOptions options)
    {
        // Request BINDING, not just response writing. The framework's web defaults carry no enum
        // converter, so a body carrying the protocol's own `"disposition":"ran"` failed to bind and
        // the caller got an empty-bodied 400 — on the two endpoints that carry a disposition, which
        // is to say on the ones that make the surface work at all. Found by sending a request.
        builder.Services.ConfigureHttpJsonOptions(
            jsonOptions => FleetProtocolJson.ApplyTo(jsonOptions.SerializerOptions));

        var app = builder.Build();
        SouthEndpoints.Map(app, store, options);
        return app;
    }

    private static void ConfigureAuthRateLimiter(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // The conversation routes partition on the CREDENTIAL, not the address.
        //
        // Every one of them is authenticated, and two devices behind one forwarded address — a
        // household NAT, a corporate egress, a reverse proxy that does not forward — would otherwise
        // share a budget, so one client's catch-up storm would throttle the other's.
        //
        // The key is a hash of the presented bearer, never the bearer: a rate-limiter partition key
        // reaches metrics and diagnostics, and a token there is a token in a log. It is also not
        // validated at this point, because admission runs before the endpoint — which is the whole
        // reason it is cheap. A caller inventing tokens therefore mints partitions, each still
        // bounded, and every one of those requests then fails authentication at the route.
        options.AddPolicy(ConversationRateLimits.PolicyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                ConversationPartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = ConversationRateLimits.PermitsPerWindow,
                    Window = TimeSpan.FromSeconds(ConversationRateLimits.WindowSeconds),
                    QueueLimit = ConversationRateLimits.QueueLimit,
                    AutoReplenishment = true,
                }));

        options.AddPolicy(AuthRateLimits.PolicyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                // Per caller address. A deployment terminating TLS in front of this process is
                // responsible for presenting the real client address; if every request arrives
                // from one hop the partition collapses to a global bound, which is degraded but
                // still bounded — the failure mode is a stricter limit, never an unbounded one.
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = AuthRateLimits.PermitsPerWindow,
                    Window = TimeSpan.FromSeconds(AuthRateLimits.WindowSeconds),
                    QueueLimit = AuthRateLimits.QueueLimit,
                    AutoReplenishment = true,
                }));

        options.OnRejected = async (context, cancellationToken) =>
        {
            var response = context.HttpContext.Response;
            if (response.HasStarted)
                return;

            response.StatusCode = StatusCodes.Status429TooManyRequests;

            // §5.4: seconds, as a non-negative integer, and never the HTTP-date form. Rounded up
            // and floored at one — a retry the caller is told to make immediately is a retry that
            // will be rejected again, which is the storm the contract warns about.
            var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? (int)Math.Ceiling(retryAfter.TotalSeconds)
                : AuthRateLimits.WindowSeconds;
            response.Headers.RetryAfter =
                Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);

            await response.WriteAsJsonAsync(
                ErrorResponse.For(ProtocolErrorCode.RateLimited), FleetProtocolJson.Options,
                cancellationToken: cancellationToken);
        };
    }

    private static Task WriteFixedError(HttpContext context, int status)
    {
        if (context.Response.HasStarted)
            return Task.CompletedTask;

        context.Response.Clear();
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            ErrorResponse.For(ProtocolErrorCode.Internal), FleetProtocolJson.Options);
    }
}
