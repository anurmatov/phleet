using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Fleet.Agent;

/// <summary>
/// Startup refusal for the stock whole-agent path. An operator record is intent, not proof that
/// receiving services authenticate a workload. In particular it cannot authorize legacy ?agent
/// attribution. No lifecycle actions, trust provisioning or enrollment writes occur here.
/// </summary>
public static class StockOpenShellEnrollment
{
    public const string StockCommit = "bdb956f2cd65a635f1294fccf1d138d263bf650f";
    public const string ConfigurationKey = "StockOpenShell:EnrollmentFile";
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "version", "state", "agent", "sandboxId", "stockCommit", "policySha256", "expiresAt"
    };

    public static void Validate(IConfiguration configuration)
    {
        var enabled = configuration["StockOpenShell:Enabled"];
        var isEnabled = false;
        if (enabled is not (null or "") && !bool.TryParse(enabled, out isEnabled)) Deny("stock_enrollment_enabled_invalid");
        var path = configuration[ConfigurationKey];
        if (!isEnabled)
        {
            if (path is not (null or "")) Deny("stock_enrollment_disabled_with_file");
            return; // Opt-in only; no new behavior for existing installs.
        }
        if (path is null or "") Deny("stock_enrollment_file_missing");
        if (!Path.IsPathFullyQualified(path)) Deny("stock_enrollment_path_invalid");
        string json;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 16384) Deny("stock_enrollment_invalid");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Do not include a path, content, credential or inner exception in the startup log.
            throw new InvalidOperationException("stock_enrollment_unreadable");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!Fields.Contains(field.Name) || !seen.Add(field.Name)) Deny("stock_enrollment_invalid");
            if (!seen.SetEquals(Fields) || root.GetProperty("version").GetInt32() != 1) Deny("stock_enrollment_invalid");
            var state = root.GetProperty("state").GetString();
            if (state == "revoked") Deny("stock_enrollment_revoked");
            if (state != "active") Deny("stock_enrollment_state_unknown");
            if (string.IsNullOrWhiteSpace(configuration["Agent:Name"])
                || root.GetProperty("agent").GetString() != configuration["Agent:Name"])
                Deny("stock_enrollment_agent_mismatch");
            if (root.GetProperty("stockCommit").GetString() != StockCommit) Deny("stock_enrollment_stock_commit");
            if (string.IsNullOrWhiteSpace(root.GetProperty("sandboxId").GetString())) Deny("stock_enrollment_sandbox_missing");
            var policy = root.GetProperty("policySha256").GetString();
            if (policy is not { Length: 64 } || policy.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                Deny("stock_enrollment_policy_missing");
            if (!root.GetProperty("expiresAt").TryGetDateTimeOffset(out var expires)) Deny("stock_enrollment_invalid");
            if (expires <= DateTimeOffset.UtcNow) Deny("stock_enrollment_expired");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
            || ex is InvalidOperationException && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("stock_enrollment_invalid");
        }

        // Main has no authenticated stock-supervisor receiver yet. Do not accept an operator's
        // boolean, a header, a query identity or an old fork capability as substitute evidence.
        // Memory is the first missing required route; control and broker paths remain unqualified too.
        Deny("stock_enrollment_required_route_unverified:memory");
    }

    private static void Deny(string reason) => throw new InvalidOperationException(reason);
}
