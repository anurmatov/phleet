using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Fleet.Identity.Services;
using Fleet.Orchestrator.Services;
using Fleet.Shared.Security;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Openshell.Datamodel.V1;
using Openshell.Middleware.V1;
using Openshell.V1;

namespace Fleet.Identity.Tests;

internal sealed class BootstrapIdentityFixture : IAsyncDisposable
{
    private readonly IdentityFixture _identity = new();
    private WebApplication _receiver = null!, _ingress = null!;
    private HttpClient _client = null!;
    private OpenShellGatewayClient _gateway = null!;
    private OpenShellBootstrapTrust _trust = null!;
    private readonly CurrentGateway _wire = new();
    private bool _certificate = true;
    public int Accepted;
    public int NormalMemoryAdmitted => _identity.ReceiverAdmitted;
    public int CurrentSupervisorReads => _wire.Reads;
    public async Task StartAsync()
    {
        _identity.Options.BootstrapReceiver = "https://bootstrap.example.invalid/identity/ready";
        await _identity.StartAsync();
        var spec = new SandboxSpec { Providers = { "provider1" } };
        var plan = new OpenShellLaunchPlan("gateway1", "workspace1", Convert.ToBase64String(spec.ToByteArray()), new string('a',64),
            new string('a',64), new string('a',64), JsonSerializer.Serialize(new[] { new OpenShellExpectedProvider("provider1", "provider-id1",1,2,3,new string('a',64)) }));
        var scope = new OpenShellPreparationScope(1, "start", null, plan.Hash, DateTime.UtcNow.AddSeconds(50), plan);
        var recorded = OpenShellSupervisorProof.Verify(CurrentGateway.Target(), "provider1", CurrentGateway.Status(), DateTime.UtcNow);
        static string Quote(string s) => s.Replace("'", "''");
        await _identity.Sql($"""
            ALTER TABLE agent_open_shell_launches ADD COLUMN Workspace TEXT;
            ALTER TABLE agent_open_shell_launches ADD COLUMN PreparationJson TEXT;
            ALTER TABLE agent_open_shell_launches ADD COLUMN SecurityReadyJson TEXT;
            ALTER TABLE agent_open_shell_launches ADD COLUMN PreparationDeadline TEXT;
            ALTER TABLE agent_open_shell_launches ADD COLUMN CreateAttemptedAt TEXT;
            ALTER TABLE agent_open_shell_launches ADD COLUMN StopAttemptedAt TEXT;
            CREATE TABLE agent_open_shell_leases (AgentId INTEGER PRIMARY KEY,Owner TEXT,ExpiresAt TEXT);
            UPDATE agent_open_shell_launches SET Phase='security_ready',Workspace='workspace1',
              PreparationJson='{Quote(JsonSerializer.Serialize(scope))}', SecurityReadyJson='{Quote(JsonSerializer.Serialize(new[] {recorded}))}',
              PreparationDeadline='{scope.Deadline:yyyy-MM-dd HH:mm:ss.fffffff}',CreateAttemptedAt='{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffffff}';
            INSERT INTO agent_open_shell_leases VALUES(1,'{_identity.Identity.LaunchId:D}','{scope.Deadline:yyyy-MM-dd HH:mm:ss.fffffff}');
            """);
        var options = new OpenShellBootstrapReceiverOptions { Port=3601, Issuer=_identity.Options.Issuer, KeyId=_identity.Options.KeyId,
            AssertionPublicKeyPath=Path.Combine(_identity.DirectoryPath,"public.pem"),IngressCaPath=Path.Combine(_identity.DirectoryPath,"ca.pem"),IngressDnsName="ingress.example.invalid" };
        _trust = new(options);
        _gateway = new(new("https://gateway.example.invalid"),"gateway1","workspace1","owned-token",_wire);
        var receiver = new OpenShellBootstrapReceiver(_trust, new(_identity.Create, _gateway));
        var builder = Builder(); _receiver=builder.Build();
        _receiver.Run(async context => {
            context.Request.Scheme="https"; context.Connection.LocalPort=3601;
            context.Connection.ClientCertificate=_certificate ? _identity.OwnedIngressCertificate : null;
            context.Features.Get<IHttpRequestFeature>()!.RawTarget=context.Request.Path+context.Request.QueryString;
            await receiver.HandleAsync(context); if(context.Response.StatusCode==204) Accepted++;
        });
        await _receiver.StartAsync();
        var ingress = new BootstrapIdentityIngress(_identity.Options,
            new FleetRequestAssertionVerifier(_identity.AssertionKey.ExportSubjectPublicKeyInfo(),_identity.Options.Issuer,"orchestrator:/identity/ready",_identity.Options.KeyId),
            new ReplayStore(_identity.Store),_receiver.GetTestClient());
        builder=Builder(); _ingress=builder.Build();
        _ingress.Run(async context => {
            context.Features.Get<IHttpRequestFeature>()!.RawTarget=context.Request.Path+context.Request.QueryString;
            await ingress.HandleAsync(context);
        }); await _ingress.StartAsync();
        _client=_ingress.GetTestClient(); _client.BaseAddress=new($"https://{_identity.Options.IngressHost}:{_identity.Options.IngressPort}");
    }
    public Task<HttpRequestResult> ProofAsync()
    {
        var e=_identity.Evaluation(""); e.Target.Path="/identity/ready"; e.Headers.Clear();
        e.Config.Fields["service"]=Value.ForString("orchestrator"); return _identity.Evaluate(e);
    }
    public async Task AssertMemoryDeniedAsync(string proof)
    {
        using var ingress = await _identity.Send(IdentityFixture.Initialize, assertion: proof);
        Assert.False(ingress.IsSuccessStatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(IdentityFixture.Initialize, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Fleet-Assertion", proof);
        using var receiver = await _identity.ReceiverClient.SendAsync(request);
        Assert.False(receiver.IsSuccessStatusCode); Assert.Equal(0, _identity.ReceiverAdmitted);
    }
    public Task<HttpResponseMessage> SendAsync(string proof)
    {
        var request=new HttpRequestMessage(HttpMethod.Post,"/identity/ready") {Content=new ByteArrayContent([])};
        request.Headers.Add("X-Fleet-Assertion",proof); return _client.SendAsync(request);
    }
    public async Task FaultAsync(string fault)
    {
        _wire.Fault=fault;
        if(fault=="phase") await _identity.Sql("UPDATE agent_open_shell_launches SET Phase='created'");
        if(fault=="revoked") await _identity.Sql("UPDATE agent_open_shell_launches SET RevokedAt='2000-01-01'");
        if(fault=="lease") await _identity.Sql("UPDATE agent_open_shell_leases SET ExpiresAt='2000-01-01'");
        if(fault=="deadline") await _identity.Sql("UPDATE agent_open_shell_launches SET PreparationDeadline='2000-01-01'");
        if(fault=="certificate") _certificate=false;
    }
    private static WebApplicationBuilder Builder() {var b=WebApplication.CreateBuilder();b.WebHost.UseTestServer();b.Logging.ClearProviders();return b;}
    public async ValueTask DisposeAsync()
    {
        if(_ingress is not null) await _ingress.DisposeAsync();if(_receiver is not null) await _receiver.DisposeAsync();
        _gateway?.Dispose();_trust?.Dispose();await _identity.DisposeAsync();
    }
    private sealed class CurrentGateway : HttpMessageHandler
    {
        public string Fault=""; public int Reads;
        public static OpenShellSandboxSnapshot Target()=>new("gateway1","workspace1","sandbox1","name1",SandboxPhase.Ready,"process1",1,new Dictionary<string,string>()){ResourceVersion=1};
        public static ProviderReadinessStatus Status()=>new()
        {
            State=ProviderReadinessState.Ready,Receipt=new(){Provider="provider1",Workspace="workspace1",Desired=new(){SandboxId="sandbox1",Sandbox="name1",ProviderId="provider-id1",ProviderResourceVersion=1,ProviderEnvRevision=2,ConfigRevision=3,PolicyHash=new string('a',64),AttachmentEpoch="epoch1"}},
            Observed=new(){SessionId="session1",Sequence=1,AttachmentEpoch="epoch1",ProviderEnvRevision=2,ConfigRevision=3,PolicyHash=new string('a',64),ProcessInstanceId="process1",CredentialsInstalled=true,PolicyActive=true,LaunchEnvironmentInstalled=true},
            NetworkInstanceId="network1",ObservedTime=Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-1)),EvaluatedTime=Timestamp.FromDateTime(DateTime.UtcNow.AddMilliseconds(-1)),
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(Fault=="gateway") throw new HttpRequestException("owned gateway unavailable");
            var sandbox=new Sandbox{Metadata=new ObjectMeta{Id="sandbox1",Name="name1",Workspace="workspace1",ResourceVersion=1},Status=new SandboxStatus{Phase=SandboxPhase.Ready,MainProcessInstanceId="process1",CurrentPolicyVersion=1}};
            IMessage response;
            switch(request.RequestUri!.Segments.Last())
            {
                case "ListSandboxes":response=new ListSandboxesResponse{Sandboxes={sandbox}};break;
                case "GetSandbox":response=new SandboxResponse{Sandbox=sandbox};break;
                case "GetSandboxProviderStatus":
                    Reads++;var status=Status();if(Fault=="session") status.Observed.SessionId="replacement";
                    if(Fault=="desired"){status.Receipt.Desired.ProviderEnvRevision++;status.Observed.ProviderEnvRevision++;}
                    response=new GetSandboxProviderStatusResponse{Status=status};break;
                default:throw new Exception("unexpected actual RPC");
            }
            var data=response.ToByteArray();var frame=new byte[data.Length+5];BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1,4),data.Length);data.CopyTo(frame,5);
            var result=new HttpResponseMessage(HttpStatusCode.OK){Version=new Version(2,0),Content=new ByteArrayContent(frame)};
            result.Content.Headers.ContentType=new("application/grpc");result.TrailingHeaders.Add("grpc-status","0");return Task.FromResult(result);
        }
    }
}
