using Fleet.Comms;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using Fleet.Comms.Operations;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

// Subcommands are dispatched FIRST, before anything builds a web application.
//
// That ordering is the contract, not an implementation detail: an operator issuing an enrollment
// code or revoking a lost phone must not cause a listener to appear, not on the public address and
// not on loopback. `docker compose run --rm fleet-comms enroll issue ...` is a one-shot process
// against the store, and the only thing it opens is the database file.
if (args.Length > 0)
    return await OperatorCommands.RunAsync(args);

try
{
    return await RunServiceAsync(args);
}
catch (Exception e)
{
    // NOTHING is allowed to escape to the runtime's unhandled-exception path.
    //
    // This is not tidiness. Container acceptance found that a missing `Comms__AuthStorePath` —
    // which throws here by design — did not terminate the process at all: the message was written,
    // no listener was bound, and the process then sat at ~99% CPU indefinitely. Docker sees that as
    // `running`, so `restart: unless-stopped` never fires, an orchestrator never learns anything is
    // wrong, and a deployment that can serve nothing looks alive. A configuration mistake has to be
    // a process that stops, promptly, with a non-zero code.
    //
    // The message rather than the stack trace: what reaches here is a configuration rejection whose
    // text is written for an operator. The type is included for anything unexpected, so a genuine
    // fault is still identifiable in a container log.
    await Console.Error.WriteLineAsync(
        e is InvalidOperationException or ArgumentException
            ? e.Message
            : $"{e.GetType().Name}: {e.Message}");

    await Console.Error.FlushAsync();
    return 1;
}

