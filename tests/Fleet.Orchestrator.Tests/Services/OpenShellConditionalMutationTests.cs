using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Grpc.Core;
using Openshell.Datamodel.V1;
using Openshell.V1;

namespace Fleet.Orchestrator.Tests.Services;

public class OpenShellConditionalMutationTests
{
    private static OpenShellSandboxSnapshot Target() => new("gateway1", "workspace1", "sandbox1", "name1",
        SandboxPhase.Ready, "session1", 7, new Dictionary<string, string>()) { ResourceVersion = 12 };

    [Theory]
    [InlineData("old")]
    [InlineData("unknown")]
    [InlineData("health-error")]
    [InlineData("health-hang")]
    public async Task MissingUnreadableOrUnknownCapability_SendsZeroMutations(string fault)
    {
        var wire = new MutationWire(fault);
        using var client = Client(wire);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<Exception>(() => client.StopExactAsync(Target(), Guid.NewGuid(), timeout.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => client.DeleteExactAsync(Target(), Guid.NewGuid(), true, timeout.Token));
        Assert.Equal(0, wire.Mutations);
    }

    [Fact]
    public async Task ActualGeneratedMutations_PreserveExactIdVersionWorkspaceAndRequestId()
    {
        var wire = new MutationWire();
        using var client = Client(wire);
        var stopId = Guid.NewGuid();
        var deleteId = Guid.NewGuid();
        var stopped = await client.StopExactAsync(Target(), stopId);
        var deleted = await client.DeleteExactAsync(Target(), deleteId, true);
        Assert.Equal("sandbox1", stopped.Id);
        Assert.Equal(DeletionOutcome.Accepted, deleted.Outcome);
        Assert.Equal("sandbox1", deleted.SandboxId);
        Assert.Equal(stopId.ToString("D"), wire.Stop!.RequestId);
        Assert.Equal(deleteId.ToString("D"), wire.Delete!.RequestId);
        Assert.Equal("sandbox1", wire.Stop.ExpectedSandboxId);
        Assert.Equal("sandbox1", wire.Delete.ExpectedSandboxId);
        Assert.True(wire.Stop.HasExpectedSandboxId && wire.Stop.HasExpectedResourceVersion);
        Assert.True(wire.Delete.HasExpectedSandboxId && wire.Delete.HasExpectedResourceVersion);
        Assert.Equal(12ul, wire.Stop.ExpectedResourceVersion);
        Assert.Equal(12ul, wire.Delete.ExpectedResourceVersion);
        Assert.Equal("workspace1", wire.Stop.WorkspaceScope.Workspace);
        Assert.Equal("workspace1", wire.Delete.WorkspaceScope.Workspace);
        Assert.Equal("name1", wire.Stop.Name);
        Assert.Equal("name1", wire.Delete.Name);
        Assert.True(wire.Delete.AllowMissing);
        Assert.Equal(new[] { "Health", "StopSandbox", "Health", "DeleteSandbox" }, wire.Calls);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("lost")]
    [InlineData("replacement")]
    [InlineData("wrong-name")]
    [InlineData("wrong-workspace")]
    public async Task RefusalOrAmbiguousReply_NeverRetriesOrUsesUnguardedFallback(string fault)
    {
        var wire = new MutationWire(fault);
        using var client = Client(wire);
        if (fault is "abort" or "lost")
            Assert.Equal(fault == "abort" ? StatusCode.Aborted : StatusCode.Unavailable,
                (await Assert.ThrowsAsync<RpcException>(() => client.StopExactAsync(Target(), Guid.NewGuid()))).StatusCode);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.StopExactAsync(Target(), Guid.NewGuid()));
        Assert.Equal(1, wire.Mutations);
        Assert.NotNull(wire.Stop);
        Assert.True(wire.Stop.HasExpectedSandboxId);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("lost")]
    [InlineData("replacement")]
    [InlineData("unspecified")]
    [InlineData("unknown-outcome")]
    public async Task InvalidDeleteOutcome_IsNotCleanupProofAndNeverRetried(string fault)
    {
        var wire = new MutationWire(fault);
        using var client = Client(wire);
        if (fault is "abort" or "lost")
            Assert.Equal(fault == "abort" ? StatusCode.Aborted : StatusCode.Unavailable,
                (await Assert.ThrowsAsync<RpcException>(() => client.DeleteExactAsync(Target(), Guid.NewGuid(), true))).StatusCode);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteExactAsync(Target(), Guid.NewGuid(), true));
        Assert.Equal(1, wire.Mutations);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("workspace")]
    [InlineData("version")]
    [InlineData("request")]
    public async Task InvalidBoundTarget_FailsBeforeAnyRpc(string fault)
    {
        var wire = new MutationWire();
        using var client = Client(wire);
        var target = fault switch
        {
            "gateway" => Target() with { GatewayId = "other" },
            "workspace" => Target() with { Workspace = "other" },
            "version" => Target() with { ResourceVersion = 0 },
            _ => Target(),
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.StopExactAsync(target,
            fault == "request" ? Guid.Empty : Guid.NewGuid()));
        Assert.Empty(wire.Calls);
    }

    [Fact]
    public async Task ActualObservationFeedsConditionalMutationWithoutNameOnlyFallback()
    {
        var wire = new MutationWire();
        using var client = Client(wire);
        var observed = await client.ReadExactAsync("sandbox1");
        Assert.NotNull(observed);
        Assert.Equal(12ul, observed.ResourceVersion);
        await client.StopExactAsync(observed, Guid.NewGuid());
        Assert.Equal(observed.Id, wire.Stop!.ExpectedSandboxId);
        Assert.Equal(observed.ResourceVersion, wire.Stop.ExpectedResourceVersion);
        Assert.Equal(new[] { "ListSandboxes", "GetSandbox", "Health", "StopSandbox" }, wire.Calls);
    }

    [Fact]
    public async Task CapabilityIsReadAgainBeforeEveryMutation()
    {
        var wire = new MutationWire();
        using var client = Client(wire);
        await client.StopExactAsync(Target(), Guid.NewGuid());
        wire.Supported = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteExactAsync(Target(), Guid.NewGuid(), true));
        Assert.Equal(1, wire.Mutations);
        Assert.Null(wire.Delete);
        Assert.Equal(new[] { "Health", "StopSandbox", "Health" }, wire.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterSendingMutation_IsBoundedAndNotRetried(bool delete)
    {
        var wire = new MutationWire("mutation-hang");
        using var client = Client(wire);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (delete) await client.DeleteExactAsync(Target(), Guid.NewGuid(), false, timeout.Token);
            else await client.StopExactAsync(Target(), Guid.NewGuid(), timeout.Token);
        });
        Assert.Equal(1, wire.Mutations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlreadyAbsent_IsAcceptedOnlyWithAllowMissingAndIsNotRuntimeEvidence(bool allowMissing)
    {
        var wire = new MutationWire("absent");
        using var client = Client(wire);
        if (allowMissing)
        {
            var result = await client.DeleteExactAsync(Target(), Guid.NewGuid(), true);
            Assert.Equal(DeletionOutcome.AlreadyAbsent, result.Outcome);
            Assert.Equal("", result.SandboxId);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteExactAsync(Target(), Guid.NewGuid(), false));
        Assert.Equal(1, wire.Mutations);
    }

    private static OpenShellGatewayClient Client(MutationWire wire) => new(
        new Uri("https://gateway.example.invalid"), "gateway1", "workspace1", "synthetic-token", wire);

    internal sealed class MutationWire(string? fault = null) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public int Mutations { get; private set; }
        private bool _stopped;
        public bool Supported { get; set; } = true;
        public Func<Task>? BeforeMutation { get; set; }
        public StopSandboxRequest? Stop { get; private set; }
        public DeleteSandboxRequest? Delete { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments.Last();
            Calls.Add(method);
            Assert.Equal("Bearer synthetic-token", request.Headers.Authorization!.ToString());
            var frame = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.Equal(0, frame[0]);
            Assert.Equal(frame.Length - 5, BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1, 4)));
            IMessage reply;
            if (method == "Health")
            {
                HealthRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                if (fault == "health-hang") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                var health = new HealthResponse();
                if (fault != "old" && Supported) health.Capabilities.Add(fault == "unknown"
                    ? "sandbox-mutation-preconditions-v2" : "sandbox-mutation-preconditions-v1");
                reply = health;
            }
            else if (method is "ListSandboxes" or "GetSandbox")
            {
                var sandbox = new Sandbox
                {
                    Metadata = new ObjectMeta { Id = "sandbox1", Name = "name1", Workspace = "workspace1", ResourceVersion = _stopped ? 13ul : 12ul },
                    Status = new SandboxStatus { Phase = _stopped ? SandboxPhase.Stopped : SandboxPhase.Ready },
                };
                if (method == "ListSandboxes")
                {
                    ListSandboxesRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                    var list = new ListSandboxesResponse();
                    list.Sandboxes.Add(sandbox);
                    reply = list;
                }
                else
                {
                    var input = GetSandboxRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                    Assert.Equal("name1", input.Name);
                    reply = new SandboxResponse { Sandbox = sandbox };
                }
            }
            else
            {
                if (BeforeMutation is not null) await BeforeMutation();
                Mutations++;
                if (fault == "mutation-hang") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (method == "StopSandbox")
                {
                    Stop = StopSandboxRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                    if (fault != "abort") _stopped = true;
                    reply = new SandboxResponse { Sandbox = new Sandbox
                    {
                        Metadata = new ObjectMeta
                        {
                            Id = fault == "replacement" ? "replacement" : "sandbox1",
                            Name = fault == "wrong-name" ? "wrong" : "name1",
                            Workspace = fault == "wrong-workspace" ? "other" : "workspace1", ResourceVersion = 13,
                        },
                        Status = new SandboxStatus { Phase = SandboxPhase.Stopped },
                    }};
                }
                else
                {
                    Assert.Equal("DeleteSandbox", method);
                    Delete = DeleteSandboxRequest.Parser.ParseFrom(frame, 5, frame.Length - 5);
                    reply = new DeleteSandboxResponse
                    {
                        SandboxId = fault == "replacement" ? "replacement" : fault == "absent" ? "" : "sandbox1",
                        Outcome = fault == "absent" ? DeletionOutcome.AlreadyAbsent
                            : fault == "unspecified" ? DeletionOutcome.Unspecified
                            : fault == "unknown-outcome" ? (DeletionOutcome)99 : DeletionOutcome.Accepted,
                    };
                }
            }
            var payload = reply.ToByteArray();
            var output = new byte[5 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(1, 4), payload.Length);
            payload.CopyTo(output, 5);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Version = HttpVersion.Version20, Content = new ByteArrayContent(output) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", method is "StopSandbox" or "DeleteSandbox" && fault == "abort" ? "10"
                : (method is "StopSandbox" or "DeleteSandbox" && fault == "lost") || fault == "health-error" ? "14" : "0");
            return response;
        }
    }
}
