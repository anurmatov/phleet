using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// Unit tests for EnvFileCredentialsReader — parser contract and cache invalidation.
/// </summary>
public class EnvFileCredentialsReaderTests
{
    // ── Parser: first-= split ───────────────────────────────────────────────────

    [Fact]
    public void Parser_ValueContainingEquals_ReturnsFullValue()
    {
        // base64 values contain '=' padding — must split on first '=' only
        var path = WriteTempEnv("API_KEY=abc123=pad==");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.Equal("abc123=pad==", env["API_KEY"]);
    }

    [Fact]
    public void Parser_QuotedValue_StripsDoubleQuotes()
    {
        var path = WriteTempEnv("TOKEN=\"my-secret\"");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.Equal("my-secret", env["TOKEN"]);
    }

    [Fact]
    public void Parser_SingleQuotedValue_StripsSingleQuotes()
    {
        var path = WriteTempEnv("TOKEN='my-value'");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.Equal("my-value", env["TOKEN"]);
    }

    [Fact]
    public void Parser_CommentedLine_Ignored()
    {
        var path = WriteTempEnv("# TOKEN=should-be-ignored\nOTHER=present");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.False(env.ContainsKey("TOKEN"));
        Assert.Equal("present", env["OTHER"]);
    }

    [Fact]
    public void Parser_BlankLine_Ignored()
    {
        var path = WriteTempEnv("\n\nKEY=val\n\n");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.Equal("val", env["KEY"]);
    }

    [Fact]
    public void Parser_FileAbsent_ReturnsEmptyDict()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".env");
        var env = EnvFileCredentialsReader.LoadEnvFile(nonExistent);
        Assert.Empty(env);
    }

    [Fact]
    public void Parser_EmptyValue_StoresEmptyString()
    {
        var path = WriteTempEnv("EMPTY_KEY=");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.True(env.ContainsKey("EMPTY_KEY"));
        Assert.Equal("", env["EMPTY_KEY"]);
    }

    [Fact]
    public void Parser_DuplicateKey_LastWins()
    {
        var path = WriteTempEnv("KEY=first\nKEY=second");
        var env = EnvFileCredentialsReader.LoadEnvFile(path);
        Assert.Equal("second", env["KEY"]);
    }

    // ── Cache invalidation ─────────────────────────────────────────────────────

    [Fact]
    public void Get_AfterInvalidateCache_ReadsUpdatedValue()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "MY_KEY=original\n");

            var config = BuildConfig(path);
            var reader = new EnvFileCredentialsReader(config, NullLogger<EnvFileCredentialsReader>.Instance);

            Assert.Equal("original", reader.Get("MY_KEY"));

            // Overwrite the file — value should NOT change yet (still cached)
            File.WriteAllText(path, "MY_KEY=updated\n");

            // After InvalidateCache(), the next Get() re-reads from disk
            reader.InvalidateCache();
            Assert.Equal("updated", reader.Get("MY_KEY"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Get_AbsentKey_ReturnsNull()
    {
        var path = WriteTempEnv("OTHER=value");
        var reader = new EnvFileCredentialsReader(BuildConfig(path), NullLogger<EnvFileCredentialsReader>.Instance);
        Assert.Null(reader.Get("MISSING_KEY"));
    }

    [Fact]
    public void Get_FileAbsent_ReturnsNull()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".env");
        var reader = new EnvFileCredentialsReader(BuildConfig(nonExistent), NullLogger<EnvFileCredentialsReader>.Instance);
        Assert.Null(reader.Get("ANY_KEY"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string WriteTempEnv(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    private static IConfiguration BuildConfig(string envFilePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:EnvFilePath"] = envFilePath,
            })
            .Build();
}

/// <summary>
/// Unit tests for SetupService.UpsertEnvLines — the atomic-write .env line editor.
/// </summary>
public class SetupServiceUpsertTests
{
    // ── Basic upsert ───────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_ExistingKey_UpdatesValue()
    {
        var lines = new[] { "EXISTING=old" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["EXISTING"] = "new" });
        Assert.Contains("EXISTING=new", result);
        Assert.DoesNotContain("EXISTING=old", result);
    }

    [Fact]
    public void Upsert_NewKey_AppendsToEnd()
    {
        var lines = new[] { "OTHER=val" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["NEW_KEY"] = "myval" });
        Assert.Contains("NEW_KEY=myval", result);
        Assert.Equal("NEW_KEY=myval", result.Last());
    }

    [Fact]
    public void Upsert_EmptyFile_CreatesEntry()
    {
        var result = SetupService.UpsertEnvLines([], new Dictionary<string, string> { ["KEY"] = "val" });
        Assert.Single(result);
        Assert.Equal("KEY=val", result[0]);
    }

    [Fact]
    public void Upsert_PreservesCommentLines()
    {
        var lines = new[] { "# comment", "KEY=old" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["KEY"] = "new" });
        Assert.Contains("# comment", result);
    }

    [Fact]
    public void Upsert_ValueContainingEquals_StoredVerbatim()
    {
        // base64 with '=' padding — round-trip must be exact
        var b64 = "abc123=padded==";
        var result = SetupService.UpsertEnvLines([], new Dictionary<string, string> { ["GITHUB_APP_PEM"] = b64 });
        Assert.Equal($"GITHUB_APP_PEM={b64}", result[0]);
    }
}

// ── Sentinel exceptions for propagation error reporting ───────────────────────

public class DbUnavailableExceptionTests
{
    [Fact]
    public void DbUnavailableException_CanBeConstructedAndThrown()
    {
        var inner = new InvalidOperationException("connection refused");
        var ex = new DbUnavailableException("db_unavailable: connection refused", inner);
        Assert.Contains("db_unavailable", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}

// ── ConfigService denylist and template detection ─────────────────────────────

public class ConfigServiceTests
{
    [Theory]
    [InlineData("MYSQL_ROOT_PASSWORD", true)]
    [InlineData("MYSQL_PASSWORD", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("ORCHESTRATOR_AUTH_TOKEN", true)]
    [InlineData("TELEGRAM_BOT_TOKEN", false)]
    [InlineData("FLEET_CTO_AGENT", false)]
    [InlineData("GITHUB_APP_ID", false)]
    public void IsDenylisted_ReturnsExpectedResult(string key, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsDenylisted(key));
    }

    [Theory]
    [InlineData("TELEGRAM_{SHORTNAME}_BOT_TOKEN", true)]
    [InlineData("TELEGRAM_BOT_TOKEN", false)]
    [InlineData("FLEET_CTO_AGENT", false)]
    [InlineData("{SHORTNAME}_SOME_KEY", true)]
    public void IsAgentDerivedTemplate_ReturnsExpectedResult(string key, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsAgentDerivedTemplate(key));
    }

    [Theory]
    [InlineData("TELEGRAM_{SHORTNAME}_BOT_TOKEN", "TELEGRAM_acto_BOT_TOKEN", true)]
    [InlineData("TELEGRAM_{SHORTNAME}_BOT_TOKEN", "TELEGRAM_cto_BOT_TOKEN", true)]
    [InlineData("TELEGRAM_{SHORTNAME}_BOT_TOKEN", "TELEGRAM__BOT_TOKEN", false)]
    [InlineData("TELEGRAM_{SHORTNAME}_BOT_TOKEN", "OTHER_acto_BOT_TOKEN", false)]
    public void TemplateToRegex_MatchesExpected(string template, string key, bool shouldMatch)
    {
        var rx = ConfigService.TemplateToRegex(template);
        Assert.Equal(shouldMatch, rx.IsMatch(key));
    }
}
