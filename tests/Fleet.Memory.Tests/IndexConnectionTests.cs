using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Fleet.LocalEndpoint;
using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

/// <summary>
/// How fleet-memory reaches Qdrant (<see cref="QdrantConnection"/>): unchanged without
/// <c>Qdrant:ApiKey</c>; with a key, over <c>https</c> only to a server whose certificate the
/// platform trusts and never retried in plaintext, or over an explicit <c>http</c> URL in
/// plaintext; a key with no URL the operator gave (the shipped default is never used with one) or
/// any other URL refused before anything connects, in a message that never carries the key. The servers here are local endpoints that record requests, not
/// Qdrant: <c>bash tests/memory-qdrant/secured.sh</c> is where a real Qdrant that requires TLS and
/// a key is used. None of these needs a Qdrant, so none is skipped anywhere; the type is named so
/// that neither script selects it. Invented values only.
/// </summary>
public sealed class IndexConnectionTests
{
    private const string Key = "synthetic-qdrant-api-key-0123456789abcdef";

    private static QdrantOptions Qdrant(string url, string apiKey = "") => new() { Url = url, ApiKey = apiKey };

    private static VectorStore Store(QdrantOptions options) =>
        new(Options.Create(options), Options.Create(new EmbeddingOptions()), NullLogger<VectorStore>.Instance);

    // ── No key: as it always was ──────────────────────────────────────────────

    [Theory]
    [InlineData("http://qdrant:6334")]
    [InlineData("http://localhost:6334/")]
    [InlineData("https://qdrant.example.test:6334")]
    [InlineData("https://qdrant.example.test")]
    [InlineData("HTTPS://qdrant.example.test:6334")]
    // Any scheme but https was, and stays, a plaintext connection to the host and port.
    [InlineData("grpc://qdrant:6334")]
    public void WithoutAKey_TheClientIsBuiltFromTheUrlAsBefore(string url)
    {
        // What VectorStore passed to the client before the setting existed.
        var uri = new Uri(url);
        var (host, port, https) = (uri.Host, uri.Port, uri.Scheme == "https");

        var connection = QdrantConnection.From(Qdrant(url));

        Assert.Equal((host, port, https), (connection.Host, connection.Port, connection.Https));
        Assert.Null(connection.ApiKey);
        Assert.Null(QdrantConnection.DescribeFault(url, ""));
        Assert.Null(QdrantConnection.DescribeFault(url, null));
    }

    /// <summary>Compose hands an unset variable over as an empty string, and a blank is not a key.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ABlankKey_IsNoKey(string blank)
    {
        var connection = QdrantConnection.From(Qdrant("http://qdrant:6334", blank));

        Assert.Null(connection.ApiKey);
        Assert.False(connection.Https);
        Assert.Null(QdrantConnection.DescribeFault("http://qdrant:6334", blank));
    }

    [Fact]
    public async Task WithoutAKey_ACallGoesOutInPlaintext_WithNoKeyHeader()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var store = Store(Qdrant($"http://127.0.0.1:{stub.Port}"));

