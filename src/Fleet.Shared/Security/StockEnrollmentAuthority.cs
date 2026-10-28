using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Shared.Security;

public sealed class StockIdentityDenied(string reason) : Exception(reason);

/// <summary>
/// The fixed first-party HTTP MCP receivers that verify the outside assertion (#492). Memory keeps its
/// own <see cref="StockEnrollmentAuthority.MemoryHost"/> and read-only tool rule; every other service
/// here is selected per agent by <see cref="StockEnrollmentAuthority.Services"/>. Any other MCP name
/// has no receiving-service identity and stays refused in managed mode.
/// </summary>
public static class StockServices
{
    public const string Memory = "memory";
    public static readonly IReadOnlyList<string> MemoryTools = ["memory_get", "memory_search", "memory_list"];

    /// <summary>Receiving service → the ordinary MCP endpoint name whose existing tool grants it serves.</summary>
    public static readonly IReadOnlyDictionary<string, string> McpNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["temporal"] = "fleet-temporal",
        ["telegram"] = "fleet-telegram",
    };

    public static bool IsReceiving(string service) => service == Memory || McpNames.ContainsKey(service);
}

/// <summary>
/// Managed-mode credentials other than the native API key (#492). The real values stay outside in
/// stock providers attached to the sandbox; the workload's child environment carries only the
/// placeholder stock issued under each name, which the gateway resolves on egress.
/// </summary>
public static class StockCredentials
{
    /// <summary>The agent's own Telegram bot token (stock profile <c>telegram</c>).</summary>
    public const string TelegramBotToken = "TELEGRAM_BOT_TOKEN";

    /// <summary>The agent's derived journal ingest token (stock profile <c>fleet-journal</c>).</summary>
    public const string JournalIngestToken = "FLEET_JOURNAL_INGEST_TOKEN";

    /// <summary>The agent's derived journal read token for the comms-journal read MCP (stock profile <c>fleet-journal</c>).</summary>
    public const string JournalReadToken = "FLEET_JOURNAL_READ_TOKEN";

    public static readonly IReadOnlyList<string> ServiceKeys = [TelegramBotToken, JournalIngestToken, JournalReadToken];

    /// <summary>
    /// Syntax only, the same source-supported handle forms as the native API key: a refusal check for
    /// literals and foreign keys, NOT proof of stock issuance or isolation.
    /// </summary>
    public static bool IsPlaceholder(string? value, string key) =>
        value is { Length: <= 1024 } && System.Text.RegularExpressions.Regex.IsMatch(value,
            @"\Aopenshell:resolve:env:(?:v[0-9]+_|s[A-Za-z0-9_]+_)?" + System.Text.RegularExpressions.Regex.Escape(key) + @"\z");
}

/// <summary>
/// One non-memory receiving route: its exact HTTPS host, the agent's granted tool names on that
/// service (from existing grants, never widened) and the fixed non-identity query the receiver appends
/// after verified attribution (only Telegram's <c>formatting_mode</c>).
/// </summary>
public sealed record StockServiceRoute
{
    public string Host { get; init; } = "";
    public IReadOnlyList<string> Tools { get; init; } = [];
    public string Query { get; init; } = "";
}

/// <summary>
/// The comms-journal read MCP route (#492): the operator's journal listener host, the ordinary endpoint's
/// path and the agent's existing tool grants on it. Comms authorizes with the derived per-agent read
/// token, which stays outside; the workload's MCP header carries only the stock placeholder.
/// </summary>
public sealed record StockJournalReadRoute
{
    public const string McpName = "fleet-comms-journal";
    public string Host { get; init; } = "";
    public string Path { get; init; } = "";
    public IReadOnlyList<string> Tools { get; init; } = [];
}

