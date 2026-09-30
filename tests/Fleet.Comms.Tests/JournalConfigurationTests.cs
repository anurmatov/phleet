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
    [InlineData("0", 0)]
    [InlineData("0,", 0)]
    [InlineData(" 0 ,-100111", 1)]
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
    /// <c>0</c> is what <c>setup.sh</c> writes when no group is configured.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("0", "")]
    [InlineData("0", "-100111")]
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

    // ── media validation (#388) ──────────────────────────────────────────────

    /// <summary>
    /// Blank endpoint means media does not exist, and everything else is then irrelevant. The
    /// default state of every deployment, so it is the case that must never demand credentials.
    /// </summary>
    [Fact]
    public void Media_off_needs_nothing_and_validates_clean()
    {
        var options = EnabledOptions(Key);

        Assert.False(options.Media.Enabled);
        options.ValidateMedia();
    }

    /// <summary>
    /// Setting the endpoint turns the feature on, and every other field becomes required. A media
    /// configuration that half-exists is worse than none: uploads would open rows against a bucket
    /// nobody can address.
    /// </summary>
    [Theory]
    [InlineData("", "bucket", "media_bucket_invalid")]
    [InlineData("", "access", "media_access_key_invalid")]
    [InlineData("", "secret", "media_secret_key_invalid")]
    [InlineData("", "region", "media_region_invalid")]
    public void An_enabled_media_section_requires_every_field(string url, string missing, string code)
    {
        var options = EnabledOptions(Key);
        options.Media.Endpoint = "http://comms-minio:9000";
        options.Media.AccessKey = "runtime";
        options.Media.SecretKey = "runtime-secret";
        Assert.True(options.Media.Enabled, "the endpoint is the enabling key");

        switch (missing)
        {
            case "bucket": options.Media.Bucket = ""; break;
            case "access": options.Media.AccessKey = ""; break;
            case "secret": options.Media.SecretKey = ""; break;
            case "region": options.Media.Region = ""; break;
        }

        Assert.StartsWith(code,
            Assert.Throws<InvalidOperationException>(options.ValidateMedia).Message);
    }

    [Theory]
    [InlineData("http://comms-minio:9000")]
    [InlineData("https://media.internal:9000")]
    public void A_complete_media_configuration_validates(string url)
    {
        var options = EnabledOptions(Key);
        options.Media.Endpoint = url;
        options.Media.AccessKey = "runtime";
        options.Media.SecretKey = "runtime-secret";

        options.ValidateMedia();
    }

    [Theory]
    [InlineData("comms-minio:9000")]
    [InlineData("ftp://comms-minio:9000")]
    [InlineData("not a url")]
    public void The_media_endpoint_must_be_an_absolute_http_url(string url)
    {
        var options = EnabledOptions(Key);
        options.Media.Endpoint = url;

        Assert.StartsWith("media_endpoint_invalid",
            Assert.Throws<InvalidOperationException>(options.ValidateMedia).Message);
    }

    /// <summary>
    /// Media without a journal is a configuration mistake, not a mode. There is no table to put an
    /// object row in, so uploads would write bytes no row can find, name or ever retire.
    /// </summary>
    [Fact]
    public void Media_requires_the_journal()
    {
        var options = new CommsOptions { ConversationConnectionString = "Server=x;Port=1;User ID=u;Password=p;Database=d;" };
        options.Media.Endpoint = "http://comms-minio:9000";
        // Fully populated apart from the journal: `ValidateMedia` checks the bucket's own fields
        // FIRST, so a test that left them blank would assert the wrong code and pass for the wrong
        // reason.
        options.Media.AccessKey = "runtime";
        options.Media.SecretKey = "runtime-secret";

        Assert.StartsWith("media_requires_journal",
            Assert.Throws<InvalidOperationException>(options.ValidateMedia).Message);
    }

    /// <summary>
    /// ⚠️ A validation failure must never quote the credential it is complaining about. The codes
    /// above name fields; this is the assertion that keeps the message safe to log.
    /// </summary>
    [Fact]
    public void A_media_failure_names_the_field_and_never_the_secret()
    {
        const string secret = "sQu3rrel-THis-Must-Not-Leak-9";
        var options = EnabledOptions(Key);
        options.Media.Endpoint = "http://comms-minio:9000";
        options.Media.AccessKey = "runtime";
        options.Media.SecretKey = secret;
        options.Media.Region = "";

        var message = Assert.Throws<InvalidOperationException>(options.ValidateMedia).Message;
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Unreachable_media_starts_the_real_journal_listener_degraded()
    {
        var journal = FreePort();
        var env = MediaEnvironment($"http://127.0.0.1:{FreePort()}");
        env["Comms__Journal__Url"] = $"http://127.0.0.1:{journal}";
        using var process = Start(env);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!CanConnect(journal))
            {
                if (process.HasExited) Assert.Fail(await stderr);
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(25), "journal listener never started");
                await Task.Delay(100);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        Assert.Contains("media_state=degraded", await stderr, StringComparison.Ordinal);
        await stdout;
    }

    [Fact]
    public async Task Signed_head_success_with_anonymous_transport_failure_starts_degraded()
    {
        var endpoint = FreePort();
        var journal = FreePort();
        using var listener = new TcpListener(IPAddress.Loopback, endpoint);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var reader = new StreamReader(client.GetStream());
                    var firstLine = await reader.ReadLineAsync(stop.Token);
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                    if (firstLine?.StartsWith("HEAD ", StringComparison.Ordinal) == true)
                        await client.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                    // GET closes without any HTTP answer: this is a transport failure, not 200/500.
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var env = MediaEnvironment($"http://127.0.0.1:{endpoint}");
        env["Comms__Journal__Url"] = $"http://127.0.0.1:{journal}";
        using var process = Start(env);
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!CanConnect(journal))
            {
                if (process.HasExited) Assert.Fail(await stderr);
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "journal never started degraded");
                await Task.Delay(50);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            stop.Cancel();
            await serving;
        }
        Assert.Contains("media_state=degraded", await stderr);
        await stdout;
    }

    [Fact]
    public async Task Shared_probe_budget_expiry_after_signed_head_starts_degraded()
    {
        var endpoint = FreePort();
        var journal = FreePort();
        using var listener = new TcpListener(IPAddress.Loopback, endpoint);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var anonymousRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            try
            {
                // HEAD uses over half the shared eight-second budget. GET must outlive the
                // remainder but not its own five-second timeout, so caller cancellation wins.
                using var head = await listener.AcceptTcpClientAsync(stop.Token);
                using var headReader = new StreamReader(head.GetStream());
                Assert.StartsWith("HEAD ", await headReader.ReadLineAsync(stop.Token));
                while (await headReader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                await Task.Delay(TimeSpan.FromSeconds(4.5), stop.Token);
                await head.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                head.Close();

                using var get = await listener.AcceptTcpClientAsync(stop.Token);
                using var getReader = new StreamReader(get.GetStream());
                Assert.StartsWith("GET ", await getReader.ReadLineAsync(stop.Token));
                while (await getReader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                anonymousRequested.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var env = MediaEnvironment($"http://127.0.0.1:{endpoint}");
        env["Comms__Journal__Url"] = $"http://127.0.0.1:{journal}";
        using var process = Start(env);
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            await anonymousRequested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var deadline = Stopwatch.StartNew();
            while (!CanConnect(journal))
            {
                if (process.HasExited) Assert.Fail(await stderr);
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "journal never started degraded");
                await Task.Delay(50);
            }
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", JournalTestHost.Token("ingest"));
            using var response = await http.PostAsync(
                $"http://127.0.0.1:{journal}/journal/v1/uploads",
                new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                { sha256 = new string('a', 64), byteSize = 1, mimeType = "image/jpeg" })));
            // A valid, authenticated upload declaration must still find the media gate closed.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            stop.Cancel();
            await serving;
        }
        Assert.Contains("media_state=degraded", await stderr);
        await stdout;
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Anonymous_probe_requires_exactly_403(int status)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            // One signed HEAD, then one unsigned GET.
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = context.Request.HttpMethod == "HEAD" ? 200 : status;
                context.Response.Close();
            }
        });
        var (exit, _, stderr, _) = await RunAsync(
            MediaEnvironment($"http://127.0.0.1:{port}"), TimeSpan.FromSeconds(15));
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, exit);
        Assert.Contains("bucket_public", stderr, StringComparison.Ordinal);
    }

    private Dictionary<string, string> MediaEnvironment(string endpoint)
    {
        var env = BaseEnvironment();
        AddConversations(env);
        env["Comms__Journal__Enabled"] = "true";
        env["Comms__Journal__TokenKeys"] = Key;
        env["Comms__Journal__Url"] = $"http://127.0.0.1:{FreePort()}";
        env["Comms__Media__Endpoint"] = endpoint;
        env["Comms__Media__AccessKey"] = "synthetic-runtime";
        env["Comms__Media__SecretKey"] = "synthetic-secret";
        return env;
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
