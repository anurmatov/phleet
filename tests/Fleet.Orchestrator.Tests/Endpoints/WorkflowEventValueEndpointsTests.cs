using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.Failure.V1;
using Temporalio.Api.History.V1;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Exceptions;

namespace Fleet.Orchestrator.Tests.Endpoints;

public sealed class WorkflowEventValueEndpointsTests
{
    private const int Limit = 1048576;
    private const string Sentinel = "synthetic-payload-must-not-appear-in-errors";
    
    [Theory]
    [InlineData("json/plain")]
    [InlineData("json/protobuf")]
    [InlineData("text/plain")]
    public async Task UnicodeAndOrderedPayloads_AreExactRawText(string encoding)
    {
        var text = string.Concat(Enumerable.Repeat("𐐀e\u0301\n\\\"", 25000));
        var quoted = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        var malformed = "{not json\n" + text;
        await using var host = await Host.Start([Started(1, P(encoding, text), P("json/plain", quoted), P("json/plain", malformed))]);
        var value = await host.Value();
        Assert.Equal("complete", value.GetProperty("status").GetString());
        Assert.Equal([text, quoted, malformed], value.GetProperty("values").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
        Assert.Equal(encoding, value.GetProperty("values")[0].GetProperty("encoding").GetString());
        Assert.Equal(1, value.GetProperty("sourceEventId").GetInt64());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("reason").ValueKind);
        Assert.Equal(Limit, value.GetProperty("limitBytes").GetInt32());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("failure")]
    public async Task FieldMapping_AndExactScheduledInput(string field)
    {
        var events = new[] {
            Started(1, P("json/plain", "start")),
            new HistoryEvent { EventId=2, EventType=EventType.ActivityTaskScheduled,
                ActivityTaskScheduledEventAttributes=new() { Input=Ps(P("text/plain", "schedule")), ActivityType=new() {Name="Synthetic"} } },
            new HistoryEvent { EventId=3, EventType=EventType.WorkflowExecutionSignaled,
                WorkflowExecutionSignaledEventAttributes=new() {Input=Ps(P("text/plain", "signal"))} },
            new HistoryEvent {EventId=4, EventType=EventType.ActivityTaskCompleted,
                ActivityTaskCompletedEventAttributes=new() {ScheduledEventId=2, Result=Ps(P("json/plain", "activity result"))} },
            new HistoryEvent {EventId=5, EventType=EventType.WorkflowExecutionCompleted,
                WorkflowExecutionCompletedEventAttributes=new() {Result=Ps(P("text/plain", "workflow result"))} },
            Failed(6, "activity failure", activity:true), Failed(7, "workflow failure"),
            new HistoryEvent {EventId=8, EventType=EventType.WorkflowExecutionCanceled} };
        await using var h = await Host.Start(events, pageSize:1);
        foreach (var e in events)
        {
            var v = await h.Value(eventId:e.EventId, field:field);
            string? expected = (e.EventId,field) switch {
                (1,"input")=>"start", (2,"input")=>"schedule", (3,"input")=>"signal", (4,"input")=>"schedule",
                (4,"output")=>"activity result", (5,"output")=>"workflow result", (6,"failure")=>"activity failure", (7,"failure")=>"workflow failure", _=>null };
            Assert.Equal(expected is null ? "absent":"complete", v.GetProperty("status").GetString());
            if (expected is not null)
            {
                Assert.Equal(expected,v.GetProperty("values")[0].GetProperty("text").GetString());
                Assert.Equal(e.EventId==4 && field=="input"?2:e.EventId,v.GetProperty("sourceEventId").GetInt64());
            }
            else Empty(v,"absent","no_value");
        }
        Assert.All(h.Reads, r => Assert.False(r.WaitNewEvent));
    }

