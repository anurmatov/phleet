using System.Text.Json;
using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

public sealed class ManagedStockAgentTests
{
    private const string Placeholder = "openshell:resolve:env:v7_OPENAI_API_KEY";

    [Fact]
    public void Api_profile_prepares_placeholder_only_without_manual_auth_cache()
    {
        using var f = new Fixture();
        var configuration = f.Config();
        var binding = StockOpenShellEnrollment.Validate(configuration, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(configuration, f.Home, binding, f.Root, _ => Placeholder);
        StockOpenShellEnrollment.RequirePrepared(configuration, binding, f.Home, _ => Placeholder);
        var auth = File.ReadAllText(Path.Combine(f.Home, ".codex", "auth.json"));
        using var json = JsonDocument.Parse(auth);
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(Placeholder, json.RootElement.GetProperty("OPENAI_API_KEY").GetString());
        Assert.StartsWith("forced_login_method = \"api\"\n", File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex-host")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Home, ".codex", "auth.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Home, ".codex", "config.toml")));
        }
    }

    [Theory]
    [InlineData("openshell:resolve:env:OPENAI_API_KEY")]
    [InlineData("openshell:resolve:env:v7_OPENAI_API_KEY")]
    [InlineData("openshell:resolve:env:sabc123_OPENAI_API_KEY")]
    public void Source_supported_handle_forms_use_only_the_standard_child_environment(string placeholder)
    {
        using var f = new Fixture();
        string? EnvironmentValue(string name)
        {
            Assert.Equal("OPENAI_API_KEY", name);
            return placeholder;
        }
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, EnvironmentValue)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, EnvironmentValue);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, EnvironmentValue);
    }

    [Fact]
    public void Public_mode_existing_auth_is_not_overwritten()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        var path = Path.Combine(f.Home, ".codex", "auth.json");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        else throw new PlatformNotSupportedException("Stock startup requires Linux");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-synthetic-literal")]
    [InlineData("openshell:resolve:env:OTHER_KEY")]
    [InlineData("openshell:resolve:env:OPENAI_API_KEY\n")]
    [InlineData(" openshell:resolve:env:OPENAI_API_KEY")]
    public void Missing_literal_foreign_or_padded_placeholder_refuses_before_writes(string? value)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, _ => value));
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    [Theory]
    [InlineData("oauth", "KEY_REF")]
    [InlineData("api_key", null)]
    [InlineData("api_key", "literal key")]
    [InlineData(null, "KEY_REF")]
    public void Unsupported_mode_or_reference_refuses(string? mode, string? reference)
    {
        using var f = new Fixture();
        f.Settings["Agent:NativeAuthMode"] = mode;
        f.Settings["Agent:ProviderApiKeyEnvRef"] = reference;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, _ => Placeholder));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("oauth")]
    [InlineData("symlink")]
    public void Existing_account_cache_or_unsafe_auth_path_refuses(string fault)
    {
        using var f = new Fixture();
        var binding = StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, _ => Placeholder)!;
        var directory = Path.Combine(f.Home, ".codex");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (fault == "manual") Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
        if (fault == "oauth") File.WriteAllText(Path.Combine(directory, "auth.json"), "{\"tokens\":{\"synthetic\":true}}");
        if (fault == "symlink") File.CreateSymbolicLink(Path.Combine(directory, "auth.json"), f.Root);
        var before = File.ReadAllText(f.Root);
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(f.Config(), f.Home, binding, f.Root, _ => Placeholder));
        Assert.Equal(before, File.ReadAllText(f.Root));
    }

    [Fact]
    public void Direct_startup_rejects_wrong_auth_extra_fields_and_unforced_login()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        var auth = Path.Combine(f.Home, ".codex", "auth.json");
        var config = Path.Combine(f.Home, ".codex", "config.toml");
        foreach (var bad in new[] { "{}", "{\"OPENAI_API_KEY\":\"literal\"}", JsonSerializer.Serialize(new { OPENAI_API_KEY = Placeholder, tokens = new { } }) })
        {
            File.WriteAllText(auth, bad);
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder));
        }
        File.WriteAllText(auth, JsonSerializer.Serialize(new { OPENAI_API_KEY = Placeholder }));
        File.WriteAllText(config, File.ReadAllText(config).Replace("forced_login_method = \"api\"\n", ""));
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder));
    }

    [Fact]
    public void Manual_profile_keeps_original_auth_bytes_and_does_not_read_api_environment()
    {
        using var f = new Fixture();
        f.Settings.Remove("Agent:NativeAuthMode");
        f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
        Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
        const string manual = "{\"synthetic_manual\":true}\n";
        File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), manual);
        var c = f.Config();
        string? NoEnvironment(string _) => throw new Exception("manual profile must not discover API credentials");
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, NoEnvironment)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, NoEnvironment);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, NoEnvironment);
        Assert.Equal(manual, File.ReadAllText(Path.Combine(f.Home, ".codex", "auth.json")));
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(f.Home, ".codex")));
        Assert.DoesNotContain("forced_login_method", File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    [Theory]
    [InlineData("key_ref")]
    [InlineData("mixedCaseRef")]
    public void Saved_reference_names_are_accepted_without_resolving_them(string reference)
    {
        using var f = new Fixture();
        f.Settings["Agent:ProviderApiKeyEnvRef"] = reference;
        var c = f.Config();
        string? ChildEnvironment(string key)
        {
            Assert.Equal("OPENAI_API_KEY", key);
            return Placeholder;
        }
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, ChildEnvironment)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, ChildEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_profile_under_symlinked_home_refuses_before_directory_or_file_writes(bool manual)
    {
        using var f = new Fixture();
        if (manual)
        {
            f.Settings.Remove("Agent:NativeAuthMode");
            f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
            Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
            File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), "{\"synthetic\":true}");
        }
        var actual = f.Home + ".actual";
        Directory.Move(f.Home, actual);
        Directory.CreateSymbolicLink(f.Home, actual);
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder));
        Assert.False(Directory.Exists(Path.Combine(actual, ".codex")));
    }

    [Fact]
    public void Revoked_admission_after_validation_cannot_prepare_any_file()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        var path = f.Settings["StockOpenShell:AuthorityFile"]!;
        var record = JsonSerializer.Deserialize<StockEnrollmentAuthority>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(record with { State = "revoked" }));
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder));
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    [Theory]
    [InlineData("Agent:NativeAuthMode", "api_key")]
    [InlineData("Agent:ProviderApiKeyEnvRef", "EXTERNAL_KEY_REF")]
    public void Disabled_stock_cannot_fall_back_with_managed_auth_fields(string key, string value)
    {
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Equal("stock_enrollment_disabled_with_native_auth", Assert.Throws<InvalidOperationException>(
            () => StockOpenShellEnrollment.Validate(c, "/synthetic", () => 10001)).Message);
    }

    [Fact]
    public void Malformed_prepared_auth_is_a_content_free_refusal()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        File.WriteAllText(Path.Combine(f.Home, ".codex", "auth.json"), "synthetic-private-invalid-json");
        Assert.Equal("stock_enrollment_native_not_prepared", Assert.Throws<InvalidOperationException>(
            () => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder)).Message);
    }

    // ── own Telegram bot and journal capture (#492) ────────────────────────

    private const string TelegramPlaceholder = "openshell:resolve:env:v7_TELEGRAM_BOT_TOKEN";
    private const string JournalPlaceholder = "openshell:resolve:env:v7_FLEET_JOURNAL_INGEST_TOKEN";

    /// <summary>The stock-issued child environment: placeholders only, under the provider credential names.</summary>
    private static Func<string, string?> ChildEnvironment(string? telegram = TelegramPlaceholder, string? journal = JournalPlaceholder,
        string? read = null, string? cross = null) => key => key switch
    {
        "OPENAI_API_KEY" => Placeholder,
        StockCredentials.TelegramBotToken => telegram,
        StockCredentials.JournalIngestToken => journal,
        StockCredentials.JournalReadToken => read,
        StockCredentials.JournalCrossChatToken => cross,
        _ => throw new Exception("unexpected environment read"),
    };

    private const string CrossPlaceholder = "openshell:resolve:env:v7_FLEET_JOURNAL_CROSS_CHAT_TOKEN";

    private static void SelectFilesSendCrossChat(Fixture f)
    {
        SelectBotAndJournal(f);
        f.Settings["Journal:FilesEnabled"] = "True";
        f.Settings["Journal:SendEnabled"] = "True";
        f.Settings["Journal:CrossChatEnabled"] = "True";
        File.WriteAllText(Path.Combine(f.Root[..^"root.toml".Length], ".mcp.json"),
            "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"},\"fleet-journal-files\":{\"url\":\"http://127.0.0.1:8091/journal-files/v1/mcp\"}}}");
    }

    [Fact]
    public void Journal_files_send_and_cross_chat_accept_only_the_issued_placeholders()
    {
        using var f = new Fixture();
        SelectFilesSendCrossChat(f);
        f.Settings["Agent:AllowedTools:1"] = "mcp__fleet-journal-files__send_attachment";
        var env = ChildEnvironment(read: ReadPlaceholder, cross: CrossPlaceholder);
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;

        var overlay = StockOpenShellEnrollment.ManagedCredentialOverlay(c, env);
        Assert.Equal(ReadPlaceholder, overlay["Journal:ReadToken"]);
        Assert.Equal(CrossPlaceholder, overlay["Journal:CrossChatToken"]);
        var daemon = new ConfigurationBuilder().AddInMemoryCollection(f.Settings).AddInMemoryCollection(overlay).Build();
        Assert.NotNull(StockOpenShellEnrollment.Validate(daemon, f.Home, () => 10001, f.Root, env));
        var journal = daemon.GetSection(Fleet.Journal.Client.JournalOptions.Section).Get<Fleet.Journal.Client.JournalOptions>()!;
        Assert.Null(journal.DescribeFault());
        Assert.True(journal.FilesEnabled && journal.SendEnabled && journal.CrossChatEnabled);

        // The native config names the agent-local listener, narrowed to the granted tool.
        StockOpenShellEnrollment.PrepareNative(daemon, f.Home, b, f.Root, env);
        StockOpenShellEnrollment.RequirePrepared(daemon, b, f.Home, env);
        Assert.Contains("\n[mcp_servers.fleet-journal-files]\nurl = \"http://127.0.0.1:8091/journal-files/v1/mcp\"\nenabled_tools = [\"send_attachment\"]\n",
            File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    [Theory]
    [InlineData("read_literal")]
    [InlineData("read_missing")]
    [InlineData("read_foreign")]
    [InlineData("cross_literal")]
    [InlineData("cross_missing")]
    [InlineData("cross_without_send")]
    [InlineData("literal_read_slot")]
    [InlineData("files_without_capture")]
    public void Journal_files_send_and_cross_chat_refuse_literal_missing_or_foreign_tokens(string fault)
    {
        using var f = new Fixture();
        SelectFilesSendCrossChat(f);
        string? read = ReadPlaceholder, cross = CrossPlaceholder;
        switch (fault)
        {
            case "read_literal": read = "cj1.read.agent1.synthetic-mac"; break;
            case "read_missing": read = null; break;
            case "read_foreign": read = CrossPlaceholder; break;
            case "cross_literal": cross = "cj1.read-cross-chat.agent1.synthetic-mac"; break;
            case "cross_missing": cross = null; break;
            case "cross_without_send": f.Settings.Remove("Journal:SendEnabled"); break;
            case "literal_read_slot": f.Settings["Journal:ReadToken"] = "cj1.read.agent1.synthetic-mac"; break;
            case "files_without_capture": f.Settings.Remove("StockOpenShell:JournalCapture"); break;
        }
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, ChildEnvironment(read: read, cross: cross)));
        Assert.StartsWith("stock_enrollment_", refusal.Message);
        Assert.DoesNotContain("synthetic", refusal.Message);
    }

    private static void SelectBotAndJournal(Fixture f)
    {
        f.Settings["StockOpenShell:TelegramBotId"] = "424242";
        f.Settings["StockOpenShell:JournalCapture"] = "True"; // the JSON provider's rendering of the orchestrator's true
    }

    [Fact]
    public void Own_bot_and_journal_accept_only_the_issued_placeholders_before_and_after_the_overlay()
    {
        using var f = new Fixture();
        SelectBotAndJournal(f);
        var env = ChildEnvironment();
        var c = f.Config();
        Assert.NotNull(StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)); // validation-only entrypoint run

        var overlay = StockOpenShellEnrollment.ManagedCredentialOverlay(c, env);
        Assert.Equal("424242:" + TelegramPlaceholder, overlay["Telegram:BotToken"]);
        Assert.Equal(JournalPlaceholder, overlay["Journal:IngestToken"]);
        Assert.Equal("true", overlay["Journal:StockPlaceholders"]);
        var daemon = new ConfigurationBuilder().AddInMemoryCollection(f.Settings).AddInMemoryCollection(overlay).Build();
        Assert.NotNull(StockOpenShellEnrollment.Validate(daemon, f.Home, () => 10001, f.Root, env)); // final daemon start
        var journal = daemon.GetSection(Fleet.Journal.Client.JournalOptions.Section).Get<Fleet.Journal.Client.JournalOptions>()!;
        Assert.True(journal.Enabled);
        Assert.Null(journal.DescribeFault());
        Assert.Equal(424242, new global::Telegram.Bot.TelegramBotClientOptions(daemon["Telegram:BotToken"]!).BotId);
    }

    [Theory]
    [InlineData("telegram_env_literal")]
    [InlineData("telegram_env_missing")]
    [InlineData("telegram_env_foreign")]
    [InlineData("journal_env_literal")]
    [InlineData("journal_env_missing")]
    [InlineData("bot_id_invalid")]
    [InlineData("literal_bot_token")]
    [InlineData("other_bot_token")]
    [InlineData("literal_ingest_token")]
    [InlineData("read_token")]
    [InlineData("send")]
    [InlineData("stock_flag_without_capture")]
    [InlineData("manual_profile")]
    public void Literal_missing_foreign_or_unsupported_bot_and_journal_inputs_refuse(string fault)
    {
        using var f = new Fixture();
        SelectBotAndJournal(f);
        string? telegram = TelegramPlaceholder, journal = JournalPlaceholder;
        switch (fault)
        {
            case "telegram_env_literal": telegram = "424242:synthetic-literal-token"; break;
            case "telegram_env_missing": telegram = null; break;
            case "telegram_env_foreign": telegram = "openshell:resolve:env:OPENAI_API_KEY"; break;
            case "journal_env_literal": journal = "cj1.ingest.agent1.synthetic-mac"; break;
            case "journal_env_missing": journal = null; break;
            case "bot_id_invalid": f.Settings["StockOpenShell:TelegramBotId"] = "synthetic"; break;
            case "literal_bot_token": f.Settings["Telegram:BotToken"] = "424242:synthetic-literal-token"; break;
            case "other_bot_token": f.Settings["Telegram:BotToken"] = "777:" + TelegramPlaceholder; break;
            case "literal_ingest_token": f.Settings["Journal:IngestToken"] = "cj1.ingest.agent1.synthetic-mac"; break;
            case "read_token": f.Settings["Journal:ReadToken"] = "cj1.read.agent1.synthetic-mac"; break;
            case "send": f.Settings["Journal:SendEnabled"] = "true"; break;
            case "stock_flag_without_capture":
                f.Settings.Remove("StockOpenShell:JournalCapture");
                f.Settings["Journal:StockPlaceholders"] = "true";
                break;
            case "manual_profile": // the manual profile keeps refusing the bot and journal
                f.Settings.Remove("Agent:NativeAuthMode");
                f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
                Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
                File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), "{\"synthetic\":true}");
                break;
        }
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, ChildEnvironment(telegram, journal)));
        Assert.StartsWith("stock_enrollment_", refusal.Message);
        Assert.DoesNotContain("synthetic", refusal.Message);
    }

    [Theory]
    [InlineData("StockOpenShell:TelegramBotId", "424242")]
    [InlineData("StockOpenShell:JournalCapture", "true")]
    [InlineData("Journal:StockPlaceholders", "true")]
    public void Ordinary_agents_refuse_managed_route_selectors_and_get_no_overlay(string key, string value)
    {
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Equal("stock_enrollment_disabled_with_managed_route", Assert.Throws<InvalidOperationException>(
            () => StockOpenShellEnrollment.Validate(c, "/synthetic", () => 10001)).Message);
        var ordinary = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "424242:synthetic-ordinary-token", ["StockOpenShell:TelegramBotId"] = "424242",
        }).Build();
        Assert.Empty(StockOpenShellEnrollment.ManagedCredentialOverlay(ordinary, _ => throw new Exception("ordinary agents read no placeholder")));
    }

    [Fact]
    public void Ordinary_journal_validation_is_unchanged_and_stock_placeholders_are_capture_only()
    {
        const string ordinary = "cj1.ingest.agent1.syntheticmac";
        Assert.Null(new Fleet.Journal.Client.JournalOptions { IngestToken = ordinary }.DescribeFault());
        Assert.NotNull(new Fleet.Journal.Client.JournalOptions { IngestToken = JournalPlaceholder }.DescribeFault());
        Assert.Null(new Fleet.Journal.Client.JournalOptions { IngestToken = JournalPlaceholder, StockPlaceholders = true }.DescribeFault());
        Assert.NotNull(new Fleet.Journal.Client.JournalOptions { IngestToken = ordinary, StockPlaceholders = true }.DescribeFault());
        Assert.NotNull(new Fleet.Journal.Client.JournalOptions { IngestToken = JournalPlaceholder, StockPlaceholders = true, FilesEnabled = true }.DescribeFault());
    }

    [Fact]
    public async Task Journal_requests_carry_only_the_placeholder_bearer()
    {
        var inner = new ManagedStockAgentTelegramTests.Stub();
        var client = new Fleet.Journal.Client.JournalHttpClient(
            new HttpClient(inner) { BaseAddress = new Uri("https://journal.example.test") }, JournalPlaceholder);
        await client.PostAsync("{}"u8.ToArray(), default);
        var request = Assert.Single(inner.Requests);
        Assert.Equal("https://journal.example.test/journal/v1/messages", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer " + JournalPlaceholder, request.Authorization);
    }

    // ── comms-journal read MCP (#492) ──────────────────────────────────────

    private const string ReadPlaceholder = "openshell:resolve:env:v7_FLEET_JOURNAL_READ_TOKEN";
    private static readonly StockJournalReadRoute JournalReadRoute = new()
    {
        Host = "journal.example.test", Path = "/journal/v1/mcp", Tools = ["get_message", "search_messages"],
    };

    private static Func<string, string?> ReadEnvironment(string? read = ReadPlaceholder) => key => key switch
    {
        "OPENAI_API_KEY" => Placeholder,
        StockCredentials.JournalReadToken => read,
        _ => throw new Exception("unexpected environment read"),
    };

    [Fact]
    public void Comms_journal_read_mcp_carries_only_the_issued_placeholder_header_and_existing_grants()
    {
        using var f = new Fixture(JournalReadRoute);
        f.Settings["Agent:AllowedTools:1"] = "mcp__fleet-comms-journal__get_message";
        f.Settings["Agent:AllowedTools:2"] = "mcp__fleet-comms-journal__not_in_the_route";
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, ReadEnvironment())!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, ReadEnvironment());
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, ReadEnvironment());
        var config = File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml"));
        Assert.Contains("\n[mcp_servers.fleet-comms-journal]\nurl = \"https://journal.example.test/journal/v1/mcp\"\nenabled_tools = [\"get_message\"]\n"
            + "http_headers = { Authorization = \"Bearer " + ReadPlaceholder + "\" }\n", config);
        Assert.DoesNotContain("cj1.", config);
        // A literal in the environment after preparation no longer matches the prepared config.
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, ReadEnvironment("cj1.read.agent1.synthetic-mac")));
    }

    [Theory]
    [InlineData("cj1.read.agent1.synthetic-mac")]
    [InlineData(null)]
    [InlineData("openshell:resolve:env:v7_FLEET_JOURNAL_INGEST_TOKEN")]
    [InlineData("openshell:resolve:env:v7_FLEET_JOURNAL_READ_TOKEN\" }\nurl = \"synthetic")]
    public void Comms_journal_read_mcp_refuses_a_literal_missing_or_foreign_placeholder(string? read)
    {
        using var f = new Fixture(JournalReadRoute);
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, ReadEnvironment(read))!;
        var refusal = Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, ReadEnvironment(read)));
        Assert.Equal("stock_enrollment_journal_read_placeholder_invalid", refusal.Message);
        Assert.False(File.Exists(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    [Fact]
    public void Comms_journal_read_mcp_must_be_exactly_the_record_route()
    {
        using var f = new Fixture(JournalReadRoute with { Path = "/journal/v1/other" });
        File.WriteAllText(Path.Combine(f.Root[..^"root.toml".Length], ".mcp.json"),
            "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"},\"fleet-comms-journal\":{\"url\":\"https://journal.example.test/journal/v1/mcp\"}}}");
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, ReadEnvironment()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "managed-agent-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(_dir, "home");
        public string Root => Path.Combine(_dir, "root.toml");
        public Dictionary<string, string?> Settings { get; } = new();
        public Fixture(StockJournalReadRoute? journalRead = null)
        {
            Directory.CreateDirectory(Home);
            File.WriteAllText(Root, HarnessDefaults.Build(AgentHarness.InputFor(new AgentOptions { Name = "agent1", Role = "synthetic", Provider = "codex", WorkDir = _dir }, _ => null)!).CodexRootToml);
            var authority = Path.Combine(_dir, "authority.json");
            var password = Path.Combine(_dir, "password");
            File.WriteAllText(password, "synthetic");
            File.WriteAllText(authority, JsonSerializer.Serialize(new StockEnrollmentAuthority
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
                AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
                MemoryHost = "memory.example.test", BrokerHost = "broker.example.test", PolicySha256 = new string('b', 64), StockCommit = StockOpenShellEnrollment.StockCommit,
                JournalRead = journalRead,
            }));
            foreach (var (key, value) in new Dictionary<string, string?>
            {
                ["StockOpenShell:Enabled"] = "true", ["StockOpenShell:EnrollmentFile"] = authority,
                ["StockOpenShell:AuthorityFile"] = authority, ["StockOpenShell:BrokerPasswordFile"] = password,
                ["Agent:Name"] = "agent1", ["Agent:ShortName"] = "agent1", ["Agent:Role"] = "synthetic", ["Agent:Provider"] = "codex",
                ["Agent:Model"] = "sol", ["Agent:WorkDir"] = _dir, ["Agent:AllowedTools:0"] = "mcp__memory__memory_get",
                ["Agent:NativeAuthMode"] = "api_key", ["Agent:ProviderApiKeyEnvRef"] = "EXTERNAL_KEY_REF", ["RabbitMq:Host"] = "broker.example.test"
            }) Settings[key] = value;
            File.WriteAllText(Path.Combine(_dir, ".mcp.json"), journalRead is null
                ? "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"}}}"
                : "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"},\"fleet-comms-journal\":{\"url\":\"https://" + journalRead.Host + journalRead.Path + "\"}}}");
        }
        public IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        public void Dispose() => Directory.Delete(_dir, true);
    }
}

