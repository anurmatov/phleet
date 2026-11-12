using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Comms.Contracts;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
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
    /// <remarks>
    /// There is no credential store here (#530 D1, D7). The orchestrator owns sign-in, and every
    /// north request is answered by asking it through <see cref="ISignInCheck"/>.
    /// </remarks>
    public static IServiceCollection AddNorthBoundary(this IServiceCollection services)
    {
        // TryAdd, not Add. These are defaults: a host that registered a substitute before calling
        // this keeps it. With Add, the last registration wins and the substitute is silently
        // discarded — which would have made every TTL assertion run against the system clock and
        // pass vacuously.
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

        // The byte store, also LAZY and also for the same reason: an install without an attachment
        // root never constructs it and never creates a directory.
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CommsOptions>>().Value;

            if (!options.AttachmentsEnabled)
            {
                throw new InvalidOperationException(
                    $"{CommsOptions.SectionName}:{nameof(CommsOptions.AttachmentRootPath)} is not "
                    + "configured, so there is no attachment store to resolve. Reaching this means "
                    + "an attachment route was mapped on an install that did not enable the feature.");
            }

            return new AttachmentStore(
                options.AttachmentRootPath,
                provider.GetRequiredService<ILogger<AttachmentStore>>());
        });

        services.TryAddSingleton(TimeProvider.System);

        // Who a caller is, asked of the orchestrator (#530 D7). Also LAZY: an install without the
        // conversation feature maps no north route, so nothing ever resolves it and the shared key it
        // is built from is never required.
        services.TryAddSingleton<ISignInCheck>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CommsOptions>>().Value;
            return new OrchestratorSignInCheck(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                options.OrchestratorUrl,
                options.Journal.Keys()[0],
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<OrchestratorSignInCheck>>());
        });

        services.AddRateLimiter(ConfigureRateLimiter);
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

        // Media, registered only when a bucket is configured. Like the sweeps below, this is read
        // straight from configuration: options are not resolvable until after Build().
        if (!string.IsNullOrWhiteSpace(
                builder.Configuration.GetSection(CommsOptions.SectionName)
                    [$"{nameof(CommsOptions.Media)}:{nameof(Fleet.Comms.Configuration.MediaOptions.Endpoint)}"]))
        {
            builder.Services.AddJournalMedia();
            builder.Services.AddHostedService(provider => (JournalMediaHealth)provider
                .GetRequiredService<JournalMediaGate>());
        }

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
        var attachmentRoot = conversationSection[nameof(CommsOptions.AttachmentRootPath)];

        // Media, read straight from configuration like the four above (options are not resolvable
        // until after Build()), and registered only when a bucket is configured.
        var mediaEndpoint = conversationSection[
            $"{nameof(CommsOptions.Media)}:{nameof(Fleet.Comms.Configuration.MediaOptions.Endpoint)}"];

        if (!string.IsNullOrWhiteSpace(mediaEndpoint)) builder.Services.AddJournalMedia();

        // The journal's in-process status, shared by the retention sweep below and the journal
        // listener Program builds. Registered only when the journal is on, like everything else of it.
        var journalEnabled = bool.TryParse(
            conversationSection[$"{nameof(CommsOptions.Journal)}:{nameof(JournalOptions.Enabled)}"],
            out var journalFlag) && journalFlag;

        if (journalEnabled)
            builder.Services.TryAddSingleton(provider => new JournalRuntimeStats(provider.GetService<TimeProvider>()));

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
                    provider.GetRequiredService<ILogger<GarbageCollector>>(),

                    // The attachment sweeps are folded into the EXISTING maintenance loop rather
                    // than given a hosted service of their own (#308 D3). Null when no root is
                    // configured, which leaves every attachment path in the collector inert.
                    string.IsNullOrWhiteSpace(attachmentRoot)
                        ? null
                        : new AttachmentStore(
                            attachmentRoot,
                            provider.GetRequiredService<ILogger<AttachmentStore>>()),

                    // The journal sweep rides the same tick (#375). Null with the journal off, so
                    // the collector never touches a journal table on an install that did not opt in.
                    //
                    // The object store is threaded through so retention can mark an object
                    // `deleting` in the same batch that removes its message — after the cascade
                    // nothing connects the object to anything, and the bucket keeps the bytes
                    // forever with no row left to find them by.
                    journalEnabled
                        ? new JournalRetention(
                            conversationConnection,
                            provider.GetRequiredService<IOptions<CommsOptions>>().Value.Journal.MessageRetention,
                            provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value.OutboxBatchSize,
                            provider.GetRequiredService<ILogger<JournalRetention>>(),
                            provider.GetRequiredService<JournalRuntimeStats>(),
                            time: null,
                            objects: string.IsNullOrWhiteSpace(mediaEndpoint)
                                ? null
                                : provider.GetRequiredService<IJournalObjectStore>())
                        : null,

                    // The object sweep, on the same tick and AFTER retention (#388): retention
                    // marks, the sweeper removes, and the other order would leave a retired object
                    // sitting for another hour. Null without media, which leaves journal_objects
                    // and the bucket untouched.
                    string.IsNullOrWhiteSpace(mediaEndpoint)
                        ? null
                        : new JournalObjectSweeper(
                            conversationConnection,
                            provider.GetRequiredService<IJournalObjectStore>(),
                            provider.GetRequiredService<ILogger<JournalObjectSweeper>>(),
                            provider.GetRequiredService<JournalRuntimeStats>(),
                            // The same clock everything else in the journal uses. The sweeper's whole
                            // job is comparing rows against `created_at`, and `created_at` is written
                            // by a store that takes a TimeProvider — a sweep on a different clock
                            // from the one that stamped the row is an age comparison between two
                            // clocks, which is wrong in both directions: too eager if the sweeper's
                            // clock runs ahead, and silently never if it runs behind.
                            provider.GetService<TimeProvider>())
                        ),
                provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                provider.GetRequiredService<ILogger<ConversationMaintenanceService>>()));

            // The drain needs a broker. Without one the outboxes still accumulate correctly and the
            // client is unaffected — its submission is already durable — so a missing broker is a
            // logged degradation rather than a refusal to start.
            //
            // One drain for every agent (#530 D9): a command's routing key is its conversation's agent,
            // read from the row, and that agent's queue is declared before the first publish to it.
            if (!string.IsNullOrWhiteSpace(brokerConnection))
            {
                builder.Services.AddHostedService(provider => new OutboxDrainService(
                    new RabbitMqOutboxTransport(
                        brokerConnection, ConversationBroker.CommandExchange,
                        provider.GetRequiredService<ILogger<OutboxDrainService>>()),
                    new RabbitMqOutboxTransport(
                        brokerConnection, ConversationBroker.EventExchange,
                        provider.GetRequiredService<ILogger<OutboxDrainService>>()),
                    conversationConnection,
                    provider.GetRequiredService<IOptions<ConversationStoreOptions>>().Value,
                    provider.GetRequiredService<ILogger<OutboxDrainService>>()));
            }
        }

        var app = builder.Build();

        // Fail closed, at the edge. A sign-in check that could not be answered is a 503 with the fixed
        // Internal body and is NEVER a pass (§16, #530 MUST NOT 3); anything else unhandled is a 500
        // with the same body, so no exception message can reach a client (§13). Both statuses carry
        // identical bytes — the distinction is the status line, and a client must not parse the body
        // to tell them apart (§5.2).
        //
        // Outermost, so it also covers a fault inside the limiter below.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (SignInUnavailableException)
            {
                await WriteFixedError(context, StatusCodes.Status503ServiceUnavailable);
            }
            catch (SignInBusyException)
            {
                // The orchestrator is throttling sign-in. Said as this service's own 429, with the
                // same Retry-After floor the limiter uses.
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.RetryAfter = "1";
                    await context.Response.WriteAsJsonAsync(
                        ErrorResponse.For(ProtocolErrorCode.RateLimited), FleetProtocolJson.Options);
                }
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
        // refused without asking the orchestrator or taking a store transaction.
        app.UseRateLimiter();

        // The north surface, only when conversations are configured.
        //
        // An install that has not configured them maps no north route at all: every path is a 404
        // from the router rather than a handler that decided to refuse, no WebSocket middleware is
        // in the pipeline, nothing asks the orchestrator, and no database connection is attempted.
        // That is what makes this an opt-in feature rather than one that merely defaults to off.
        var commsOptions = app.Services.GetRequiredService<IOptions<CommsOptions>>().Value;

        if (commsOptions.ConversationsEnabled)
        {
            // Only reached on the enabled path, so a disabled install adds no middleware at all.
            app.UseWebSockets();
            app.MapNorthApi();
            app.MapConversationApi(commsOptions.AttachmentsEnabled);
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
        WebApplicationBuilder builder,
        IConversationStore? conversations = null, string? migrationStatusConnectionString = null)
    {
        var app = builder.Build();

        // Liveness: the process is running and can answer. Deliberately says nothing about the
        // store — that is /ready's job, and conflating them makes a restart loop out of a
        // recoverable dependency failure.
        app.MapGet("/health", () => Results.Json(new { status = "ok" }, FleetProtocolJson.Options));

        // Readiness: one trivial read against the real conversation store, when there is one.
        //
        // This is the probe that separates "started and permanently broken" from "healthy". It does
        // NOT ask the orchestrator: sign-in being briefly unreachable is answered 503 per request, and
        // a readiness failure here would restart this container over a dependency it does not own.
        app.MapGet("/ready", async (CancellationToken ct) =>
        {
            try
            {
                // The conversation store, when the feature is enabled — and the SCHEMA VERSION as well
                // as reachability.
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
    /// The conversation limiter's partition key: the hashed credential — a bearer or a browser's
    /// session cookie — when one is presented, and the caller's address otherwise.
    /// </summary>
    /// <remarks>
    /// The address fallback matters: an upgrade or a route call arriving with no credential at all
    /// must not land in one shared "anonymous" partition, because that turns every unauthenticated
    /// caller into a denial of service against every other one.
    /// </remarks>
    private static string ConversationPartitionKey(HttpContext context)
    {
        // Behind the dashboard's proxy every caller shares one address, so the credential is what
        // separates them.
        return PresentedCredential.Of(context.Request) is { } credential
            ? $"cred:{credential.Digest()}"
            : $"addr:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    /// <param name="attachments">
    /// The byte store, when configured. Null leaves the south attachment route unmapped.
    /// </param>
    public static WebApplication BuildSouthApp(
        WebApplicationBuilder builder, IConversationStore store, CommsOptions options,
        AttachmentStore? attachments = null)
    {
        // Request BINDING, not just response writing. The framework's web defaults carry no enum
        // converter, so a body carrying the protocol's own `"disposition":"ran"` failed to bind and
        // the caller got an empty-bodied 400 — on the two endpoints that carry a disposition, which
        // is to say on the ones that make the surface work at all. Found by sending a request.
        builder.Services.ConfigureHttpJsonOptions(
            jsonOptions => FleetProtocolJson.ApplyTo(jsonOptions.SerializerOptions));

        var app = builder.Build();
        SouthEndpoints.Map(app, store, options, attachments);
        return app;
    }

    /// <summary>
    /// Build the journal application: the internal ingest and status surface, on its own listener
    /// (#375).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A FOURTH application, separate from north, south and ops for the reason each of those is
    /// separate: a routing mistake must not be able to put a journal route on a surface with a
    /// different caller, or put another surface's credential in front of this one. Journal
    /// publishers hold a journal token and never the south bearer.
    /// </para>
    /// <para>
    /// Container-network only. The compose example publishes no host port for it.
    /// </para>
    /// <para>
    /// ⚠️ It never migrates. The store reports a schema below 0004 as unavailable instead.
    /// </para>
    /// <para>
    /// The read tools (#394) are served here and only here, at <see cref="JournalMcp.Path"/>.
    /// </para>
    /// </remarks>
    /// <param name="reads">
    /// The read store. Null builds the MySQL one over the same connection string the ingest store
    /// uses; it opens no connection until the first read.
    /// </param>
    public static WebApplication BuildJournalApp(
        WebApplicationBuilder builder, IJournalStore store, CommsOptions options,
        JournalRuntimeStats stats, TimeProvider? time = null, JournalMedia? media = null,
        IJournalReadStore? reads = null)
    {
        var keys = options.Journal.Keys();
        var excluded = options.Journal.ExcludedChats();
        var grants = new JournalReadGrants(options.Journal.AllScopeSubjects());

        JournalMcp.AddServices(builder.Services, grants, stats, reads is not null
            ? _ => reads
            : provider => new MySqlJournalReadStore(
                options.ConversationConnectionString,
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.Comms.Journal.Read")), time, excluded);

        builder.Services.AddSingleton<IJournalCrossChatAuthorization>(sp => new JournalCrossChatAuthorizationClient(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, options.Journal.OrchestratorUrl, keys[0]));
        var app = builder.Build();
        var journalLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.Comms.Journal");

        // Outermost: nothing unhandled reaches a caller as a stack trace or a driver message. It is
        // still logged — type and subject only, since a driver message can carry a connection
        // string — and counted, so a fault is visible in /journal/v1/status and on the meter.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception e) when (!context.Response.HasStarted)
            {
                journalLogger.LogError("journal request from {Subject} failed: {Error}",
                    context.Items[JournalAuth.SubjectItem] as string ?? "(unauthenticated)", e.GetType().Name);
                stats.Rejected("internal");

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"internal\"}");
            }
        });

        JournalAuth.Use(app, keys, stats);

        // Media, when the deployment configured a bucket. The two upload routes are mapped HERE
        // and nowhere else — the same rule that keeps a journal route off north, south and ops: a
        // route that accepts megabytes must not be reachable from a listener with a different
        // credential in front of it.
        if (media is not null)
        {
            // ⚠️ The object table and the message table are written by ONE store instance, so media
            //    requires the real store rather than a substitute. A host that wired a fake
            //    IJournalStore and turned media on would otherwise accept uploads into a bucket
            //    nothing could ever attach them to.
            if (store is not MySqlJournalStore journalStore)
                throw new InvalidOperationException(
                    "media_requires_journal_store: the journal listener was built with a store that "
                    + "is not the MySQL store, so there is no journal_objects table to write.");

            if (!ReferenceEquals(journalStore.Objects, media.Objects))
                throw new InvalidOperationException(
                    "media_requires_journal_store: the MySQL store and the media gate were built "
                    + "with different object stores. Uploads and commits must go through one.");

            JournalUploadEndpoints.Map(
                app, media.Objects, media.Bytes, stats, time ?? TimeProvider.System, journalLogger,
                media.Gate);
        }

        JournalEndpoints.Map(
            app, store, stats, excluded, time ?? TimeProvider.System, journalLogger,
            media?.Gate, media?.Bytes, media?.Objects, grants.AllScopeSubjects);

        // The read tools, behind the same authentication as every route above (#394).
        JournalMcp.Map(app);
        var send = new JournalSendSourceResolver(app.Services.GetRequiredService<IJournalReadStore>() as IJournalSendSource,
            app.Services.GetRequiredService<JournalBindingScope>(), app.Services.GetRequiredService<IJournalCrossChatAuthorization>());
        JournalAttachmentSendEndpoints.Map(app, send);
        JournalAttachmentContentEndpoint.Map(app, media?.Bytes, stats, time, send);

        return app;
    }

    /// <summary>
    /// The media half of the journal listener, as one value. Null everywhere it is absent means
    /// "no bucket configured", which is the state every deployment was in before #388.
    /// </summary>
    /// <param name="Gate">
    /// The interface, not the concrete health service: the routes ask it a question, and a
    /// composition that could only be built with the real prober could not be exercised without a
    /// bucket. <c>Program</c> passes the hosted <see cref="JournalMediaHealth"/>, which is the only
    /// implementation a deployment ever has.
    /// </param>
    /// <summary>
    /// Parse one ingest body with the REAL ingest parser and report the outcome.
    /// </summary>
    /// <returns>
    /// <c>(0, null)</c> when the record is accepted; otherwise the status the ingest route would
    /// answer and the error it would name.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠️ This exists for ONE reason: <see cref="Routes.JournalRecordParser"/> is internal and
    /// <c>InternalsVisibleTo</c> names only Fleet.Comms.Tests. The drainer's contract is not "my
    /// JSON has the field I expect" — it is "the parser that answers real ingests accepts what I
    /// put on the wire." A test in Fleet.Journal.Client.Tests that re-implemented these rules would
    /// agree with the drainer by construction and catch nothing; the only honest seam is the same
    /// code the route calls. Nothing but that test calls this.
    /// </para>
    /// <para>
    /// No media gate is consulted, so this is the parser alone: what a record with upload references
    /// looks like to the parser on a deployment that HAS a bucket.
    /// </para>
    /// </remarks>
    public static (int Status, string? Error, string? Field) ValidateIngestRecord(
        ReadOnlyMemory<byte> body, DateTimeOffset now)
    {
        var record = Routes.JournalRecordParser.Parse(body, now, out var failure);
        if (record is not null) return (0, null, null);

        ArgumentNullException.ThrowIfNull(failure);
        return (failure.Status, failure.Error, failure.Field);
    }

    public sealed record JournalMedia(
        JournalMediaGate Gate, IJournalObjectStore Bytes, MySqlJournalObjectStore Objects);

    /// <summary>
    /// The media services, registered only when a bucket is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Factories, not instances: <see cref="S3ObjectStore"/> builds an SDK client, and a client
    /// constructed on an install with no bucket is a credential holder that exists for no reason.
    /// </para>
    /// <para>
    /// These are on the NORTH container's provider, which is where the maintenance loop lives. The
    /// journal listener gets its own instance from <c>Program</c> — the two share the bucket and
    /// the credentials, and neither shares a mutable state that matters.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddJournalMedia(this IServiceCollection services)
    {
        services.TryAddSingleton<IJournalObjectStore>(provider =>
        {
            var media = provider.GetRequiredService<IOptions<CommsOptions>>().Value.Media;

            if (!media.Enabled)
                throw new InvalidOperationException(
                    "Comms__Media__Endpoint is not configured, so there is no object store to "
                    + "resolve. Reaching this means an upload route or a sweep was registered on an "
                    + "install that did not enable media.");

            return new S3ObjectStore(
                new JournalMediaOptions
                {
                    Endpoint = media.Endpoint,
                    Bucket = media.Bucket,
                    AccessKey = media.AccessKey,
                    SecretKey = media.SecretKey,
                    Region = media.Region,
                    RequestTimeout = media.RequestTimeout,
                },
                provider.GetRequiredService<ILogger<S3ObjectStore>>());
        });

        // The bucket is wired into the row store so a dedup loser's bytes can be deleted once the
        // transaction that removed its row has committed. Two halves of one object, one owner.
        services.TryAddSingleton(provider => new MySqlJournalObjectStore(
            provider.GetRequiredService<IOptions<CommsOptions>>().Value.ConversationConnectionString,
            provider.GetRequiredService<ILogger<MySqlJournalObjectStore>>())
        {
            Bytes = provider.GetRequiredService<IJournalObjectStore>(),
        });

        services.TryAddSingleton<JournalMediaGate>(provider => new JournalMediaHealth(
            provider.GetRequiredService<IJournalObjectStore>(),
            provider.GetRequiredService<ILogger<JournalMediaHealth>>()));

        return services;
    }

    private static void ConfigureRateLimiter(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // The conversation routes partition on the CREDENTIAL, not the address.
        //
        // Every one of them is authenticated, and two devices behind one forwarded address — a
        // household NAT, a corporate egress, a reverse proxy that does not forward — would otherwise
        // share a budget, so one client's catch-up storm would throttle the other's.
        //
        // The key is a hash of the presented credential, never the credential: a rate-limiter partition key
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
                : ConversationRateLimits.WindowSeconds;
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
