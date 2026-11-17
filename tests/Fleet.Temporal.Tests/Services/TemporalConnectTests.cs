using Fleet.LocalEndpoint;
using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Services;
using Fleet.Temporal.Workflows.Fleet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Extensions.Hosting;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// <see cref="TemporalConnect"/>, the one place the bridge builds a Temporal client, through each of
/// the bridge's four ways to Temporal (the tools' client factory, the two initializers, the hosted
/// worker). Without <c>TemporalBridge:BearerToken</c> each reaches a server the way it did before
/// the setting existed: in plaintext, with no credential. With it, every call of each carries
/// <c>Authorization: Bearer &lt;token&gt;</c>, still in plaintext; and a token that cannot be sent
/// stops each before it connects, never dropped to connect without it. The server here is a local
/// endpoint that records requests, not a Temporal: it shows what is sent, not what a Temporal that
/// checks tokens would accept. Invented tokens only.
/// </summary>
public sealed class TemporalConnectTests
{
    private const string GetSystemInfo = "/temporal.api.workflowservice.v1.WorkflowService/GetSystemInfo";

    private static readonly DataConverter Converter = DataConverter.Default with
    {
        PayloadConverter = new DefaultPayloadConverter(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    private const string Token = "synthetic-temporal-token-0123456789abcdef";

    private static TemporalBridgeOptions Bridge(string address, params string[] namespaces) =>
        new() { TemporalAddress = address, Namespaces = [.. namespaces] };

    private static TemporalBridgeOptions WithToken(TemporalBridgeOptions bridge, string token = Token)
    {
        bridge.BearerToken = token;
        return bridge;
    }

    // ── The options ───────────────────────────────────────────────────────────

    [Fact]
    public void TheOptions_AreTheAddressAndTheNamespace_AndNothingElse()
    {
        var connect = TemporalConnect.Options(Bridge("temporal.example:7233"), "fleet");

        Assert.Equal("temporal.example:7233", connect.TargetHost);
        Assert.Equal("fleet", connect.Namespace);
        Assert.Same(DataConverter.Default, connect.DataConverter);
        AssertNoCredentialAndNoTls(connect);
    }

    [Fact]
    public void TheOptions_CarryTheDataConverterThatIsGiven()
    {
        var connect = TemporalConnect.Options(Bridge("temporal.example:7233"), "fleet", Converter);

        Assert.Same(Converter, connect.DataConverter);
        Assert.Equal("temporal.example:7233", connect.TargetHost);
        Assert.Equal("fleet", connect.Namespace);
        AssertNoCredentialAndNoTls(connect);
    }

    /// <summary>
    /// The worker's client is built by the hosting package from options the seam sets. They are what
    /// the three-string registration used to set (address, namespace, the data converter), on the
    /// task queue named after the namespace.
    /// </summary>
    [Fact]
    public void AWorker_IsOnItsNamespacesTaskQueue_AndItsClientOptionsComeFromTheSeam()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        TemporalConnect.AddWorker(services, Bridge("temporal.example:7233"), "fleet", Converter)
            .AddWorkflow<NotifyCtoWorkflow>();
        using var provider = services.BuildServiceProvider();

        var worker = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get("fleet");

        Assert.Equal("fleet", worker.TaskQueue);
        Assert.NotNull(worker.ClientOptions);
        Assert.Equal("temporal.example:7233", worker.ClientOptions.TargetHost);
        Assert.Equal("fleet", worker.ClientOptions.Namespace);
        Assert.Same(Converter, worker.ClientOptions.DataConverter);
        AssertNoCredentialAndNoTls(worker.ClientOptions);
        Assert.IsType<TemporalWorkerService>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    public void EachNamespace_GetsItsOwnWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var bridge = Bridge("temporal.example:7233");
        foreach (var ns in new[] { "fleet", "second" })
            TemporalConnect.AddWorker(services, bridge, ns, Converter).AddWorkflow<NotifyCtoWorkflow>();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>();

        Assert.Equal("fleet", options.Get("fleet").ClientOptions!.Namespace);
        Assert.Equal("second", options.Get("second").ClientOptions!.Namespace);
        Assert.Equal(2, provider.GetServices<IHostedService>().Count());
    }

    // ── Each way to Temporal, on the wire ─────────────────────────────────────

    [Fact]
    public async Task TheSeam_ConnectsInPlaintext_WithNoCredential()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();

        var client = await TemporalConnect.ConnectAsync(Bridge($"127.0.0.1:{stub.Port}"), "fleet");

        Assert.Equal("fleet", client.Options.Namespace);
        AssertOnlyUncredentialedRequests(stub);
    }