/// <summary>#492: the managed adapter's loopback POST cancel is the only extra HTTP route.</summary>
public sealed class ManagedStockAgentLoopbackCancelTests
{
    [Theory]
    [InlineData(true, "POST", "/cancel", "127.0.0.1", true)]
    [InlineData(true, "POST", "/cancel/task%201", "::1", true)]
    [InlineData(true, "POST", "/cancel", "::ffff:127.0.0.1", true)]
    [InlineData(true, "POST", "/cancel", "10.0.0.5", false)]        // non-loopback, whatever headers say
    [InlineData(true, "GET", "/cancel", "127.0.0.1", false)]
    [InlineData(true, "POST", "/cancel_bg/x", "127.0.0.1", false)]
    [InlineData(true, "POST", "/cancel/a/b", "127.0.0.1", false)]
    [InlineData(true, "POST", "/memory", "127.0.0.1", false)]
    [InlineData(false, "POST", "/cancel", "127.0.0.1", false)]      // manual profile keeps health-only
    public void Only_managed_loopback_post_cancel_is_admitted(bool managed, string method, string path, string remote, bool allowed) =>
        Assert.Equal(allowed, StockOpenShellEnrollment.AllowsManagedLoopbackCancel(managed, method, path, System.Net.IPAddress.Parse(remote)));

