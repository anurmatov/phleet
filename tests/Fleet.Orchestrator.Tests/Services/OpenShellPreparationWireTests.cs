extern alias FleetAgent;
using System.Buffers.Binary;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Guard = FleetAgent::Fleet.Agent.Services.OpenShellStartupGuard;
using System.Net;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Openshell.Datamodel.V1;
using Openshell.V1;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellPreparationWireTests
{
    [Theory]
    [InlineData("ready")]
    [InlineData("replaced")]
    [InlineData("process")]
    [InlineData("session")]
    [InlineData("persisted")]
    [InlineData("epoch")]
    [InlineData("credentials")]
    [InlineData("unavailable")]
    public async Task ActualGeneratedCurrentReadiness_ReadsExactIdAroundNameKeyedStatus(string fault)
    {
        var wire = new Wire(fault);
        using var client = Client(wire);
        if (fault == "ready")
            Assert.Equal("session1", (await client.ReadCurrentSupervisorAsync(OpenShellPreparationTests.Target(), "provider1")).SessionId);
        else await Assert.ThrowsAnyAsync<Exception>(() => client.ReadCurrentSupervisorAsync(OpenShellPreparationTests.Target(), "provider1"));
        Assert.Equal(1, wire.StatusReads); Assert.Equal(0, wire.Mutations);
    }
    [Fact]
    public async Task ActualCreate_UsesGeneratedRequestAndOneImmutableRequestId()
    {
        var wire = new Wire("ready");
        using var owned = new OpenShellBootstrapArtifactTests.OwnedEngine();
        using var engine = new HttpClient(owned) { BaseAddress = new("http://owned-engine.invalid") };
        var artifacts = new OpenShellBootstrapArtifacts(engine, owned.Custody);
        using var client = new OpenShellGatewayClient(new("https://gateway.example.invalid"), "gateway1", "workspace1", "owned-token", wire, artifacts);
        var launch = Guid.NewGuid(); var epoch = Guid.NewGuid(); var request = Guid.NewGuid();
        var created = await client.CreateAsync(OpenShellPolicyCompilerTests.Plan(), launch, epoch, request, DateTime.UtcNow.AddSeconds(50));
        Assert.Equal("sandbox1", created.Id); Assert.Equal(1, wire.Mutations);
        Assert.Equal(request.ToString("D"), wire.Create!.RequestId);
        Assert.Equal(launch.ToString("D"), wire.Create.Labels["phleet-launch"]);
        Assert.Equal(epoch.ToString("D"), wire.Create.Labels["phleet-epoch"]);
        Assert.Equal("workspace1", wire.Create.WorkspaceScope.Workspace);
        Assert.Equal("--OpenShell:Bootstrap=true", wire.Create.Spec.Command[2]);
        Assert.Equal(1, owned.Archives); Assert.Equal(0, owned.Starts);
        Assert.Single(wire.Create.ServiceExposures);
        Assert.Equal(ServiceAuthorizationMode.BearerPassthrough, wire.Create.ServiceExposures[0].AuthorizationMode);
        Assert.Contains(OpenShellBootstrapArtifacts.VolumeName(launch, epoch), wire.Create.Spec.Template.DriverConfig.ToString());
        // The bytes consumed by the actual guard came through Docker's archive request,
        // not an injected file-reader function or a baked fixture capability.
        var configuration = new ConfigurationBuilder().AddCommandLine(wire.Create.Spec.Command.Skip(2).ToArray()).Build();
        var guard = new Guard(configuration, owned.MountRoot);
        Assert.Equal(launch, guard.LaunchId); Assert.Equal(epoch, guard.IdentityEpoch);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + artifacts.ReadCapability(OpenShellPolicyCompilerTests.Plan().Hash, launch, epoch);
        var ingress = new OwnedBootstrapIngress(); using var outside = new HttpClient(ingress);
        await guard.ProbeAsync(context, outside);
        Assert.Equal(204, context.Response.StatusCode); Assert.Equal(1, ingress.Requests);
    }
    [Theory]
    [InlineData("service_ready")]
    [InlineData("service_id")]
    [InlineData("service_name")]
    [InlineData("service_workspace")]
    [InlineData("service_port")]
    [InlineData("service_auth")]
    [InlineData("service_url")]
    [InlineData("service_replaced")]
    public async Task BootstrapControlEndpoint_IsExactCurrentIdAndPinnedOriginBeforeDisclosure(string fault)
    {
        var wire = new Wire(fault);
        using var client = new OpenShellGatewayClient(new("https://gateway.example.invalid"), "gateway1", "workspace1", "owned-token", wire,
            serviceRoutingOrigin: new("https://service.example.invalid"));
        if (fault == "service_ready")
            Assert.Equal("https://workspace1--name1--bootstrap.service.example.invalid/", (await client.ReadBootstrapEndpointAsync(OpenShellPreparationTests.Target())).AbsoluteUri);
        else await Assert.ThrowsAnyAsync<Exception>(() => client.ReadBootstrapEndpointAsync(OpenShellPreparationTests.Target()));
        Assert.Equal(0, wire.Mutations);
    }

    [Fact]
    public async Task CreateWithoutQualifiedCapabilityDelivery_SendsNoMutation()
    {
        var wire = new Wire("ready"); using var client = Client(wire);
        await Assert.ThrowsAsync<OpenShellStateConflictException>(() => client.CreateAsync(OpenShellPolicyCompilerTests.Plan(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddSeconds(50)));
        Assert.Equal(0, wire.Mutations);
    }

    private sealed class OwnedBootstrapIngress : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++; Assert.Equal("/identity/ready", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("X-Fleet-Assertion"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
    private static OpenShellGatewayClient Client(Wire wire) => new(new("https://gateway.example.invalid"), "gateway1", "workspace1", "owned-token", wire);
    private sealed class Wire(string fault) : HttpMessageHandler
    {
        public int StatusReads, Mutations, ServiceReads;
        public CreateSandboxRequest? Create;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer owned-token", request.Headers.Authorization!.ToString());
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(bytes.Length - 5, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)));
            var method = request.RequestUri!.Segments.Last();
            var sandbox = new Sandbox
            {
                Metadata = new ObjectMeta { Id = (fault == "replaced" && StatusReads > 0 || fault == "service_replaced" && ServiceReads > 0) ? "replacement1" : "sandbox1",
                    Name = Create?.Name ?? "name1", Workspace = "workspace1", ResourceVersion = 1 },
                Status = new SandboxStatus { Phase = SandboxPhase.Ready, MainProcessInstanceId = "process1", CurrentPolicyVersion = 1 },
            };
            IMessage response;
            switch (method)
            {
                case "ListSandboxes": response = new ListSandboxesResponse { Sandboxes = { sandbox } }; break;
                case "GetSandbox": response = new SandboxResponse { Sandbox = sandbox }; break;
                case "GetSandboxProviderStatus":
                    var input = GetSandboxProviderStatusRequest.Parser.ParseFrom(bytes, 5, bytes.Length - 5);
                    Assert.Equal("name1", input.Sandbox); Assert.Equal("workspace1", input.WorkspaceScope.Workspace);
                    Assert.Equal("provider1", input.Provider); Assert.Empty(input.ReceiptId); StatusReads++;
                    if (fault == "unavailable") throw new HttpRequestException("owned unavailable");
                    var status = OpenShellPreparationTests.Ready();
                    if (fault == "process") status.Observed.ProcessInstanceId = "old";
                    if (fault == "session") status.Observed.SessionId = "";
                    if (fault == "persisted") status.State = ProviderReadinessState.Persisted;
                    if (fault == "epoch") status.Observed.AttachmentEpoch = "old";
                    if (fault == "credentials") status.Observed.CredentialsInstalled = false;
                    response = new GetSandboxProviderStatusResponse { Status = status }; break;
                case "GetService":
                    var get = GetServiceRequest.Parser.ParseFrom(bytes, 5, bytes.Length - 5);
                    Assert.Equal("name1", get.Sandbox); Assert.Equal("bootstrap", get.Name); Assert.Equal("workspace1", get.WorkspaceScope.Workspace);
                    ServiceReads++;
                    response = new ServiceEndpointResponse
                    {
                        Endpoint = new() { SandboxId = fault == "service_id" ? "foreign1" : "sandbox1", Sandbox = "name1",
                            Metadata = new() { Workspace = fault == "service_workspace" ? "other" : "workspace1" },
                            Name = fault == "service_name" ? "other" : "bootstrap", TargetPort = fault == "service_port" ? 8091u : 8080u,
                            AuthorizationMode = fault == "service_auth" ? ServiceAuthorizationMode.Strip : ServiceAuthorizationMode.BearerPassthrough },
                        Url = fault == "service_url" ? "https://foreign.example.invalid/" : "https://workspace1--name1--bootstrap.service.example.invalid/",
                    }; break;
                case "Health": response = new HealthResponse { Capabilities = { "sandbox-mutation-preconditions-v1" } }; break;
                case "CreateSandbox":
                    Create = CreateSandboxRequest.Parser.ParseFrom(bytes, 5, bytes.Length - 5); Mutations++;
                    sandbox.Metadata.Name = Create.Name;
                    response = new SandboxResponse { Sandbox = sandbox }; break;
                default: throw new Exception("Unexpected actual RPC " + method);
            }
            var message = response.ToByteArray(); var frame = new byte[message.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), message.Length); message.CopyTo(frame, 5);
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Version = new Version(2, 0), Content = new ByteArrayContent(frame) };
            result.Content.Headers.ContentType = new("application/grpc"); result.TrailingHeaders.Add("grpc-status", "0"); return result;
        }
    }
}
