using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Shared.Security;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Openshell.Extension.V1;
using Openshell.Middleware.V1;

namespace Fleet.StockIdentity;

public sealed record StockAuthorityFile(string Path, string? Directory = null);

// The stock gateway supplies authenticated sandbox context. No identity is accepted from a
// workload header/query; the assertion key and operator enrollment stay outside the workload.
public sealed class StockSupervisorMiddleware(StockAuthorityFile file) : SupervisorMiddleware.SupervisorMiddlewareBase
{
    private StockEnrollmentAuthority Authenticate(ServerCallContext context, string? sandbox)
    {
        try
        {
            if (!context.GetHttpContext().Request.IsHttps) throw new StockIdentityDenied("tls_required");
            // Gateway control-plane negotiation precedes allocation. Only data evaluation may
            // load active workload authority; a manifest/config response never grants data access.
            // Managed mode selects the per-agent record by the gateway-verified sandbox claim.
            var record = sandbox is null ? LoadGatewayTrust() : StockAuthoritySource.Resolve(file.Path, file.Directory, sandbox);
            var headers = context.RequestHeaders.Where(h => h.Key == "authorization").ToArray();
            if (headers.Length != 1 || headers[0].IsBinary || !headers[0].Value.StartsWith("Bearer ", StringComparison.Ordinal))
                throw new StockIdentityDenied("gateway_token");
            var verifier = new GatewayTokenVerifier(record, StockEnrollmentAuthority.ReadText(record.GatewayJwksPath));
            if (sandbox is null) verifier.VerifyControlPlane(headers[0].Value[7..]);
            else verifier.Verify(headers[0].Value[7..], sandbox);
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
        { throw new StockIdentityDenied("gateway_trust_invalid"); }
    }

    private StockEnrollmentAuthority LoadGatewayTrust()
    {
        var bytes = StockEnrollmentAuthority.ReadBounded(file.Path);
        using var document = JsonDocument.Parse(bytes);
        StockRequestProof.Unique(document.RootElement);
        var record = JsonSerializer.Deserialize<StockEnrollmentAuthority>(bytes,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (record is null || string.IsNullOrWhiteSpace(record.GatewayIssuer)
            || string.IsNullOrWhiteSpace(record.GatewayAudience) || !Path.IsPathFullyQualified(record.GatewayJwksPath))
            throw new StockIdentityDenied("gateway_trust_invalid");
        return record;
    }
    public override Task<MiddlewareManifest> Describe(MiddlewareDescribeRequest request, ServerCallContext context)
    {
        try
        {
            var record = Authenticate(context, null);
            if (request.Gateway?.ProtocolVersion is not { Major: 1, Minor: 0 }
                || !request.Gateway.SupportedCapabilities.Contains("openshell.supervisor-middleware.contract")
                || request.Gateway.RequiredCapabilities.Any(c => c != "openshell.supervisor-middleware.contract"))
                throw new StockIdentityDenied("protocol_unsupported");
            return Task.FromResult(new MiddlewareManifest {
                Name = "stock-memory", ExpectedAudience = record.GatewayAudience,
                Extension = new PeerMetadata { ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 0 },
                    ImplementationName = "phleet/stock-identity", ImplementationVersion = "1",
                    SupportedCapabilities = { "openshell.supervisor-middleware.contract" } },
                Bindings = { new MiddlewareBinding { Operation = SupervisorMiddlewareOperation.HttpRequest,
                    Phase = SupervisorMiddlewarePhase.PreCredentials, MaxPayloadBytes = 262144,
                    RequestTimeout = Duration.FromTimeSpan(TimeSpan.FromSeconds(2)) } } });
        }
        catch (StockIdentityDenied) { throw new RpcException(new Status(StatusCode.Unauthenticated, "identity_denied")); }
    }
    public override Task<ValidateConfigResponse> ValidateConfig(ValidateConfigRequest request, ServerCallContext context)
    {
        try
        {
            Authenticate(context, null);
            return Task.FromResult(new ValidateConfigResponse { Valid = ValidConfig(request.Config), Reason = "memory_only" });
        }
        catch (StockIdentityDenied) { throw new RpcException(new Status(StatusCode.Unauthenticated, "identity_denied")); }
    }
    private static bool ValidConfig(Struct? config) => config?.Fields.Count == 1
        && config.Fields.TryGetValue("service", out var value) && value.KindCase == Value.KindOneofCase.StringValue
        && value.StringValue == "memory";

    public override Task<HttpRequestResult> EvaluateHttpRequest(HttpRequestEvaluation request, ServerCallContext context)
    {
        try
        {
            var record = Authenticate(context, request.Context?.SandboxId ?? "");
            if (request.Context?.SandboxId != record.SandboxId || !ValidConfig(request.Config)
                || request.Phase != SupervisorMiddlewarePhase.PreCredentials || request.Target is null
                || request.Target.Scheme != "https" || request.Target.Host != record.MemoryHost || request.Target.Port != 443
                || request.Target.Path != "/mcp" || request.Target.Query != "" || request.Body.Length > 262144
                || request.Headers.Count > 128 || request.Headers.Sum(h => h.CalculateSize()) > 65536)
                throw new StockIdentityDenied("request_scope");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? session = null;
            foreach (var header in request.Headers)
            {
                if (!names.Add(header.Name) || header.Value.Any(c => c is '\r' or '\n' or '\0')
                    || header.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                    || header.Name.StartsWith("X-Fleet-", StringComparison.OrdinalIgnoreCase)
                        && !header.Name.Equals(StockRequestProof.Header, StringComparison.OrdinalIgnoreCase))
                    throw new StockIdentityDenied("identity_override");
                if (header.Name.Equals("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase)) session = header.Value;
            }
            StockMemoryRequest.Validate(request.Target.Method, request.Body.ToByteArray(), session);
            return Task.FromResult(new HttpRequestResult { Decision = Decision.Allow, HeaderMutations = {
                new HeaderMutation { Write = new WriteHeader { Name = StockRequestProof.Header,
                    Value = StockRequestProof.Issue(record, request.Target.Method, request.Body.ToByteArray(), session),
                    OnExisting = ExistingHeaderAction.Overwrite } } } });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Task.FromResult(new HttpRequestResult { Decision = Decision.Deny, ReasonCode = "identity_denied" }); }
    }
}
