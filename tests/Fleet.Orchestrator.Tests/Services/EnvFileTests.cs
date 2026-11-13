using System.Text.Json;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The orchestrator reads <c>.env</c> by one rule and rewrites it under one lock, whichever service
/// asks.
/// </summary>
public class EnvFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"envfile-{Guid.NewGuid():N}");
    private string EnvPath => Path.Combine(_dir, ".env");

    public EnvFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string content)
    {
        File.WriteAllText(EnvPath, content);
        return EnvPath;
    }

    // ── One parse rule ───────────────────────────────────────────────────────────────────────────

    [Theory]
    // Unchanged for every reader: what all three did before.
    [InlineData("KEY=plain", "plain")]
    [InlineData("KEY=  spaced  ", "spaced")]
    [InlineData("KEY=\"double\"", "double")]
    [InlineData("KEY=\"unbalanced", "unbalanced")]
    [InlineData("KEY=abc123=pad==", "abc123=pad==")]
    [InlineData("KEY=", "")]
    [InlineData("KEY=it's", "it's")]
    [InlineData("KEY=\"it's\"", "it's")]
    // The readers disagreed here. Provisioning and setup kept the quotes and so delivered them into
    // the container; the config reader stripped them. One pair goes, as Docker Compose reads it.
    [InlineData("KEY='single'", "single")]
    [InlineData("KEY=''", "")]
    // And here, the other way: the config reader stripped any single quote at either end, which
    // changed a value that merely ends in one. It stays, as provisioning always had it.
    [InlineData("KEY=ends-with'", "ends-with'")]
    [InlineData("KEY='starts-with", "'starts-with")]
    [InlineData("KEY=\"'inner'\"", "'inner'")]
    [InlineData("KEY=''doubled''", "'doubled'")]
    public void AValue_IsReadTheSameWay_ByEveryReader(string line, string expected)
    {
        var path = Write(line + "\n");

        Assert.Equal(expected, EnvFile.Read(path)["KEY"]);
        Assert.Equal(expected, ContainerProvisioningService.LoadEnvFile(path)["KEY"]);
        Assert.Equal(expected, EnvFileCredentialsReader.LoadEnvFile(path)["KEY"]);
        Assert.Equal(expected, EnvFile.ReadOrThrow(path)["KEY"]);
    }

    [Fact]
    public void Lines_ThatAreNotAssignments_AreSkipped_AndTheLastAssignmentWins()
    {
        var path = Write("# KEY=commented\n\nno equals here\n=no key\nKEY=first\n  KEY = second  \n");

        var env = EnvFile.Read(path);

        Assert.Equal("second", Assert.Single(env).Value);
    }

    [Fact]
    public void AMissingFile_IsEmpty_ForBothReads()
    {
        var missing = Path.Combine(_dir, "absent.env");

        Assert.Empty(EnvFile.Read(missing));
        Assert.Empty(EnvFile.ReadOrThrow(missing));
    }

    [Fact]
    public void AFileThatCannotBeRead_IsEmptyForRead_AndAnErrorForReadOrThrow()
    {
        var path = Write("KEY=value\n");

        // Held open for exclusive use, as by another writer: it exists and cannot be read.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(EnvFile.Read(path));
            Assert.ThrowsAny<IOException>(() => EnvFile.ReadOrThrow(path));
        }

        Assert.Equal("value", EnvFile.Read(path)["KEY"]);
    }

    [Fact]
    public async Task ASingleQuotedValue_ReachesTheContainerWithoutItsQuotes()
    {
        await using var harness = ProvisioningHarness.Create();
        File.AppendAllText(harness.EnvFile, "SOME_TOKEN='token-value'\n");
        await harness.SeedAsync(db => db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "tester", Model = "sonnet", Provider = "claude",
            ContainerName = "fleet-agent1", MemoryLimitMb = 512,
            EnvRefs = [new AgentEnvRef { EnvKeyName = "SOME_TOKEN" }],
        }));

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var env = JsonDocument.Parse(harness.CreateBodies.Single()).RootElement.GetProperty("Env")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("SOME_TOKEN=token-value", env);
    }

    // ── One write lock ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WritesFromSetupAndFromTheConfigApi_AtTheSameTime_AllLand()
    {
        Write("EXISTING=kept\n");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:EnvFilePath"] = EnvPath,
                ["Provisioning:BaseDir"] = _dir,
            })
            .Build();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var docker = new DockerService(NullLogger<DockerService>.Instance, new HttpClient { BaseAddress = new Uri("http://localhost") });
        var provisioning = new ContainerProvisioningService(
            scopes, docker, config, new JournalTokenService(config), NullLogger<ContainerProvisioningService>.Instance);
        var setup = new SetupService(
            config, docker, provisioning, scopes, NullLogger<SetupService>.Instance,
            new EnvFileCredentialsReader(config, NullLogger<EnvFileCredentialsReader>.Instance));
        var configService = new ConfigService(
            config, Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions { Host = "", Exchange = "" }),
            scopes, NullLogger<ConfigService>.Instance);

        const int each = 40;
        var writes = new List<Task>();
        for (var i = 0; i < each; i++)
        {
            var n = i;
            writes.Add(Task.Run(() => setup.SetEnvVarAsync($"SETUP_KEY_{n}", $"s{n}")));
            writes.Add(Task.Run(() => configService.PutValuesAsync(new Dictionary<string, string> { [$"CONFIG_KEY_{n}"] = $"c{n}" })));
        }
        await Task.WhenAll(writes);

        var env = EnvFile.Read(EnvPath);
        Assert.Equal("kept", env["EXISTING"]);
        for (var i = 0; i < each; i++)
        {
            Assert.Equal($"s{i}", env.GetValueOrDefault($"SETUP_KEY_{i}"));
            Assert.Equal($"c{i}", env.GetValueOrDefault($"CONFIG_KEY_{i}"));
        }
        Assert.False(File.Exists(EnvPath + ".tmp"));

        await setup.DeleteEnvVarAsync("SETUP_KEY_0");
        Assert.False(EnvFile.Read(EnvPath).ContainsKey("SETUP_KEY_0"));
        Assert.Equal(2 * each, EnvFile.Read(EnvPath).Count);
    }

    [Fact]
    public async Task AWriter_WaitsForTheOneBeforeIt()
    {
        Write("A=1\n");
        var firstIsEditing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letItFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(() => EnvFile.EditAsync(EnvPath, lines =>
        {
            firstIsEditing.SetResult();
            letItFinish.Task.Wait(TimeSpan.FromSeconds(10));
            return EnvFile.Upsert(lines, new Dictionary<string, string> { ["B"] = "2" });
        }));
        await firstIsEditing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = Task.Run(() => EnvFile.EditAsync(
            EnvPath, lines => EnvFile.Upsert(lines, new Dictionary<string, string> { ["C"] = "3" })));
        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        letItFinish.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        // The second read what the first wrote: nothing was lost.
        Assert.Equal(["A=1", "B=2", "C=3"], File.ReadAllLines(EnvPath));
    }

    [Fact]
    public async Task AFailedWrite_LeavesTheFileAsItWas_AndNoTempFileBesideIt()
    {
        Write("A=1\n");

        await Assert.ThrowsAsync<ArgumentException>(() => EnvFile.EditAsync(
            EnvPath, lines => EnvFile.Upsert(lines, new Dictionary<string, string> { ["B"] = "two\nlines" })));

        Assert.Equal("A=1\n", File.ReadAllText(EnvPath));
        Assert.False(File.Exists(EnvPath + ".tmp"));
        // And the lock was released.
        await EnvFile.EditAsync(EnvPath, lines => lines).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── One source for the CTO agent ─────────────────────────────────────────────────────────────

    private IConfiguration ConfigurationWith(string? ctoInEnvironment) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:EnvFilePath"] = EnvPath,
                ["FLEET_CTO_AGENT"] = ctoInEnvironment,
            })
            .Build();

    [Fact]
    public async Task ACtoWrittenToTheFileWhileRunning_IsTheCto_ThoughTheEnvironmentStillSaysBlank()
    {
        // The orchestrator's environment is the file as it was when the container was created.
        Write("FLEET_CTO_AGENT=\n");
        var configuration = ConfigurationWith("");
        Assert.Null(CtoAgentSetting.Resolve(configuration));

        // Creating the first co-cto agent writes its name to the file.
        await EnvFile.EditAsync(EnvPath, lines => EnvFile.Upsert(lines, new Dictionary<string, string> { ["FLEET_CTO_AGENT"] = "agent1" }));

        Assert.Equal("agent1", CtoAgentSetting.Resolve(configuration));
        // Provisioning reads the same answer from the lines it already loaded.
        Assert.Equal("agent1", CtoAgentSetting.Resolve(EnvFile.Read(EnvPath), configuration));
    }

    [Theory]
    [InlineData("FLEET_CTO_AGENT=from-file\n", "from-environment", "from-file")]
    [InlineData("FLEET_CTO_AGENT=\n", "from-environment", "from-environment")]
    [InlineData("OTHER=1\n", " from-environment ", "from-environment")]
    [InlineData("OTHER=1\n", " ", null)]
    [InlineData("OTHER=1\n", null, null)]
    public void TheFileComesFirst_ThenTheEnvironment(string file, string? environment, string? expected)
    {
        Write(file);

        Assert.Equal(expected, CtoAgentSetting.Resolve(ConfigurationWith(environment)));
    }

    [Fact]
    public void WithNoFileAtAll_TheEnvironmentIsTheAnswer()
    {
        Assert.Equal("agent1", CtoAgentSetting.Resolve(ConfigurationWith("agent1")));
    }
}
