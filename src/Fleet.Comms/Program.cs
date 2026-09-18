using Fleet.Comms;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Conversations;
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
    var opsApp = CommsApp.BuildOpsApp(opsBuilder, northApp.Services.GetRequiredService<IAuthStore>());

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

    WebApplication? southApp = null;

    if (options.ConversationsEnabled)
    {
        var store = new MySqlConversationStore(
            options.ConversationConnectionString,
            new ConversationStoreOptions(),
            northApp.Services.GetRequiredService<ILogger<MySqlConversationStore>>());

        var southBuilder = WebApplication.CreateBuilder();
        southBuilder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        southBuilder.WebHost.UseUrls(options.SouthUrl);

        southApp = southBuilder.Build();
        SouthEndpoints.Map(southApp, store, options);
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

    await Task.WhenAll(hosts.Select(h => h.StartAsync()));

    await Task.WhenAny(hosts.Select(h => h.WaitForShutdownAsync()));
    await Task.WhenAll(hosts.Select(h => h.StopAsync()));
    return shutdownRequested ? 0 : 1;
}

/// <summary>Named so the test host can reference the entry-point assembly.</summary>
public partial class Program;
