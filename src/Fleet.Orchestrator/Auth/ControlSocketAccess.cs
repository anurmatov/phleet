using System.Net.WebSockets;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Auth;

/// <summary>Live control/log sockets retain the credential that opened them, not just its first verdict.</summary>
public static class ControlSocketAccess
{
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(20);

    public static bool SameOrigin(HttpRequest request)
    {
        // The edge supplies the original scheme. Direct connections use their own transport.
        var scheme = request.Headers[OrchestratorAuth.EdgeHeader] == "1"
            ? request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme
            : request.Scheme;
        return Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            && origin.GetLeftPart(UriPartial.Authority).Equals($"{scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
            && origin.AbsolutePath == "/" && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment);
    }

    public static async Task WatchAsync(HttpContext http, WebSocket socket, CancellationTokenSource lifetime)
    {
        var auth = http.RequestServices.GetRequiredService<CallerAuthenticator>();
        var original = CallerIdentity.Of(http);
        var bearer = http.Request.Headers.Authorization.FirstOrDefault();
        var cookie = http.Request.Headers.Cookie.ToString();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(RecheckInterval, lifetime.Token);
                var current = await auth.ResolveAsync(bearer, cookie, lifetime.Token, fresh: true)
                    .WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
                if (current.Kind == original.Kind && current.PrincipalId == original.PrincipalId
                    && current.DeviceId == original.DeviceId && current.CredentialId == original.CredentialId
                    && (current.Kind != CallerKind.None || !auth.IsRequired)) continue;
                break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { /* An unavailable auth check is not permission to keep streaming. */ }
        socket.Abort();
        await lifetime.CancelAsync();
    }
}
