using Fleet.Identity.Configuration;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Fleet.Identity.Services;

/// <summary>Separate bootstrap-only audience/receiver. Never invokes the memory ingress or session APIs.</summary>
public sealed class BootstrapIdentityIngress(IdentityOptions options, FleetRequestAssertionVerifier verifier,
    ReplayStore replay, HttpClient receiver)
{
    public async Task HandleAsync(HttpContext context)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        admission.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (!options.HasBootstrapReceiver) { context.Response.StatusCode = 503; return; }
            if (!context.Request.IsHttps || context.Request.Host.Host != options.IngressHost || context.Request.Host.Port != options.IngressPort)
                throw new IdentityDeniedException("bootstrap_scope");
            var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
            if (context.Request.ContentLength is > 0 || await context.Request.Body.ReadAsync(new byte[1], admission.Token) != 0)
                throw new IdentityDeniedException("bootstrap_body");
            var headers = context.Request.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v ?? ""))).ToArray();
            if (headers.Length > 128 || headers.Sum(h => h.Key.Length + h.Value.Length) > 65536) throw new IdentityDeniedException("bootstrap_headers");
            RequestRules.ValidateBootstrap(context.Request.Method, target, [], headers, true);
            var assertion = context.Request.Headers["X-Fleet-Assertion"];
            if (assertion.Count != 1 || assertion[0] is null) throw new IdentityDeniedException("bootstrap_proof");
            var (identity, jti) = verifier.Verify(assertion[0]!, RequestRules.Bind("POST", target, [], null), "orchestrator", "/identity/ready");
            if (identity.GatewayId != options.GatewayId) throw new IdentityDeniedException("bootstrap_gateway");
            await replay.ConsumeAsync(identity, jti, assertion[0]!, null, admission.Token).WaitAsync(admission.Token);
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            execution.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Post, options.BootstrapReceiver) { Content = new ByteArrayContent([]) };
            request.Headers.Add("X-Fleet-Assertion", assertion[0]);
            using var result = await receiver.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, execution.Token).WaitAsync(execution.Token);
            context.Response.StatusCode = result.StatusCode == System.Net.HttpStatusCode.NoContent ? 204
                : (int)result.StatusCode is 401 or 403 or 409 ? (int)result.StatusCode : 503;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { context.Response.StatusCode = ex is IdentityDeniedException ? 403 : ex is System.Security.Cryptography.CryptographicException ? 401 : 503; }
        context.Response.Headers.CacheControl = "no-store";
    }
}
