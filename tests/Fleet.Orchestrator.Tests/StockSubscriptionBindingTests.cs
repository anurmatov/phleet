using System.Text;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #502 strict outside-file and stock-output parsers. Synthetic identities only; source checks, not a stock run.
/// </summary>
public sealed class StockSubscriptionBindingTests : IDisposable
{
    internal const string ProviderId = "3f2b8c1e-5d4a-4b6f-9e7d-1a2b3c4d5e6f";
    internal const string Owner = "4f0c6c8e-6f39-4f5b-9d50-6b1b4b7e2a11";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir;

    public StockSubscriptionBindingTests() => _dir = PrivateBase.Create("subscription-binding-");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal static JsonObject Source(string agent = "agent1", string workspace = "ws-a", DateTimeOffset? expires = null, string? profile = null) => new()
    {
        ["version"] = 1, ["purpose"] = "compatibility_only", ["agent"] = agent, ["providerName"] = agent + "-codex-sub",
        ["providerId"] = ProviderId, ["providerWorkspace"] = workspace, ["sandboxWorkspace"] = workspace,
        ["accountId"] = "synthetic-account_01", ["ownerGeneration"] = Owner, ["gatewayGeneration"] = "gateway-1@start",
        ["profileSha256"] = profile ?? new string('c', 64),
        ["expiresAt"] = (expires ?? Now.AddHours(2)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    /// <summary>Writes the outside binding as its owner would: 0600 file in a 0700 directory.</summary>
    internal static string WriteSource(string configDir, JsonObject source, string agent = "agent1")
    {
        var directory = Path.Combine(configDir, "managed", agent);
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(Path.Combine(configDir, "managed"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "subscription.json");
        File.WriteAllText(path, source.ToJsonString());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private (StockSubscriptionSource? Source, string? Fault) Read(uint? uid = null) =>
        StockSubscriptionBinding.Read(_dir, "agent1", Now, uid ?? StockOpenShellRuntime.OwnerUid(_dir)!.Value, StockOpenShellRuntime.OwnerUid);

    [Fact]
    public void Valid_source_is_read_with_its_digest()
    {
        var path = WriteSource(_dir, Source());
        var (source, fault) = Read();
        Assert.Null(fault);
        Assert.Equal(("agent1-codex-sub", "ws-a", "ws-a", "synthetic-account_01"), (source!.ProviderName, source.ProviderWorkspace, source.SandboxWorkspace, source.AccountId));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), source.SourceSha256);
    }

    [Theory]
    [InlineData("missing", StockSubscriptionBinding.Missing)]
    [InlineData("file_mode", StockSubscriptionBinding.Invalid)]
    [InlineData("dir_mode", StockSubscriptionBinding.Invalid)]
    [InlineData("owner", StockSubscriptionBinding.Invalid)]
    [InlineData("symlink", StockSubscriptionBinding.Invalid)]
    [InlineData("writable_ancestor", StockSubscriptionBinding.Invalid)]
    [InlineData("oversize", StockSubscriptionBinding.Invalid)]
    [InlineData("expired", StockSubscriptionBinding.Expired)]
    public void Stat_owner_mode_ancestor_and_expiry_faults_are_named(string fault, string code)
    {
        var path = fault == "missing" ? null : WriteSource(_dir, Source(expires: fault == "expired" ? Now.AddSeconds(-1) : null));
        uint? uid = null;
        switch (fault)
        {
            case "file_mode": File.SetUnixFileMode(path!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); break;
            case "dir_mode": File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute); break;
            case "owner": uid = StockOpenShellRuntime.OwnerUid(_dir)!.Value + 1; break;
            case "symlink":
                var target = Path.Combine(_dir, "elsewhere.json");
                File.Move(path!, target);
                File.CreateSymbolicLink(path!, target);
                break;
            case "writable_ancestor":
                File.SetUnixFileMode(Path.Combine(_dir, "managed"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                break;
            case "oversize":
                var big = Source();
                big["accountId"] = new string('a', 256);
                File.WriteAllText(path!, big.ToJsonString() + new string(' ', StockSubscriptionBinding.MaxBytes));
                break;
        }
        Assert.Equal(code, Read(uid).Fault);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing_field")]
    [InlineData("version")]
    [InlineData("purpose")]
    [InlineData("agent")]
    [InlineData("provider_name")]
    [InlineData("provider_id")]
    [InlineData("workspace_mismatch")]
    [InlineData("wildcard")]
    [InlineData("account")]
    [InlineData("owner_zero")]
    [InlineData("profile")]
    [InlineData("expiry_offset")]
    public void Unknown_duplicate_missing_or_out_of_bounds_fields_are_invalid(string fault)
    {
        var source = Source();
        var json = source.ToJsonString();
        switch (fault)
        {
            case "unknown": source["token"] = "x"; json = source.ToJsonString(); break;
            case "duplicate": json = json.TrimEnd('}') + ",\"agent\":\"agent1\"}"; break;
            case "missing_field": source.Remove("expiresAt"); json = source.ToJsonString(); break;
            case "version": source["version"] = 2; json = source.ToJsonString(); break;
            case "purpose": source["purpose"] = "production"; json = source.ToJsonString(); break;
            case "agent": source["agent"] = "agent2"; json = source.ToJsonString(); break;
            case "provider_name": source["providerName"] = "agent1-openai"; json = source.ToJsonString(); break;
            case "provider_id": source["providerId"] = "prov-123"; json = source.ToJsonString(); break;
            case "workspace_mismatch": source["sandboxWorkspace"] = "ws-b"; json = source.ToJsonString(); break;
            case "wildcard": source["providerWorkspace"] = "*"; source["sandboxWorkspace"] = "*"; json = source.ToJsonString(); break;
            case "account": source["accountId"] = "has space"; json = source.ToJsonString(); break;
            case "owner_zero": source["ownerGeneration"] = Guid.Empty.ToString(); json = source.ToJsonString(); break;
            case "profile": source["profileSha256"] = new string('C', 64); json = source.ToJsonString(); break;
            case "expiry_offset": source["expiresAt"] = "2026-10-30T14:00:00+02:00"; json = source.ToJsonString(); break;
        }
        Assert.Equal(StockSubscriptionBinding.Invalid, StockSubscriptionBinding.Parse(Encoding.UTF8.GetBytes(json), "agent1", Now).Fault);
    }

    [Fact]
    public void Provider_page_parser_accepts_pinned_shape_and_refuses_unknown_or_duplicate_keys()
    {
        const string page = "{\"providers\":[{\"id\":\"" + ProviderId + "\",\"name\":\"agent1-codex-sub\",\"workspace\":\"ws-a\",\"type\":\"codex-subscription-native\","
            + "\"credential_keys\":[\"CODEX_AUTH_ACCESS_TOKEN\"],\"resource_version\":3,\"created_at\":\"2026-10-30 10:00:00\"}],\"next_page_token\":\"\"}";
        var parsed = StockSubscriptionBinding.ParseProviderPage(page);
        Assert.Equal(["CODEX_AUTH_ACCESS_TOKEN"], Assert.Single(parsed!.Value.Entries).CredentialKeys);
        Assert.Null(StockSubscriptionBinding.ParseProviderPage(page.Replace("\"type\"", "\"secret\":\"v\",\"type\"")));
        Assert.Null(StockSubscriptionBinding.ParseProviderPage(page.Replace("\"name\":\"agent1-codex-sub\"", "\"name\":\"agent1-codex-sub\",\"name\":\"x\"")));
        Assert.Null(StockSubscriptionBinding.ParseProviderPage("[" + page + "]"));
        Assert.Null(StockSubscriptionBinding.ParseProviderPage(page.Replace(",\"next_page_token\":\"\"", "")));
    }

    internal static string RefreshStatus(string provider, string status = "refreshed", string recovery = "-", string expires = "2026-10-30 12:40:00",
        string next = "2026-10-30 12:35:00", string last = "2026-10-30 11:35:00", string failure = "", string error = "-") =>
        StockSubscriptionBinding.RefreshHeader() + "\n"
        + StockSubscriptionBinding.Column(provider, 24) + "  " + StockSubscriptionBinding.Column("CODEX_AUTH_ACCESS_TOKEN", 28) + "  "
        + StockSubscriptionBinding.Column("oauth2_refresh_token", 28) + "  " + StockSubscriptionBinding.Column(status, 24) + "  "
        + StockSubscriptionBinding.Column(recovery, 18) + "  " + StockSubscriptionBinding.Column(expires, 20) + "  "
        + StockSubscriptionBinding.Column(next, 20) + "  " + StockSubscriptionBinding.Column(last, 20) + "  "
        + StockSubscriptionBinding.Column(failure, 44) + "  " + error + "\n";

    [Fact]
    public void Refresh_status_ready_window_and_strict_shape()
    {
        var ready = StockSubscriptionBinding.ParseRefreshStatus(RefreshStatus("agent1-codex-sub"), "agent1-codex-sub");
        Assert.Null(StockSubscriptionBinding.RefreshFault(ready, Now));
        // The 120 s margin must be strictly before NEXT_REFRESH and access expiry.
        Assert.Equal(StockSubscriptionBinding.ProviderUnverified, StockSubscriptionBinding.RefreshFault(ready, new DateTimeOffset(2026, 10, 30, 12, 33, 0, TimeSpan.Zero)));
        // A long provider name overflows its column and is still matched by its exact prefix.
        var longName = "a-much-longer-agent-name-codex-sub";
        Assert.NotNull(StockSubscriptionBinding.ParseRefreshStatus(RefreshStatus(longName), longName));
        // Null LAST_REFRESH is explicit no-successful-refresh evidence, still ready when configured.
        Assert.Null(StockSubscriptionBinding.RefreshFault(StockSubscriptionBinding.ParseRefreshStatus(
            RefreshStatus("agent1-codex-sub", status: "configured", last: "-"), "agent1-codex-sub"), Now));
    }

    [Theory]
    [InlineData("reauthorization_required", "reauthorize", "oauth_invalid_grant", "2026-10-30 12:35:00")]
    [InlineData("error", "retry", "upstream_unavailable", "2026-10-30 12:35:00")]
    [InlineData("refresh_in_progress", "-", "", "2026-10-30 12:35:00")]
    [InlineData("refreshed", "-", "", "manual")]
    public void Refresh_status_that_is_not_ready_blocks(string status, string recovery, string failure, string next) =>
        Assert.Equal(StockSubscriptionBinding.ProviderUnverified, StockSubscriptionBinding.RefreshFault(StockSubscriptionBinding.ParseRefreshStatus(
            RefreshStatus("agent1-codex-sub", status, recovery, failure: failure, next: next), "agent1-codex-sub"), Now));

    [Fact]
    public void Refresh_status_ambiguity_is_refused()
    {
        var good = RefreshStatus("agent1-codex-sub");
        Assert.Null(StockSubscriptionBinding.ParseRefreshStatus("No refresh configuration found for provider 'agent1-codex-sub' credential 'CODEX_AUTH_ACCESS_TOKEN'.\n", "agent1-codex-sub"));
        Assert.Null(StockSubscriptionBinding.ParseRefreshStatus(good + good.Split('\n')[1] + "\n", "agent1-codex-sub"));
        Assert.Null(StockSubscriptionBinding.ParseRefreshStatus(good, "agent2-codex-sub"));
        Assert.Null(StockSubscriptionBinding.ParseRefreshStatus(RefreshStatus("agent1-codex-sub", status: new string('s', 30)), "agent1-codex-sub"));
        Assert.Null(StockSubscriptionBinding.ParseRefreshStatus("\u001b[1m" + good, "agent1-codex-sub"));
        Assert.Null(StockSubscriptionBinding.ParseRefreshStatus(RefreshStatus("agent1-codex-sub", expires: "2026-13-45 99:00:00"), "agent1-codex-sub"));
    }

    [Fact]
    public void Attached_page_parser_reads_names_only()
    {
        var parsed = StockSubscriptionBinding.ParseAttachedPage("{\"providers\":[{\"name\":\"agent1-codex-sub\",\"type\":\"codex-subscription-native\",\"credential_keys\":[],\"config_keys\":[]}],\"next_page_token\":\"\"}");
        Assert.Equal(["agent1-codex-sub"], parsed!.Value.Names);
        Assert.Null(StockSubscriptionBinding.ParseAttachedPage("{\"providers\":[],\"next_page_token\":\"\",\"x\":1}"));
    }
}

/// <summary>
/// #502: a private test base whose ancestors carry no group/other write bits, as the outside binding check requires
/// (the shared temp directory is world-writable). Created under the account's home directory.
/// </summary>
internal static class PrivateBase
{
    public static string Create(string prefix)
    {
        var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : Path.GetTempPath();
        var root = Path.Combine(home, ".phleet-test-private");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dir = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }
}
