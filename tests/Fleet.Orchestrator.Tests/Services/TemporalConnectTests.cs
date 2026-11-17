using Fleet.LocalEndpoint;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Temporalio.Converters;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// <see cref="TemporalConnect"/>, the one place the orchestrator builds a Temporal client, and the
/// registry's connection through it. Without <c>Temporal:BearerToken</c> a connection reaches a
/// server the way it did before the setting existed: in plaintext, with no credential. With it,
/// every call carries <c>Authorization: Bearer &lt;token&gt;</c>, still in plaintext; and a token
/// that cannot be sent stops the service before it connects, never dropped to connect without it.
/// The server here is a local endpoint that records requests, not a Temporal: it shows what is
/// sent, not what a Temporal that checks tokens would accept. Invented tokens only.
/// (<c>TemporalConnectSeamTests</c> in Fleet.Temporal.Tests reads the source of both services for a
/// client built anywhere else.)
/// </summary>
public sealed class TemporalConnectTests
{
    private const string GetSystemInfo = "/temporal.api.workflowservice.v1.WorkflowService/GetSystemInfo";
    private const string Token = "synthetic-temporal-token-0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static TemporalClientRegistry Registry(GrpcStub stub, string token, params string[] namespaces) =>
        // As Program.cs registers it: no test connection put in its place.
        new(Options.Create(new TemporalOptions { Address = $"127.0.0.1:{stub.Port}", Namespaces = [.. namespaces], BearerToken = token }),
            NullLogger<TemporalClientRegistry>.Instance);

    private static IConfiguration Configuration(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Temporal:BearerToken"] = token })
            .Build();

    // ── No token: as it always was ────────────────────────────────────────────

    [Fact]
    public void WithoutAToken_TheOptionsAreTheAddressAndTheNamespace_AndNothingElse()
    {
        var connect = TemporalConnect.Options(new TemporalOptions { Address = "configured.example:7233" }, "temporal.example:7233", "fleet");

        // The address is the one the caller connects to, as the registry always passed it.
        Assert.Equal("temporal.example:7233", connect.TargetHost);
        Assert.Equal("fleet", connect.Namespace);
        Assert.Same(DataConverter.Default, connect.DataConverter);
        Assert.Null(connect.Tls);
        Assert.Null(connect.ApiKey);
        Assert.Null(connect.RpcMetadata);
        Assert.Null(connect.RpcBinaryMetadata);
    }

    [Fact]
    public async Task WithoutAToken_TheRegistryConnectsEachNamespace_InPlaintext_WithNoCredential()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();

        var clients = await Registry(stub, token: "", "fleet", "second").GetClientsAsync();

        Assert.Equal(["fleet", "second"], clients.Keys.Order());
        Assert.Equal("fleet", clients["fleet"].Options.Namespace);
        Assert.Equal("second", clients["second"].Options.Namespace);
        Assert.Equal(2, stub.Requests.Count(r => r.Path == GetSystemInfo));
        Assert.All(stub.Requests, r =>
        {
            Assert.DoesNotContain("authorization", r.Headers.Keys);
            Assert.Equal("http", r.Scheme);
        });
    }

    // ── With a token ──────────────────────────────────────────────────────────

    /// <summary>
    /// The header is connection metadata. The SDK's own API-key field stays empty and its TLS
    /// options stay unset, so the transport is what it was: in the pinned SDK an API key with no TLS
    /// options turns TLS on (pinned by
    /// <c>ThePinnedSdk_TurnsTlsOn_ForAnApiKeyWithNoTlsOptions</c> in Fleet.Temporal.Tests).
    /// </summary>
    [Fact]
    public void WithAToken_TheOptionsCarryTheAuthorizationHeader_AndNothingAboutTransportChanges()
    {
        var connect = TemporalConnect.Options(new TemporalOptions { BearerToken = Token }, "temporal.example:7233", "fleet");

        Assert.Equal([new KeyValuePair<string, string>("authorization", "Bearer " + Token)], connect.RpcMetadata);
        Assert.Null(connect.Tls);
        Assert.Null(connect.ApiKey);
        Assert.Null(connect.RpcBinaryMetadata);
        Assert.Equal("temporal.example:7233", connect.TargetHost);
        Assert.Equal("fleet", connect.Namespace);
    }

    [Fact]
    public async Task WithAToken_TheRegistrysClients_SendItOnEveryCall_InPlaintext()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var registry = Registry(stub, Token, "fleet", "second");

        var clients = await registry.GetClientsAsync();
        // A call a handler makes; the stub answers UNIMPLEMENTED, and the request is what matters.
        await Assert.ThrowsAnyAsync<Exception>(() => clients["second"].GetWorkflowHandle("example-workflow").DescribeAsync());

        Assert.Equal(2, stub.Requests.Count(r => r.Path == GetSystemInfo));
        Assert.Contains(stub.Requests, r => r.Path.EndsWith("/DescribeWorkflowExecution", StringComparison.Ordinal));
        Assert.All(stub.Requests, r =>
        {
            Assert.Equal("Bearer " + Token, r.Headers.GetValueOrDefault("authorization"));
            Assert.Equal("http", r.Scheme);
        });
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
        var error = Assert.Throws<TemporalBearerTokenException>(
            () => TemporalConnect.Options(new TemporalOptions { BearerToken = token }, "temporal.example:7233", "fleet"));

        Assert.StartsWith("Temporal:BearerToken ", error.Message);
        Assert.DoesNotContain(Token, error.ToString());
    }

    /// <summary>
    /// The registry with a token it cannot send connects no namespace, and nothing reaches the
    /// server: the token is not dropped to connect without it. (Startup refuses first; this is the
    /// registry on its own.)
    /// </summary>
    [Fact]
    public async Task WithAnUnusableToken_TheRegistryConnectsNothing()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();

        var clients = await Registry(stub, "Bearer " + Token, "fleet").GetClientsAsync();

        Assert.Empty(clients);
        await Task.Delay(200);
        Assert.Empty(stub.Requests);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

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
        Assert.StartsWith("Temporal:BearerToken ", fault);
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TEMPORAL:BEARERTOKEN"] = "Bearer " + Token })
            .Build();

        Assert.NotNull(TemporalConnect.DescribeStartupFault(configuration));
        Assert.Equal("Bearer " + Token, configuration.GetSection(TemporalOptions.Section).Get<TemporalOptions>()!.BearerToken);
        Assert.Equal("Temporal:BearerToken", TemporalOptions.BearerTokenSetting);
    }

    /// <summary>
    /// The orchestrator itself, started with a token that has a line break at its end: it ends with
    /// exit code 1 and the refusal on stderr, before it logs or connects anything, and no part of
    /// the token is in what it printed.
    /// </summary>
    [Fact]
    public async Task TheService_StartedWithAnUnusableToken_ExitsWithCode1_AndSaysWhy_WithoutTheToken()
    {
        var jwt = SyntheticJwt.ExpiringAt(DateTimeOffset.UtcNow.AddDays(90));

        var run = await ServiceProcess.RunAsync("Fleet.Orchestrator.dll", new Dictionary<string, string>
        {
            ["Temporal__BearerToken"] = jwt.Token + "\r",
            ["Temporal__Address"] = "127.0.0.1:1",
        }, TimeSpan.FromSeconds(60));

        Assert.True(run.ExitCode is not null, "the orchestrator was still running after 60 s with an unusable token: it did not refuse to start.");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            "Fleet.Orchestrator startup aborted, exiting 1: Temporal:BearerToken has a blank or a line break in it",
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
        Assert.Contains("The Temporal bearer token (Temporal:BearerToken) " + says, entry.Text);
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
}
