using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// #502: the outside-owned per-agent subscription binding at <c>&lt;ConfigDir&gt;/managed/&lt;agent&gt;/subscription.json</c>,
/// as read before any effect. Account metadata is a local selection, not issuer authorization; the owner generation is
/// a local trial-lineage id, not a token-generation counter.
/// </summary>
public sealed record StockSubscriptionSource(string Agent, string ProviderName, string ProviderId, string ProviderWorkspace,
    string SandboxWorkspace, string AccountId, Guid OwnerGeneration, string GatewayGeneration, string ProfileSha256,
    DateTimeOffset ExpiresAt, string SourceSha256);

/// <summary>One provider of a scoped pinned 0.1.5 <c>provider list -o json</c> page: identities and key names, never values.</summary>
public sealed record StockProviderEntry(string Id, string Name, string Workspace, string Type, IReadOnlyList<string> CredentialKeys);

/// <summary>The one <c>provider refresh status</c> row for the subscription credential (UTC, second precision).</summary>
public sealed record StockRefreshObservation(string Status, string Recovery, DateTimeOffset? ExpiresAt, DateTimeOffset? NextRefresh,
    DateTimeOffset? LastRefresh, string FailureCode);

/// <summary>
/// #502 strict readers: the new outside binding file and the pinned stock CLI observations. No OAuth client, credential
/// reader or gateway SDK; every ambiguity or unexpected shape refuses with a named, secret-free code.
/// </summary>
public static class StockSubscriptionBinding
{
    public const string Mode = "subscription";
    public const string Purpose = "compatibility_only";
    /// <summary>Profile id of the reviewed restricted profile (deploy/openshell/codex-subscription.yaml).</summary>
    public const string ProfileId = "codex-subscription-native";
    public const string CredentialKey = "CODEX_AUTH_ACCESS_TOKEN";
    public const string RefreshStrategy = "oauth2_refresh_token";
    public const int MaxBytes = 16 * 1024;
    /// <summary>Refresh-window margin: fresh UTC plus this must be strictly before NEXT_REFRESH and access expiry.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(120);

    public const string Missing = "subscription_binding_missing";
    public const string Invalid = "subscription_binding_invalid";
    public const string Expired = "subscription_binding_expired";
    public const string Changed = "subscription_binding_changed";
    public const string Unrecoverable = "subscription_binding_unrecoverable";
    public const string ProviderUnverified = "subscription_provider_unverified";
    public const string OwnerConflict = "subscription_owner_conflict";

    private static readonly string[] Fields =
    [
        "version", "purpose", "agent", "providerName", "providerId", "providerWorkspace", "sandboxWorkspace",
        "accountId", "ownerGeneration", "gatewayGeneration", "profileSha256", "expiresAt",
    ];

    public static string ProviderNameFor(string agent) => agent + "-codex-sub";

    public static string SourcePath(string configDir, string agent) => Path.Combine(configDir, "managed", agent, "subscription.json");

