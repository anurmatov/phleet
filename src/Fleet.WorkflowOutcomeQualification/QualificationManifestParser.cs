using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

public static class QualificationManifestParser
{
    public const int SchemaVersion = 1;
    public const string Kind = "workflow-outcomes-process-qualification";

    private static readonly string[] RootKeys =
    [
        "schemaVersion", "kind", "target", "executables", "configFiles", "policyFiles", "docker", "temporal",
        "runtime", "fixture", "allowedOperations", "scenarios", "authorizedRuntimeOracles",
        "cleanup", "maxDurationSeconds"
    ];

    private static readonly string[] TargetKeys =
        ["registry", "approvalRef", "sourceRef", "sourceCommit", "sourceTree", "images"];
    private static readonly string[] ImageKeys = ["reference", "digest", "buildCommit"];
    private static readonly string[] HashedFileKeys = ["path", "sha256"];
    private static readonly string[] DockerKeys = ["endpoint", "configFile", "context", "engineId", "apiVersion", "serverVersion"];
    private static readonly string[] TemporalKeys =
        ["endpoint", "namespace", "runId", "taskQueue", "tlsMode", "serverName", "identitySha256", "serverVersion"];
    private static readonly string[] RuntimeKeys =
    [
        "sourceDirectory", "workingDirectory", "pathEnvironment", "orchestratorBaseUrl", "brokerEndpoint",
        "databaseServer", "databaseName", "connectionEnv", "seedSql", "seedSqlSha256", "scenarioBatchId",
        "scenarioNonce", "scenarioItemId", "scenarioVersion", "scenarioGeneration",
        "scenarioAttempt", "expectedTurnOutputSha256", "terminalHash"
    ];
    private static readonly string[] FixtureKeys =
        ["cliVersion", "model", "protocol", "endpoint", "shellArgv", "shellEnvironment", "shellDeadlineSeconds"];
    private static readonly string[] CleanupKeys = ["projectName", "owned", "verify", "commands"];

