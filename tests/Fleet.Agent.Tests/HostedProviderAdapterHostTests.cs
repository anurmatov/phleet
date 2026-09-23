using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services.HostedProviders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Fleet.Agent.Tests.ResponsesNamespaceAdapterTests;

namespace Fleet.Agent.Tests;

/// <summary>
/// #335 D4 / AC8: the adapter lives on its own loopback-only app, and only there.
/// </summary>
public sealed class HostedProviderAdapterHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hosted-adapter-{Guid.NewGuid():N}");

    public HostedProviderAdapterHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* teardown only */ }
    }

    private HostedProviderKeyStore LoadedKeyStore()
    {
        var path = Path.Combine(_dir, "key");
        File.WriteAllText(path, "adapter-host-test-key");
        var store = new HostedProviderKeyStore(path);
        store.Load("DEEPSEEK_API_KEY");
        return store;
    }

    private static IOptions<AgentOptions> Agent(string model = "deepseek/deepseek-v4-pro") => Options.Create(new AgentOptions
    {
        Name = "fleet-agent1",
        Role = "generic-role",
        WorkDir = "/workspace",
        Provider = "codex",
        Model = model,
        HostedProvider = true,
        HostedProviderKeyEnv = "DEEPSEEK_API_KEY",
    });

    [Fact]
    public async Task BindsExactlyOneLoopbackAddress_AndServesOnlyTheAdapterRoute()
    {
        var upstream = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"output\":[]}", Encoding.UTF8, "application/json"),
        }));
        await using var host = new HostedProviderAdapterHost(Agent(), LoadedKeyStore(), NullLoggerFactory.Instance, upstream);

        await host.StartAsync(CancellationToken.None);
        var baseAddress = await host.BaseAddress.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("127.0.0.1", baseAddress.Host);
        Assert.True(baseAddress.Port > 0);

        using var client = new HttpClient { BaseAddress = baseAddress };

        var models = await client.GetAsync("/deepseek/models");
        Assert.Equal(HttpStatusCode.NotFound, models.StatusCode);
        Assert.Equal(0, upstream.Calls);

        var responses = await client.PostAsync("/deepseek/responses",
            new StringContent("{\"model\":\"deepseek-v4-pro\",\"input\":[]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, responses.StatusCode);
        Assert.Equal(1, upstream.Calls);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NonLoopbackAddressOnTheAdapterPort_IsRefused()
    {
        var upstream = new RecordingHandler((_, _) => throw new InvalidOperationException("must not be called"));
        await using var host = new HostedProviderAdapterHost(Agent(), LoadedKeyStore(), NullLoggerFactory.Instance, upstream);
        await host.StartAsync(CancellationToken.None);
        var port = (await host.BaseAddress.WaitAsync(TimeSpan.FromSeconds(5))).Port;

        var external = FirstNonLoopbackIPv4();
        Assert.True(external is not null, "No non-loopback IPv4 address on this machine: the refusal check cannot run.");

        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        var ex = await Assert.ThrowsAnyAsync<SocketException>(() => tcp.ConnectAsync(external!, port));
        Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
        Assert.Equal(0, upstream.Calls);

        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ModelThatResolvesToNoHostedProvider_RefusesToConstruct()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new HostedProviderAdapterHost(Agent("gpt-5"), LoadedKeyStore(), NullLoggerFactory.Instance));
    }

    /// <summary>
    /// The main agent app, built from the shipped registration graph with the hosted provider on,
    /// has no adapter route: the adapter is reachable only on its own loopback port.
    /// </summary>
    [Fact]
    public async Task MainAgentApp_Returns404ForTheAdapterRoute()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _dir });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = _dir,
            ["Agent:Provider"] = "codex",
            ["Agent:Model"] = "deepseek/deepseek-v4-pro",
            ["Agent:HostedProvider"] = "true",
            ["Agent:HostedProviderKeyEnv"] = "DEEPSEEK_API_KEY",
        });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAgentCoreServices(builder.Configuration);
        builder.Services.AddSingleton(LoadedKeyStore());

        await using var app = builder.Build();
        app.MapGet("/health", () => "ok");
        await app.StartAsync();

        var mainAddress = new Uri(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single());
        var adapterAddress = await app.Services.GetRequiredService<HostedProviderAdapterHost>()
            .BaseAddress.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(mainAddress.Port, adapterAddress.Port);

        using var client = new HttpClient { BaseAddress = mainAddress };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var post = await client.PostAsync("/deepseek/responses",
            new StringContent("{\"model\":\"deepseek-v4-pro\",\"input\":[]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);

        await app.StopAsync();
    }

    private static IPAddress? FirstNonLoopbackIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
}