    /// <summary>A named stock workspace: a lowercase DNS-label-shaped scope, never empty, a wildcard or an all-workspaces selector.</summary>
    public static bool ValidWorkspace(string? workspace) =>
        workspace is not null && Regex.IsMatch(workspace, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z");

    /// <summary>
    /// Reads and validates the outside binding before any mutation. File owner uid must equal the effective uid, mode
    /// exactly 0600, the containing directory exactly 0700, and every existing ancestor a non-symlink with no
    /// group/other write bits; any failed check is <see cref="Invalid"/>.
    /// </summary>
    public static (StockSubscriptionSource? Source, string? Fault) Read(string configDir, string agent, DateTimeOffset now,
        uint effectiveUid, Func<string, uint?> ownerUid)
    {
        var path = SourcePath(configDir, agent);
        var entry = StockOpenShellRuntime.AuthorityEntry(path);
        if (entry is null) return (null, Missing);
        if (entry != true || OperatingSystem.IsWindows()) return (null, Invalid);
        byte[] bytes;
        try
        {
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite) || ownerUid(path) != effectiveUid)
                return (null, Invalid);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (new DirectoryInfo(directory).LinkTarget is not null
                || File.GetUnixFileMode(directory) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                return (null, Invalid);
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
            {
                if (!dir.Exists) continue;
                if (dir.LinkTarget is not null
                    || (File.GetUnixFileMode(dir.FullName) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                    return (null, Invalid);
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (stream.Length > MaxBytes) return (null, Invalid);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return (null, Invalid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, Invalid); }
        return Parse(bytes, agent, now);
    }

    internal static (StockSubscriptionSource? Source, string? Fault) Parse(byte[] bytes, string agent, DateTimeOffset now)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, Invalid);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !values.TryAdd(property.Name, property.Value)) return (null, Invalid);
            if (values.Count != Fields.Length) return (null, Invalid);
            if (values["version"].ValueKind != JsonValueKind.Number || !values["version"].TryGetInt32(out var version) || version != 1)
                return (null, Invalid);
            string? Text(string name) => values[name].ValueKind == JsonValueKind.String ? values[name].GetString() : null;
            var providerId = Text("providerId");
            var providerWorkspace = Text("providerWorkspace");
            var sandboxWorkspace = Text("sandboxWorkspace");
            var accountId = Text("accountId");
            var gateway = Text("gatewayGeneration");
            var profile = Text("profileSha256");
            var expires = Text("expiresAt");
            if (Text("purpose") != Purpose || Text("agent") != agent || Text("providerName") != ProviderNameFor(agent)
                || providerId is null || !Guid.TryParseExact(providerId, "D", out var providerGuid) || providerGuid.ToString("D") != providerId
                || providerGuid == Guid.Empty
                || !ValidWorkspace(providerWorkspace) || !ValidWorkspace(sandboxWorkspace) || providerWorkspace != sandboxWorkspace
                || accountId is null || !Regex.IsMatch(accountId, @"\A[A-Za-z0-9_-]{1,256}\z")
                || Text("ownerGeneration") is not { } owner || !Guid.TryParseExact(owner, "D", out var ownerGeneration) || ownerGeneration == Guid.Empty
                || gateway is not { Length: > 0 and <= 512 } || gateway.Any(char.IsControl)
                || profile is null || !Regex.IsMatch(profile, @"\A[0-9a-f]{64}\z")
                || expires is null || !Regex.IsMatch(expires, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z\z")
                || !DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt))
                return (null, Invalid);
            if (expiresAt <= now) return (null, Expired);
            return (new StockSubscriptionSource(agent, ProviderNameFor(agent), providerId, providerWorkspace!, sandboxWorkspace!, accountId,
                ownerGeneration, gateway, profile, expiresAt, Convert.ToHexStringLower(SHA256.HashData(bytes))), null);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException or InvalidOperationException)
        { return (null, Invalid); }
    }

    private static readonly HashSet<string> ProviderFields = new(StringComparer.Ordinal)
    {
        "id", "name", "workspace", "type", "credential_keys", "config_keys", "labels", "resource_version", "created_at", "credential_expires_at_ms",
    };

    /// <summary>
    /// One pinned 0.1.5 <c>provider list -o json</c> page: exactly <c>{"providers":[…],"next_page_token":"…"}</c> with the
    /// pinned provider fields (key names only). Null for any other shape, a duplicate key or an unknown field.
    /// </summary>
    public static (List<StockProviderEntry> Entries, string NextToken)? ParseProviderPage(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !UniqueKeys(root) || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("next_page_token", out var next) || next.ValueKind != JsonValueKind.String) return null;
            var entries = new List<StockProviderEntry>();
            foreach (var provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object || !UniqueKeys(provider)
                    || provider.EnumerateObject().Any(p => !ProviderFields.Contains(p.Name))) return null;
                string? Field(string name) => provider.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (Field("id") is not { } id || Field("name") is not { } name || Field("workspace") is not { } workspace || Field("type") is not { } type
                    || !provider.TryGetProperty("credential_keys", out var keys) || keys.ValueKind != JsonValueKind.Array
                    || keys.EnumerateArray().Any(k => k.ValueKind != JsonValueKind.String)) return null;
                entries.Add(new(id, name, workspace, type, [.. keys.EnumerateArray().Select(k => k.GetString()!)]));
            }
            return (entries, next.GetString()!);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One pinned 0.1.5 <c>sandbox provider list -o json</c> page: attached provider names. Null for any other shape.
    /// </summary>
    public static (List<string> Names, string NextToken)? ParseAttachedPage(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !UniqueKeys(root) || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("next_page_token", out var next) || next.ValueKind != JsonValueKind.String) return null;
            var names = new List<string>();
            foreach (var provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object || !UniqueKeys(provider)
                    || !provider.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
                names.Add(name.GetString()!);
            }
            return (names, next.GetString()!);
        }
        catch (JsonException) { return null; }
    }

    private static bool UniqueKeys(JsonElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(p => seen.Add(p.Name));
    }

    // Pinned 0.1.5 openshell-cli commands/provider.rs refresh_status_header/refresh_status_row: fixed minimum widths,
    // two-space separators, UTC "YYYY-MM-DD HH:MM:SS" or "-" timestamps, NEXT_REFRESH "manual" when unscheduled.
    private static readonly int[] Widths = [24, 28, 28, 24, 18, 20, 20, 20, 44];
    private static readonly string[] Headers =
        ["PROVIDER", "CREDENTIAL_KEY", "STRATEGY", "STATUS", "RECOVERY", "EXPIRES_AT", "NEXT_REFRESH", "LAST_REFRESH", "FAILURE_CODE"];

    internal static string Column(string value, int width) => value.Length >= width ? value : value.PadRight(width);

    /// <summary>The exact uncoloured header line the pinned CLI prints.</summary>
    internal static string RefreshHeader() =>
        string.Concat(Headers.Select((h, i) => Column(h, Widths[i]) + "  ")) + "LAST_ERROR";

    /// <summary>
    /// Strict parse of <c>provider refresh status &lt;provider&gt; --credential-key CODEX_AUTH_ACCESS_TOKEN</c> (colour off):
    /// exactly the header plus one row for this provider/key/strategy, every later column inside its pinned width.
    /// Null for anything else (no row, extra rows, overflowing or unparsable columns).
    /// </summary>
    public static StockRefreshObservation? ParseRefreshStatus(string stdout, string provider)
    {
        var lines = stdout.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length != 2 || lines[0] != RefreshHeader()) return null;
        var row = lines[1];
        var prefix = Column(provider, Widths[0]) + "  " + Column(CredentialKey, Widths[1]) + "  " + Column(RefreshStrategy, Widths[2]) + "  ";
        if (!row.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var values = new List<string>();
        var at = prefix.Length;
        for (var i = 3; i < Widths.Length; i++)
        {
            if (row.Length < at + Widths[i] + 2 || row.Substring(at + Widths[i], 2) != "  ") return null;
            var value = row.Substring(at, Widths[i]).TrimEnd(' ');
            if (value.StartsWith(' ') || value.Contains("  ", StringComparison.Ordinal)) return null;
            values.Add(value);
            at += Widths[i] + 2;
        }
        if (at >= row.Length) return null; // LAST_ERROR is always printed ("-" when empty)
        DateTimeOffset? Time(string text, out bool ok)
        {
            ok = true;
            if (text == "-") return null;
            if (Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\z")
                && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
            ok = false;
            return null;
        }
        var expires = Time(values[2], out var expiresOk);
        DateTimeOffset? next = null;
        var nextOk = true;
        if (values[3] != "manual") next = Time(values[3], out nextOk);
        var last = Time(values[4], out var lastOk);
        if (!expiresOk || !nextOk || !lastOk) return null;
        return new(values[0], values[1], expires, next, last, values[5]);
    }

    /// <summary>
    /// Readiness plus the refresh-window rule: a ready status with no recovery action or failure, an access expiry and
    /// a scheduled next refresh both strictly later than fresh UTC plus <see cref="RefreshMargin"/>; else
    /// <see cref="ProviderUnverified"/>. A null LAST_REFRESH is allowed only as explicit no-successful-refresh evidence.
    /// </summary>
    public static string? RefreshFault(StockRefreshObservation? observation, DateTimeOffset now)
    {
        if (observation is not { Status: "configured" or "refreshed", Recovery: "-", FailureCode: "", ExpiresAt: { } expires, NextRefresh: { } next })
            return ProviderUnverified;
        var edge = now + RefreshMargin;
        return edge < next && edge < expires ? null : ProviderUnverified;
    }
}