    [Fact]
    public async Task IdentityIsExact_AcrossRunsNamespacesAndEscapedIds()
    {
        await using var h = await Host.Start([]);
        h.Page = (ns,r) => Task.FromResult(new WorkflowHistoryEventPage([Started(1,P("text/plain", ns+"|"+r.Id+"|"+r.RunId))],null));
        foreach (var ns in new[] {"alpha","beta"})
        foreach (var run in new[] {"run-one","run-two"," padded-run "})
        foreach (var id in new[] {"same", "part/a+b%2F", " padded ", "a%25/b+?"})
        {
            var v=await h.Value(ns:ns,id:id,run:run);
            Assert.Equal(ns+"|"+id+"|"+run,v.GetProperty("values")[0].GetProperty("text").GetString());
            Assert.Equal(ns,v.GetProperty("namespace").GetString());
            Assert.Equal(id,v.GetProperty("workflowId").GetString());
            Assert.Equal(run,v.GetProperty("runId").GetString());
        }
        Assert.All(h.Reads,r=>Assert.False(string.IsNullOrWhiteSpace(r.RunId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteMarkersInNamespaceAndId_DoNotChangeLiteralId(bool upperRoute)
    {
        const string id="part/event-value/a+b%2F";
        await using var h=await Host.Start([Started(1)],namespaces:["event-value"]);
        var path=upperRoute
            ? "/API/WORKFLOWS/event-value/EVENT-VALUE/"+Uri.EscapeDataString(id)+"?runId=run-one&eventId=1&field=input"
            : Path(ns:"event-value",id:id);
        using var response=await h.Client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var value=JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(id,value.GetProperty("workflowId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?runId=r&eventId=1")]
    [InlineData("?runId=r&eventId=1&field=other")]
    [InlineData("?runId=&eventId=1&field=input")]
    [InlineData("?runId=%20&eventId=1&field=input")]
    [InlineData("?runId=r&runId=s&eventId=1&field=input")]
    [InlineData("?runId=r&eventId=0&field=input")]
    [InlineData("?runId=r&eventId=-1&field=input")]
    [InlineData("?runId=r&eventId=9223372036854775808&field=input")]
    [InlineData("?runId=r&eventId=1&eventId=2&field=input")]
    [InlineData("?runId=r&eventId=1&field=input&field=output")]
    [InlineData("?runId=r&eventId=1.0&field=input")]
    public async Task InvalidQueries_RefusedBeforeTemporal(string query)
    {
        await using var h=await Host.Start([Started(1)]);
        await h.Error("/api/workflows/alpha/event-value/wf"+query,400,"invalid_request");
        Assert.Empty(h.Reads);Assert.Equal(0,h.Connects);
    }

    [Fact]
    public async Task BlankId_RefusedBeforeTemporal()
    {
        await using var h=await Host.Start([]);
        await h.Error("/api/workflows/alpha/event-value/%20?runId=r&eventId=1&field=input",400,"invalid_request");
        Assert.Equal(0,h.Connects);
    }

    [Theory]
    [InlineData("Alpha")]
    [InlineData("missing")]
    public async Task NamespaceMustMatchOrdinally(string ns)
    {
        await using var h=await Host.Start([]);
        await h.Error(Path(ns:ns),404,"not_found"); Assert.Equal(0,h.Connects);
    }

    [Fact]
    public async Task CaseCollidingConfiguredNamespaces_NeverReadAnotherNamespacesClient()
    {
        await using var h=await Host.Start([Started(1)],namespaces:["alpha","ALPHA"]);
        await h.Error(Path(ns:"ALPHA"),503,"history_unavailable");Assert.Empty(h.Reads);
    }

    [Fact]
    public async Task LookupStopsAfterResolvedTarget_WithoutFollowingNewRun()
    {
        await using var h=await Host.Start([Started(1,P("text/plain","first")),Started(2)],pageSize:1);
        await h.Value();Assert.Single(h.Reads);
        h.Events=[new HistoryEvent {EventId=1,EventType=EventType.WorkflowExecutionContinuedAsNew,WorkflowExecutionContinuedAsNewEventAttributes=new(){NewExecutionRunId="other-run"}}];
        Empty(await h.Value(),"unsupported","event_type");Assert.Equal(2,h.Reads.Count);
        Assert.All(h.Reads,r=>Assert.Equal("run-one",r.RunId));
    }

    [Fact]
    public async Task MissingEventAndMissingRun_AreNotAbsentValues()
    {
        await using var h=await Host.Start([Started(1)]);
        await h.Error(Path(eventId:2),404,"not_found");
        h.Page=(_,_)=>throw new RpcException(RpcException.StatusCode.NotFound,Sentinel,[]);
        await h.Error(Path(run:"missing"),404,"not_found");
        Assert.All(h.Reads,r=>Assert.NotNull(r.RunId));
    }

    [Theory]
    [InlineData("empty","complete",null)]
    [InlineData("none","absent","no_value")]
    [InlineData("no_failure","absent","no_value")]
    [InlineData("empty_failure","complete",null)]
    [InlineData("binary","unsupported","encoding")]
    [InlineData("null","unsupported","encoding")]
    [InlineData("missing_encoding","unsupported","encoding")]
    [InlineData("mixed","unsupported","encoding")]
    [InlineData("bad_utf8","unsupported","invalid_utf8")]
    [InlineData("unknown_event","unsupported","event_type")]
    [InlineData("encoded_failure","unsupported","encoded_failure")]
    public async Task EmptyAbsentAndUnsupported_AreAllOrNone(string kind,string status,string? reason)
    {
        var e=kind switch {
            "empty"=>Started(1,P("text/plain","")), "none"=>Started(1),
            "no_failure"=>new HistoryEvent {EventId=1,EventType=EventType.WorkflowExecutionFailed,WorkflowExecutionFailedEventAttributes=new()},
            "empty_failure"=>Failed(1,""),
            "binary"=>Started(1,P("binary/plain",Sentinel)),"null"=>Started(1,P("binary/null","")),
            "missing_encoding"=>Started(1,new Payload {Data=ByteString.CopyFromUtf8(Sentinel)}),
            "mixed"=>Started(1,P("text/plain",Sentinel),P("gzip/plain","compressed")),
            "bad_utf8"=>Started(1,new Payload {Metadata={{"encoding",ByteString.CopyFromUtf8("json/plain")}},Data=ByteString.CopyFrom([0xc3,0x28])}),
            "unknown_event"=>new HistoryEvent {EventId=1,EventType=EventType.ActivityTaskTimedOut},
            "encoded_failure"=>Failed(1,Sentinel,encoded:true),_=>throw new ArgumentException(kind)};
        await using var h=await Host.Start([e]); var v=await h.Value(field:kind is "encoded_failure" or "no_failure" or "empty_failure"?"failure":"input");
        if(status=="complete") Assert.Equal("",v.GetProperty("values")[0].GetProperty("text").GetString());
        else Empty(v,status,reason!);
    }

    [Theory]
    [InlineData(1048576,false,false)]
    [InlineData(1048577,false,false)]
    [InlineData(1048577,true,false)]
    [InlineData(1048577,false,true)]
    [InlineData(1048577,true,true)]
    public async Task ByteLimit_HasPrecedenceBeforeEncodingAndUtf8(int size,bool unsupported,bool invalid)
    {
        var data=Enumerable.Repeat((byte)'a',size).ToArray(); if(invalid)data[0]=0xff;
        var payload=new Payload {Data=ByteString.CopyFrom(data),Metadata={{"encoding",ByteString.CopyFromUtf8(unsupported?"binary/plain":"text/plain")}}};
        await using var h=await Host.Start([Started(1,payload)]);var v=await h.Value();
        if(size==Limit) {Assert.Equal("complete",v.GetProperty("status").GetString());Assert.Equal(Limit,v.GetProperty("values")[0].GetProperty("text").GetString()!.Length);}
        else Empty(v,"too_large","value_size");
    }

    [Fact]
    public async Task AggregateSizeAndFailureMessages_UseSameLimit()
    {
        await using var h=await Host.Start([Started(1,P("binary/plain",new string('a',Limit/2)),P("text/plain",new string('b',Limit/2+1))),Failed(2,string.Empty)]);
        Empty(await h.Value(),"too_large","value_size");
        foreach(var size in new[]{Limit,Limit+1})
        {
            h.Events=[Failed(2,new string('x',size))];var v=await h.Value(eventId:2,field:"failure");
            if(size==Limit)Assert.Equal(size,v.GetProperty("values")[0].GetProperty("text").GetString()!.Length);
            else Empty(v,"too_large","value_size");
        }
        h.Events=[Failed(2,new string('x',Limit+1),encoded:true)];
        Empty(await h.Value(eventId:2,field:"failure"),"too_large","value_size");
    }

    [Theory]
    [InlineData("missing_schedule")]
    [InlineData("wrong_schedule")]
    [InlineData("bad_schedule_id")]
    [InlineData("missing_attributes")]
    [InlineData("read_denied")]
    [InlineData("transport_timeout")]
    public async Task InconsistentOrUnreadableHistory_IsSanitized503(string kind)
    {
        var completed=new HistoryEvent {EventId=3,EventType=EventType.ActivityTaskCompleted,ActivityTaskCompletedEventAttributes=new(){ScheduledEventId=kind=="bad_schedule_id"?0:1}};
        await using var h=await Host.Start(kind=="wrong_schedule"?[Started(1),completed]:[completed]);
        if(kind=="missing_attributes")h.Events=[new HistoryEvent {EventId=3,EventType=EventType.WorkflowExecutionStarted}];
        if(kind=="read_denied")h.Page=(_,_)=>throw new RpcException(RpcException.StatusCode.PermissionDenied,Sentinel,[]);
        if(kind=="transport_timeout")h.Page=(_,_)=>throw new TimeoutException(Sentinel);
        await h.Error(Path(eventId:3),503,"history_unavailable");
    }

    [Fact]
    public async Task UnconfiguredAndUnconnectedTemporal_Are503()
    {
        await using var h=await Host.Start([],configured:false);await h.Error(Path(),503,"history_unavailable");
        await using var down=await Host.Start([],connected:false);await down.Error(Path(),503,"history_unavailable");
    }

    [Fact]
    public async Task TotalDeadline_CoversClientAcquisition()
    {
        await using var h=await Host.Start([]);h.ConnectGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request=h.Client.GetAsync(Path());await h.ConnectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Clock.Expire();
        using var response=await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(503,(int)response.StatusCode);Assert.Contains("history_timeout",await response.Content.ReadAsStringAsync());
        Assert.Empty(h.Reads);h.ConnectGate.TrySetResult(h.Temporal);
    }

    [Fact]
    public async Task OneDeadline_CoversBothHistoryPasses_AndStopsFurtherPages()
    {
        await using var h=await Host.Start([]);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Page=async(_,r)=>{
            if(h.Reads.Count==1)return new WorkflowHistoryEventPage([new HistoryEvent {EventId=3,EventType=EventType.ActivityTaskCompleted,ActivityTaskCompletedEventAttributes=new(){ScheduledEventId=1}}],null);
            entered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,r.Rpc!.CancellationToken!.Value);
            return new WorkflowHistoryEventPage([],null);
        };
        var request=h.Client.GetAsync(Path(eventId:3));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,h.Clock.Timers);h.Clock.Expire();
        using var response=await request.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(503,(int)response.StatusCode);
        Assert.Contains("history_timeout",await response.Content.ReadAsStringAsync());Assert.Equal(2,h.Reads.Count);
    }

    [Fact]
    public async Task RequestAbort_CancelsRpcWithoutReadingLaterPages()
    {
        await using var h=await Host.Start([]);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Page=async(_,r)=>{entered.TrySetResult();try{await Task.Delay(Timeout.InfiniteTimeSpan,r.Rpc!.CancellationToken!.Value);}catch(OperationCanceledException){cancelled.TrySetResult();throw;}return new WorkflowHistoryEventPage([],null);};
        using var stop=new CancellationTokenSource();var request=h.Client.GetAsync(Path(),stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Single(h.Reads);
    }

    [Fact]
    public async Task AuthBoundary_IsInherited_IncludingUnauthenticatedInternalGet()
    {
        await using var h=await Host.Start([Started(1,P("text/plain","allowed"))]);
        foreach(var bearer in new string?[]{null,"invalid"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,Path());request.Headers.Add(OrchestratorAuth.EdgeHeader,"1");
            if(bearer is not null)request.Headers.Authorization=new("Bearer",bearer);
            using var response=await h.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);Assert.Empty(h.Reads);
        }
        var auth=h.App.Services.GetRequiredService<AuthService>();
        var browser=(await auth.SignInBrowserAsync(UserRecord.OwnerId,"test browser")).Value!;
        var enrollment=(await auth.IssueEnrollmentCodeAsync(UserRecord.OwnerId,"test app")).Value!;
        var device=(await auth.RegisterDeviceAsync(enrollment.EnrollmentCode,"test app")).Value!;
        var token=(await auth.MintTokenAsync(device.DeviceId,device.DeviceSecret)).Value!;
        foreach(var kind in new[]{"service","browser","app","internal"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,Path());
            if(kind!="internal")request.Headers.Add(OrchestratorAuth.EdgeHeader,"1");
            if(kind=="browser")request.Headers.Add("Cookie",CallerAuthenticator.SessionCookie+"="+browser.SessionToken);
            else if(kind!="internal")request.Headers.Authorization=new("Bearer",kind=="service"?Host.Service:token.AccessToken);
            using var response=await h.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        }
        Assert.Equal(4,h.Reads.Count);
    }

    [Fact]
    public async Task UnavailableSignInStore_RefusesBeforeHistory()
    {
        await using var h=await Host.Start([Started(1)]);
        var auth=h.App.Services.GetRequiredService<AuthService>();
        var session=(await auth.SignInBrowserAsync(UserRecord.OwnerId,"test browser")).Value!;
        ((InMemoryAuthStore)h.App.Services.GetRequiredService<IAuthStore>()).FailEveryOperation=true;
        using var request=new HttpRequestMessage(HttpMethod.Get,Path());request.Headers.Add(OrchestratorAuth.EdgeHeader,"1");
        request.Headers.Add("Cookie",CallerAuthenticator.SessionCookie+"="+session.SessionToken);
        using var response=await h.Client.SendAsync(request);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Empty(h.Reads);Assert.Equal(0,h.Connects);
    }

    [Fact]
    public async Task SummaryRemainsBoundedAndIndependent_AndLateEventIsReadable()
    {
        var text=Sentinel+new string('x',3000);
        var events=Enumerable.Range(1,301).Select(i=>Started(i,P("json/plain",text))).ToArray();
        await using var h=await Host.Start(events,pageSize:100);
        using var before=await h.Client.GetAsync("/api/workflows/alpha/history/wf?runId=run-one");
        var summary=await before.Content.ReadAsStringAsync();var rows=JsonDocument.Parse(summary).RootElement;
        Assert.Equal(300,rows.GetArrayLength());Assert.Equal(text[..2000]+"…",rows[0].GetProperty("inputSummary").GetString());
        Assert.Equal(4,h.Reads.Count); // Summary still fetches its original full history, not the new reader.
        var value=await h.Value(eventId:301);Assert.Equal(text,value.GetProperty("values")[0].GetProperty("text").GetString());
        using var after=await h.Client.GetAsync("/api/workflows/alpha/history/wf?runId=run-one");
        Assert.Equal(summary,await after.Content.ReadAsStringAsync());
        Assert.All(h.Reads,r=>Assert.Equal("run-one",r.RunId));
    }

    private static void Empty(JsonElement value,string status,string reason)
    {
        Assert.Equal(status,value.GetProperty("status").GetString());Assert.Equal(reason,value.GetProperty("reason").GetString());
        Assert.Empty(value.GetProperty("values").EnumerateArray());Assert.Equal(JsonValueKind.Null,value.GetProperty("sourceEventId").ValueKind);
    }
    private static string Path(string ns="alpha",string id="wf",string run="run-one",long eventId=1,string field="input") =>
        $"/api/workflows/{ns}/event-value/{Uri.EscapeDataString(id)}?runId={Uri.EscapeDataString(run)}&eventId={eventId}&field={field}";
    private static Payload P(string encoding,string text)=>new(){Metadata={{"encoding",ByteString.CopyFromUtf8(encoding)}},Data=ByteString.CopyFromUtf8(text)};
    private static Payloads Ps(params Payload[] payloads){var p=new Payloads();p.Payloads_.AddRange(payloads);return p;}
    private static HistoryEvent Started(long id,params Payload[] ps)=>new(){EventId=id,EventType=EventType.WorkflowExecutionStarted,WorkflowExecutionStartedEventAttributes=new(){Input=Ps(ps)}};
    private static HistoryEvent Failed(long id,string message,bool activity=false,bool encoded=false)
    {
        var failure=new Failure{Message=message,EncodedAttributes=encoded?P("binary/plain","encoded"):null};
        return activity?new(){EventId=id,EventType=EventType.ActivityTaskFailed,ActivityTaskFailedEventAttributes=new(){Failure=failure}}:
            new(){EventId=id,EventType=EventType.WorkflowExecutionFailed,WorkflowExecutionFailedEventAttributes=new(){Failure=failure}};
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _expire;
        public int Timers;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(10),dueTime);Timers++;_expire=()=>callback(state);return new ClockTimer();
        }
        public void Expire()=>_expire!();
        private sealed class ClockTimer:ITimer {public bool Change(TimeSpan dueTime,TimeSpan period)=>true;public void Dispose(){} public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    }
    private sealed class Host : IAsyncDisposable
    {
        public const string Service="synthetic-service-token";
        public WebApplication App=null!; public HttpClient Client=null!;public ITemporalClient Temporal=null!;
        public HistoryEvent[] Events=[];public int PageSize;public int Connects;
        public readonly List<FetchWorkflowHistoryEventPageInput> Reads=[];
        public readonly DeadlineClock Clock=new();
        private readonly List<ITemporalClient> _clients=[];
        public readonly TaskCompletionSource ConnectionEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ITemporalClient>? ConnectGate;
        public Func<string,FetchWorkflowHistoryEventPageInput,Task<WorkflowHistoryEventPage>>? Page;
        private readonly CaptureLogs _logs=new();
        public static async Task<Host> Start(HistoryEvent[] events,int pageSize=100,bool configured=true,bool connected=true,string[]? namespaces=null)
        {
            var h=new Host{Events=events,PageSize=pageSize};
            var builder=WebApplication.CreateBuilder();builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{{"Orchestrator:AuthToken",Service}});
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();builder.Logging.AddProvider(h._logs);
            builder.Services.AddSingleton<ISecretHasher>(new Argon2idSecretHasher(memoryKib:64,iterations:1));
            builder.Services.AddFleetSignIn(durable:false);builder.Services.AddSingleton<WorkflowStore>();
            builder.Services.AddSingleton(sp => new TemporalClientRegistry(Options.Create(new TemporalOptions{Address=configured?"temporal.test:7233":"",Namespaces=[..namespaces ?? ["alpha","beta"]]}),sp.GetRequiredService<ILogger<TemporalClientRegistry>>())
            {
                Time=h.Clock,Connect=async(_,ns)=>{
                    h.Connects++;h.ConnectionEntered.TrySetResult();
                    if(!connected)throw new IOException("synthetic unavailable");
                    if(h.ConnectGate is not null)return await h.ConnectGate.Task;
                    return h.NewClient(ns);
                }
            });
            h.Temporal=h.NewClient("alpha");
            h.App=builder.Build();await h.App.Services.GetRequiredService<AuthService>().EnsureOwnerAsync(null);
            h.App.UseFleetAccess();h.App.MapWorkflowEndpoints();await h.App.StartAsync();h.Client=new HttpClient {BaseAddress=new Uri(h.App.Urls.Single())};return h;
        }
        private ITemporalClient NewClient(string ns)
        {
            var client=Substitute.For<ITemporalClient>();_clients.Add(client);client.OutboundInterceptor.Returns(new Pages(this,ns));
            client.GetWorkflowHandle(Arg.Any<string>(),Arg.Any<string?>(),Arg.Any<string?>()).Returns(c=>new WorkflowHandle(client,c.ArgAt<string>(0),c.ArgAt<string?>(1)));
            return client;
        }
        private sealed class Pages(Host host,string ns):ClientOutboundInterceptor(null!)
        {
            public override Task<WorkflowHistoryEventPage> FetchWorkflowHistoryEventPageAsync(FetchWorkflowHistoryEventPageInput input)
            {
                host.Reads.Add(input);
                if(host.Page is not null)return host.Page(ns,input);
                var offset=input.NextPageToken is null?0:BitConverter.ToInt32(input.NextPageToken);
                var page=host.Events.Skip(offset).Take(host.PageSize).ToArray();var next=offset+page.Length;
                return Task.FromResult(new WorkflowHistoryEventPage(page,next<host.Events.Length?BitConverter.GetBytes(next):null));
            }
        }
        public async Task<JsonElement> Value(string ns="alpha",string id="wf",string run="run-one",long eventId=1,string field="input")
        {
            using var response=await Client.GetAsync(Path(ns,id,run,eventId,field));Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            Assert.Equal("no-store",response.Headers.CacheControl?.ToString());return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        public async Task Error(string path,int status,string error)
        {
            using var response=await Client.GetAsync(path);var text=await response.Content.ReadAsStringAsync();Assert.Equal(status,(int)response.StatusCode);
            Assert.Equal(error,JsonDocument.Parse(text).RootElement.GetProperty("error").GetString());
            Assert.Equal("no-store",response.Headers.CacheControl?.ToString());Assert.DoesNotContain(Sentinel,text);
            Assert.DoesNotContain(Sentinel,string.Join("\n",_logs.Messages));
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();await App.DisposeAsync();
            foreach(var client in _clients)
                Assert.DoesNotContain(client.ReceivedCalls(),c => c.GetMethodInfo().Name is not ("GetWorkflowHandle" or "get_OutboundInterceptor"));
        }
    }
    private sealed class CaptureLogs:ILoggerProvider
    {
        public readonly ConcurrentQueue<string> Messages=new();
        public ILogger CreateLogger(string categoryName)=>new CaptureLogger(this);public void Dispose(){}
        private sealed class CaptureLogger(CaptureLogs capture):ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
            public bool IsEnabled(LogLevel logLevel)=>true;
            public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter)=>capture.Messages.Enqueue(formatter(state,error)+(error is null ? "" : "\n"+error));
        }
    }
}
