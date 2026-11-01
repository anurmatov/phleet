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

    // ── #502 subscription native preparation (synthetic handles and account only) ─────────────

    private const string Handle = "openshell:resolve:env:v3_CODEX_AUTH_ACCESS_TOKEN";
    private const string Account = "synthetic-account_01";

    private static Fixture SubscriptionFixture(string memoryName = "memory")
    {
        var f = new Fixture(memoryName: memoryName);
        f.Settings["Agent:NativeAuthMode"] = "subscription";
        f.Settings["Agent:ProviderApiKeyEnvRef"] = null;
        f.Settings["StockOpenShell:SubscriptionAccountId"] = Account;
        f.Settings["StockOpenShell:SubscriptionProvider"] = "agent1-codex-sub";
        f.Settings["StockOpenShell:SubscriptionOwnerGeneration"] = "4f0c6c8e-6f39-4f5b-9d50-6b1b4b7e2a11";
        return f;
    }

    private static string? SubscriptionEnv(string name) => name == "CODEX_AUTH_ACCESS_TOKEN" ? Handle : null;

    [Fact]
    public void Subscription_prepares_placeholder_only_cache_and_exact_native_config()
    {
        using var f = SubscriptionFixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, SubscriptionEnv)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, SubscriptionEnv);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, SubscriptionEnv);

        using var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Home, ".codex", "auth.json")));
        var root = auth.RootElement;
        Assert.Equal("chatgptAuthTokens", root.GetProperty("auth_mode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("OPENAI_API_KEY").ValueKind);
        var tokens = root.GetProperty("tokens");
        Assert.Equal(Handle, tokens.GetProperty("access_token").GetString());
        Assert.Equal("", tokens.GetProperty("refresh_token").GetString());
        Assert.Equal(Account, tokens.GetProperty("account_id").GetString());
        Assert.EndsWith(".placeholder", tokens.GetProperty("id_token").GetString());

        // Forced root keys first, generated defaults unchanged (one [analytics]), the exact descriptor, one [features]
        // block and exactly one native memory table narrowed to memory_get.
        var config = File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml"));
        var defaults = File.ReadAllText(f.Root);
        Assert.Equal("forced_login_method = \"chatgpt\"\nforced_chatgpt_workspace_id = \"" + Account + "\"\nmodel_provider = \"openai-http\"\n"
            + defaults
            + "\n[model_providers.openai-http]\nname = \"OpenAI\"\nrequires_openai_auth = true\nsupports_websockets = false\n"
            + "supports_standalone_web_search = true\nhttp_headers = { version = \"0.164.0\" }\n"
            + "\n[features]\nplugins = false\napps = false\n"
            + "\n[mcp_servers.memory]\nurl = \"https://memory.example.test/mcp\"\nenabled_tools = [\"memory_get\"]\n", config);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(config, @"^\[analytics\]$", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(config, @"^\[mcp_servers\.", System.Text.RegularExpressions.RegexOptions.Multiline));
        foreach (var absent in new[] { "base_url", "env_key", "experimental_bearer_token", "aws", "Authorization", "\nauth =" })
            Assert.DoesNotContain(absent, config);
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex-host")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(f.Home, ".codex")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Home, ".codex", "auth.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Home, ".codex", "config.toml")));
        }
        Assert.True(StockOpenShellEnrollment.IsManaged(c));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("literal")]
    [InlineData("foreign")]
    [InlineData("api_env")]
    [InlineData("api_ref")]
    [InlineData("host_cache")]
    [InlineData("account")]
    [InlineData("provider")]
    [InlineData("extra_route")]
    [InlineData("no_memory_read")]
    public void Subscription_wrong_input_exits_before_registration_and_writes(string fault)
    {
        using var f = SubscriptionFixture();
        Func<string, string?> env = SubscriptionEnv;
        switch (fault)
        {
            case "missing": env = _ => null; break;
            case "literal": env = n => n == "CODEX_AUTH_ACCESS_TOKEN" ? "synthetic-literal-access-value" : null; break;
            case "foreign": env = n => n == "CODEX_AUTH_ACCESS_TOKEN" ? "openshell:resolve:env:OPENAI_API_KEY" : null; break;
            case "api_env": env = n => n == "OPENAI_API_KEY" ? "openshell:resolve:env:OPENAI_API_KEY" : SubscriptionEnv(n); break;
            case "api_ref": f.Settings["Agent:ProviderApiKeyEnvRef"] = "EXTERNAL_KEY_REF"; break;
            case "host_cache": Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host")); break;
            case "account": f.Settings["StockOpenShell:SubscriptionAccountId"] = "bad account"; break;
            case "provider": f.Settings["StockOpenShell:SubscriptionProvider"] = "other-codex-sub"; break;
            case "extra_route": f.Settings["StockOpenShell:TelegramBotId"] = "424242"; break;
            // #512: any fixed read now suffices, so only a grant set without one refuses.
            case "no_memory_read": f.Settings["Agent:AllowedTools:0"] = "mcp__memory__memory_store"; break;
        }
        var c = f.Config();
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
            StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        });
        Assert.StartsWith("stock_enrollment_", error.Message);
        Assert.DoesNotContain("synthetic-literal", error.Message);
        Assert.False(File.Exists(Path.Combine(f.Home, ".codex", "auth.json")));
    }

    [Fact]
    public void Subscription_registration_refuses_drifted_cache_config_or_stub()
    {
        using var f = SubscriptionFixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, SubscriptionEnv)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, SubscriptionEnv);
        var authPath = Path.Combine(f.Home, ".codex", "auth.json");
        var configPath = Path.Combine(f.Home, ".codex", "config.toml");
        var good = File.ReadAllText(authPath);
        var goodConfig = File.ReadAllText(configPath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(good)!;
        foreach (var mutate in new Action<System.Text.Json.Nodes.JsonNode>[]
        {
            n => n["tokens"]!["refresh_token"] = "synthetic-refresh",
            n => n["tokens"]!["access_token"] = "openshell:resolve:env:v4_CODEX_AUTH_ACCESS_TOKEN",
            n => n["tokens"]!["account_id"] = "other-account",
            n => n["tokens"]!["id_token"] = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln",
            n => n["OPENAI_API_KEY"] = "openshell:resolve:env:OPENAI_API_KEY",
            n => n["extra"] = true,
        })
        {
            var copy = node.DeepClone();
            mutate(copy);
            File.WriteAllText(authPath, copy.ToJsonString());
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, SubscriptionEnv));
        }
        File.WriteAllText(authPath, good);
        foreach (var drift in new[] { goodConfig + "\n[mcp_servers.other]\nurl = \"https://other.example.test/mcp\"\n",
                     goodConfig.Replace("supports_websockets = false", "supports_websockets = true"),
                     goodConfig.Replace("enabled_tools = [\"memory_get\"]", "enabled_tools = [\"memory_get\", \"memory_search\"]") })
        {
            File.WriteAllText(configPath, drift);
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, SubscriptionEnv));
        }
        File.WriteAllText(configPath, goodConfig);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, SubscriptionEnv);
    }

    // ── #505: the native memory route keeps the workload's own endpoint name ─────────────

    private static string MemoryTable(string name, string tools) =>
        $"\n[mcp_servers.{name}]\nurl = \"https://memory.example.test/mcp\"\nenabled_tools = {tools}\n";

    private static int McpTables(string config) =>
        System.Text.RegularExpressions.Regex.Matches(config, @"^\[mcp_servers\.", System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    [Theory]
    [InlineData("memory", "fleet-memory")]
    [InlineData("fleet-memory", "memory")]
    public void Api_profile_registers_one_memory_table_under_the_workload_name_with_only_its_own_grants(string name, string other)
    {
        using var f = new Fixture(memoryName: name);
        f.Settings["Agent:AllowedTools:1"] = $"mcp__{name}__memory_search";
        f.Settings["Agent:AllowedTools:2"] = $"mcp__{other}__memory_list"; // the other accepted name's grant never applies
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder);
        var config = File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml"));
        Assert.EndsWith(MemoryTable(name, "[\"memory_get\",\"memory_search\"]"), config);
        Assert.Equal(1, McpTables(config));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("fleet-memory")]
    public void Subscription_registers_exactly_its_selected_name_reads_under_the_workload_name(string name)
    {
        using var f = SubscriptionFixture(name);
        f.Settings["Agent:AllowedTools:1"] = $"mcp__{name}__memory_search";
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, SubscriptionEnv)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, SubscriptionEnv);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, SubscriptionEnv);
        var config = File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml"));
        // #512: no forced get-only replacement; the same selected-name grants as API-key mode.
        Assert.EndsWith("\n[features]\nplugins = false\napps = false\n" + MemoryTable(name, "[\"memory_get\",\"memory_search\"]"), config);
        Assert.Equal(1, McpTables(config));
    }

    [Theory]
    [InlineData("both", "stock_enrollment_mcp_route_invalid")]
    [InlineData("case_variant", "stock_enrollment_mcp_route_invalid")]
    [InlineData("custom", "stock_enrollment_mcp_route_invalid")]
    [InlineData("plaintext", "stock_enrollment_mcp_route_invalid")]
    [InlineData("other_host", "stock_enrollment_mcp_route_invalid")]
    [InlineData("extra_field", "stock_enrollment_mcp_route_invalid")]
    [InlineData("duplicate_key", "stock_enrollment_profile_invalid")]
    [InlineData("opposite_grant", "stock_enrollment_memory_tools_missing")]
    public void Memory_name_destination_shape_and_grant_faults_refuse_before_any_native_write(string fault, string code)
    {
        using var f = new Fixture(memoryName: "fleet-memory");
        const string url = "{\"url\":\"https://memory.example.test/mcp\"}";
        string? mcp = fault switch
        {
            "both" => "{\"mcpServers\":{\"fleet-memory\":" + url + ",\"memory\":" + url + "}}",
            "case_variant" => "{\"mcpServers\":{\"Fleet-Memory\":" + url + "}}",
            "custom" => "{\"mcpServers\":{\"memory-2\":" + url + "}}",
            "plaintext" => "{\"mcpServers\":{\"fleet-memory\":{\"url\":\"http://memory.example.test/mcp\"}}}",
            "other_host" => "{\"mcpServers\":{\"fleet-memory\":{\"url\":\"https://other.example.test/mcp\"}}}",
            "extra_field" => "{\"mcpServers\":{\"fleet-memory\":{\"url\":\"https://memory.example.test/mcp\",\"headers\":{}}}}",
            "duplicate_key" => "{\"mcpServers\":{\"fleet-memory\":" + url + ",\"fleet-memory\":" + url + "}}",
            _ => null,
        };
        if (mcp is not null) File.WriteAllText(f.McpPath, mcp);
        if (fault == "opposite_grant") f.Settings["Agent:AllowedTools:0"] = "mcp__memory__memory_get";
        var c = f.Config();

        var refusal = Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder));

        Assert.Equal(code, refusal.Message);
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    [Fact]
    public void A_memory_name_changed_after_validation_refuses_preparation_and_registration()
    {
        using var f = new Fixture(memoryName: "fleet-memory");
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        var original = File.ReadAllText(f.McpPath);
        // Both names before preparation: no alias is picked and nothing is written.
        File.WriteAllText(f.McpPath, "{\"mcpServers\":{\"fleet-memory\":{\"url\":\"https://memory.example.test/mcp\"},\"memory\":{\"url\":\"https://memory.example.test/mcp\"}}}");
        Assert.Equal("stock_enrollment_mcp_route_invalid", Assert.Throws<InvalidOperationException>(
            () => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder)).Message);
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
        // Prepared under one name, then renamed: registration no longer matches the prepared native table.
        File.WriteAllText(f.McpPath, original);
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        File.WriteAllText(f.McpPath, original.Replace("fleet-memory", "memory"));
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder));
    }

    [Theory]
    [InlineData("mcp__memory__memory_get")]
    [InlineData("mcp__memory__memory_search")]
    [InlineData("mcp__fleet-memory__memory_store")]
    public void Subscription_under_the_ordinary_name_needs_its_own_read_grant(string grant)
    {
        using var f = SubscriptionFixture("fleet-memory");
        f.Settings["Agent:AllowedTools:0"] = grant;
        var c = f.Config();
        Assert.StartsWith("stock_enrollment_", Assert.Throws<InvalidOperationException>(() =>
        {
            var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, SubscriptionEnv)!;
            StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, SubscriptionEnv);
        }).Message);
        Assert.False(File.Exists(Path.Combine(f.Home, ".codex", "auth.json")));
    }

    // ── #512: explicitly granted read-only memory tools in both managed auth modes ─────────────

    private static Fixture ManagedFixture(string mode, string name) => mode == "subscription" ? SubscriptionFixture(name) : new Fixture(memoryName: name);

    private static Func<string, string?> ManagedEnv(string mode) => mode == "subscription" ? SubscriptionEnv : _ => Placeholder;

    /// <summary>Replaces the configured AllowedTools with exactly these entries.</summary>
    private static void Grants(Fixture f, params string[] grants)
    {
        foreach (var key in f.Settings.Keys.Where(k => k.StartsWith("Agent:AllowedTools:", StringComparison.Ordinal)).ToList()) f.Settings.Remove(key);
        for (var i = 0; i < grants.Length; i++) f.Settings[$"Agent:AllowedTools:{i}"] = grants[i];
    }

    // A2–A5, A11: preparation then registration in both auth modes under either accepted name: the native memory table is
    // exactly the selected-name read grants (deterministic, identical at both stages); the other name's grant never counts.
    [Theory]
    [InlineData("api_key", "memory", "memory_get")]
    [InlineData("api_key", "fleet-memory", "memory_list")]
    [InlineData("api_key", "memory", "memory_search")]
    [InlineData("api_key", "fleet-memory", "memory_get", "memory_list", "memory_search")]
    [InlineData("subscription", "fleet-memory", "memory_get")]
    [InlineData("subscription", "memory", "memory_list")]
    [InlineData("subscription", "fleet-memory", "memory_search")]
    [InlineData("subscription", "memory", "memory_get", "memory_list", "memory_search")]
    public void ManagedMemoryReads_both_modes_register_exactly_the_selected_name_reads(string mode, string name, params string[] reads)
    {
        using var f = ManagedFixture(mode, name);
        var other = name == "memory" ? "fleet-memory" : "memory";
        Grants(f, [.. reads.Select(r => $"mcp__{name}__{r}"), $"mcp__{other}__memory_get", $"mcp__{other}__memory_search"]);
        var env = ManagedEnv(mode);
        var c = f.Config();

        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        var prepared = File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml"));
        // Registration: the same validation, then the prepared native profile must equal the deterministic builder's bytes.
        var registered = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.RequirePrepared(c, registered, f.Home, env);

        Assert.EndsWith(MemoryTable(name, JsonSerializer.Serialize(reads)), prepared);
        Assert.Equal(1, McpTables(prepared));
        Assert.Equal(prepared, File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    // A11: managed memory grants outside the fixed reads, or none at all under the selected name, refuse before any native
    // write instead of being filtered away, in both auth modes.
    [Theory]
    [InlineData("api_key", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_list", "mcp__memory__memory_store")]
    [InlineData("api_key", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_get", "mcp__memory__memory_unknown")]
    [InlineData("api_key", "stock_enrollment_memory_tools_missing", "mcp__memory__memory_delete")]
    [InlineData("api_key", "stock_enrollment_memory_tools_missing", "mcp__fleet-memory__memory_list")]
    [InlineData("subscription", "stock_enrollment_mcp_route_invalid", "mcp__memory__memory_search", "mcp__memory__memory_update")]
    [InlineData("subscription", "stock_enrollment_memory_tools_missing", "mcp__memory__memory_store")]
    [InlineData("subscription", "stock_enrollment_memory_tools_missing", "mcp__fleet-memory__memory_get")]
    public void ManagedMemoryReads_unsupported_or_empty_managed_memory_grants_refuse_before_any_native_write(string mode, string code, params string[] grants)
    {
        using var f = ManagedFixture(mode, "memory");
        Grants(f, grants);
        var env = ManagedEnv(mode);
        var c = f.Config();

        var refusal = Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env));

        Assert.Equal(code, refusal.Message);
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    [Fact]
    public void ManagedMemoryReads_manual_profile_keeps_its_read_intersection()
    {
        using var f = new Fixture();
        f.Settings.Remove("Agent:NativeAuthMode");
        f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
        Grants(f, "mcp__memory__memory_get", "mcp__memory__memory_store");
        Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
        File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), "{\"synthetic_manual\":true}\n");
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => null)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => null);

        Assert.False(StockOpenShellEnrollment.IsManaged(c));
        Assert.EndsWith(MemoryTable("memory", "[\"memory_get\"]"), File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    // A11: registration refuses a native table that no longer matches the current selected-name reads: an edited table, or
    // reads added after preparation (stale native config) until preparation runs again.
    [Theory]
    [InlineData("api_key")]
    [InlineData("subscription")]
    public void ManagedMemoryReads_registration_refuses_a_native_table_drifted_from_the_prepared_reads(string mode)
    {
        using var f = ManagedFixture(mode, "fleet-memory");
        Grants(f, "mcp__fleet-memory__memory_list");
        var env = ManagedEnv(mode);
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        var path = Path.Combine(f.Home, ".codex", "config.toml");
        var good = File.ReadAllText(path);
        Assert.EndsWith(MemoryTable("fleet-memory", "[\"memory_list\"]"), good);

        File.WriteAllText(path, good.Replace("enabled_tools = [\"memory_list\"]", "enabled_tools = [\"memory_list\",\"memory_get\"]"));
        Assert.Equal("stock_enrollment_native_not_prepared",
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, env)).Message);
        File.WriteAllText(path, good);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, env);

        f.Settings["Agent:AllowedTools:1"] = "mcp__fleet-memory__memory_search";
        var widened = f.Config();
        Assert.Equal("stock_enrollment_native_not_prepared",
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(widened, b, f.Home, env)).Message);
        StockOpenShellEnrollment.PrepareNative(widened, f.Home, b, f.Root, env);
        Assert.EndsWith(MemoryTable("fleet-memory", "[\"memory_list\",\"memory_search\"]"), File.ReadAllText(path));
    }

    [Fact]
    public void Subscription_metadata_without_stock_mode_and_api_profile_with_it_both_refuse()
    {
        using var api = new Fixture();
        api.Settings["StockOpenShell:SubscriptionAccountId"] = Account;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(api.Config(), api.Home, () => 10001, api.Root, _ => Placeholder));
        using var off = new Fixture();
        foreach (var key in off.Settings.Keys.Where(k => k.StartsWith("StockOpenShell:", StringComparison.Ordinal)).ToList()) off.Settings.Remove(key);
        off.Settings["Agent:NativeAuthMode"] = null;
        off.Settings["Agent:ProviderApiKeyEnvRef"] = null;
        off.Settings["StockOpenShell:SubscriptionAccountId"] = Account;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(off.Config(), off.Home, () => 10001, off.Root, _ => null));
    }

    // ── #510: the subscription branch keeps the agent's ordinary bot, journal and first-party MCP routes ─────────────

    private const string ServicesMcp = "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"},"
        + "\"fleet-telegram\":{\"url\":\"https://telegram.example.test/mcp\"},\"fleet-temporal\":{\"url\":\"https://temporal.example.test/mcp\"},"
        + "\"fleet-comms-journal\":{\"url\":\"https://journal.example.test/journal/v1/mcp\"}}}";

    /// <summary>A subscription workload whose outside record enrolls Telegram/Temporal routes and the journal read route.</summary>
    private static Fixture SubscriptionServicesFixture()
    {
        var f = SubscriptionFixture();
        var path = f.Settings["StockOpenShell:AuthorityFile"]!;
        File.WriteAllText(path, JsonSerializer.Serialize(StockEnrollmentAuthority.Load(path) with
        {
            Services = new Dictionary<string, StockServiceRoute>
            {
                ["telegram"] = new() { Host = "telegram.example.test", Tools = ["send_message"], Query = "formatting_mode=1" },
                ["temporal"] = new() { Host = "temporal.example.test", Tools = ["temporal_start_workflow"] },
            },
            JournalRead = JournalReadRoute,
        }));
        File.WriteAllText(f.McpPath, ServicesMcp);
        SelectBotAndJournal(f);
        f.Settings["Agent:AllowedTools:1"] = "mcp__fleet-telegram__send_message";
        f.Settings["Agent:AllowedTools:2"] = "mcp__fleet-temporal__temporal_start_workflow";
        f.Settings["Agent:AllowedTools:3"] = "mcp__fleet-comms-journal__get_message";
        return f;
    }

    /// <summary>The stock-issued child environment of a subscription workload with services: placeholders only.</summary>
    private static Func<string, string?> SubscriptionServicesEnv(string? telegram = TelegramPlaceholder, string? journal = JournalPlaceholder,
        string? read = ReadPlaceholder, string? handle = Handle) => key => key switch
    {
        "CODEX_AUTH_ACCESS_TOKEN" => handle,
        StockCredentials.TelegramBotToken => telegram,
        StockCredentials.JournalIngestToken => journal,
        StockCredentials.JournalReadToken => read,
        "OPENAI_API_KEY" or "CODEX_API_KEY" => null,
        _ => throw new Exception("unexpected environment read"),
    };

    [Fact]
    public void Subscription_services_prepare_every_enrolled_route_with_placeholder_slots_and_existing_grants()
    {
        using var f = SubscriptionServicesFixture();
        var env = SubscriptionServicesEnv();
        var c = f.Config();
        Assert.NotNull(StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)); // validation-only entrypoint run

        var overlay = StockOpenShellEnrollment.ManagedCredentialOverlay(c, env);
        Assert.Equal("424242:" + TelegramPlaceholder, overlay["Telegram:BotToken"]);
        Assert.Equal(JournalPlaceholder, overlay["Journal:IngestToken"]);
        var daemon = new ConfigurationBuilder().AddInMemoryCollection(f.Settings).AddInMemoryCollection(overlay).Build();
        var b = StockOpenShellEnrollment.Validate(daemon, f.Home, () => 10001, f.Root, env)!; // final daemon start
        StockOpenShellEnrollment.PrepareNative(daemon, f.Home, b, f.Root, env);
        StockOpenShellEnrollment.RequirePrepared(daemon, b, f.Home, env);

        // Unchanged subscription auth custody: forced ChatGPT/account root keys, the descriptor, suppression, the issued handle.
        var config = File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml"));
        Assert.StartsWith("forced_login_method = \"chatgpt\"\nforced_chatgpt_workspace_id = \"" + Account + "\"\nmodel_provider = \"openai-http\"\n", config);
        Assert.Contains("\n[features]\nplugins = false\napps = false\n" + MemoryTable("memory", "[\"memory_get\"]"), config);
        using (var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Home, ".codex", "auth.json"))))
            Assert.Equal(Handle, auth.RootElement.GetProperty("tokens").GetProperty("access_token").GetString());
        // Every enrolled route, narrowed to the granted tools the record permits; the read route carries only its placeholder.
        Assert.Contains("\n[mcp_servers.fleet-telegram]\nurl = \"https://telegram.example.test/mcp\"\nenabled_tools = [\"send_message\"]\n", config);
        Assert.Contains("\n[mcp_servers.fleet-temporal]\nurl = \"https://temporal.example.test/mcp\"\nenabled_tools = [\"temporal_start_workflow\"]\n", config);
        Assert.Contains("\n[mcp_servers.fleet-comms-journal]\nurl = \"https://journal.example.test/journal/v1/mcp\"\nenabled_tools = [\"get_message\"]\n"
            + "http_headers = { Authorization = \"Bearer " + ReadPlaceholder + "\" }\n", config);
        Assert.Equal(4, McpTables(config));
        foreach (var absent in new[] { "cj1.", "base_url", "env_key", "experimental_bearer_token" }) Assert.DoesNotContain(absent, config);
        // The ordinary readers see placeholder-only slots.
        var journal = daemon.GetSection(Fleet.Journal.Client.JournalOptions.Section).Get<Fleet.Journal.Client.JournalOptions>()!;
        Assert.True(journal.Enabled);
        Assert.Null(journal.DescribeFault());
        Assert.Equal(424242, new global::Telegram.Bot.TelegramBotClientOptions(daemon["Telegram:BotToken"]!).BotId);
    }

    [Theory]
    [InlineData("telegram_literal")]
    [InlineData("telegram_foreign")]
    [InlineData("journal_missing")]
    [InlineData("read_foreign")]
    [InlineData("handle_foreign")]
    [InlineData("literal_bot_slot")]
    [InlineData("extra_route")]
    [InlineData("unenrolled_route")]
    [InlineData("opposite_memory_grant")]
    public void Subscription_services_refuse_literal_missing_or_foreign_handles_and_extra_routes_before_any_write(string fault)
    {
        using var f = SubscriptionServicesFixture();
        string? telegram = TelegramPlaceholder, journal = JournalPlaceholder, read = ReadPlaceholder, handle = Handle;
        switch (fault)
        {
            case "telegram_literal": telegram = "424242:synthetic-literal-token"; break;
            case "telegram_foreign": telegram = Handle; break;
            case "journal_missing": journal = null; break;
            case "read_foreign": read = JournalPlaceholder; break;
            case "handle_foreign": handle = TelegramPlaceholder; break;
            case "literal_bot_slot": f.Settings["Telegram:BotToken"] = "424242:synthetic-literal-token"; break;
            case "extra_route": File.WriteAllText(f.McpPath, ServicesMcp.Replace("}}}", "},\"other\":{\"url\":\"https://other.example.test/mcp\"}}}")); break;
            case "unenrolled_route": File.WriteAllText(f.McpPath, ServicesMcp.Replace(",\"fleet-temporal\":{\"url\":\"https://temporal.example.test/mcp\"}", "")); break;
            case "opposite_memory_grant": f.Settings["Agent:AllowedTools:0"] = "mcp__fleet-memory__memory_get"; break;
        }
        var env = SubscriptionServicesEnv(telegram, journal, read, handle);
        var c = f.Config();

        var refusal = Assert.Throws<InvalidOperationException>(() =>
        {
            var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
            StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        });

        Assert.StartsWith("stock_enrollment_", refusal.Message);
        Assert.DoesNotContain("synthetic", refusal.Message);
        Assert.False(File.Exists(Path.Combine(f.Home, ".codex", "auth.json")));
        Assert.False(File.Exists(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    [Fact]
    public void Subscription_services_registration_refuses_a_widened_or_missing_route_table()
    {
        using var f = SubscriptionServicesFixture();
        var env = SubscriptionServicesEnv();
        var c = new ConfigurationBuilder().AddInMemoryCollection(f.Settings)
            .AddInMemoryCollection(StockOpenShellEnrollment.ManagedCredentialOverlay(f.Config(), env)).Build();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, env)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, env);
        var path = Path.Combine(f.Home, ".codex", "config.toml");
        var good = File.ReadAllText(path);
        const string telegram = "\n[mcp_servers.fleet-telegram]\nurl = \"https://telegram.example.test/mcp\"\nenabled_tools = [\"send_message\"]\n";
        foreach (var drift in new[] { good.Replace("enabled_tools = [\"send_message\"]", "enabled_tools = [\"send_message\", \"send_photo\"]"),
                     good.Replace(telegram, "\n"), good.Replace("enabled_tools = [\"memory_get\"]", "enabled_tools = [\"memory_get\", \"memory_search\"]") })
        {
            File.WriteAllText(path, drift);
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, env));
        }
        File.WriteAllText(path, good);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, env);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "managed-agent-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(_dir, "home");
        public string Root => Path.Combine(_dir, "root.toml");
        public Dictionary<string, string?> Settings { get; } = new();
        public string McpPath => Path.Combine(_dir, ".mcp.json");
        /// <param name="memoryName">#505: the workload's memory endpoint name and the prefix of its <c>memory_get</c> grant.</param>
        public Fixture(StockJournalReadRoute? journalRead = null, string memoryName = "memory")
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
                ["Agent:Model"] = "sol", ["Agent:WorkDir"] = _dir, ["Agent:AllowedTools:0"] = $"mcp__{memoryName}__memory_get",
                ["Agent:NativeAuthMode"] = "api_key", ["Agent:ProviderApiKeyEnvRef"] = "EXTERNAL_KEY_REF", ["RabbitMq:Host"] = "broker.example.test"
            }) Settings[key] = value;
            File.WriteAllText(McpPath, journalRead is null
                ? "{\"mcpServers\":{\"" + memoryName + "\":{\"url\":\"https://memory.example.test/mcp\"}}}"
                : "{\"mcpServers\":{\"" + memoryName + "\":{\"url\":\"https://memory.example.test/mcp\"},\"fleet-comms-journal\":{\"url\":\"https://" + journalRead.Host + journalRead.Path + "\"}}}");
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
