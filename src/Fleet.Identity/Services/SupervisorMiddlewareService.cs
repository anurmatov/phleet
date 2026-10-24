using Fleet.Identity.Configuration;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Openshell.Extension.V1;
using Openshell.Middleware.V1;

namespace Fleet.Identity.Services;

public sealed class SupervisorMiddlewareService(IdentityOptions options, GatewayTokenVerifier tokens,
    LaunchIdentityStore launches, RequestAssertionIssuer issuer, ILogger<SupervisorMiddlewareService> logger)
    : SupervisorMiddleware.SupervisorMiddlewareBase
{
    public override Task<MiddlewareManifest> Describe(MiddlewareDescribeRequest request, ServerCallContext context)
    {
        Authenticate(context, null);
        if (request.Gateway?.ProtocolVersion is not { Major: 1, Minor: 0 }
            || !request.Gateway.SupportedCapabilities.Contains("openshell.supervisor-middleware.contract")
            || request.Gateway.RequiredCapabilities.Any(c => c != "openshell.supervisor-middleware.contract"))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "protocol_unsupported"));
        return Task.FromResult(new MiddlewareManifest {
            Name = "verified-memory", ExpectedAudience = options.GatewayAudience,
            Extension = new PeerMetadata { ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
                ImplementationName = "phleet/identity", ImplementationVersion = "1",
                SupportedCapabilities = { "openshell.supervisor-middleware.contract" } },
            Bindings = { new MiddlewareBinding { Operation = SupervisorMiddlewareOperation.HttpRequest,
                Phase = SupervisorMiddlewarePhase.PreCredentials, MaxPayloadBytes = RequestRules.MaxBody,
                RequestTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(2)) } } });
    }
    public override Task<ValidateConfigResponse> ValidateConfig(ValidateConfigRequest request, ServerCallContext context)
    {
        Authenticate(context, null);
        return Task.FromResult(new ValidateConfigResponse { Valid = ValidConfig(request.Config),
            Reason = ValidConfig(request.Config) ? "" : "config_unsupported" });
    }
    public override async Task<HttpRequestResult> EvaluateHttpRequest(HttpRequestEvaluation request, ServerCallContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            Authenticate(context, request.Context?.SandboxId ?? "");
            if (request.Phase != SupervisorMiddlewarePhase.PreCredentials || !ValidConfig(request.Config)
                || request.Target is null || request.Context is null || request.Context.CalculateSize() > 4096
                || request.Target.CalculateSize() > 32768 || request.Headers.Count > 128
                || request.Headers.Sum(h => h.CalculateSize()) > 65536
                || request.Target.Scheme != "https" || request.Target.Host != options.IngressHost
                || request.Target.Port != options.IngressPort || request.Target.Query != "")
                throw new IdentityDeniedException("hook_scope");
            var headers = request.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToArray();
            var body = request.Body.ToByteArray();
            RequestRules.Validate(request.Target.Method, request.Target.Path, body, headers, false);
            var session = headers.SingleOrDefault(h => h.Key.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase)).Value;
            // An expired OWNED session can carry a proof to ingress for authenticated 404, never tool admission.
            var identity = await launches.ResolveAsync(options.GatewayId, request.Context.SandboxId, session, deadline.Token, allowExpiredSession: true)
                .WaitAsync(deadline.Token);
            var assertion = issuer.Issue(identity, RequestRules.Bind(request.Target.Method, request.Target.Path, body, session));
            return new HttpRequestResult { Decision = Decision.Allow, HeaderMutations = {
                new HeaderMutation { Write = new WriteHeader { Name = "X-Fleet-Assertion", Value = assertion,
                    OnExisting = ExistingHeaderAction.Overwrite } } } };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Identity hook refused: {Reason}", ex is IdentityDeniedException denied ? denied.Code : "dependency_unavailable");
            return new HttpRequestResult { Decision = Decision.Deny, ReasonCode = "identity_denied" };
        }
    }
    private static bool ValidConfig(Struct? config) => config is not null && config.Fields.Count == 1
        && config.Fields.TryGetValue("service", out var service) && service.KindCase == Value.KindOneofCase.StringValue && service.StringValue == "memory";
    private void Authenticate(ServerCallContext context, string? sandbox)
    {
        var authorization = context.RequestHeaders.Where(h => h.Key == "authorization").ToArray();
        if (authorization.Length != 1 || authorization[0].IsBinary || !authorization[0].Value.StartsWith("Bearer ", StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "gateway_token"));
        try { tokens.Verify(authorization[0].Value[7..], sandbox); }
        catch (IdentityDeniedException) { throw new RpcException(new Status(StatusCode.Unauthenticated, "gateway_token")); }
    }
}
