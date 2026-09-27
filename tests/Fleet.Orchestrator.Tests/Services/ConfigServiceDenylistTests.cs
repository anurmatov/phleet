using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// The conversation journal's keys never leave or enter <c>.env</c> through the config API (#375
/// AC16b): the signing key would otherwise be served to any caller of <c>/api/config/*</c>.
/// </summary>
public sealed class ConfigServiceDenylistTests : IDisposable
{
    private static readonly string[] JournalKeys =
    [
        "FLEET_COMMS_JOURNAL_KEY",
        "FLEET_COMMS_JOURNAL_ENABLED",
        "FLEET_COMMS_JOURNAL_BIND",
        "FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS",
        "FLEET_COMMS_JOURNAL_RETENTION",
    ];

    private readonly string _envFile = Path.GetTempFileName();

    public void Dispose()
    {
        try { File.Delete(_envFile); } catch (IOException) { }
    }

    [Theory]
    [InlineData("FLEET_COMMS_JOURNAL_KEY")]
    [InlineData("FLEET_COMMS_JOURNAL_ENABLED")]
    [InlineData("FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS")]
    [InlineData("fleet_comms_journal_key")]
    public void Every_journal_key_is_denylisted(string key)
    {
        Assert.True(ConfigService.IsDenylisted(key));
    }

    /// <summary>The prefix is exact: neighbouring comms keys are unaffected.</summary>
    [Theory]
    [InlineData("FLEET_COMMS_ENABLED")]
    [InlineData("FLEET_COMMS_BIND")]
    [InlineData("FLEET_COMMS_JOURNALS")]
    public void Neighbouring_keys_are_not_denylisted(string key)
    {
        Assert.False(ConfigService.IsDenylisted(key));
    }

    /// <summary>What <c>/api/config/all</c> serves.</summary>
    [Fact]
    public async Task GetAll_never_returns_a_journal_key()
    {
        var service = await ServiceWithJournalKeysAsync();

        var all = service.GetAll();

        Assert.True(all.ContainsKey("FLEET_COMMS_ENABLED"), "the file was not read at all");
        foreach (var key in JournalKeys)
            Assert.False(all.ContainsKey(key), $"{key} was returned");
    }

    /// <summary>What <c>/api/config/values?keys=…</c> serves.</summary>
    [Fact]
    public async Task GetValues_never_returns_a_journal_key_even_when_asked_by_name()
    {
        var service = await ServiceWithJournalKeysAsync();

        var result = await service.GetValuesAsync([.. JournalKeys, "FLEET_COMMS_ENABLED"]);

        Assert.Equal("true", result.Literals["FLEET_COMMS_ENABLED"]);
        foreach (var key in JournalKeys)
            Assert.False(result.Literals.ContainsKey(key), $"{key} was returned");
    }

    [Fact]
    public async Task A_direct_write_of_the_signing_key_is_refused_and_the_file_is_unchanged()
    {
        var service = await ServiceWithJournalKeysAsync();
        var before = await File.ReadAllTextAsync(_envFile);

        await Assert.ThrowsAsync<DenylistedException>(() => service.PutValuesAsync(
            new Dictionary<string, string> { ["FLEET_COMMS_JOURNAL_KEY"] = "replaced" }));

        Assert.Equal(before, await File.ReadAllTextAsync(_envFile));
    }

    /// <summary><c>set_config_values</c> refuses the key before anything is written.</summary>
    [Fact]
    public async Task Set_config_values_refuses_the_signing_key()
    {
        var service = await ServiceWithJournalKeysAsync();
        var before = await File.ReadAllTextAsync(_envFile);

        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer config-token";
        var accessor = new HttpContextAccessor { HttpContext = http };

        var tool = new SetConfigValuesTool(
            service,
            new ConfigurationBuilder().AddInMemoryCollection(
                [new KeyValuePair<string, string?>("Orchestrator:ConfigToken", "config-token")]).Build(),
            accessor,
            NullLogger<SetConfigValuesTool>.Instance);

        var result = JsonDocument.Parse(await tool.SetConfigValuesAsync(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["FLEET_COMMS_JOURNAL_KEY"] = "replaced" })))
            .RootElement;

        Assert.Equal("denylisted", result.GetProperty("error").GetString());
        Assert.Equal(before, await File.ReadAllTextAsync(_envFile));
    }

    private async Task<ConfigService> ServiceWithJournalKeysAsync()
    {
        await File.WriteAllTextAsync(_envFile,
            "FLEET_COMMS_ENABLED=true\n"
            + "FLEET_COMMS_JOURNAL_ENABLED=true\n"
            + "FLEET_COMMS_JOURNAL_BIND=http://0.0.0.0:8083\n"
            + "FLEET_COMMS_JOURNAL_KEY=AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKissLS4v\n"
            + "FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS=-100111\n"
            + "FLEET_COMMS_JOURNAL_RETENTION=365.00:00:00\n");

        var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"denylist_{Guid.NewGuid():N}").Options);

        var services = new ServiceCollection();
        services.AddScoped<OrchestratorDbContext>(_ => db);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Provisioning:EnvFilePath", _envFile)])
            .Build();

        return new ConfigService(
            config,
            Microsoft.Extensions.Options.Options.Create(
                new Fleet.Orchestrator.Configuration.RabbitMqOptions { Host = "", Exchange = "" }),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConfigService>.Instance);
    }
}