// One operator-owned file, read afresh at each admission. This is NOT a lifecycle or grant store.
public sealed record StockEnrollmentAuthority
{
    public string State { get; init; } = "";
    public string Agent { get; init; } = "";
    public string SandboxId { get; init; } = "";
    public Guid Epoch { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string GatewayIssuer { get; init; } = "";
    public string GatewayAudience { get; init; } = "";
    public string GatewayJwksPath { get; init; } = "";
    public string AssertionPrivateKeyPath { get; init; } = "";
    public string AssertionPublicKeyPath { get; init; } = "";
    public string MemoryHost { get; init; } = "";
    public string BrokerHost { get; init; } = "";
    public string PolicySha256 { get; init; } = "";
    public string StockCommit { get; init; } = "";
    /// <summary>Optional non-memory routes by receiving service; absent in memory-only and older records.</summary>
    public IReadOnlyDictionary<string, StockServiceRoute>? Services { get; init; }
    /// <summary>Optional comms-journal read MCP route; absent unless the agent has that endpoint.</summary>
    public StockJournalReadRoute? JournalRead { get; init; }

    /// <summary>The exact receiving host for a service; an unenrolled service refuses.</summary>
    public string ServiceHost(string service) => service == StockServices.Memory ? MemoryHost
        : Services?.GetValueOrDefault(service)?.Host ?? throw new StockIdentityDenied("service_not_enrolled");

    /// <summary>Callable tools: memory's fixed read-only set, otherwise exactly the record's grants.</summary>
    public IReadOnlyList<string> ServiceTools(string service) => service == StockServices.Memory ? StockServices.MemoryTools
        : Services?.GetValueOrDefault(service)?.Tools ?? throw new StockIdentityDenied("service_not_enrolled");

    private static bool ValidServices(StockEnrollmentAuthority record)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.MemoryHost };
        if (record.JournalRead is { } journal && (Uri.CheckHostName(journal.Host) != UriHostNameType.Dns || !hosts.Add(journal.Host)
            || journal.Path is null || !System.Text.RegularExpressions.Regex.IsMatch(journal.Path, @"\A(?:/[A-Za-z0-9_-]{1,64}){1,8}\z")
            || journal.Tools is null || journal.Tools.Count > 128
            || journal.Tools.Any(t => t is null || !System.Text.RegularExpressions.Regex.IsMatch(t, @"\A[A-Za-z0-9_.-]{1,128}\z"))
            || journal.Tools.Distinct(StringComparer.Ordinal).Count() != journal.Tools.Count))
            return false;
        if (record.Services is not { } services) return true;
        foreach (var (service, route) in services)
        {
            if (!StockServices.McpNames.ContainsKey(service) || route is null || Uri.CheckHostName(route.Host) != UriHostNameType.Dns
                || !hosts.Add(route.Host) || route.Tools is null || route.Tools.Count > 128
                || route.Tools.Any(t => t is null || !System.Text.RegularExpressions.Regex.IsMatch(t, @"\A[A-Za-z0-9_.-]{1,128}\z"))
                || route.Tools.Distinct(StringComparer.Ordinal).Count() != route.Tools.Count
                || route.Query != "" && !(service == "telegram" && System.Text.RegularExpressions.Regex.IsMatch(route.Query ?? "", @"\Aformatting_mode=[0-2]\z")))
                return false;
        }
        return true;
    }

    public static byte[] ReadBounded(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 16384) throw new StockIdentityDenied("authority_size");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new StockIdentityDenied("authority_size");
        return bytes;
    }
    public static string ReadText(string path) => new UTF8Encoding(false, true).GetString(ReadBounded(path));

    public static StockEnrollmentAuthority Load(string path)
    {
        try
        {
            var bytes = ReadBounded(path);
            using var document = JsonDocument.Parse(bytes);
            StockRequestProof.Unique(document.RootElement);
            var record = JsonSerializer.Deserialize<StockEnrollmentAuthority>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false })!;
            if (record.State != "active" || record.ExpiresAt <= DateTimeOffset.UtcNow || record.Epoch == Guid.Empty
                || string.IsNullOrWhiteSpace(record.Agent) || record.Agent.Length > 64 || record.Agent.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                || string.IsNullOrWhiteSpace(record.SandboxId) || record.SandboxId.Length > 256
                || string.IsNullOrWhiteSpace(record.GatewayIssuer) || string.IsNullOrWhiteSpace(record.GatewayAudience)
                || !Path.IsPathFullyQualified(record.GatewayJwksPath)
                || !Path.IsPathFullyQualified(record.AssertionPrivateKeyPath)
                || !Path.IsPathFullyQualified(record.AssertionPublicKeyPath)
                || Uri.CheckHostName(record.MemoryHost) != UriHostNameType.Dns || !ValidServices(record))
                throw new StockIdentityDenied("enrollment_denied");
            return record;
        }
        catch (StockIdentityDenied) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { throw new StockIdentityDenied("authority_unavailable"); }
    }
}

