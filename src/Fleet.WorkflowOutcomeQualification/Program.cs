using Fleet.WorkflowOutcomeQualification;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help")
    {
        Usage();
        return args.Length == 0 ? 2 : 0;
    }

    try
    {
        switch (args[0])
        {
            case "__owned-command" when args.Length == 2:
                return await OwnedCommandSession.RunAsync(args[1]);
            case "self-test":
                return SelfTest.Run();
            case "validate":
                return await ValidateAsync(ParseArguments(args[1..], ["manifest"]));
            case "fixture":
                return await RunFixtureAsync(ParseArguments(args[1..], ["manifest", "mode", "nonce"]));
            case "run":
                return await RunDriverAsync(ParseArguments(args[1..], ["manifest", "approval", "observations", "results-dir"]));
            default:
                Console.Error.WriteLine("REFUSED: unknown command name");
                Usage();
                return 2;
        }
    }
    catch (JsonException)
    {
        Console.Error.WriteLine("REFUSED: invalid JSON");
        return 2;
    }
    catch (QualificationBlockedException exception)
    {
        Console.Error.WriteLine("BLOCKED: " + exception.Message);
        return 3;
    }
    catch (QualificationException exception)
    {
        Console.Error.WriteLine("REFUSED: " + exception.Message);
        return 2;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("BLOCKED: operation was cancelled");
        return 3;
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or TimeoutException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("BLOCKED: " + exception.GetType().Name);
        return 3;
    }
}

static Dictionary<string, string> ParseArguments(string[] args, string[] allowed)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            throw new QualificationException("arguments must be --name value");
        if (!allowed.Contains(args[index][2..], StringComparer.Ordinal))
            throw new QualificationException("unknown argument name");
        if (!values.TryAdd(args[index][2..], args[index + 1]))
            throw new QualificationException("duplicate argument '" + args[index] + "'");
        index++;
    }
    return values;
}

static string Required(Dictionary<string, string> values, string name)
{
    if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        throw new QualificationException("--" + name + " is required");
    return value;
}

static ProcessQualificationManifest ReadManifest(string path, out string hash)
{
    if (!File.Exists(path)) throw new QualificationException("manifest file does not exist");
    if (new FileInfo(path).Length > 1024 * 1024) throw new QualificationException("manifest exceeds the size bound");
    var bytes = File.ReadAllBytes(path);
    hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
    using var stream = new MemoryStream(bytes, writable: false);
    return QualificationManifestParser.Parse(stream);
}

static async Task<int> ValidateAsync(Dictionary<string, string> args)
{
    var path = Required(args, "manifest");
    var manifest = ReadManifest(path, out var hash);
    using var docker = new DockerEngineClient(manifest.Docker);
    using var temporal = new TemporalMetadataClient(manifest.Temporal);
    var driver = new ProcessQualificationDriver(path, new ProcessCommandRunner(), docker, temporal, hash);
    var plan = await driver.ValidateAsync();
    Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

static async Task<int> RunFixtureAsync(Dictionary<string, string> args)
{
    var path = Required(args, "manifest");
    var manifest = ReadManifest(path, out var hash);
    var modeValue = args.TryGetValue("mode", out var mode) ? mode : "turn";
    if (modeValue is not ("turn" or "shell-probe"))
        throw new QualificationException("--mode must be turn or shell-probe");
    if (!Uri.TryCreate(manifest.Fixture.Endpoint, UriKind.Absolute, out var endpoint))
        throw new QualificationException("fixture endpoint is invalid");
    var nonce = args.TryGetValue("nonce", out var supplied) ? supplied : manifest.Runtime.ScenarioNonce;
    if (nonce != manifest.Runtime.ScenarioNonce) throw new QualificationException("fixture nonce does not match manifest");
    await using var server = new InferenceFixtureServer(
        manifest.Fixture,
        modeValue == "shell-probe" ? InferenceFixtureMode.ShellProbe : InferenceFixtureMode.Turn,
        nonce, endpoint.Port);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(manifest.MaxDurationSeconds));
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    await server.StartAsync(cancellation.Token);
    Console.WriteLine("FIXTURE_LISTENING http://localhost:" + server.Port + "/v1/responses mode=" + modeValue);
    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    return 0;
}

static async Task<int> RunDriverAsync(Dictionary<string, string> args)
{
    var path = Required(args, "manifest");
    var approval = Required(args, "approval");
    var observations = Required(args, "observations");
    var manifest = ReadManifest(path, out var hash);
    using var docker = new DockerEngineClient(manifest.Docker);
    using var temporal = new TemporalMetadataClient(manifest.Temporal);
    var driver = new ProcessQualificationDriver(path, new ProcessCommandRunner(), docker, temporal, hash);
    var receipt = await driver.RunAsync(approval, observations);
    if (args.TryGetValue("results-dir", out var resultsDirectory))
    {
        Directory.CreateDirectory(resultsDirectory);
        await ProcessQualificationDriver.WriteReceiptAsync(
            receipt, Path.Combine(resultsDirectory, "process-qualification.json"));
    }
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    if (receipt.Failed > 0) return 1;
    if (receipt.Blocked > 0 || receipt.Unrun > 0) return 3;
    return 0;
}

static void Usage()
{
    Console.Error.WriteLine(
        """
        Usage:
          Fleet.WorkflowOutcomeQualification self-test
          Fleet.WorkflowOutcomeQualification validate --manifest <file>
          Fleet.WorkflowOutcomeQualification fixture --manifest <file> [--mode turn|shell-probe] [--nonce <value>]
          Fleet.WorkflowOutcomeQualification run --manifest <file> --approval <https-url> --observations <file> [--results-dir <dir>]
        """);
}