        // The stub answers UNIMPLEMENTED: the call arrived, which is all this needs.
        var error = await Assert.ThrowsAsync<RpcException>(() => store.EnsureCollectionAsync());

        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/qdrant.Collections/List", request.Path);
        Assert.DoesNotContain("api-key", request.Headers.Keys);
        Assert.DoesNotContain("authorization", request.Headers.Keys);
    }

    // ── A key, over https ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://qdrant.example.test:6334", "qdrant.example.test", 6334)]
    [InlineData("https://qdrant.example.test", "qdrant.example.test", 443)]
    [InlineData("HTTPS://Qdrant.Example.Test:6334/", "qdrant.example.test", 6334)]
    public void AKeyWithAnHttpsUrl_IsCarried_OverTls(string url, string host, int port)
    {
        var connection = QdrantConnection.From(Qdrant(url, Key));

        Assert.Equal(Key, connection.ApiKey);
        Assert.True(connection.Https);
        Assert.Equal((host, port), (connection.Host, connection.Port));
        Assert.Null(QdrantConnection.DescribeFault(url, Key));
    }

    /// <summary>
    /// A TLS connection that fails is not retried in plaintext: an https URL whose server speaks
    /// only plaintext gets no request at all, so neither the key nor a keyless call reaches it.
    /// </summary>
    [Fact]
    public async Task AKeyWithAnHttpsUrl_IsNeverDowngraded_ToPlaintext()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var store = Store(Qdrant($"https://127.0.0.1:{stub.Port}", Key));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.EnsureCollectionAsync());

        await Task.Delay(100);
        Assert.Empty(stub.Requests);
        Assert.DoesNotContain(Key, error.ToString());
    }

    // ── A key, over an explicit http URL ──────────────────────────────────────

    [Theory]
    [InlineData("http://qdrant:6334", "qdrant", 6334)]
    [InlineData("http://localhost:6334/", "localhost", 6334)]
    [InlineData("HTTP://Qdrant.Example.Test:6334", "qdrant.example.test", 6334)]
    [InlineData("http://qdrant.example.test", "qdrant.example.test", 80)]
    public void AKeyWithAnExplicitHttpUrl_IsCarried_InPlaintext(string url, string host, int port)
    {
        var connection = QdrantConnection.From(Qdrant(url, Key));

        Assert.Equal(Key, connection.ApiKey);
        Assert.False(connection.Https);
        Assert.Equal((host, port), (connection.Host, connection.Port));
        Assert.Null(QdrantConnection.DescribeFault(url, Key));
    }

    /// <summary>
    /// Over http the key is on the wire, as the configured value, on every call: what a trusted
    /// private network carries. <c>bash tests/memory-qdrant/secured.sh</c> shows a real Qdrant
    /// accepting it and refusing a missing or wrong one.
    /// </summary>
    [Fact]
    public async Task WithAKey_OverExplicitHttp_ACallCarriesTheKey()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();
        var store = Store(Qdrant($"http://127.0.0.1:{stub.Port}", $"  {Key}\n"));

        var error = await Assert.ThrowsAsync<RpcException>(() => store.EnsureCollectionAsync());

        Assert.Equal(StatusCode.Unimplemented, error.StatusCode);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/qdrant.Collections/List", request.Path);
        Assert.Equal("http", request.Scheme);
        Assert.Equal(Key, request.Headers["api-key"]);
        Assert.DoesNotContain(Key, error.ToString());
    }

    [Fact]
    public void AKey_IsUsedWithoutTheBlanksAroundIt()
    {
        Assert.Equal(Key, QdrantConnection.From(Qdrant("https://qdrant.example.test:6334", $"  {Key}\r\n")).ApiKey);
    }

    /// <summary>
    /// The client the connection builds holds the key it will send with every call. Read from the
    /// SDK's own objects (Qdrant.Client 1.13.0), because over https the header itself is only on the
    /// wire of a TLS connection to a server the platform trusts: <c>bash tests/memory-qdrant/secured.sh</c>
    /// shows it there (over http, <see cref="WithAKey_OverExplicitHttp_ACallCarriesTheKey"/> sees it).
    /// If the SDK's internals move, this says so; it does not pass by default.
    /// </summary>
    [Theory]
    [InlineData(Key, "https")]
    [InlineData(Key, "http")]
    [InlineData(null, "http")]
    public void TheClientThatIsBuilt_HoldsTheConfiguredKey_OrNone(string? key, string scheme)
    {
        var url = $"{scheme}://qdrant.example.test:6334";
        using var client = QdrantConnection.From(Qdrant(url, key ?? "")).CreateClient();

        var grpcClient = Field(client, "_grpcClient");
        var channel = Field(grpcClient, "_ownedChannel");
        var configuration = Field(channel, "_configuration");
        var held = configuration.GetType().GetProperty("ApiKey")!.GetValue(configuration);

        Assert.Equal(key, held);
        Assert.Equal("qdrant.example.test:6334", ((ChannelBase)channel).Target);
    }

    /// <summary>
    /// Normal certificate validation, and nothing sent before it passes: a server whose certificate
    /// no trusted authority issued gets no request and so no key. There is no setting that changes
    /// this.
    /// </summary>
    [Fact]
    public async Task AServerWhoseCertificateIsNotTrusted_IsRefused_AndNeverSeesTheKey()
    {
        await using var stub = await GrpcStub.StartUntrustedTlsAsync("localhost");
        var store = Store(Qdrant($"https://localhost:{stub.Port}", Key));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.EnsureCollectionAsync());

        Assert.Empty(stub.Requests);
        Assert.DoesNotContain(Key, error.ToString());
        // The transport's refusal, not an answer from the server.
        var rpc = Assert.IsType<RpcException>(error);
        Assert.NotEqual(StatusCode.Unimplemented, rpc.StatusCode);
    }

    // ── A key, with a URL that does not say https or http: refused ────────────

    [Theory]
    [InlineData("grpc://qdrant:6334")]
    [InlineData("ftp://qdrant:6334")]
    [InlineData("qdrant:6334")]
    [InlineData("localhost:6334")]
    [InlineData("//qdrant:6334")]
    [InlineData("/qdrant")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void AKeyWithAUrlThatIsNotExplicitHttpsOrHttp_IsRefused_NamingTheTwoSettings_AndNeverTheKey(string? url)
    {
        var fault = QdrantConnection.DescribeFault(url, Key);

        Assert.NotNull(fault);
        Assert.StartsWith("Qdrant:ApiKey is set and Qdrant:Url is not an explicit https or http URL.", fault);
        Assert.EndsWith("Nothing was connected.", fault);
        Assert.DoesNotContain(Key, fault);
        Assert.DoesNotContain(Key[..12], fault);
    }

    /// <summary>
    /// The store cannot be made with a refused configuration: there is no client that would send
    /// the key to a URL that does not say how, and none that drops the key to connect without it.
    /// </summary>
    [Theory]
    [InlineData("grpc://qdrant:6334")]
    [InlineData("qdrant:6334")]
    public void TheStore_CannotBeConstructedWithAKeyAndARefusedUrl(string url)
    {
        var fromOptions = Assert.Throws<QdrantConfigurationException>(() => QdrantConnection.From(Qdrant(url, Key)));
        var fromStore = Assert.Throws<QdrantConfigurationException>(() => Store(Qdrant(url, Key)));

        Assert.Equal(QdrantConnection.DescribeFault(url, Key), fromOptions.Message);
        Assert.Equal(fromOptions.Message, fromStore.Message);
        Assert.DoesNotContain(Key, fromStore.ToString());
    }

    [Fact]
    public async Task ARefusedConfiguration_ConnectsToNothing()
    {
        await using var stub = await GrpcStub.StartPlaintextAsync();

        Assert.Throws<QdrantConfigurationException>(() => Store(Qdrant($"grpc://127.0.0.1:{stub.Port}", Key)));

        await Task.Delay(100);
        Assert.Empty(stub.Requests);
    }

    // ── What is said ──────────────────────────────────────────────────────────

    [Fact]
    public void TheLogLine_SaysWhetherAKeyIsInUse_AndNeverTheKey()
    {
        var withKey = QdrantConnection.From(Qdrant("https://qdrant.example.test:6334", Key));
        var overHttp = QdrantConnection.From(Qdrant("http://qdrant:6334", Key));
        var without = QdrantConnection.From(Qdrant("http://qdrant:6334"));

        Assert.Equal("https://qdrant.example.test:6334, with an API key", withKey.ToString());
        Assert.Equal("https://qdrant.example.test:6334, with an API key", $"{withKey}");
        Assert.Equal("http://qdrant:6334, with an API key", overHttp.ToString());
        Assert.Equal("http://qdrant:6334, no API key", without.ToString());
    }

    // ── The process ───────────────────────────────────────────────────────────

    /// <summary>
    /// fleet-memory itself, started with a key and a URL that is neither https nor http: it ends
    /// with exit code 1 and the refusal on stderr, and neither stream carries the key.
    /// </summary>
    [Fact]
    public async Task TheService_StartedWithAKeyAndARefusedUrl_ExitsWithCode1_AndSaysWhy_WithoutTheKey()
    {
        var run = await ServiceProcess.RunAsync("Fleet.Memory.dll", new Dictionary<string, string>
        {
            ["Qdrant__Url"] = "grpc://qdrant.example.test:6334",
            ["Qdrant__ApiKey"] = Key,
            ["Storage__Path"] = Path.Combine(Path.GetTempPath(), "fleet-memory-never-created-" + Guid.NewGuid().ToString("N")),
        }, TimeSpan.FromSeconds(60));

        Assert.True(run.ExitCode is not null, "fleet-memory was still running after 60 s with a key and a grpc URL: it did not refuse to start.");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(Refusal, run.Errors);
        Assert.DoesNotContain(Key, run.Errors);
        Assert.DoesNotContain(Key, run.Output);
        // Before the host is built: nothing was logged, so nothing was started.
        Assert.Equal("", run.Output.Trim());
    }

    // ── Where the URL comes from: the operator, or the shipped default ────────

    private const string Refusal =
        "Fleet.Memory startup aborted, exiting 1: Qdrant:ApiKey is set and Qdrant:Url is not an explicit https or http URL.";

    [Fact]
    public void WithoutAKey_AndNoUrlGiven_TheDefaultAddressIsUsed()
    {
        var connection = QdrantConnection.From(new QdrantOptions { DefaultUrl = "http://qdrant:6334" });

        Assert.Equal(("qdrant", 6334, false), (connection.Host, connection.Port, connection.Https));
        Assert.Null(connection.ApiKey);
    }

    [Theory]
    [InlineData("http://qdrant:6334")]
    [InlineData("https://qdrant.example.test:6334")]
    public void AKey_NeverUsesTheDefaultAddress(string defaultUrl)
    {
        var options = new QdrantOptions { DefaultUrl = defaultUrl, ApiKey = Key };

        var error = Assert.Throws<QdrantConfigurationException>(() => QdrantConnection.From(options));

        Assert.Equal(QdrantConnection.DescribeFault("", Key), error.Message);
        Assert.DoesNotContain(Key, error.Message);
    }

    [Fact]
    public void AGivenUrl_WinsOverTheDefault()
    {
        var connection = QdrantConnection.From(new QdrantOptions { Url = "http://qdrant.example.test:6334", DefaultUrl = "http://qdrant:6334", ApiKey = Key });

        Assert.Equal(("qdrant.example.test", false, Key), (connection.Host, connection.Https, connection.ApiKey));
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root.FullName;
    }

    /// <summary>The service's own <c>appsettings.json</c>, the one its image ships beside it.</summary>
    private static string AppSettings() => Path.Combine(RepositoryRoot(), "src/Fleet.Memory/appsettings.json");

    /// <summary>
    /// fleet-memory's <c>Qdrant__*</c> lines of <c>docker-compose.example.yml</c>, as Compose gives
    /// them to the container for this <c>.env</c>: <c>${NAME:-default}</c> is the value when set and
    /// not empty, else the default; <c>${NAME}</c> is the value or empty.
    /// </summary>
    private static Dictionary<string, string> ComposeQdrantEnvironment(IReadOnlyDictionary<string, string> dotEnv)
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.example.yml"));
        var service = Regex.Match(compose, @"^  fleet-memory:\r?\n(?<body>(?:^(?:    .*)?\r?\n)+)", RegexOptions.Multiline);
        Assert.True(service.Success, "docker-compose.example.yml has no fleet-memory service");
        var lines = Regex.Matches(service.Groups["body"].Value, @"^      - (?<name>Qdrant__\w+)=(?<value>.*?)\r?$", RegexOptions.Multiline);
        Assert.True(lines.Count >= 3, "the fleet-memory service gives no Qdrant settings");
        return lines.ToDictionary(
            line => line.Groups["name"].Value,
            line => Regex.Replace(line.Groups["value"].Value, @"\$\{(?<var>[A-Z0-9_]+)(?::-(?<default>[^}]*))?\}",
                m => dotEnv.TryGetValue(m.Groups["var"].Value, out var set) && set.Length > 0 ? set : m.Groups["default"].Value));
    }

    /// <summary>The options the service binds: its <c>appsettings.json</c>, then the variables.</summary>
    private static QdrantOptions Bound(IReadOnlyDictionary<string, string> environment) =>
        new ConfigurationBuilder()
            .AddJsonFile(AppSettings())
            .AddInMemoryCollection(environment.ToDictionary(e => e.Key.Replace("__", ":"), e => (string?)e.Value))
            .Build()
            .GetSection(QdrantOptions.Section).Get<QdrantOptions>()!;

    [Fact]
    public void Keyless_TheShippedDefaults_AreWhatTheyWere()
    {
        var direct = QdrantConnection.From(Bound(new Dictionary<string, string>()));
        var compose = QdrantConnection.From(Bound(ComposeQdrantEnvironment(new Dictionary<string, string>())));

        Assert.Equal("http://localhost:6334, no API key", direct.ToString());
        Assert.Equal("http://qdrant:6334, no API key", compose.ToString());
    }

    [Theory]
    [InlineData("https://qdrant.example.internal:6334", "https://qdrant.example.internal:6334, with an API key")]
    [InlineData("http://qdrant:6334", "http://qdrant:6334, with an API key")]
    public void ThroughCompose_AKeyWithTheAddressSet_IsCarried_OverTheSchemeGiven(string url, string expected)
    {
        var environment = ComposeQdrantEnvironment(new Dictionary<string, string>
        {
            ["FLEET_QDRANT_URL"] = url,
            ["FLEET_QDRANT_API_KEY"] = Key,
        });

        Assert.Equal(expected, QdrantConnection.From(Bound(environment)).ToString());
    }

    /// <summary>
    /// The key alone, with no address, through each way fleet-memory is started: its own
    /// <c>appsettings.json</c>, and the example compose file's lines on top of it (the image ships
    /// that file). Each has a default address; neither may be what a key goes to. The process ends
    /// with exit code 1 before anything connects, and neither stream carries the key.
    /// </summary>
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("docker-compose.example.yml")]
    public async Task TheService_StartedWithAKeyAndNoAddress_ExitsWithCode1_WithoutTheKey(string through)
    {
        var environment = through == "appsettings.json"
            ? new Dictionary<string, string> { ["Qdrant__ApiKey"] = Key }
            : ComposeQdrantEnvironment(new Dictionary<string, string> { ["FLEET_QDRANT_API_KEY"] = Key });
        Assert.Equal(Key, environment["Qdrant__ApiKey"]);
        Assert.False(environment.TryGetValue("Qdrant__Url", out var given) && given.Length > 0);
        // The default is there to be used, and still refused.
        Assert.False(string.IsNullOrEmpty(Bound(environment).DefaultUrl));
        environment["Storage__Path"] = Path.Combine(Path.GetTempPath(), "fleet-memory-never-created-" + Guid.NewGuid().ToString("N"));

        var run = await ServiceProcess.RunAsync("Fleet.Memory.dll", environment, TimeSpan.FromSeconds(60), appSettings: AppSettings());

        Assert.True(run.ExitCode is not null, $"fleet-memory was still running after 60 s with a key and no address ({through}): it did not refuse to start.");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(Refusal, run.Errors);
        Assert.DoesNotContain(Key, run.Errors);
        Assert.DoesNotContain(Key, run.Output);
        Assert.Equal("", run.Output.Trim());
    }

    // ── The one place ─────────────────────────────────────────────────────────

    private static readonly Regex[] Constructions =
    [
        new(@"\bnew\s+QdrantClient\s*\("),
        new(@"\bnew\s+QdrantGrpcClient\s*\("),
        new(@"\bQdrant(Grpc)?Client\s+\w+\s*=\s*new\s*\("),
        new(@"\bQdrantChannel\s*\.\s*ForAddress\b"),
    ];

    private static bool ConstructsAClient(string source) => Constructions.Any(pattern => pattern.IsMatch(source));

    /// <summary>
    /// A Qdrant client is constructed in one file of <c>src</c>. A client built anywhere else would
    /// be one the key and the https rule do not reach.
    /// </summary>
    [Fact]
    public void NoClient_IsConstructedOutsideTheOnePlace()
    {
        const string seam = "src/Fleet.Memory/Data/QdrantConnection.cs";
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var files = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root.FullName, path).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(f => !f.Relative.Split('/').Any(part => part is "bin" or "obj" or "node_modules"))
            .ToList();
        Assert.True(files.Count > 200, $"only {files.Count} source files were found under src: the scan is not looking at the repository");

        var elsewhere = files.Where(f => f.Relative != seam && ConstructsAClient(File.ReadAllText(f.Path))).Select(f => f.Relative).ToList();

        Assert.True(elsewhere.Count == 0, "A Qdrant client must be built by QdrantConnection. Outside it: " + string.Join(", ", elsewhere));
        Assert.True(ConstructsAClient(File.ReadAllText(Path.Combine(root.FullName, seam))), "the scan no longer sees the construction it guards");
        Assert.Contains("QdrantConnection.From(qdrantOptions.Value).CreateClient()",
            File.ReadAllText(Path.Combine(root.FullName, "src/Fleet.Memory/Data/VectorStore.cs")));
    }

    [Theory]
    [InlineData("var c = new QdrantClient(uri.Host, uri.Port, https: true);")]
    [InlineData("QdrantClient c = new(host, port);")]
    [InlineData("var g = new QdrantGrpcClient(address, apiKey);")]
    [InlineData("var ch = QdrantChannel.ForAddress(address);")]
    public void TheScan_SeesEveryWayToBuildAClient(string code) => Assert.True(ConstructsAClient(code));

    [Theory]
    [InlineData("private readonly QdrantClient _client = QdrantConnection.From(qdrantOptions.Value).CreateClient();")]
    [InlineData("var collections = await _client.ListCollectionsAsync(ct);")]
    public void TheScan_LeavesTheUseOfAClientAlone(string code) => Assert.False(ConstructsAClient(code));

    private static object Field(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner)
        ?? throw new InvalidOperationException(
            $"{owner.GetType().Name}.{name} is not there: the Qdrant SDK's internals moved. Check that QdrantConnection.CreateClient "
            + "still passes the key (bash tests/memory-qdrant/secured.sh shows it on the wire) and point this test at where it is kept.");
}