/// <summary>
/// Outside-signed request proof bound to the receiving service, so a proof issued for one receiver
/// never verifies at another. A proof without the service claim fails binding.
/// </summary>
public sealed record StockRequestProof(string Agent, string Sandbox, Guid Epoch, string Method, string SessionHash,
    string BodyHash, long Expires, string Nonce, string Service)
{
    public const string Header = "X-Fleet-Assertion";
    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public static string HashSession(string? value) => Hash(Encoding.UTF8.GetBytes(value ?? ""));
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value)
    {
        if (value.Length > 16384 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || value.Length % 4 == 1)
            throw new StockIdentityDenied("assertion_encoding");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (Encode(bytes) != value) throw new StockIdentityDenied("assertion_encoding");
        return bytes;
    }
    public static void Unique(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
            if (!names.Add(p.Name)) throw new StockIdentityDenied("duplicate_field");
    }
    public static string Issue(StockEnrollmentAuthority record, string service, string method, byte[] body, string? session)
    {
        if (!StockServices.IsReceiving(service)) throw new StockIdentityDenied("service_unsupported");
        var proof = new StockRequestProof(record.Agent, record.SandboxId, record.Epoch, method, HashSession(session),
            Hash(body), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 15, Guid.NewGuid().ToString("N"), service);
        using var key = ECDsa.Create(); key.ImportFromPem(StockEnrollmentAuthority.ReadText(record.AssertionPrivateKeyPath));
        RequireP256(key);
        var input = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "stock-memory-v1" })) + "."
            + Encode(JsonSerializer.SerializeToUtf8Bytes(proof));
        return input + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    public static StockRequestProof Verify(StockEnrollmentAuthority record, string service, string token, string method, byte[] body, string? session)
    {
        try
        {
            if (token.Length > 16384) throw new StockIdentityDenied("assertion_size");
            var parts = token.Split('.'); if (parts.Length != 3) throw new StockIdentityDenied("assertion_shape");
            using var header = JsonDocument.Parse(Decode(parts[0])); Unique(header.RootElement);
            if (header.RootElement.EnumerateObject().Count() != 2 || header.RootElement.GetProperty("alg").GetString() != "ES256"
                || header.RootElement.GetProperty("typ").GetString() != "stock-memory-v1") throw new StockIdentityDenied("assertion_header");
            using var key = ECDsa.Create(); key.ImportFromPem(StockEnrollmentAuthority.ReadText(record.AssertionPublicKeyPath)); RequireP256(key);
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new StockIdentityDenied("assertion_signature");
            var bytes = Decode(parts[1]); using var claims = JsonDocument.Parse(bytes); Unique(claims.RootElement);
            var proof = JsonSerializer.Deserialize<StockRequestProof>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (proof.Agent != record.Agent || proof.Sandbox != record.SandboxId || proof.Epoch != record.Epoch
                || proof.Service != service || !StockServices.IsReceiving(service) || proof.Method != method || proof.BodyHash != Hash(body) || proof.SessionHash != HashSession(session)
                || proof.Expires <= now || proof.Expires > now + 15 || !Guid.TryParseExact(proof.Nonce, "N", out _))
                throw new StockIdentityDenied("assertion_binding");
            return proof;
        }
        catch (StockIdentityDenied) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or CryptographicException
            or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw new StockIdentityDenied("assertion_invalid"); }
    }
    private static void RequireP256(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new StockIdentityDenied("assertion_key");
    }
}
