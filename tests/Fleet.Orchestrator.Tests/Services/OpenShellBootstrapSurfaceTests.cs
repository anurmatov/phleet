using System.Net;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellBootstrapSurfaceTests
{
    [Theory]
    [InlineData(false,3600,"/identity/ready",404)]
    [InlineData(true,3600,"/identity/ready",404)]
    [InlineData(true,3601,"/api/agents",404)]
    [InlineData(true,3601,"/identity/ready",503)]
    public async Task ActualListenerBoundary_NeverFallsThroughToNormalOrAdminHandlers(bool enabled,int port,string path,int status)
    {
        var b=WebApplication.CreateBuilder();b.WebHost.UseTestServer();b.Services.AddSingleton(new OpenShellBootstrapReceiverOptions{Enabled=enabled,Port=3601});
        await using var app=b.Build();var admitted=0;
        app.Use(async (context,next)=>{context.Connection.LocalPort=port;await next();});
        app.UseOpenShellBootstrapReceiver();app.Run(context=>{admitted++;return Task.CompletedTask;});await app.StartAsync();
        using var response=await app.GetTestClient().PostAsync(path,null);
        Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.Equal(0,admitted);
    }
    [Fact]
    public void Configuration_MapsReceiverOnlyToOrchestratorAndKeepsOutsideIdentityMappingDisabled()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Fleet.sln")))root=root.Parent;
        Assert.NotNull(root);
        var text=File.ReadAllText(Path.Combine(root.FullName,"docker-compose.example.yml"));
        var compose=new DeserializerBuilder().Build().Deserialize<Dictionary<object,object>>(text);
        var services=(Dictionary<object,object>)compose["services"];
        var environment=(List<object>)((Dictionary<object,object>)services["fleet-orchestrator"])["environment"];
        Assert.Contains("OpenShellBootstrapReceiver__Enabled=${FLEET_BOOTSTRAP_RECEIVER_ENABLED:-false}",environment.Cast<string>());
        Assert.DoesNotContain(((List<object>)((Dictionary<object,object>)services["fleet-bridge"])["environment"]).Cast<string>(),
            e=>e.StartsWith("OpenShellBootstrapReceiver__",StringComparison.Ordinal));
        Assert.Contains("#   Identity__BootstrapReceiver: ${FLEET_IDENTITY_BOOTSTRAP_RECEIVER:-}",text);
        Assert.Contains("FLEET_IDENTITY_BOOTSTRAP_RECEIVER=",File.ReadAllText(Path.Combine(root.FullName,".env.example")));
        Assert.Contains("FLEET_IDENTITY_BOOTSTRAP_RECEIVER=",File.ReadAllText(Path.Combine(root.FullName,"setup.sh")));
    }
}