    [Fact]
    public async Task TheToolsClientFactory_ConnectsThroughTheSeam_OncePerNamespace()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var factory = new TemporalClientFactory(Options.Create(Bridge($"127.0.0.1:{stub.Port}")));

        var first = await factory.GetClientAsync("fleet");
        var again = await factory.GetClientAsync("fleet");

        Assert.Same(first, again);
        Assert.Equal("fleet", first.Options.Namespace);
        Assert.Single(stub.Requests, r => r.Path == GetSystemInfo);
        AssertOnlyUncredentialedRequests(stub);
    }

    [Fact]
    public async Task TheNamespaceInitializer_ConnectsThroughTheSeam()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var initializer = new NamespaceInitializer(
            Options.Create(Bridge($"127.0.0.1:{stub.Port}", "fleet")), NullLogger<NamespaceInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Contains(stub.Requests, r => r.Path == GetSystemInfo);
        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/RegisterNamespace", StringComparison.Ordinal));
        AssertOnlyUncredentialedRequests(stub);
    }

    [Fact]
    public async Task TheSearchAttributeInitializer_ConnectsThroughTheSeam()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var initializer = new SearchAttributeInitializer(
            Options.Create(Bridge($"127.0.0.1:{stub.Port}", "fleet")), NullLogger<SearchAttributeInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Contains(stub.Requests, r => r.Path == GetSystemInfo);
        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/ListSearchAttributes", StringComparison.Ordinal));
        AssertOnlyUncredentialedRequests(stub);
    }

    /// <summary>
    /// The worker's client is the one connection this service does not make itself: the hosting
    /// package makes it, when the worker service starts, from the options the seam registered.
    /// </summary>
    [Fact]
    public async Task TheHostedWorker_ConnectsWithTheOptionsTheSeamRegistered()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        TemporalConnect.AddWorker(services, Bridge($"127.0.0.1:{stub.Port}"), "fleet", Converter)
            .AddWorkflow<NotifyCtoWorkflow>();
        await using var provider = services.BuildServiceProvider();
        var worker = Assert.Single(provider.GetServices<IHostedService>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var first = await stub.FirstRequestAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(GetSystemInfo, first.Path);
            AssertOnlyUncredentialedRequests(stub);
        }
        finally
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await worker.StopAsync(limit.Token); } catch { /* the stub is no Temporal: the worker has nothing to poll */ }
        }
    }

    // ── With a token: the options ─────────────────────────────────────────────

    /// <summary>
    /// The header is connection metadata. The SDK's own API-key field stays empty and its TLS
    /// options stay unset, so the transport is what it was (see
    /// <see cref="ThePinnedSdk_TurnsTlsOn_ForAnApiKeyWithNoTlsOptions"/> for why not the API key).
    /// </summary>
    [Fact]
    public void WithAToken_TheOptionsCarryTheAuthorizationHeader_AndNothingAboutTransportChanges()
    {
        var connect = TemporalConnect.Options(WithToken(Bridge("temporal.example:7233")), "fleet", Converter);

        Assert.Equal([new KeyValuePair<string, string>("authorization", "Bearer " + Token)], connect.RpcMetadata);
        Assert.Null(connect.Tls);
        Assert.Null(connect.ApiKey);
        Assert.Null(connect.RpcBinaryMetadata);
        Assert.Equal("temporal.example:7233", connect.TargetHost);
        Assert.Equal("fleet", connect.Namespace);
        Assert.Same(Converter, connect.DataConverter);
    }

    [Fact]
    public void WithAToken_AWorkersClientOptionsCarryTheHeaderToo()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        TemporalConnect.AddWorker(services, WithToken(Bridge("temporal.example:7233")), "fleet", Converter)
            .AddWorkflow<NotifyCtoWorkflow>();
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IOptionsMonitor<TemporalWorkerServiceOptions>>().Get("fleet").ClientOptions!;

        Assert.Equal([new KeyValuePair<string, string>("authorization", "Bearer " + Token)], client.RpcMetadata);
        Assert.Null(client.Tls);
        Assert.Null(client.ApiKey);
    }

    // ── With a token: each way to Temporal, on the wire ───────────────────────

    [Fact]
    public async Task WithAToken_TheSeamsClient_SendsItOnTheCallThatConnects_InPlaintext()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();

        await TemporalConnect.ConnectAsync(WithToken(Bridge($"127.0.0.1:{stub.Port}")), "fleet");

        var connect = Assert.Single(stub.Requests);
        Assert.Equal(GetSystemInfo, connect.Path);
        Assert.Equal("Bearer " + Token, connect.Headers["authorization"]);
        Assert.Equal("http", connect.Scheme);
    }

    [Fact]
    public async Task WithAToken_TheToolsClientFactory_SendsItOnEveryCall()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var factory = new TemporalClientFactory(Options.Create(WithToken(Bridge($"127.0.0.1:{stub.Port}"))));

        var client = await factory.GetClientAsync("fleet");
        // A call a tool makes; the stub answers UNIMPLEMENTED, and the request is what matters.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetWorkflowHandle("example-workflow").DescribeAsync());

        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/DescribeWorkflowExecution", StringComparison.Ordinal));
        AssertEveryRequestCarriesTheToken(stub);
    }

    [Fact]
    public async Task WithAToken_TheNamespaceInitializer_SendsItOnEveryCall()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var initializer = new NamespaceInitializer(
            Options.Create(WithToken(Bridge($"127.0.0.1:{stub.Port}", "fleet"))), NullLogger<NamespaceInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/RegisterNamespace", StringComparison.Ordinal));
        AssertEveryRequestCarriesTheToken(stub);
    }

    [Fact]
    public async Task WithAToken_TheSearchAttributeInitializer_SendsItOnEveryCall()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var initializer = new SearchAttributeInitializer(
            Options.Create(WithToken(Bridge($"127.0.0.1:{stub.Port}", "fleet"))), NullLogger<SearchAttributeInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/ListSearchAttributes", StringComparison.Ordinal));
        AssertEveryRequestCarriesTheToken(stub);
    }

    [Fact]
    public async Task WithAToken_TheHostedWorkersClient_SendsItOnEveryCall()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        TemporalConnect.AddWorker(services, WithToken(Bridge($"127.0.0.1:{stub.Port}")), "fleet", Converter)
            .AddWorkflow<NotifyCtoWorkflow>();
        await using var provider = services.BuildServiceProvider();
        var worker = Assert.Single(provider.GetServices<IHostedService>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var first = await stub.FirstRequestAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(GetSystemInfo, first.Path);
            AssertEveryRequestCarriesTheToken(stub);
        }
        finally
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await worker.StopAsync(limit.Token); } catch { /* the stub is no Temporal: the worker has nothing to poll */ }
        }
        // Whatever the worker went on to ask before it stopped carried the token as well.
        AssertEveryRequestCarriesTheToken(stub);
    }

    /// <summary>
    /// Why the header is metadata and not the SDK's API key. In the pinned SDK (Temporalio 1.11.1)
    /// an API key with no TLS options makes the connection TLS, so the same token given that way
    /// never reaches a plaintext server. This is the SDK's behaviour, constructed here on purpose
    /// and nowhere in <c>src</c>; if a later SDK changes it, this fails and the choice can be looked
    /// at again.
    /// </summary>
    [Fact]
    public async Task ThePinnedSdk_TurnsTlsOn_ForAnApiKeyWithNoTlsOptions()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var viaApiKey = new TemporalClientConnectOptions($"127.0.0.1:{stub.Port}") { Namespace = "fleet", ApiKey = Token };

        await Assert.ThrowsAnyAsync<Exception>(() => TemporalClient.ConnectAsync(viaApiKey).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Empty(stub.Requests);
    }

    // ── A token that cannot be sent ───────────────────────────────────────────

    public static TheoryData<string> UnusableTokens => new()
    {
        "   ",
        "Bearer " + Token,
        Token + "\n",
        "synthetic token",
        "synthetic\u00e9token",
    };

    [Theory]
    [MemberData(nameof(UnusableTokens))]
    public void AnUnusableToken_GivesNoOptionsAtAll(string token)
    {
        var bridge = WithToken(Bridge("temporal.example:7233"), token);

        var error = Assert.Throws<TemporalBearerTokenException>(() => TemporalConnect.Options(bridge, "fleet"));

        Assert.StartsWith("TemporalBridge:BearerToken ", error.Message);
        Assert.DoesNotContain(Token, error.ToString());
    }

    /// <summary>
    /// Each way to Temporal, given a token it cannot send: none of them connects without it. The
    /// factory and the worker fail; the two initializers skip, as they do when Temporal cannot be
    /// reached. Nothing arrives at the server from any of them.
    /// </summary>
    [Fact]
    public async Task WithAnUnusableToken_NoWayToTemporalConnectsWithoutIt()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        TemporalBridgeOptions Unusable() => WithToken(Bridge($"127.0.0.1:{stub.Port}", "fleet"), "Bearer " + Token);

        await Assert.ThrowsAsync<TemporalBearerTokenException>(() => TemporalConnect.ConnectAsync(Unusable(), "fleet"));
        await Assert.ThrowsAsync<TemporalBearerTokenException>(
            () => new TemporalClientFactory(Options.Create(Unusable())).GetClientAsync("fleet"));
        await new NamespaceInitializer(Options.Create(Unusable()), NullLogger<NamespaceInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        await new SearchAttributeInitializer(Options.Create(Unusable()), NullLogger<SearchAttributeInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        var services = new ServiceCollection();
        services.AddLogging();
        TemporalConnect.AddWorker(services, Unusable(), "fleet", Converter).AddWorkflow<NotifyCtoWorkflow>();
        await using var provider = services.BuildServiceProvider();
        Assert.Throws<TemporalBearerTokenException>(() => provider.GetServices<IHostedService>().ToList());

        await Task.Delay(200);
        Assert.Empty(stub.Requests);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private static IConfiguration Configuration(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TemporalBridge:BearerToken"] = token })
            .Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Token)]
    public void AtStartup_NoTokenOrAUsableOne_IsNoFault(string? token)
    {
        Assert.Null(TemporalConnect.DescribeStartupFault(Configuration(token)));
    }

    [Theory]
    [MemberData(nameof(UnusableTokens))]
    public void AtStartup_AnUnusableToken_IsAFault_ThatNamesTheSettingAndNotTheValue(string token)
    {
        var fault = TemporalConnect.DescribeStartupFault(Configuration(token));

        Assert.NotNull(fault);
        Assert.StartsWith("TemporalBridge:BearerToken ", fault);
        Assert.EndsWith("Nothing was connected.", fault);
        Assert.DoesNotContain(Token, fault);
    }

    /// <summary>
    /// One setting, read by the startup check and by the options every client is built from,
    /// whatever the case of its name (the process test below gives it as the environment variable).
    /// </summary>
    [Fact]
    public void TheStartupCheck_AndTheOptions_ReadTheSameSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TEMPORALBRIDGE:BEARERTOKEN"] = "Bearer " + Token })
            .Build();

        Assert.NotNull(TemporalConnect.DescribeStartupFault(configuration));
        Assert.Equal("Bearer " + Token,
            configuration.GetSection(TemporalBridgeOptions.Section).Get<TemporalBridgeOptions>()!.BearerToken);
        Assert.Equal("TemporalBridge:BearerToken", TemporalBridgeOptions.BearerTokenSetting);
    }

    /// <summary>
    /// The bridge itself, started with a token that has "Bearer " in front: it ends with exit code
    /// 1 and the refusal on stderr, before it logs or connects anything, and no part of the token
    /// is in what it printed.
    /// </summary>
    [Fact]
    public async Task TheService_StartedWithAnUnusableToken_ExitsWithCode1_AndSaysWhy_WithoutTheToken()
    {
        var jwt = SyntheticJwt.ExpiringAt(DateTimeOffset.UtcNow.AddDays(90));

        var run = await ServiceProcess.RunAsync("Fleet.Temporal.dll", new Dictionary<string, string>
        {
            ["TemporalBridge__BearerToken"] = "Bearer " + jwt.Token,
            ["TemporalBridge__TemporalAddress"] = "127.0.0.1:1",
            ["TemporalBridge__Namespaces__0"] = "fleet",
        }, TimeSpan.FromSeconds(60));

        Assert.True(run.ExitCode is not null, "the bridge was still running after 60 s with an unusable token: it did not refuse to start.");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            "Fleet.Temporal startup aborted, exiting 1: TemporalBridge:BearerToken starts with \"Bearer \". Give the token alone",
            run.Errors);
        Assert.Equal("", run.Output.Trim());
        foreach (var secret in jwt.Secrets)
        {
            Assert.DoesNotContain(secret, run.Errors);
            Assert.DoesNotContain(secret, run.Output);
        }
    }

    // ── What is logged about the token ────────────────────────────────────────

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            // Everything a sink could write: the rendered line, the structured state, the exception.
            Entries.Add((logLevel, $"{formatter(state, exception)}|{state}|{exception}"));
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(400, LogLevel.Information, "expires at 2027-07-06T12:00:00Z.")]
    [InlineData(10, LogLevel.Warning, "expires at 2026-06-11T12:00:00Z, less than 30 days from now.")]
    [InlineData(-10, LogLevel.Error, "expired at 2026-05-22T12:00:00Z by its own exp claim.")]
    public void ATokenThatSaysWhenItExpires_IsLoggedOnce_WithTheInstant_AndNoPartOfTheToken(int daysFromNow, LogLevel level, string says)
    {
        var jwt = SyntheticJwt.ExpiringAt(Now.AddDays(daysFromNow));
        var logger = new ListLogger();

        TemporalConnect.LogTokenExpiry(logger, Configuration(jwt.Token), Now);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Contains("The Temporal bearer token (TemporalBridge:BearerToken) " + says, entry.Text);
        foreach (var secret in jwt.Secrets) Assert.DoesNotContain(secret, entry.Text);
        Assert.DoesNotContain(jwt.Signature[..16], entry.Text);
        Assert.DoesNotContain(jwt.Payload[..16], entry.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Token)]
    [InlineData("abc.def.ghi")]
    [InlineData("Bearer " + Token)]
    public void NoToken_AnOpaqueOne_OrOneThatWillNotBeSent_LogsNothing(string? token)
    {
        var logger = new ListLogger();

        TemporalConnect.LogTokenExpiry(logger, Configuration(token), Now);

        Assert.Empty(logger.Entries);
    }

    private static void AssertNoCredentialAndNoTls(TemporalClientConnectOptions connect)
    {
        Assert.Null(connect.Tls);
        Assert.Null(connect.ApiKey);
        Assert.Null(connect.RpcMetadata);
        Assert.Null(connect.RpcBinaryMetadata);
    }

    /// <summary>Requests arrived, all over the plaintext listener, and each carries the token and only that credential.</summary>
    private static void AssertEveryRequestCarriesTheToken(GrpcStub stub)
    {
        Assert.NotEmpty(stub.Requests);
        Assert.All(stub.Requests, r =>
        {
            Assert.Equal("Bearer " + Token, r.Headers.GetValueOrDefault("authorization"));
            Assert.Equal("http", r.Scheme);
        });
    }

    /// <summary>Requests arrived over the plaintext listener, and none carries a credential.</summary>
    private static void AssertOnlyUncredentialedRequests(GrpcStub stub)
    {
        Assert.NotEmpty(stub.Requests);
        Assert.All(stub.Requests, r =>
        {
            Assert.DoesNotContain("authorization", r.Headers.Keys);
            Assert.Equal("http", r.Scheme);
        });
    }
}
