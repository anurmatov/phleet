using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Openshell.Datamodel.V1;
using Openshell.V1;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public class OpenShellGatewayClientTests
{
    [Fact]
    public async Task ActualGeneratedClient_ConsumesAllPages_ThenVerifiesExactId()
    {
        var handler = new WireHandler();
        using var client = Client(handler);
        var result = await client.ReadExactAsync("id2");
        Assert.Equal("id2", result!.Id);
        Assert.Equal("supervisor2", result.SupervisorInstanceId);
        Assert.Equal(3, handler.Requests);
        Assert.Equal("name2", handler.GetName);
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("readerror")]
    [InlineData("replaced")]
    public async Task IncompleteOrChangedObservation_NeverReturnsAbsenceOrReady(string fault)
    {
        using var client = Client(new WireHandler(fault));
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReadExactAsync("id2"));
    }

    [Fact]
    public async Task MissingId_RequiresExhaustedInventory_NotGetByDisplayName()
    {
        var handler = new WireHandler();
        using var client = Client(handler);
        Assert.Null(await client.ReadExactAsync("not-present"));
        Assert.Equal(2, handler.Requests);
        Assert.Null(handler.GetName);
    }

    [Fact]
    public async Task SnapshotLabels_AreImmutableCopies()
    {
        using var client = Client(new WireHandler());
        var snapshot = Assert.Single(await client.InventoryAsync(), x => x.Id == "id2");
        Assert.Equal("launch2", snapshot.Labels["phleet.io/launch-id"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)snapshot.Labels).Add("override", "value"));
    }

    [Fact]
    public void PinnedMutationProtocol_PreservesConditionalFieldPresenceAndTags()
    {
        Assert.Equal(5, DeleteSandboxRequest.Descriptor.FindFieldByName("expected_sandbox_id").FieldNumber);
        Assert.Equal(6, DeleteSandboxRequest.Descriptor.FindFieldByName("expected_resource_version").FieldNumber);
        Assert.Equal(4, StopSandboxRequest.Descriptor.FindFieldByName("expected_sandbox_id").FieldNumber);
        Assert.Equal(5, StopSandboxRequest.Descriptor.FindFieldByName("expected_resource_version").FieldNumber);
        Assert.True(DeleteSandboxRequest.Descriptor.FindFieldByName("expected_sandbox_id").HasPresence);
        Assert.True(StopSandboxRequest.Descriptor.FindFieldByName("expected_resource_version").HasPresence);
        Assert.Equal(3, HealthResponse.Descriptor.FindFieldByName("capabilities").FieldNumber);
    }

    [Fact]
    public async Task DefaultWorkspace_UsesPinnedEmptyMetadataNormalization()
    {
        using var client = new OpenShellGatewayClient(new Uri("https://gateway.example.invalid"),
            "gateway1", "default", "owned-fixture-token", new WireHandler("empty-workspace", "default"));
        Assert.Equal("default", (await client.ReadExactAsync("id2"))!.Workspace);
    }

    [Fact]
    public async Task Cancellation_BoundsTheActualGeneratedRpc()
    {
        using var client = Client(new WireHandler("hang"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InventoryAsync(deadline.Token));
    }

    [Fact]
    public void VendoredProtocol_HasExactRecordedSourceHashes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent;
        Assert.NotNull(root);
        var dir = Path.Combine(root.FullName, "src/Fleet.Orchestrator/OpenShell");
        var manifest = File.ReadAllLines(Path.Combine(dir, "UPSTREAM"));
        var hashes = manifest.Where(l => l.Length > 66 && l[64..66] == "  ").ToArray();
        Assert.Equal(4, hashes.Length);
        foreach (var line in hashes)
            Assert.Equal(line[..64], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(dir, line[66..])))));
    }

    private static OpenShellGatewayClient Client(WireHandler handler) =>
        new(new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "owned-fixture-token", handler);

    private sealed class WireHandler(string? fault = null, string workspace = "workspace1") : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? GetName { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (fault == "hang") await Task.Delay(TimeSpan.FromSeconds(30), ct);
            Assert.Equal("Bearer owned-fixture-token", request.Headers.Authorization!.ToString());
            var frame = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(0, frame[0]);
            Assert.Equal(frame.Length - 5, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1, 4)));
            IMessage message;
            if (request.RequestUri!.AbsolutePath.EndsWith("/ListSandboxes", StringComparison.Ordinal))
            {
                var input = ListSandboxesRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                Assert.Equal(workspace, input.WorkspaceScope.Workspace);
                Assert.Equal("", input.LabelSelector);
                Assert.Equal(100, input.PageSize);
                var second = input.PageToken == "next";
                var response = new ListSandboxesResponse { NextPageToken = second && fault != "repeat" ? "" : "next" };
                response.Sandboxes.Add(Sandbox(second ? (fault == "duplicate" ? "id1" : "id2") : "id1"));
                message = response;
            }
            else
            {
                Assert.EndsWith("/openshell.v1.OpenShell/GetSandbox", request.RequestUri.AbsolutePath);
                var input = GetSandboxRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                GetName = input.Name;
                message = new SandboxResponse { Sandbox = Sandbox(fault == "replaced" ? "new-id" : "id2") };
            }
            var payload = message.ToByteArray();
            var bytes = new byte[payload.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1, 4), payload.Length);
            payload.CopyTo(bytes, 5);
            var output = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(bytes) };
            output.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            output.TrailingHeaders.Add("grpc-status", fault == "readerror" ? "14" : "0");
            return output;
        }

        private Sandbox Sandbox(string id) => new()
        {
            Metadata = new ObjectMeta
            {
                Id = id, Name = id == "id1" ? "name1" : "name2",
                Workspace = fault == "foreign" ? "foreign" : fault == "empty-workspace" ? "" : workspace,
                Labels = { { "phleet.io/launch-id", "launch2" } },
            },
            Status = new SandboxStatus { Phase = SandboxPhase.Ready, MainProcessInstanceId = "supervisor2", CurrentPolicyVersion = 1 },
        };
    }
}
