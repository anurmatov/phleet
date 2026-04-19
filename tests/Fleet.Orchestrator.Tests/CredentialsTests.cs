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
/// Unit tests for CredentialsService.UpsertEnvLine — the atomic-write .env line editor.
/// </summary>
public class CredentialsServiceUpsertTests
{
    // ── Basic upsert ───────────────────────────────────────────────────────────

    [Fact]
    public void Upsert_ExistingKey_UpdatesValue()
    {
        var lines = new[] { "EXISTING=old" };
        var result = CredentialsService.UpsertEnvLine(lines, "EXISTING", "new");
        Assert.Contains("EXISTING=new", result);
        Assert.DoesNotContain("EXISTING=old", result);
    }

    [Fact]
    public void Upsert_NewKey_AppendsToEnd()
    {
        var lines = new[] { "OTHER=val" };
        var result = CredentialsService.UpsertEnvLine(lines, "NEW_KEY", "myval");
        Assert.Contains("NEW_KEY=myval", result);
        Assert.Equal("NEW_KEY=myval", result.Last());
    }

    [Fact]
    public void Upsert_EmptyFile_CreatesEntry()
    {
        var result = CredentialsService.UpsertEnvLine([], "KEY", "val");
        Assert.Single(result);
        Assert.Equal("KEY=val", result[0]);
    }

    [Fact]
    public void Upsert_PreservesCommentLines()
    {
        var lines = new[] { "# comment", "KEY=old" };
        var result = CredentialsService.UpsertEnvLine(lines, "KEY", "new");
        Assert.Contains("# comment", result);
    }

    [Fact]
    public void Upsert_ValueContainingEquals_StoredVerbatim()
    {
        // base64 with '=' padding — round-trip must be exact
        var b64 = "abc123=padded==";
        var result = CredentialsService.UpsertEnvLine([], "GITHUB_APP_PEM", b64);
        Assert.Equal($"GITHUB_APP_PEM={b64}", result[0]);
    }

    [Fact]
    public void Upsert_DuplicateKey_ReplacesFirstPreservesRest()
    {
        var lines = new[] { "KEY=first", "OTHER=x", "KEY=second" };
        var result = CredentialsService.UpsertEnvLine(lines, "KEY", "new");
        // First occurrence replaced; subsequent occurrences preserved as-is
        Assert.Equal("KEY=new", result[0]);
        Assert.Equal("OTHER=x", result[1]);
        Assert.Equal("KEY=second", result[2]);
        Assert.Equal(3, result.Length);
    }

    // ── Retry exception sentinel types ────────────────────────────────────────

    [Fact]
    public void ComposeUnavailableException_CanBeConstructedAndThrown()
    {
        var inner = new IOException("disk error");
        var ex = new ComposeUnavailableException("compose_file_absent: /fleet/docker-compose.yml", inner);
        Assert.Contains("compose_file_absent", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void DbUnavailableException_CanBeConstructedAndThrown()
    {
        var inner = new InvalidOperationException("connection refused");
        var ex = new DbUnavailableException("db_unavailable: connection refused", inner);
        Assert.Contains("db_unavailable", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    // ── Propagation set validation ─────────────────────────────────────────────

    [Fact]
    public void Registry_DuplicateKey_ThrowsOnLoad()
    {
        var json = """
            {
              "entries": [
                { "key": "FOO", "description": "d", "category": "infra", "editable": true, "bootstrapOnly": false, "sensitive": false, "confirmRecreate": false, "consumers": [] },
                { "key": "FOO", "description": "d2", "category": "infra", "editable": true, "bootstrapOnly": false, "sensitive": false, "confirmRecreate": false, "consumers": [] }
              ]
            }
            """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var ex = Assert.Throws<InvalidOperationException>(() => CredentialsService.LoadRegistry(path));
            Assert.Contains("duplicate key", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FOO", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Registry_AbsentFile_ThrowsOnLoad()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var ex = Assert.Throws<InvalidOperationException>(() => CredentialsService.LoadRegistry(nonExistent));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_ValidFile_LoadsAllEntries()
    {
        var json = """
            {
              "entries": [
                { "key": "KEY_A", "description": "a", "category": "infra", "editable": true, "bootstrapOnly": false, "sensitive": false, "confirmRecreate": false, "consumers": [] },
                { "key": "KEY_B", "description": "b", "category": "auth", "editable": false, "bootstrapOnly": true, "sensitive": true, "confirmRecreate": true, "consumers": ["svc"] }
              ]
            }
            """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var registry = CredentialsService.LoadRegistry(path);
            Assert.Equal(2, registry.Entries.Count);
            Assert.True(registry.TryGet("KEY_A", out var a) && a!.Editable);
            Assert.True(registry.TryGet("KEY_B", out var b) && !b!.Editable && b.Sensitive);
        }
        finally { File.Delete(path); }
    }
}
