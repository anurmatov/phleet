using System.Buffers.Binary;
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
        var wire = new Wire("ready"); using var client = Client(wire);
        var launch = Guid.NewGuid(); var epoch = Guid.NewGuid(); var request = Guid.NewGuid();
        var created = await client.CreateAsync(OpenShellPolicyCompilerTests.Plan(), launch, epoch, request, DateTime.UtcNow.AddSeconds(50));
        Assert.Equal("sandbox1", created.Id); Assert.Equal(1, wire.Mutations);
        Assert.Equal(request.ToString("D"), wire.Create!.RequestId);
        Assert.Equal(launch.ToString("D"), wire.Create.Labels["phleet-launch"]);
        Assert.Equal(epoch.ToString("D"), wire.Create.Labels["phleet-epoch"]);
        Assert.Equal("workspace1", wire.Create.WorkspaceScope.Workspace);
        Assert.Equal("--OpenShell:Bootstrap=true", wire.Create.Spec.Command[2]);
    }
    private static OpenShellGatewayClient Client(Wire wire) => new(new("https://gateway.example.invalid"), "gateway1", "workspace1", "owned-token", wire);
    private sealed class Wire(string fault) : HttpMessageHandler
    {
        public int StatusReads, Mutations;
        public CreateSandboxRequest? Create;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer owned-token", request.Headers.Authorization!.ToString());
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(bytes.Length - 5, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)));
            var method = request.RequestUri!.Segments.Last();
            var sandbox = new Sandbox
            {
                Metadata = new ObjectMeta { Id = fault == "replaced" && StatusReads > 0 ? "replacement1" : "sandbox1",
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