static async Task<int> RunServiceAsync(string[] args)
{
    // The north listener. The south surface is a SEPARATE listener, built below when the
    // conversation feature is configured; nothing here maps a south route.
    //
    // Listener addresses, certificates and any credential come from the host environment at run time.
    // None of them is in this repository.
    var northApp = CommsApp.BuildNorthApp(WebApplication.CreateBuilder(args));

    // The operations listener is a SEPARATE application on its own address (see CommsApp.BuildOpsApp).
    // It shares the auth store instance so `/ready` probes the same store the routes use — a readiness
    // check against a second connection to the same file would still miss an exhausted pool.
    var opsUrl = northApp.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CommsOptions>>().Value.OpsUrl;

    // Validated before either host starts. The loopback-only property of the readiness endpoint was
    // previously true of the default value and of nothing else: `OpsUrl` went straight to `UseUrls`,
    // so a non-loopback or wildcard address published the store's availability oracle to whoever could
    // reach it. A default is not an invariant.
    OpsListenerAddress.EnsureLoopbackOnly(opsUrl);

    // Own builder, own configuration. `CreateBuilder()` reads ASPNETCORE_URLS, which is the north
    // listener's address — UseUrls then overrides it and the host logs a warning every start. Clearing
    // the inherited value means the ops listener's address comes from exactly one place.
    var opsBuilder = WebApplication.CreateBuilder();
    opsBuilder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
    opsBuilder.WebHost.UseUrls(opsUrl);
    // Readiness sees the conversation store only when the feature is enabled, so a disabled install
    // probes exactly what it probed before — same transaction, same connections, same timing.
    var conversationOptions = northApp.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CommsOptions>>().Value;

    var opsApp = CommsApp.BuildOpsApp(
        opsBuilder,
        northApp.Services.GetRequiredService<IAuthStore>(),
        conversationOptions.ConversationsEnabled
            ? northApp.Services.GetRequiredService<IConversationStore>()
            : null,

        // The schema version is read through whichever credential is configured. The runtime account
        // can read `schema_migrations` — it is a SELECT — and a deployment that keeps the DDL
        // credential off the running container must still be able to report what schema it is on.
        conversationOptions.ConversationsEnabled
            ? conversationOptions.ConversationConnectionString
            : null);

    // ── The south listener, only when the conversation feature is configured ─────────
    //
    // An install that has not configured it is byte-identical to the auth slice: no third host is
    // built, no south route exists, and no database connection is attempted. That is asserted
    // rather than assumed — it is the difference between an opt-in feature and one that merely
    // defaults to off.
    //
    // Its binding rule is the OPPOSITE of the ops listener's, and deliberately so. Ops is
    // loopback-only because only this container's healthcheck calls it. The south caller is a
    // DIFFERENT container, so a loopback bind would make the surface unreachable by construction;
    // it binds the container network and is kept private by never being published as a host port
    // and never being proxied.
    var options = northApp.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<CommsOptions>>().Value;

    options.ValidateConversations();

    // The journal is validated here too, before any host starts: every failure is a process that
    // exits 1 with a fixed `journal_…` code and never the key.
    options.ValidateJournal();

    // Media is validated the same way, and the bucket is PROBED before any host serves a request.
    //
    // Two different outcomes, and the split is the design:
    //   • a configuration that cannot work (missing field, unknown bucket, rejected credentials)
    //     exits 1 with a fixed `media_…` / `credentials_rejected` code and never the value;
    //   • a bucket that is merely not answering starts DEGRADED — uploads answer 503, the text
    //     journal works, the container stays healthy, and the probe loop recovers on its own.
    // A wrong credential is not the second case: it will not repair itself, and a Comms that came
    // up permanently degraded is how a broken deployment hides from its own healthcheck.
    options.ValidateMedia();

    Fleet.Comms.CommsApp.JournalMedia? media = null;

    if (options.Media.Enabled)
    {
        var mediaLogger = northApp.Services
            .GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.Comms.Journal.Media");
        var objectLogger = northApp.Services
            .GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.Conversations.Journal.S3ObjectStore");

        var bucket = new Fleet.Conversations.Journal.S3ObjectStore(
            new Fleet.Conversations.Journal.JournalMediaOptions
            {
                Endpoint = options.Media.Endpoint,
                Bucket = options.Media.Bucket,
                AccessKey = options.Media.AccessKey,
                SecretKey = options.Media.SecretKey,
                Region = options.Media.Region,
                RequestTimeout = options.Media.RequestTimeout,
            },
            objectLogger);

        var gate = new Fleet.Conversations.Journal.JournalMediaHealth(bucket, mediaLogger);

        // One probe, with a hard budget. `StartupProbeBudget` is why this is bounded rather than
        // relying on the SDK's own timeout: an operator with a typo'd endpoint should see an exit
        // code, not a container that hangs in "starting" until the orchestrator kills it.
        var objects = new Fleet.Conversations.Journal.MySqlJournalObjectStore(
            options.ConversationConnectionString, mediaLogger);

        // One probe, bounded. `false` is a bucket that is not answering; `JournalProbeFailureException`
        // is a bucket that answered and refused. Only the first is degraded.
        bool reachable;
        try
        {
            reachable = await ProbeWithinBudgetAsync(bucket);
        }
        catch (Fleet.Conversations.Journal.JournalProbeFailureException)
        {
            // The message is the fixed code; the SDK's own text is deliberately not repeated, and
            // neither carries the secret.
            await Console.Error.WriteLineAsync(
                "credentials_rejected: Comms__Media__Endpoint is set but the bucket refused this "
                + "account, or the bucket does not exist. Fix the credentials or the bucket name — "
                + "media_state=degraded does not cover a configuration that cannot work.");
            return 1;
        }

        media = new Fleet.Comms.CommsApp.JournalMedia(gate, bucket, objects);

        if (reachable)
        {
            gate.MarkStartupState(true);
        }
        else
        {
            await Console.Error.WriteLineAsync(
                "media_state=degraded: the journal object store is not reachable; uploads answer "
                + "503 media_unavailable until it is. The journal itself is unaffected.");
        }

        // ⚠️ The last guard before a listener accepts an upload: the bucket must not be world
        //    readable. A public bucket would make every archived attachment readable by anyone who
        //    can guess a key, and the credentials are not what protects the bytes there.
        if (await BucketIsPublicAsync(options.Media))
        {
            await Console.Error.WriteLineAsync(
                "bucket_public: an unsigned read of the journal bucket did not return 403. The bucket must "
                + "deny anonymous listing; refusing to start with a public object store.");
            return 1;
        }
    }

    WebApplication? southApp = null;

    if (options.ConversationsEnabled)
    {
        // The SAME instance the north routes use, resolved from the north app's container rather
        // than constructed again here. Two stores would be two connection pools, two sets of
        // options and two answers to "is the schema current?" — and the readiness probe would be
        // reporting on whichever one it happened to hold.
        var store = northApp.Services.GetRequiredService<IConversationStore>();

        var southBuilder = WebApplication.CreateBuilder();
        southBuilder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        southBuilder.WebHost.UseUrls(options.SouthUrl);

        // The SAME byte store the north routes use, for the same reason: two would be two roots and
        // two answers to "where does this attachment live". Null when no root is configured, which
        // leaves the south attachment route unmapped.
        var attachments = options.AttachmentsEnabled
            ? northApp.Services.GetRequiredService<Fleet.Conversations.AttachmentStore>()
            : null;

        // Composed by CommsApp, not here, so the south suite drives the same graph this line does.
        southApp = CommsApp.BuildSouthApp(southBuilder, store, options, attachments);
    }

    // ── The journal listener, only when the journal is enabled (#375) ─────────────────
    //
    // Off means nothing is bound, registered or started. On, it is a fourth application on its own
    // container-network address — never published, never proxied — and it never accepts the south
    // bearer. It never migrates: a schema below 0004 is answered as unavailable.
    WebApplication? journalApp = null;

    if (options.Journal.Enabled)
    {
        var journalBuilder = WebApplication.CreateBuilder();
        journalBuilder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        journalBuilder.WebHost.UseUrls(options.Journal.Url);

        var journalStore = new Fleet.Conversations.Journal.MySqlJournalStore(
            options.ConversationConnectionString,
            northApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fleet.Comms.Journal.Store"))
        {
            Objects = media?.Objects,
        };

        if (media is not null)
        {
            // The probe loop runs on the JOURNAL host, not the north one: the bucket is the
            // journal's dependency, and a deployment with the journal off must not probe a bucket
            // it never enabled.
            //
            // `media.Gate` is the interface, so the concrete prober is hosted through it. The cast
            // is the same one CommsApp's media registration uses: the value the routes gate on and
            // the loop that refreshes it are one object, and a second instance would probe a bucket
            // the routes never ask about.
            journalBuilder.Services.AddHostedService(_ =>
                (Fleet.Conversations.Journal.JournalMediaHealth)media.Gate);
        }

        journalApp = CommsApp.BuildJournalApp(
            journalBuilder, journalStore, options,
            northApp.Services.GetRequiredService<Fleet.Conversations.Journal.JournalRuntimeStats>(),
            media: media);
    }

    // Both or neither. If either listener cannot bind — port already in use, address unavailable — the
    // process fails rather than coming up half-configured: a north listener with no readiness path is
    // the state an operator cannot diagnose, and a readiness path with no north listener is a service
    // that reports ready and serves nothing.
    // A requested shutdown is a success; a listener that stopped on its own is not.
    //
    // Returning non-zero unconditionally made every ordinary `docker compose stop` look like a crash,
    // which is the kind of noise that gets restart policies and alerts quietly loosened. The signal is
    // what distinguishes them: SIGTERM or SIGINT means someone asked, so the process exits 0. One host
    // stopping with no signal means it failed after start, and a half-configured service must not
    // report that it finished its work.
    var shutdownRequested = false;
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => shutdownRequested = true);
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => shutdownRequested = true);

    // Every configured listener, or none. A process serving two of three is the state an operator
    // cannot diagnose from the outside.
    var hosts = southApp is null
        ? new[] { northApp, opsApp }
        : [northApp, opsApp, southApp];

    // The journal first, and alone: a port already in use is reported as its own code before any
    // other listener has served a request.
    if (journalApp is not null)
    {
        try
        {
            await journalApp.StartAsync();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"journal_bind_failed: could not bind Comms__Journal__Url ({e.GetType().Name}).");
        }

        hosts = [journalApp, .. hosts];
        await Task.WhenAll(hosts.Skip(1).Select(h => h.StartAsync()));
    }
    else
    {
        await Task.WhenAll(hosts.Select(h => h.StartAsync()));
    }

    await Task.WhenAny(hosts.Select(h => h.WaitForShutdownAsync()));
    await Task.WhenAll(hosts.Select(h => h.StopAsync()));
    return shutdownRequested ? 0 : 1;
}

