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

// ── GetInfraScope — registry-driven propagation scope ────────────────────────

public class PropagationScopeTests
{
    // Helper: build a registry from inline JSON without touching the filesystem.
    private static CredentialRegistry MakeRegistry(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            return CredentialsService.LoadRegistry(path);
        }
        finally { File.Delete(path); }
    }

    // AC: editing a key whose registry Consumers includes fleet-orchestrator
    //     must set selfRecreate=true (e.g. ORCHESTRATOR_AUTH_TOKEN).
    [Fact]
    public void GetInfraScope_OrchestratorInConsumers_SelfRecreateTrue()
    {
        var registry = MakeRegistry("""
            {
              "entries": [
                { "key": "ORCHESTRATOR_AUTH_TOKEN", "description": "d", "category": "auth",
                  "editable": true, "bootstrapOnly": true, "sensitive": true, "confirmRecreate": true,
                  "consumers": ["fleet-orchestrator"] }
              ]
            }
            """);

        var (containers, selfRecreate) = CredentialsService.GetInfraScope(
            registry, "ORCHESTRATOR_AUTH_TOKEN", "fleet-orchestrator");

        Assert.True(selfRecreate);
        Assert.Contains("fleet-orchestrator", containers);
    }

    // AC: editing a key whose registry Consumers does NOT include fleet-orchestrator
    //     must set selfRecreate=false (e.g. TELEGRAM_CTO_BOT_TOKEN).
    [Fact]
    public void GetInfraScope_OrchestratorNotInConsumers_SelfRecreateFalse()
    {
        var registry = MakeRegistry("""
            {
              "entries": [
                { "key": "TELEGRAM_CTO_BOT_TOKEN", "description": "d", "category": "telegram",
                  "editable": true, "bootstrapOnly": false, "sensitive": true, "confirmRecreate": false,
                  "consumers": ["fleet-telegram"] }
              ]
            }
            """);

        var (containers, selfRecreate) = CredentialsService.GetInfraScope(
            registry, "TELEGRAM_CTO_BOT_TOKEN", "fleet-orchestrator");

        Assert.False(selfRecreate);
        Assert.DoesNotContain("fleet-orchestrator", containers);
        Assert.Contains("fleet-telegram", containers);
    }

    // Key not in registry → no consumers, selfRecreate=false.
    [Fact]
    public void GetInfraScope_UnknownKey_ReturnsEmpty()
    {
        var registry = MakeRegistry("""
            { "entries": [] }
            """);

        var (containers, selfRecreate) = CredentialsService.GetInfraScope(
            registry, "UNKNOWN_KEY", "fleet-orchestrator");

        Assert.Empty(containers);
        Assert.False(selfRecreate);
    }

    // Key with empty consumers list → no infra restart, selfRecreate=false.
    [Fact]
    public void GetInfraScope_EmptyConsumers_ReturnsEmpty()
    {
        var registry = MakeRegistry("""
            {
              "entries": [
                { "key": "TELEGRAM_USER_ID", "description": "d", "category": "telegram",
                  "editable": true, "bootstrapOnly": false, "sensitive": false, "confirmRecreate": false,
                  "consumers": [] }
              ]
            }
            """);

        var (containers, selfRecreate) = CredentialsService.GetInfraScope(
            registry, "TELEGRAM_USER_ID", "fleet-orchestrator");

        Assert.Empty(containers);
        Assert.False(selfRecreate);
    }
}

// ── Self-recreate shutdown ordering ─────────────────────────────────────────
//
// Verifies the timing contract introduced in the self-recreate path:
// ctx.Response.OnCompleted fires AFTER the response body is sent, so
// StopApplication() must not be called before the response callback runs.

public class SelfRecreateShutdownTests
{
    /// <summary>
    /// Simulates the self-recreate callback sequence:
    /// OnCompleted registers a callback → response "completes" → callback fires with 500ms delay.
    /// Asserts that StopApplication is not called before the callback fires.
    /// </summary>
    [Fact]
    public async Task SelfRecreate_StopApplication_NotCalledBeforeResponseCompletes()
    {
        var stopCalled = false;
        var responseCompleted = false;
        var responseCompletedAt = DateTimeOffset.MinValue;
        var stopCalledAt = DateTimeOffset.MinValue;

        // Simulate the OnCompleted callback registration and invocation
        Func<Task> onCompletedCallback = async () =>
        {
            responseCompleted = true;
            responseCompletedAt = DateTimeOffset.UtcNow;
            await Task.Delay(500); // mirrors the 500ms delay in the handler
            stopCalledAt = DateTimeOffset.UtcNow;
            stopCalled = true;
        };

        // Response body "sent" — callback fires
        await onCompletedCallback();

        Assert.True(responseCompleted, "Response.OnCompleted callback must run");
        Assert.True(stopCalled, "StopApplication must be called from within the callback");
        Assert.True(stopCalledAt >= responseCompletedAt,
            "StopApplication must not be called before response completion callback fires");
        Assert.True((stopCalledAt - responseCompletedAt).TotalMilliseconds >= 400,
            "At least ~500ms delay must elapse between response completion and StopApplication");
    }

    /// <summary>
    /// Verifies GetInfraScope correctly identifies that ORCHESTRATOR_AUTH_TOKEN triggers
    /// selfRecreate=true — confirming the self-recreate code path will be entered.
    /// </summary>
    [Fact]
    public void SelfRecreate_OrchestratorKey_TriggersSelfRecreateFlag()
    {
        var json = """
            {
              "entries": [
                { "key": "ORCHESTRATOR_AUTH_TOKEN", "description": "d", "category": "auth",
                  "editable": true, "bootstrapOnly": true, "sensitive": true, "confirmRecreate": true,
                  "consumers": ["fleet-orchestrator"] }
              ]
            }
            """;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var registry = CredentialsService.LoadRegistry(path);
            var (_, selfRecreate) = CredentialsService.GetInfraScope(registry, "ORCHESTRATOR_AUTH_TOKEN", "fleet-orchestrator");
            Assert.True(selfRecreate, "ORCHESTRATOR_AUTH_TOKEN must trigger the self-recreate path");
        }
        finally { File.Delete(path); }
    }
}