    public static readonly IReadOnlySet<string> RequiredImageRoles =
        new HashSet<string>(["agent", "orchestrator", "temporalBridge"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> RequiredOperations =
        new HashSet<string>(
        [
            "docker-version", "docker-image-agent", "docker-image-orchestrator",
            "docker-image-temporal-bridge", "compose-up", "compose-down", "mysql-seed",
            "mysql-verify", "codex-version", "git-source", "git-status"
        ], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> AllowedOperationNames =
        new HashSet<string>(RequiredOperations.Concat(["crash-coordinator", "restart-coordinator", "receipt-read"]), StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> BaseScenarios =
    [
        "success-zero-turn", "failure-native-turn", "denials", "crash-after-admission",
        "lost-post-reconciliation", "no-human-emissions", "late-confirm-before-ack"
    ];

    public static readonly IReadOnlySet<string> RuntimeOracleScenarios =
        new HashSet<string>(["outage-handoff", "db-keyring-restore"], StringComparer.Ordinal);

    private static readonly HashSet<string> ReservedResourceNames =
        new(StringComparer.Ordinal)
        {
            "default", "temporal-system", "fleet", "fleet-mysql", "fleet-orchestrator",
            "fleet-temporal-bridge", "fleet-bridge", "fleet-dashboard", "rabbitmq", "temporal-server"
        };

    public static ProcessQualificationManifest Parse(Stream stream)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        return Parse(document.RootElement);
    }

    public static ProcessQualificationManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        return Parse(document.RootElement);
    }

    private static ProcessQualificationManifest Parse(JsonElement root)
    {
        Object(root, RootKeys, "manifest");
        Int(root.GetProperty("schemaVersion"), "schemaVersion", SchemaVersion, SchemaVersion);
        String(root.GetProperty("kind"), "kind", Kind, Kind);

        var target = ParseTarget(root.GetProperty("target"));
        var executables = ParseExecutables(root.GetProperty("executables"));
        var config = ParseHashedFiles(root.GetProperty("configFiles"), "configFiles");
        var policies = ParseHashedFiles(root.GetProperty("policyFiles"), "policyFiles");
        RequireNamedFile(config, "agent.json", "configFiles");
        RequireNamedFile(config, "orchestrator.json", "configFiles");
        RequireNamedFile(config, "temporal-bridge.json", "configFiles");
        RequireNamedFile(policies, "ownership.json", "policyFiles");
        RequireNamedFile(policies, "classifiers.json", "policyFiles");
        RequireNamedFile(policies, "recovery-approvals.json", "policyFiles");

        var docker = ParseDocker(root.GetProperty("docker"));
        var temporal = ParseTemporal(root.GetProperty("temporal"));
        var runtime = ParseRuntime(root.GetProperty("runtime"));
        var fixture = ParseFixture(root.GetProperty("fixture"));
        var operations = ParseStringSet(root.GetProperty("allowedOperations"), "allowedOperations", AllowedOperationNames, RequiredOperations);
        var scenarios = ParseStringList(root.GetProperty("scenarios"), "scenarios");
        if (!scenarios.SequenceEqual(BaseScenarios, StringComparer.Ordinal))
            throw new QualificationException("scenarios must exactly list the seven base process scenarios in order");

        var oracles = ParseStringSet(
            root.GetProperty("authorizedRuntimeOracles"), "authorizedRuntimeOracles", RuntimeOracleScenarios, new HashSet<string>(StringComparer.Ordinal));
        if (oracles.Count == 0 && root.GetProperty("authorizedRuntimeOracles").GetArrayLength() != 0)
            throw new QualificationException("authorizedRuntimeOracles contains an unknown oracle");

        var cleanup = ParseCleanup(root.GetProperty("cleanup"));
        var duration = Int(root.GetProperty("maxDurationSeconds"), "maxDurationSeconds", 60, 7200);

        return new ProcessQualificationManifest(
            target, executables, config, policies, docker, temporal, runtime, fixture, operations,
            scenarios, oracles, cleanup, duration);
    }

    private static IReadOnlyDictionary<string, HashedFile> ParseExecutables(JsonElement element)
    {
        Object(element, ["git", "codex"], "executables");
        var result = new Dictionary<string, HashedFile>(StringComparer.Ordinal);
        foreach (var entry in element.EnumerateObject())
        {
            Object(entry.Value, HashedFileKeys, "executables." + entry.Name);
            result.Add(entry.Name, new HashedFile(
                SafeRelativePath(entry.Value.GetProperty("path"), "executable.path"),
                Sha256(entry.Value.GetProperty("sha256"), "executable.sha256")));
        }
        return result;
    }

    private static QualificationTarget ParseTarget(JsonElement element)
    {
        Object(element, TargetKeys, "target");
        var registry = Nonempty(element.GetProperty("registry"), "target.registry");
        var approval = HttpsUrl(element.GetProperty("approvalRef"), "target.approvalRef");
        var sourceRef = Nonempty(element.GetProperty("sourceRef"), "target.sourceRef");
        var commit = GitHash(element.GetProperty("sourceCommit"), "target.sourceCommit");
        var tree = GitHash(element.GetProperty("sourceTree"), "target.sourceTree");
        if (element.GetProperty("images").ValueKind != JsonValueKind.Object)
            throw new QualificationException("target.images must be an object");

        var images = new Dictionary<string, ImageTarget>(StringComparer.Ordinal);
        foreach (var property in element.GetProperty("images").EnumerateObject())
        {
            if (!RequiredImageRoles.Contains(property.Name))
                throw new QualificationException($"target.images has unknown role '{property.Name}'");
            if (images.ContainsKey(property.Name))
                throw new QualificationException($"target.images has duplicate role '{property.Name}'");
            var image = property.Value;
            Object(image, ImageKeys, "target.images." + property.Name);
            var reference = Nonempty(image.GetProperty("reference"), "image.reference");
            if (!reference.StartsWith(registry + "/", StringComparison.Ordinal))
                throw new QualificationException("image.reference must start with target.registry");
            var digest = Digest(image.GetProperty("digest"), "image.digest");
            if (!reference.EndsWith("@" + digest, StringComparison.Ordinal))
                throw new QualificationException("image.reference must pin image.digest");
            var build = GitHash(image.GetProperty("buildCommit"), "image.buildCommit");
            if (build != commit) throw new QualificationException("image.buildCommit must equal target.sourceCommit");
            images[property.Name] = new ImageTarget(reference, digest, build);
        }
        if (!RequiredImageRoles.SetEquals(images.Keys))
            throw new QualificationException("target.images must define exactly agent, orchestrator and temporalBridge");

        return new QualificationTarget(registry, approval, sourceRef, commit, tree, images);
    }

    private static IReadOnlyList<HashedFile> ParseHashedFiles(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new QualificationException(name + " must be a nonempty array");
        var files = new List<HashedFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            Object(item, HashedFileKeys, name);
            var path = SafeRelativePath(item.GetProperty("path"), name + ".path");
            if (!seen.Add(path)) throw new QualificationException(name + " contains duplicate path '" + path + "'");
            files.Add(new HashedFile(path, Sha256(item.GetProperty("sha256"), name + ".sha256")));
        }
        return files;
    }

    private static DockerTarget ParseDocker(JsonElement element)
    {
        Object(element, DockerKeys, "docker");
        var endpoint = Nonempty(element.GetProperty("endpoint"), "docker.endpoint");
        if (!endpoint.StartsWith("unix://", StringComparison.Ordinal) && !endpoint.StartsWith("tcp://", StringComparison.Ordinal))
            throw new QualificationException("docker.endpoint must be an explicit unix:// or tcp:// endpoint");
        if (endpoint.Contains("${", StringComparison.Ordinal) || endpoint.Contains("..", StringComparison.Ordinal))
            throw new QualificationException("docker.endpoint must not contain placeholders or parent traversal");
        return new DockerTarget(
            endpoint,
            SafeRelativePath(element.GetProperty("configFile"), "docker.configFile"),
            Nonempty(element.GetProperty("context"), "docker.context"),
            Nonempty(element.GetProperty("engineId"), "docker.engineId"),
            ApiVersion(element.GetProperty("apiVersion"), "docker.apiVersion"),
            Nonempty(element.GetProperty("serverVersion"), "docker.serverVersion"));
    }

    private static TemporalTarget ParseTemporal(JsonElement element)
    {
        Object(element, TemporalKeys, "temporal");
        var endpointValue = Nonempty(element.GetProperty("endpoint"), "temporal.endpoint");
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || !endpoint.IsDefaultPort && endpoint.Port == 0
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new QualificationException("temporal.endpoint must be an explicit http(s) origin without userinfo, query or fragment");

        var ns = BoundedIdentifier(element.GetProperty("namespace"), "temporal.namespace", 1, 64);
        var runId = BoundedIdentifier(element.GetProperty("runId"), "temporal.runId", 1, 128);
        var queue = BoundedIdentifier(element.GetProperty("taskQueue"), "temporal.taskQueue", 1, 256);
        if (!string.Equals(ns, queue, StringComparison.Ordinal))
            throw new QualificationException("temporal.taskQueue must equal temporal.namespace");
        var mode = String(element.GetProperty("tlsMode"), "temporal.tlsMode", "server-auth", "server-auth");
        if (endpoint.Scheme == Uri.UriSchemeHttps && mode != "server-auth")
            throw new QualificationException("https temporal.endpoint requires tlsMode=server-auth");
        if (endpoint.Scheme == Uri.UriSchemeHttp && mode != "loopback")
            throw new QualificationException("http temporal.endpoint requires tlsMode=loopback");
        if (mode == "loopback" && !endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new QualificationException("loopback temporal.endpoint must use localhost");
        var serverName = Nonempty(element.GetProperty("serverName"), "temporal.serverName");
        if (mode == "server-auth" && !string.Equals(serverName, endpoint.Host, StringComparison.Ordinal))
            throw new QualificationException("temporal.serverName must equal the endpoint host");
        return new TemporalTarget(
            endpointValue, ns, runId, queue, mode, serverName,
            Sha256(element.GetProperty("identitySha256"), "temporal.identitySha256"),
            Nonempty(element.GetProperty("serverVersion"), "temporal.serverVersion"));
    }

    private static RuntimeTarget ParseRuntime(JsonElement element)
    {
        Object(element, RuntimeKeys, "runtime");
        var orchestrator = Nonempty(element.GetProperty("orchestratorBaseUrl"), "runtime.orchestratorBaseUrl");
        if (!Uri.TryCreate(orchestrator, UriKind.Absolute, out var api)
            || api.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment)
            || !string.IsNullOrEmpty(api.UserInfo) || api.AbsolutePath != "/")
            throw new QualificationException("runtime.orchestratorBaseUrl must be an exact origin");
        var broker = Nonempty(element.GetProperty("brokerEndpoint"), "runtime.brokerEndpoint");
        if (!broker.StartsWith("amqp://", StringComparison.Ordinal) && !broker.StartsWith("amqps://", StringComparison.Ordinal))
            throw new QualificationException("runtime.brokerEndpoint must be amqp(s)://");
        var connectionEnv = Identifier(element.GetProperty("connectionEnv"), "runtime.connectionEnv");
        if (connectionEnv is not ("QUALIFICATION_MYSQL_CONNECTION" or "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION"))
            throw new QualificationException("runtime.connectionEnv must name an approved isolated MySQL connection");
        var batchId = Guid(element.GetProperty("scenarioBatchId"), "runtime.scenarioBatchId");
        var nonce = Nonempty(element.GetProperty("scenarioNonce"), "runtime.scenarioNonce");
        if (nonce.Length is < 16 or > 128 || !nonce.All(char.IsAsciiLetterOrDigit))
            throw new QualificationException("runtime.scenarioNonce must be 16..128 ASCII letters or digits");
        var itemId = Guid(element.GetProperty("scenarioItemId"), "runtime.scenarioItemId");
        var version = Int(element.GetProperty("scenarioVersion"), "runtime.scenarioVersion", 1, int.MaxValue);
        var generation = Long(element.GetProperty("scenarioGeneration"), "runtime.scenarioGeneration", 1, long.MaxValue);
        var attempt = Int(element.GetProperty("scenarioAttempt"), "runtime.scenarioAttempt", 1, 100);
        var turnHash = Sha256(element.GetProperty("expectedTurnOutputSha256"), "runtime.expectedTurnOutputSha256");
        var terminalHash = Sha256(element.GetProperty("terminalHash"), "runtime.terminalHash");
        return new RuntimeTarget(
            SafeRelativePath(element.GetProperty("sourceDirectory"), "runtime.sourceDirectory"),
            SafeRelativePath(element.GetProperty("workingDirectory"), "runtime.workingDirectory"),
            String(element.GetProperty("pathEnvironment"), "runtime.pathEnvironment", "/usr/bin:/bin", "/usr/bin:/bin"),
            orchestrator,
            broker,
            BoundedIdentifier(element.GetProperty("databaseServer"), "runtime.databaseServer", 1, 253),
            BoundedIdentifier(element.GetProperty("databaseName"), "runtime.databaseName", 1, 64),
            connectionEnv,
            SafeRelativePath(element.GetProperty("seedSql"), "runtime.seedSql"),
            Sha256(element.GetProperty("seedSqlSha256"), "runtime.seedSqlSha256"),
            batchId, nonce, itemId, version, generation, attempt, turnHash, terminalHash);
    }

    private static InferenceFixtureTarget ParseFixture(JsonElement element)
    {
        Object(element, FixtureKeys, "fixture");
        var cliVersion = Nonempty(element.GetProperty("cliVersion"), "fixture.cliVersion");
        if (!string.Equals(cliVersion, "0.159.2", StringComparison.Ordinal))
            throw new QualificationException("fixture.cliVersion must be exactly 0.159.2");
        var model = BoundedIdentifier(element.GetProperty("model"), "fixture.model", 1, 128);
        var protocol = String(element.GetProperty("protocol"), "fixture.protocol", "openai-responses-v1", "openai-responses-v1");
        var endpointValue = Nonempty(element.GetProperty("endpoint"), "fixture.endpoint");
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || endpoint.Port is < 1 or > 65535
            || endpoint.AbsolutePath != "/v1/"
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new QualificationException("fixture.endpoint must be an explicit loopback http://localhost:<port>/v1/ origin");

        var argv = ParseStringList(element.GetProperty("shellArgv"), "fixture.shellArgv");
        if (!argv.SequenceEqual(new[] { "/bin/sh", "-c", "printf '%s' \"$PROCESS_QUALIFICATION_NONCE\"" }, StringComparer.Ordinal))
            throw new QualificationException("fixture.shellArgv must be the fixed harmless nonce printf");
        var environment = ParseStringList(element.GetProperty("shellEnvironment"), "fixture.shellEnvironment");
        if (environment.Any(name => !Identifier(name, "fixture.shellEnvironment", out _)))
            throw new QualificationException("fixture.shellEnvironment must contain environment variable names only");
        if (!environment.SequenceEqual(new[] { "PROCESS_QUALIFICATION_NONCE" }, StringComparer.Ordinal))
            throw new QualificationException("fixture.shellEnvironment must include PROCESS_QUALIFICATION_NONCE");
        var deadline = Int(element.GetProperty("shellDeadlineSeconds"), "fixture.shellDeadlineSeconds", 1, 60);
        return new InferenceFixtureTarget(cliVersion, model, protocol, endpointValue, argv, environment, deadline);
    }

    private static CleanupTarget ParseCleanup(JsonElement element)
    {
        Object(element, CleanupKeys, "cleanup");
        var project = BoundedIdentifier(element.GetProperty("projectName"), "cleanup.projectName", 3, 63);
        var owned = ParseStringList(element.GetProperty("owned"), "cleanup.owned");
        if (owned.Count == 0 || owned.Distinct(StringComparer.Ordinal).Count() != owned.Count)
            throw new QualificationException("cleanup.owned must be a nonempty unique list");
        if (!owned.Contains(project, StringComparer.Ordinal))
            throw new QualificationException("cleanup.projectName must be listed in cleanup.owned");
        foreach (var name in owned)
        {
            if (!BoundedIdentifier(name, "cleanup.owned", 3, 63, out var error))
                throw new QualificationException("cleanup.owned: " + error);
            if (ReservedResourceNames.Contains(name))
                throw new QualificationException("cleanup.owned names a reserved production resource");
        }
        if (!element.GetProperty("verify").GetBoolean())
            throw new QualificationException("cleanup.verify must be true");
        var commands = ParseStringList(element.GetProperty("commands"), "cleanup.commands");
        if (commands.Any(command => !AllowedOperationNames.Contains(command)))
            throw new QualificationException("cleanup.commands contains an operation outside the allowlist");
        if (!commands.Contains("compose-down"))
            throw new QualificationException("cleanup.commands must include compose-down");
        return new CleanupTarget(project, owned, true, commands);
    }

    private static void Object(JsonElement element, string[] allowed, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new QualificationException(name + " must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new QualificationException(name + " has duplicate key '" + property.Name + "'");
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new QualificationException(name + " has unknown key '" + property.Name + "'");
        }
        foreach (var key in allowed)
            if (!seen.Contains(key)) throw new QualificationException(name + " is missing key '" + key + "'");
    }

    private static string Nonempty(JsonElement element, string name)
    {
        var value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (string.IsNullOrWhiteSpace(value)) throw new QualificationException(name + " must be a nonempty string");
        return value!;
    }

    private static string String(JsonElement element, string name, string expected, string allowed)
    {
        var value = Nonempty(element, name);
        if (!string.Equals(value, expected, StringComparison.Ordinal))
            throw new QualificationException(name + " must be exactly '" + allowed + "'");
        return value;
    }

    private static string GitHash(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (value.Length is not (40 or 64) || !value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new QualificationException(name + " must be 40 or 64 lowercase hexadecimal characters");
        return value;
    }

    private static string Sha256(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (value.Length != 64 || !value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new QualificationException(name + " must be 64 lowercase hexadecimal characters");
        return value;
    }

    private static string Digest(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (!value.StartsWith("sha256:", StringComparison.Ordinal))
            throw new QualificationException(name + " must start with sha256:");
        var hash = value[7..];
        if (hash.Length != 64 || !hash.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new QualificationException(name + " must be sha256: plus 64 lowercase hexadecimal characters");
        return value;
    }

    private static string ApiVersion(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (value.Length < 2 || value[0] != 'v' || !value[1..].All(c => char.IsAsciiDigit(c) || c == '.'))
            throw new QualificationException(name + " must look like v1.43");
        return value;
    }

    private static string SafeRelativePath(JsonElement element, string name)
    {
        var value = Nonempty(element, name).Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal) || value.Contains('\0'))
            throw new QualificationException(name + " must be a safe relative path");
        if (value.StartsWith("fleet/", StringComparison.Ordinal))
            throw new QualificationException(name + " must not point into the runtime data directory");
        return value;
    }

    private static string BoundedIdentifier(JsonElement element, string name, int min, int max)
    {
        var value = Nonempty(element, name);
        if (!BoundedIdentifier(value, name, min, max, out var error)) throw new QualificationException(error);
        return value;
    }

    private static bool BoundedIdentifier(string value, string name, int min, int max, out string error)
    {
        error = "";
        if (value.Length < min || value.Length > max
            || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            error = name + " must be " + min + ".." + max + " ASCII identifier characters";
            return false;
        }
        return true;
    }

    private static bool Identifier(string value, string name, out string error) =>
        BoundedIdentifier(value, name, 3, 128, out error) && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string Identifier(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (!Identifier(value, name, out var error)) throw new QualificationException(error);
        return value;
    }

    private static int Int(JsonElement element, string name, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < min || value > max)
            throw new QualificationException(name + " must be an integer from " + min + " to " + max);
        return value;
    }

    private static void Int(JsonElement element, string name, int min, int max, int expected)
    {
        var value = Int(element, name, min, max);
        if (value != expected) throw new QualificationException(name + " must be exactly " + expected);
    }

    private static Guid Guid(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (!System.Guid.TryParseExact(value, "D", out var guid) || guid == System.Guid.Empty)
            throw new QualificationException(name + " must be a nonempty canonical GUID");
        return guid;
    }

    private static long Long(JsonElement element, string name, long min, long max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value) || value < min || value > max)
            throw new QualificationException(name + " must be an integer from " + min + " to " + max);
        return value;
    }

    private static string HttpsUrl(JsonElement element, string name)
    {
        var value = Nonempty(element, name);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
            throw new QualificationException(name + " must be an https URL with a record path and no credentials");
        return value;
    }

    private static IReadOnlyList<string> ParseStringList(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new QualificationException(name + " must be an array");
        return element.EnumerateArray().Select(item => Nonempty(item, name + "[]")).ToArray();
    }

    private static IReadOnlySet<string> ParseStringSet(
        JsonElement element, string name, IReadOnlySet<string> allowed, IReadOnlySet<string> required)
    {
        var values = ParseStringList(element, name);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!allowed.Contains(value)) throw new QualificationException(name + " contains unknown value '" + value + "'");
            if (!set.Add(value)) throw new QualificationException(name + " contains duplicate value '" + value + "'");
        }
        foreach (var value in required)
            if (!set.Contains(value)) throw new QualificationException(name + " is missing required value '" + value + "'");
        return set;
    }

    private static void RequireNamedFile(IReadOnlyList<HashedFile> files, string fileName, string name)
    {
        if (!files.Any(file => Path.GetFileName(file.Path) == fileName))
            throw new QualificationException(name + " must include " + fileName);
    }
}

public sealed class QualificationException(string message) : Exception(message);

public sealed class QualificationBlockedException(string message) : Exception(message);
