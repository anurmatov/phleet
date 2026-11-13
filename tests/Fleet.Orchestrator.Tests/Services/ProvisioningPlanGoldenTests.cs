using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Everything one provision decides, for six agents that between them reach every branch of the
/// plan: the preview, the Docker calls and their bodies, every file under <c>.generated/</c> and
/// every log line. Compared, byte for byte, with what the same agents produced before provisioning
/// was split into several classes.
/// </summary>
/// <remarks>
/// <para>
/// The files under <c>Fixtures/ProvisioningPlanGolden</c> were written by this test against
/// <see cref="ContainerProvisioningService"/> while it was still one class. They are the evidence
/// that moving the code changed nothing, so they are regenerated only for a change that is meant
/// to alter a provision, never to make a refactor pass:
/// <c>UPDATE_PROVISIONING_GOLDEN=1 dotnet test --filter ProvisioningPlanGoldenTests</c>.
/// </para>
/// <para>
/// Two things vary between runs and are replaced before comparing: the temp directory standing in
/// for the fleet base directory, and a minted secret placeholder. Everything else is fixed by the
/// seed below, journal and chat tokens included, because they are an HMAC of a fixed key.
/// </para>
/// </remarks>
public partial class ProvisioningPlanGoldenTests
{
    private const string LocalUrl = "http://inference-host:11434";
    private const string BoundKey = "EXAMPLE_API_KEY";
    private const string StyleName = "terse";

    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Journal:TokenKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()).TrimEnd('='),
        ["Journal:ExcludedChatIds"] = "-1005,-1003",
        ["Provisioning:WhisperServiceUrl"] = "http://fleet-whisper:8080",
        ["Provisioning:KokoroServiceUrl"] = "http://fleet-kokoro-tts:8880",
        ["Provisioning:ContainerLogMaxSize"] = "10m",
        ["Provisioning:ContainerLogMaxFile"] = "3",
        ["Provisioning:AgentImage"] = "fleet:agent-golden",
        ["Chat:SouthUrl"] = "http://fleet-comms:8082/",
        ["SecretProxy:Enabled"] = "true",
    };

    public static TheoryData<string> Agents => new()
    {
        "agent-cloud", "agent-local", "agent-codex-local", "agent-codex-hosted", "agent-gemini", "agent-secret",
    };

    [Theory]
    [MemberData(nameof(Agents))]
    public async Task AProvision_ProducesExactlyWhatItDidBeforeTheSplit(string agentName)
    {
        await using var harness = await StartAsync();

        var document = new StringBuilder();

        var preview = await harness.Service.PreviewAsync(agentName);
        document.AppendLine("## preview");
        document.AppendLine($"container: {preview.ContainerName} (resolved {preview.ResolvedContainerName})");
        AppendSpec(document, preview.Desired);
        document.AppendLine("diffs:");
        foreach (var diff in preview.Diffs) document.AppendLine($"  {diff}");
        var requestsBefore = harness.DockerRequests.Count;
        var logsBefore = harness.Logs.Entries.Count;

        var result = await harness.Service.ProvisionAsync(agentName);

        document.AppendLine().AppendLine("## result");
        document.AppendLine($"{(result.Success ? "ok" : "failed")}: {result.Message}");

        document.AppendLine().AppendLine("## docker calls");
        foreach (var request in harness.DockerRequests.Skip(requestsBefore)) document.AppendLine(request);

        document.AppendLine().AppendLine("## create body");
        foreach (var body in harness.CreateBodies) document.AppendLine(Pretty(body));

        document.AppendLine().AppendLine("## network connects");
        foreach (var (network, body) in harness.NetworkConnects) document.AppendLine($"{network}: {body}");

        document.AppendLine().AppendLine("## generated files");
        var generated = harness.GeneratedDir(preview.ContainerName);
        foreach (var file in Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            document.AppendLine($"--- {Path.GetRelativePath(generated, file).Replace('\\', '/')}");
            document.AppendLine(await File.ReadAllTextAsync(file));
        }

        document.AppendLine("## log lines");
        foreach (var (level, message) in harness.Logs.Entries.Skip(logsBefore)) document.AppendLine($"[{level}] {message}");

        var actual = Normalize(document.ToString(), harness.BaseDir);
        var fixture = Path.Combine("Fixtures", "ProvisioningPlanGolden", agentName + ".txt");

        if (Environment.GetEnvironmentVariable("UPDATE_PROVISIONING_GOLDEN") == "1")
        {
            var source = Path.Combine(RepoRoot(), "tests", "Fleet.Orchestrator.Tests", fixture);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, actual);
            return;
        }

        var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, fixture));
        Assert.Equal(expected, actual);
    }

    private static void AppendSpec(StringBuilder document, ContainerSpec spec)
    {
        document.AppendLine($"image: {spec.Image}");
        document.AppendLine($"memory: {spec.MemoryBytes}");
        document.AppendLine($"secret network address: {spec.SecretNetworkAddress ?? "(none)"}");
        document.AppendLine("env:");
        foreach (var entry in spec.Env) document.AppendLine($"  {entry}");
        document.AppendLine("binds:");
        foreach (var entry in spec.Binds) document.AppendLine($"  {entry}");
        document.AppendLine("networks:");
        foreach (var entry in spec.Networks) document.AppendLine($"  {entry}");
        document.AppendLine("extra hosts:");
        foreach (var entry in spec.ExtraHosts) document.AppendLine($"  {entry}");
    }

    private static string Pretty(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string Normalize(string text, string baseDir) =>
        PlaceholderPattern().Replace(text.Replace(baseDir, "<base>", StringComparison.Ordinal), "<placeholder>")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex(@"fleet_placeholder_[A-Za-z0-9_-]{43}")]
    private static partial Regex PlaceholderPattern();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Fleet.sln not found above the test output.");
    }

    // ── The seed ──────────────────────────────────────────────────────────────

    private static async Task<ProvisioningHarness> StartAsync()
    {
        var harness = ProvisioningHarness.Create(Config);

        File.AppendAllText(harness.EnvFile, string.Join('\n',
            "FLEET_PRIMARY_HUMAN_USER_ID=4242",
            "FLEET_COMMS_MEDIA_ENDPOINT=http://comms-media:9000",
            "GITHUB_APP_PEM=c3ludGhldGljLXBlbQ==",
            "TELEGRAM_CLOUD_BOT_TOKEN=synthetic-cloud-bot-token",
            "TELEGRAM_GEMINI_BOT_TOKEN=synthetic-gemini-bot-token",
            "TELEGRAM_USER_ID=4242",
            "PLAIN_SETTING=synthetic-plain-value",
            "QUOTED_SETTING='synthetic quoted value'",
            $"{BoundKey}=synthetic-bound-value",
            "ZAI_CODING_PLAN_API_KEY=synthetic-hosted-key",
            "") );

        // Present, so the credential binds each provider gets are part of the picture.
        foreach (var name in new[] { ".claude-credentials.json", ".codex-credentials.json", ".gemini-credentials.json" })
            File.WriteAllText(Path.Combine(harness.BaseDir, name), "{}");

        var ca = Path.Combine(harness.BaseDir, SecretProxyState.CaDirectory);
        Directory.CreateDirectory(ca);
        File.WriteAllText(Path.Combine(ca, SecretProxyState.CaCertificateFile), "synthetic certificate\n");

        var credentialDir = Path.Combine(harness.BaseDir, "credentials");
        Directory.CreateDirectory(credentialDir);
        var presentCredential = Path.Combine(credentialDir, "deploy.key");
        File.WriteAllText(presentCredential, "synthetic key\n");

        await harness.SeedAsync(db =>
        {
            db.OutputStyles.Add(new OutputStyle { Name = StyleName, Body = "Answer in one short paragraph.\n", Description = "Short answers" });

            var baseInstruction = Instruction("base", current: 2, "Base v1\n", "Base v2: be careful.\n");
            var developer = Instruction("developer", current: 1, "Developer instruction.\n");
            var review = Instruction("review", current: 9, "Review v1\n", "Review v2: the latest, since nothing is current.\n");
            var empty = new Instruction { Name = "unversioned" };
            db.Instructions.AddRange(baseInstruction, developer, review, empty);

            db.ProjectContexts.Add(new ProjectContext
            {
                Name = "Alpha", CurrentVersion = 2,
                Versions = [Context(1, "# Alpha v1\n"), Context(2, "# Alpha v2\n\nThe canonical context.\n")],
            });
            db.ProjectContexts.Add(new ProjectContext { Name = "beta", CurrentVersion = 1 });

            var present = new CredentialFile { Name = "deploy", FileName = "deploy.key", FilePath = presentCredential };
            var missing = new CredentialFile { Name = "gone", FileName = "gone.key", FilePath = Path.Combine(credentialDir, "gone.key") };
            db.CredentialFiles.AddRange(present, missing);

            db.SecretBindings.Add(new SecretBinding { EnvKeyName = BoundKey, Hosts = "api.example.test" });

            db.Agents.Add(new Agent
            {
                Name = "agent-cloud", DisplayName = "Agent Cloud", Role = "developer", ShortName = "Cloudy",
                Model = "claude-opus-5-5", Provider = "claude", Effort = "high",
                ContainerName = "fleet-agent-cloud", MemoryLimitMb = 2048, HostPort = 9100,
                OutputStyle = StyleName, MountDockerSock = true, AutoMemoryEnabled = false,
                JournalEnabled = true, JournalCrossChatEnabled = true,
                HarnessExtra = "--disallowedTools WebSearch\nMAX_MCP_OUTPUT_TOKENS=12000\n",
                ProactiveIntervalMinutes = 30, GroupListenMode = "all", PrefixMessages = true,
                FormattingMode = FormattingMode.Rich, CanReceiveChatRequests = true,
                RequestReceivedMessage = "Got it.",
                Tools =
                [
                    Tool("Bash"), Tool("WebSearch", enabled: false), Tool("mcp__fleet-memory__memory_store"),
                    Tool("mcp__fleet-comms-journal__get_message"), Tool("MCP__fleet-journal-files__SEND_ATTACHMENT"),
                    Tool("mcp__fleet-journal-files__fetch_attachment"),
                ],
                Projects = [Project("alpha"), Project("beta"), Project("missing")],
                McpEndpoints =
                [
                    Mcp("fleet-telegram", "http://fleet-telegram:3200/?agent=stale"),
                    Mcp("fleet-memory", "http://fleet-memory:3100/"),
                    Mcp("fleet-temporal", "http://fleet-temporal-bridge:3001"),
                    Mcp("fleet-comms-journal", "http://fleet-comms:8084/journal/v1/mcp?x=1"),
                    Mcp("custom", "http://custom-host:9999/mcp?token=keep", "sse"),
                ],
                Instructions =
                [
                    new AgentInstruction { Instruction = review, LoadOrder = 2 },
                    new AgentInstruction { Instruction = developer, LoadOrder = 2 },
                    new AgentInstruction { Instruction = baseInstruction, LoadOrder = 1 },
                    new AgentInstruction { Instruction = empty, LoadOrder = 3 },
                ],
                EnvRefs = [Env("TELEGRAM_CLOUD_BOT_TOKEN"), Env("TELEGRAM_USER_ID"), Env("PLAIN_SETTING"), Env("QUOTED_SETTING"), Env("NOT_IN_ENV_FILE")],
                TelegramUsers = [new AgentTelegramUser { UserId = 4242 }, new AgentTelegramUser { UserId = 77 }],
                TelegramGroups = [new AgentTelegramGroup { GroupId = -1001 }],
                Networks = [new AgentNetwork { NetworkName = "fleet-net" }, new AgentNetwork { NetworkName = "project-net" }],
                CredentialMounts =
                [
                    new AgentCredentialMount { CredentialFile = present, MountPath = "/workspace/.ssh/deploy.key" },
                    new AgentCredentialMount { CredentialFile = missing, MountPath = "/workspace/.ssh/gone.key", Mode = "rw" },
                ],
            });

            db.Agents.Add(new Agent
            {
                Name = "agent-local", DisplayName = "Agent Local", Role = "tester",
                Model = "qwen3.8:27b", Provider = "claude", LocalBaseUrl = LocalUrl + "/", Effort = "off",
                ContextWindow = 131_072, ContainerName = "fleet-agent-local", MemoryLimitMb = 512,
                OutputStyle = StyleName, ChatEnabled = false,
                Tools = [Tool("Read")],
                Instructions = [new AgentInstruction { Instruction = baseInstruction, LoadOrder = 1 }],
            });

            db.Agents.Add(new Agent
            {
                Name = "agent-codex-local", DisplayName = "Agent Codex Local", Role = "devops",
                Model = "ollama/qwen3.8:27b", Provider = "codex", LocalBaseUrl = LocalUrl,
                ContextWindow = 65_536, ContainerName = "fleet-agent-codex-local", MemoryLimitMb = 1024,
                CodexSandboxMode = "workspace-write", HarnessExtra = "history.persistence = \"save-all\"",
                Tools = [Tool("Bash"), Tool("mcp__fleet-temporal__notify_cto")],
                EnvRefs = [Env("CODEX_OSS_BASE_URL")],
                Projects = [Project("Alpha")],
            });

            db.Agents.Add(new Agent
            {
                Name = "agent-codex-hosted", DisplayName = "Agent Codex Hosted", Role = "product-manager",
                Model = "zai/glm-5.3", Provider = "codex", Effort = "high",
                ContainerName = "fleet-agent-codex-hosted", MemoryLimitMb = 1024,
                Tools = [Tool("Bash")],
                EnvRefs = [Env("ZAI_CODING_PLAN_API_KEY")],
            });

            db.Agents.Add(new Agent
            {
                Name = "agent-gemini", DisplayName = "Agent Gemini", Role = "co-cto",
                Model = "gemini-2.5-pro", Provider = "gemini",
                ContainerName = "fleet-agent-gemini", MemoryLimitMb = 768, Image = "fleet:agent-pinned",
                OutputStyle = StyleName, JournalEnabled = true, TelegramSendOnly = true,
                Tools = [Tool("mcp__fleet-journal-files__fetch_attachment")],
                Instructions = [new AgentInstruction { Instruction = developer, LoadOrder = 5 }],
                EnvRefs = [Env("TELEGRAM_GEMINI_BOT_TOKEN")],
                TelegramUsers = [new AgentTelegramUser { UserId = 4242 }],
            });

            db.Agents.Add(new Agent
            {
                Name = "agent-secret", DisplayName = "Agent Secret", Role = "developer",
                Model = "claude-sonnet-5", Provider = "claude",
                ContainerName = "fleet-agent-secret", MemoryLimitMb = 512,
                EnvRefs = [Env(BoundKey), Env("PLAIN_SETTING")],
            });
        });

        return harness;
    }

    private static Instruction Instruction(string name, int current, params string[] versions) => new()
    {
        Name = name, CurrentVersion = current,
        Versions = [.. versions.Select((content, index) => new InstructionVersion { VersionNumber = index + 1, Content = content })],
    };

    private static ProjectContextVersion Context(int number, string content) => new() { VersionNumber = number, Content = content };
    private static AgentTool Tool(string name, bool enabled = true) => new() { ToolName = name, IsEnabled = enabled };
    private static AgentProject Project(string name) => new() { ProjectName = name };
    private static AgentEnvRef Env(string key) => new() { EnvKeyName = key };
    private static AgentMcpEndpoint Mcp(string name, string url, string transport = "http") =>
        new() { McpName = name, Url = url, TransportType = transport };
}
