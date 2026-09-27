using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Fleet.Comms.Configuration;

namespace Fleet.Comms.Tests;

/// <summary>
/// Journal configuration: off by default, the exclusion-list rule, and startup validation through
/// the REAL entry point (#375 AC10, AC14, AC16a).
/// </summary>
public sealed class JournalConfigurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "journal-config-" + Guid.NewGuid().ToString("N"));

    public JournalConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_journal_is_off_by_default_and_validates_as_a_no_op()
    {
        var options = new CommsOptions();

        Assert.False(options.Journal.Enabled);
        Assert.Equal("http://0.0.0.0:8083", options.Journal.Url);
        Assert.Equal(TimeSpan.FromDays(365), options.Journal.MessageRetention);

        // Nothing is required of an install that did not opt in.
        options.ValidateJournal();
    }

    // ── the exclusion list (AC16a) ───────────────────────────────────────────

    [Theory]
    [InlineData("", 0)]
    [InlineData(",", 0)]
    [InlineData("-1001, ", 1)]
    [InlineData(" -5 ,-5", 1)]
    [InlineData("+7,-7", 2)]
    public void A_valid_list_parses_ignoring_blank_elements_and_collapsing_duplicates(string value, int count)
    {
        Assert.Equal(count, JournalOptions.ParseExcludedChatIds(value).Count);
    }

    [Fact]
    public void The_last_example_holds_one_id()
    {
        Assert.Equal([-5L], JournalOptions.ParseExcludedChatIds(" -5 ,-5").ToArray());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("-1001,abc")]
    [InlineData("1 2")]
    public void An_invalid_element_is_refused(string value)
    {
        Assert.Throws<FormatException>(() => JournalOptions.ParseExcludedChatIds(value));
    }

    [Fact]
    public void More_than_256_distinct_ids_are_refused_and_256_are_not()
    {
        var at = string.Join(',', Enumerable.Range(1, 256));
        var over = string.Join(',', Enumerable.Range(1, 257));

        Assert.Equal(256, JournalOptions.ParseExcludedChatIds(at).Count);
        Assert.Throws<FormatException>(() => JournalOptions.ParseExcludedChatIds(over));
    }

    /// <summary>
    /// The compose default is <c>${FLEET_GROUP_CHAT_ID:-},${FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS:-}</c>,
    /// so with neither set it is a lone comma, and with only the group set it has an empty tail.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("-1009876543210", "")]
    [InlineData("-1009876543210", "-100111,-100222")]
    [InlineData("", "-100111")]
    public void The_compose_default_resolution_is_valid(string group, string extra)
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.example.yml"));
        const string line = "Comms__Journal__ExcludedChatIds=${FLEET_GROUP_CHAT_ID:-},${FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS:-}";
        Assert.Contains(line, compose, StringComparison.Ordinal);

        var resolved = $"{group},{extra}";
        var options = EnabledOptions(Key, excluded: resolved);

        options.ValidateJournal();
    }

    // ── validation codes, in process ─────────────────────────────────────────

    [Theory]
    [InlineData("http://0.0.0.0:8083", true)]
    [InlineData("http://*:8083", true)]
    [InlineData("https://journal.internal:8443", true)]
    [InlineData("not a url", false)]
    [InlineData("ftp://0.0.0.0:8083", false)]
    [InlineData("", false)]
    public void The_url_must_be_an_absolute_http_url(string url, bool valid)
    {
        var options = EnabledOptions(Key);
        options.Journal.Url = url;

        if (valid) options.ValidateJournal();
        else Assert.StartsWith("journal_url_invalid", Assert.Throws<InvalidOperationException>(options.ValidateJournal).Message);
    }

    [Fact]
    public void Retention_below_one_day_is_refused()
    {
        var options = EnabledOptions(Key);
        options.Journal.MessageRetention = TimeSpan.FromHours(23);

        Assert.StartsWith("journal_retention_invalid",
            Assert.Throws<InvalidOperationException>(options.ValidateJournal).Message);
    }

    // ── the real entry point (AC14) ──────────────────────────────────────────

    private static readonly string Key = JournalTestHost.KeyA;

    /// <summary>A key that is too short, and distinctive enough to find in any output.</summary>
    private const string ShortSecret = "TOOSHORTsecretKEYbytes";

    public static TheoryData<string, string, string?, string?, string?> StartupCases() => new()
    {
        // code, key, excluded ids, url, conversations ("on" or null)
        { "journal_requires_conversations", Key, null, null, null },
        { "journal_key_invalid", "", null, null, "on" },
        { "journal_key_invalid", ShortSecret, null, null, "on" },
        { "journal_key_invalid", $"{Key},{ShortSecret}", null, null, "on" },
        { "journal_excluded_ids_invalid", Key, "abc", null, "on" },
        { "journal_excluded_ids_invalid", Key, "0", null, "on" },
        { "journal_url_invalid", Key, null, "not a url", "on" },
    };

    /// <summary>
    /// Each startup failure is a process that exits 1 within 10 s, names its code, and prints no key
    /// bytes on either stream.
    /// </summary>
    [Theory]
    [MemberData(nameof(StartupCases))]
    public async Task Each_startup_failure_exits_1_promptly_without_the_key(
        string code, string keys, string? excluded, string? url, string? conversations)
    {
        var env = BaseEnvironment();
        env["Comms__Journal__Enabled"] = "true";
        env["Comms__Journal__TokenKeys"] = keys;
        if (excluded is not null) env["Comms__Journal__ExcludedChatIds"] = excluded;
        if (url is not null) env["Comms__Journal__Url"] = url;
        if (conversations is not null) AddConversations(env);

        var (exit, stdout, stderr, elapsed) = await RunAsync(env, TimeSpan.FromSeconds(10));

        Assert.Equal(1, exit);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
        Assert.Contains(code, stderr, StringComparison.Ordinal);

        foreach (var secret in new[] { Key, ShortSecret })
        {
            Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, stderr, StringComparison.Ordinal);
        }
    }

    /// <summary>A journal port already in use stops the process with its own code.</summary>
    [Fact]
    public async Task A_journal_port_in_use_exits_1_with_journal_bind_failed()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var env = BaseEnvironment();
        AddConversations(env);
        env["Comms__Journal__Enabled"] = "true";
        env["Comms__Journal__TokenKeys"] = Key;
        env["Comms__Journal__Url"] = $"http://127.0.0.1:{port}";

        var (exit, _, stderr, _) = await RunAsync(env, TimeSpan.FromSeconds(30));

        Assert.Equal(1, exit);
        Assert.Contains("journal_bind_failed", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>Comms__Journal__Enabled</c> unset, the configured journal address is never bound,
    /// while the service itself is up (AC10).
    /// </summary>
    [Fact]
    public async Task With_the_journal_unset_nothing_listens_on_its_address()
    {
        var north = FreePort();
        var journal = FreePort();

        var env = BaseEnvironment();
        env["ASPNETCORE_URLS"] = $"http://127.0.0.1:{north}";
        env["Comms__OpsUrl"] = $"http://127.0.0.1:{FreePort()}";
        env["Comms__Journal__Url"] = $"http://127.0.0.1:{journal}";

        using var process = Start(env);
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!CanConnect(north))
            {
                if (process.HasExited)
                    Assert.Fail("the service exited: " + await process.StandardError.ReadToEndAsync());
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "the north listener never came up");
                await Task.Delay(100);
            }

            Assert.False(CanConnect(journal), "something is listening on the journal address");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CommsOptions EnabledOptions(string keys, string excluded = "") => new()
    {
        ConversationConnectionString = "Server=unused;",
        Journal = new JournalOptions { Enabled = true, TokenKeys = keys, ExcludedChatIds = excluded },
    };

    private Dictionary<string, string> BaseEnvironment() => new()
    {
        ["DOTNET_CONTENTROOT"] = _directory,
        ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
        ["Comms__AuthStorePath"] = Path.Combine(_directory, "auth.db"),
    };

    private static void AddConversations(Dictionary<string, string> env)
    {
        // Never dialled: validation stops the process before any host starts.
        env["Comms__ConversationConnectionString"] = "Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;";
        env["Comms__SouthBearerToken"] = "a-south-credential";
        env["Comms__AgentName"] = "example-agent";
        env["Comms__SouthUrl"] = $"http://127.0.0.1:{FreePort()}";
        env["Comms__OpsUrl"] = $"http://127.0.0.1:{FreePort()}";
    }

    private Process Start(Dictionary<string, string> env)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Fleet.Comms.dll");
        Assert.True(File.Exists(assembly), $"expected the service assembly beside the tests: {assembly}");

        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _directory,
        };
        info.ArgumentList.Add(assembly);

        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("Comms__", StringComparison.Ordinal)).ToList())
            info.Environment.Remove(key);
        foreach (var (key, value) in env)
            info.Environment[key] = value;

        return Process.Start(info)!;
    }

    private async Task<(int Exit, string Stdout, string Stderr, TimeSpan Elapsed)> RunAsync(
        Dictionary<string, string> env, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        using var process = Start(env);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var deadline = new CancellationTokenSource(limit);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"the process did not exit within {limit}");
        }

        return (process.ExitCode, await stdout, await stderr, clock.Elapsed);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool CanConnect(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, "could not locate the repository root");
        return directory!.FullName;
    }
}
