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

    /// <summary>
    /// The journal object store's keys (#388). <c>FLEET_COMMS_MINIO_</c> is the one that proves the
    /// matcher's shape: the pre-existing <c>MINIO_</c> prefix is not a prefix of it, so without its
    /// own entry the root password would be served by <c>/api/config/all</c>.
    /// </summary>
    private static readonly string[] MediaKeys =
    [
        "FLEET_COMMS_MEDIA_ENABLED",
        "FLEET_COMMS_MEDIA_ACCESS_KEY",
        "FLEET_COMMS_MEDIA_SECRET_KEY",
        "FLEET_COMMS_MEDIA_BUCKET",
        "FLEET_COMMS_MINIO_ROOT_USER",
        "FLEET_COMMS_MINIO_ROOT_PASSWORD",
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

    /// <summary>
    /// AC1c: every media and media-MinIO key is denied, including the two that look like they would
    /// already be covered by <c>MINIO_</c>.
    /// </summary>
    [Theory]
    [InlineData("FLEET_COMMS_MEDIA_SECRET_KEY")]
    [InlineData("FLEET_COMMS_MEDIA_ACCESS_KEY")]
    [InlineData("FLEET_COMMS_MINIO_ROOT_USER")]
    [InlineData("FLEET_COMMS_MINIO_ROOT_PASSWORD")]
    [InlineData("fleet_comms_media_secret_key")]
    [InlineData("FLEET_COMMS_MEDIA_BUCKET")]
    public void Every_media_key_is_denylisted(string key)
    {
        Assert.True(ConfigService.IsDenylisted(key));
    }

    /// <summary>
    /// The two new prefixes are exact, and the pre-existing <c>MINIO_</c> entry is untouched: an
    /// unrelated key that merely CONTAINS one of the new prefixes is still not denied.
    /// </summary>
    [Theory]
    [InlineData("FLEET_COMMS_MEDIAL")]
    [InlineData("FLEET_COMMS_MINIOS")]
    [InlineData("COMMS_MEDIA_KEY")]
    [InlineData("MINIO_ACCESS_KEY")]
    public void A_media_prefix_that_is_not_a_prefix_does_not_denylist(string key)
    {
        // MINIO_ACCESS_KEY is denied by the pre-existing MINIO_ entry, not by the new ones; the
        // assertion below is the negative half.
        if (key == "MINIO_ACCESS_KEY") { Assert.True(ConfigService.IsDenylisted(key)); return; }
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

    /// <summary>
    /// The media keys are refused on the way in as well, by both paths: <c>PUT /api/config/values</c>
    /// and the <c>set_config_values</c> tool. A read-side denylist that a write could bypass would
    /// let an agent install its own bucket credential and then read it back.
    /// </summary>
    [Theory]
    [InlineData("FLEET_COMMS_MEDIA_SECRET_KEY")]
    [InlineData("FLEET_COMMS_MINIO_ROOT_PASSWORD")]
    public async Task A_direct_write_of_a_media_key_is_refused_and_the_file_is_unchanged(string key)
    {
        var service = await ServiceWithJournalKeysAsync();
        var before = await File.ReadAllTextAsync(_envFile);

        await Assert.ThrowsAsync<DenylistedException>(() => service.PutValuesAsync(
            new Dictionary<string, string> { [key] = "replaced" }));

        Assert.Equal(before, await File.ReadAllTextAsync(_envFile));
    }

    /// <summary>
    /// AC1c end to end: <c>GET /api/config</c> does not return them — the same surface the journal
    /// keys are checked on, with the media values present in the file.
    /// </summary>
    [Fact]
    public async Task Neither_config_surface_returns_a_media_key()
    {
        var service = await ServiceWithJournalKeysAsync();

        var all = service.GetAll();
        var values = await service.GetValuesAsync([.. MediaKeys, "FLEET_COMMS_ENABLED"]);

        foreach (var key in MediaKeys)
        {
            Assert.False(all.ContainsKey(key), $"{key} was returned by /api/config/all");
            Assert.False(values.Literals.ContainsKey(key), $"{key} was returned by /api/config/values");
        }

        Assert.Equal("true", values.Literals["FLEET_COMMS_ENABLED"]);
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
            + "FLEET_COMMS_MEDIA_ENABLED=true\n"
            + "FLEET_COMMS_MEDIA_BUCKET=comms-journal\n"
            + "FLEET_COMMS_MEDIA_ACCESS_KEY=fleet-comms\n"
            + "FLEET_COMMS_MEDIA_SECRET_KEY=a-bucket-secret\n"
            + "FLEET_COMMS_MINIO_ROOT_USER=root\n"
            + "FLEET_COMMS_MINIO_ROOT_PASSWORD=a-root-password\n"
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
