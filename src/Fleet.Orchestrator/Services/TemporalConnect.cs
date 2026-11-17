using Fleet.Orchestrator.Configuration;
using Fleet.Shared;
using Temporalio.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// The one place the orchestrator constructs a Temporal client. Every connection's options are
/// built here, so what a deployment's Temporal requires of its clients (today: an optional bearer
/// token, <see cref="Authenticate"/>) is applied in one method and no client can be built that goes
/// without it: <c>TemporalConnectSeamTests</c> fails on a Temporal client constructed anywhere else
/// in <c>src</c>.
/// </summary>
internal static class TemporalConnect
{
    /// <summary>
    /// The connect options for one namespace: the address, the namespace, and what
    /// <see cref="Authenticate"/> adds.
    /// </summary>
    /// <exception cref="TemporalBearerTokenException">A token is configured that cannot be used. Nothing is connected.</exception>
    internal static TemporalClientConnectOptions Options(TemporalOptions options, string address, string @namespace)
    {
        var connect = new TemporalClientConnectOptions(address) { Namespace = @namespace };
        Authenticate(connect, options);
        return connect;
    }

    internal static async Task<ITemporalClient> ConnectAsync(TemporalOptions options, string address, string @namespace) =>
        await TemporalClient.ConnectAsync(Options(options, address, @namespace));

    /// <summary>
    /// How this service proves itself to Temporal: the one piece to change if that ever does.
    /// With <c>Temporal:BearerToken</c> set, every call carries <c>Authorization: Bearer &lt;token&gt;</c>;
    /// with none, the options are what they were before the setting existed. A token that cannot be
    /// used throws <see cref="TemporalBearerTokenException"/>: no client is built without the
    /// header it was asked to send.
    ///
    /// The header goes in the connection's metadata, not in the SDK's <c>ApiKey</c>. In the pinned
    /// SDK (Temporalio 1.11.1) an API key with no TLS options turns TLS on with defaults, and the
    /// transport is not this setting's to change: it stays what it was, plaintext, and
    /// <c>Tls</c> is left unset. A deployment that needs the token protected in transit needs TLS,
    /// which nothing here adds.
    /// </summary>
    private static void Authenticate(TemporalClientConnectOptions connect, TemporalOptions options)
    {
        if (TemporalBearerToken.AuthorizationOrNull(options.BearerToken, TemporalOptions.BearerTokenSetting) is { } authorization)
            connect.RpcMetadata = [new KeyValuePair<string, string>(AuthorizationHeader, authorization)];
    }

    /// <summary>Lower case, as gRPC metadata keys are.</summary>
    internal const string AuthorizationHeader = "authorization";

    /// <summary>
    /// What stops this service at startup, or null: the check every client's options make, done
    /// once before anything connects. Reads the one setting, so it cannot fail on another.
    /// </summary>
    internal static string? DescribeStartupFault(IConfiguration configuration) =>
        TemporalBearerToken.DescribeFault(configuration[TemporalOptions.BearerTokenSetting], TemporalOptions.BearerTokenSetting);

    /// <summary>
    /// Says once, at startup, when the configured token expires, if it says so itself (a JWT's
    /// <c>exp</c>): information, a warning when that is near, an error when it is past. Nothing for
    /// no token or an opaque one, and never any part of the token.
    /// </summary>
    internal static void LogTokenExpiry(ILogger logger, IConfiguration configuration, DateTimeOffset now)
    {
        if (TemporalBearerToken.DescribeExpiry(
                configuration[TemporalOptions.BearerTokenSetting], TemporalOptions.BearerTokenSetting, now) is not { } notice)
            return;

        var level = notice.State switch
        {
            TemporalTokenExpiryState.Expired => LogLevel.Error,
            TemporalTokenExpiryState.ExpiresSoon => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, "{TemporalTokenExpiry}", notice.Message);
    }
}