/// <summary>
/// The startup probe, with a budget shorter than the SDK's own timeout.
/// </summary>
/// <remarks>
/// AC5's "under 10 s" is a property of THIS call, not of the SDK: with a typo'd endpoint the SDK
/// would retry for its full configured timeout, and a container that spends 30 s in "starting"
/// before exiting 1 looks like a slow start rather than a refusal.
/// </remarks>
static async Task<bool> ProbeWithinBudgetAsync(Fleet.Conversations.Journal.S3ObjectStore bucket)
{
    // Shorter than the SDK's own 30 s request timeout on purpose.
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    return await bucket.ProbeAsync(budget.Token);
}

/// <summary>
/// The anonymous probe: an unsigned bucket listing must be refused.
/// </summary>
/// <remarks>
/// <b>Not a credentials test.</b> This account's credentials were just accepted one call earlier;
/// what is being asked here is whether anonymous listing is explicitly denied. Only 403 proves
/// that. Other HTTP answers fail closed; transport failures leave startup degraded.
/// </remarks>
static async Task<bool> BucketIsPublicAsync(Fleet.Comms.Configuration.MediaOptions media)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    try
    {
        var endpoint = media.Endpoint.TrimEnd('/');
        using var response = await http.GetAsync(
            $"{endpoint}/{media.Bucket}?list-type=2", HttpCompletionOption.ResponseHeadersRead);

        return response.StatusCode != System.Net.HttpStatusCode.Forbidden;
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
    {
        // A network outage must not stop the text journal from starting degraded.
        return false;
    }
}

/// <summary>Named so the test host can reference the entry-point assembly.</summary>
public partial class Program;
