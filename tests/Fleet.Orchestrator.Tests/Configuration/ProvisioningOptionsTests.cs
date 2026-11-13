using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Configuration;

public class ProvisioningOptionsTests
{
    private static IConfigurationRoot Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => "Provisioning:" + v.Key, v => v.Value))
            .Build();

    [Fact]
    public void AnAbsentKey_TakesTheDefaultTheRawReadsUsed()
    {
        var options = ProvisioningOptions.From(Config());

        Assert.Null(options.BaseDir);
        Assert.Equal("/app/deploy/.env", options.EnvFilePath);
        Assert.Equal("fleet-net", options.DefaultNetwork);
        Assert.Equal("fleet:agent", options.AgentImage);
        Assert.Equal("", options.WhisperServiceUrl);
        Assert.Equal("", options.KokoroServiceUrl);
        Assert.Null(options.ContainerLogMaxSize);
        Assert.Null(options.ContainerLogMaxFile);
        Assert.Equal("fleet", options.ComposeProject);
        Assert.Equal("/app/roles", options.RolesDir);
        Assert.Equal("/app/projects", options.ProjectsDir);
        Assert.Null(options.SeedFilePath);
    }

    [Fact]
    public void ASetKey_IsReadAsWritten()
    {
        var options = ProvisioningOptions.From(Config(
            ("BaseDir", "/srv/fleet"), ("EnvFilePath", "/srv/fleet/.env"), ("DefaultNetwork", "other-net"),
            ("AgentImage", "fleet:agent-next"), ("WhisperServiceUrl", "http://whisper:8080"),
            ("KokoroServiceUrl", "http://tts:8880"), ("ContainerLogMaxSize", "10m"), ("ContainerLogMaxFile", "3"),
            ("ComposeProject", "stack"), ("RolesDir", "/r"), ("ProjectsDir", "/p"), ("SeedFilePath", "/seed.json")));

        Assert.Equal("/srv/fleet", options.BaseDir);
        Assert.Equal("/srv/fleet/.env", options.EnvFilePath);
        Assert.Equal("other-net", options.DefaultNetwork);
        Assert.Equal("fleet:agent-next", options.AgentImage);
        Assert.Equal("http://whisper:8080", options.WhisperServiceUrl);
        Assert.Equal("http://tts:8880", options.KokoroServiceUrl);
        Assert.Equal("10m", options.ContainerLogMaxSize);
        Assert.Equal("3", options.ContainerLogMaxFile);
        Assert.Equal("stack", options.ComposeProject);
        Assert.Equal("/r", options.RolesDir);
        Assert.Equal("/p", options.ProjectsDir);
        Assert.Equal("/seed.json", options.SeedFilePath);
    }

    // `config[key] ?? default` gave an empty value back as empty, not as the default. So does this.
    [Fact]
    public void AnEmptyKey_StaysEmpty_AndIsNotReplacedByTheDefault()
    {
        var options = ProvisioningOptions.From(Config(("EnvFilePath", ""), ("AgentImage", ""), ("ComposeProject", "")));

        Assert.Equal("", options.EnvFilePath);
        Assert.Equal("", options.AgentImage);
        Assert.Equal("", options.ComposeProject);
    }

    [Fact]
    public void EachRead_SeesWhatConfigurationHoldsNow()
    {
        var config = Config(("BaseDir", "/first"));
        Assert.Equal("/first", ProvisioningOptions.From(config).BaseDir);

        config["Provisioning:BaseDir"] = "/second";

        Assert.Equal("/second", ProvisioningOptions.From(config).BaseDir);
    }

    [Fact]
    public void RequireBaseDir_ThrowsTheMessageProvisioningAlwaysGave_WhenItIsNotConfigured()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => ProvisioningOptions.From(Config()).RequireBaseDir());

        Assert.Equal("Provisioning:BaseDir is required but not configured.", thrown.Message);
        Assert.Equal("/srv/fleet", ProvisioningOptions.From(Config(("BaseDir", "/srv/fleet"))).RequireBaseDir());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingOrBlankBaseDir_IsAStartupFault_ThatNamesTheKey(string? baseDir)
    {
        var fault = ProvisioningOptions.From(Config(("BaseDir", baseDir))).DescribeFault();

        Assert.NotNull(fault);
        Assert.StartsWith("Provisioning:BaseDir is not set.", fault);
        Assert.Contains("Provisioning__BaseDir", fault);
    }

    [Fact]
    public void ABlankEnvFilePath_IsAStartupFault_ThatNamesTheKey()
    {
        var fault = ProvisioningOptions.From(Config(("BaseDir", "/srv/fleet"), ("EnvFilePath", " "))).DescribeFault();

        Assert.NotNull(fault);
        Assert.StartsWith("Provisioning:EnvFilePath is blank.", fault);
    }

    [Fact]
    public void AUsableSection_HasNoFault_WithEveryOtherKeyLeftOut()
    {
        Assert.Null(ProvisioningOptions.From(Config(("BaseDir", "/srv/fleet"))).DescribeFault());
    }

    /// <summary>
    /// The keys and their defaults are declared in <see cref="ProvisioningOptions"/> only. A raw
    /// <c>["Provisioning:…"]</c> read anywhere else in the orchestrator is a second place for a
    /// default to drift, which is what this class replaced (the <c>.env</c> path default was
    /// repeated in seven files).
    /// </summary>
    [Fact]
    public void NoOtherFile_ReadsTheSectionByItsRawKey_OrRepeatsTheEnvFileDefault()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var separator = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Fleet.Orchestrator"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                        && !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                        && !file.EndsWith($"{separator}ProvisioningOptions.cs", StringComparison.Ordinal))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("[\"Provisioning:", StringComparison.Ordinal)
                    || text.Contains("\"/app/deploy/.env\"", StringComparison.Ordinal);
            })
            .Select(file => Path.GetFileName(file))
            .ToList();

        Assert.Empty(offenders);
    }
}