    [Fact]
    public void Missing_remote_address_is_refused() =>
        Assert.False(StockOpenShellEnrollment.AllowsManagedLoopbackCancel(true, "POST", "/cancel", null));
}

/// <summary>
/// #492: the shipped guard and cancel routes on a real Kestrel host over actual loopback TCP. Non-loopback
/// refusal is covered by <see cref="ManagedStockAgentLoopbackCancelTests"/>.
/// </summary>
public sealed class ManagedStockAgentCancelHostTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> HostAsync(bool stockMode, bool managed)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        // Explicit loopback listen overrides any ASPNETCORE_HTTP_PORTS the host environment carries.
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "codex" });
        builder.Services.AddSingleton(new TaskManager(options, NSubstitute.Substitute.For<IAgentExecutor>(),
            new SessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskManager>.Instance));
        var app = builder.Build();
        app.UseControlGuard(stockMode, managed);
        app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok());
        app.MapCancelEndpoints();
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    [Theory]
    [InlineData(true, "POST", "/cancel", 200)]
    [InlineData(true, "POST", "/cancel/task%201", 200)]
    [InlineData(true, "GET", "/cancel", 404)]
    [InlineData(true, "POST", "/cancel_bg/x", 404)]
    [InlineData(false, "POST", "/cancel", 404)] // manual profile keeps the health-only surface
    public async Task Managed_loopback_cancel_reaches_the_real_endpoint_only_when_admitted(bool managed, string method, string path, int status)
    {
        var (app, client) = await HostAsync(stockMode: true, managed);
        try
        {
            using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(200, (int)(await client.GetAsync("/health")).StatusCode);
        }
        finally { client.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// #492: the managed Telegram SDK seam. The real token stays outside; Telegram.Bot runs on the wrapped
/// stock placeholder and every request leaves as exactly the stock-substitutable API or file URL.
/// </summary>
public sealed class ManagedStockAgentTelegramTests
{
    private const string P = "openshell:resolve:env:v7_TELEGRAM_BOT_TOKEN";
    private static readonly string Token = StockTelegramHttpHandler.SdkToken(424242, P)!;

    internal sealed class Stub : HttpMessageHandler
    {
        public readonly List<(Uri Uri, string? Authorization)> Requests = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(Respond?.Invoke(request) ?? new HttpResponseMessage(System.Net.HttpStatusCode.Accepted));
        }
    }

    [Theory]
    [InlineData("/bot{T}/getUpdates", "/bot{P}/getUpdates")]
    [InlineData("/bot{T}/sendMessage", "/bot{P}/sendMessage")]
    [InlineData("/file/bot{T}/photos/file_1.jpg", "/file/bot{P}/photos/file_1.jpg")]
    public void Api_and_file_urls_are_rewritten_to_exactly_the_placeholder_path(string sent, string expected)
    {
        var handler = new StockTelegramHttpHandler(Token, new Stub());
        Assert.Equal("https://api.telegram.org" + expected.Replace("{P}", P),
            handler.Rewrite(new Uri("https://api.telegram.org" + sent.Replace("{T}", Token))).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://other.example.test/bot{T}/getMe")]
    [InlineData("http://api.telegram.org/bot{T}/getMe")]
    [InlineData("https://api.telegram.org:8443/bot{T}/getMe")]
    [InlineData("https://user@api.telegram.org/bot{T}/getMe")]
    [InlineData("https://api.telegram.org/bot{T}/getMe?offset=1")]
    [InlineData("https://api.telegram.org/bot424242:synthetic-literal-token/getMe")]
    [InlineData("https://api.telegram.org/bot{P}/getMe")]
    [InlineData("https://api.telegram.org/bot{T}/getMe/extra")]
    [InlineData("https://api.telegram.org/bot{T}/")]
    [InlineData("https://api.telegram.org/file/bot{T}/")]
    [InlineData("https://api.telegram.org/file/bot{T}/openshell:resolve:env:OPENAI_API_KEY")]
    [InlineData("https://api.telegram.org/other/bot{T}/getMe")]
    public async Task Other_hosts_shapes_and_literal_tokens_are_refused_before_the_network(string url)
    {
        var inner = new Stub();
        using var client = new HttpClient(new StockTelegramHttpHandler(Token, inner));
        var refusal = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url.Replace("{T}", Token).Replace("{P}", P)));
        Assert.Equal("stock_telegram_request_refused", refusal.Message);
        Assert.Empty(inner.Requests);
    }

    [Theory]
    [InlineData("424242:synthetic-literal-token")]
    [InlineData("424242:openshell:resolve:env:OPENAI_API_KEY")]
    [InlineData("0:openshell:resolve:env:TELEGRAM_BOT_TOKEN")]
    [InlineData("0424242:openshell:resolve:env:TELEGRAM_BOT_TOKEN")]
    [InlineData("openshell:resolve:env:TELEGRAM_BOT_TOKEN")]
    [InlineData("424242:openshell:resolve:env:TELEGRAM_BOT_TOKEN\n")]
    public void Handler_accepts_only_the_wrapped_issued_placeholder(string token)
    {
        Assert.Throws<InvalidOperationException>(() => new StockTelegramHttpHandler(token, new Stub()));
        Assert.Null(StockTelegramHttpHandler.SdkToken(424242, "synthetic-literal-token"));
        Assert.Null(StockTelegramHttpHandler.SdkToken(0, P));
    }

    [Fact]
    public void Wrapped_token_passes_telegram_bot_validation_with_the_public_bot_id()
    {
        Assert.Equal("424242:" + P, Token);
        Assert.Equal(424242, new global::Telegram.Bot.TelegramBotClientOptions(Token).BotId);
        Assert.Equal(424242, new global::Telegram.Bot.TelegramBotClient(Token, StockTelegramHttpHandler.CreateClient(Token)).BotId);
    }

    [Fact]
    public async Task Real_sdk_poll_send_and_file_calls_reach_the_inner_handler_on_the_exact_placeholder_paths()
    {
        static HttpResponseMessage Json(string json) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        var inner = new Stub
        {
            Respond = request => request.RequestUri!.AbsolutePath switch
            {
                var path when path.StartsWith("/file/", StringComparison.Ordinal) => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
                var path when path.EndsWith("/getMe", StringComparison.Ordinal) =>
                    Json("{\"ok\":true,\"result\":{\"id\":424242,\"is_bot\":true,\"first_name\":\"synthetic\",\"username\":\"synthetic_bot\"}}"),
                var path when path.EndsWith("/getUpdates", StringComparison.Ordinal) => Json("{\"ok\":true,\"result\":[]}"),
                _ => Json("{\"ok\":true,\"result\":{\"message_id\":7,\"date\":0,\"chat\":{\"id\":1001,\"type\":\"private\"}}}"),
            },
        };
        var bot = new global::Telegram.Bot.TelegramBotClient(Token, new HttpClient(new StockTelegramHttpHandler(Token, inner)));

        Assert.Equal("synthetic_bot", (await global::Telegram.Bot.TelegramBotClientExtensions.GetMe(bot)).Username);
        Assert.Empty(await global::Telegram.Bot.TelegramBotClientExtensions.GetUpdates(bot));
        Assert.Equal(7, (await global::Telegram.Bot.TelegramBotClientExtensions.SendMessage(bot, 1001, "synthetic")).MessageId);
        using var file = new MemoryStream();
        await bot.DownloadFile("photos/file_1.jpg", file);
        Assert.Equal([1, 2, 3], file.ToArray());

        Assert.Equal([$"https://api.telegram.org/bot{P}/getMe", $"https://api.telegram.org/bot{P}/getUpdates",
            $"https://api.telegram.org/bot{P}/sendMessage", $"https://api.telegram.org/file/bot{P}/photos/file_1.jpg"],
            inner.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.DoesNotContain(inner.Requests, r => r.Uri.AbsoluteUri.Contains("424242", StringComparison.Ordinal));
    }
}
