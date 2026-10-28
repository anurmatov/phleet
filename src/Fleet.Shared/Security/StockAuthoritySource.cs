using System.Text.Json;

namespace Fleet.Shared.Security;

/// <summary>
/// Managed multi-agent enrollment (#492): an optional trusted directory of per-agent authority
/// records outside every workload. The sandbox UUID offered by a caller is only an index hint;
/// exactly one active record must claim it, and every check of the single-file path still applies
/// to that record. Without a directory the existing single <c>AuthorityFile</c> is used unchanged.
/// </summary>
public static class StockAuthoritySource
{
    private const int MaxRecords = 1024;

    public static StockEnrollmentAuthority Resolve(string authorityFile, string? directory, string sandboxHint)
    {
        if (string.IsNullOrEmpty(directory)) return StockEnrollmentAuthority.Load(authorityFile);
        if (!Path.IsPathFullyQualified(directory) || string.IsNullOrWhiteSpace(sandboxHint) || sandboxHint.Length > 256)
            throw new StockIdentityDenied("authority_mapping_invalid");
        StockEnrollmentAuthority? match = null;
        foreach (var path in Records(directory))
        {
            if (new FileInfo(path).LinkTarget is not null) continue; // never follow a link out of the trusted directory
            StockEnrollmentAuthority record;
            try { record = StockEnrollmentAuthority.Load(path); }
            catch (StockIdentityDenied) { continue; } // inactive, expired or invalid records never match
            if (record.SandboxId != sandboxHint) continue;
            if (match is not null) throw new StockIdentityDenied("authority_mapping_ambiguous");
            match = record;
        }
        return match ?? throw new StockIdentityDenied("authority_mapping_missing");
    }

    /// <summary>
    /// Every active record in the directory keyed by agent, for per-agent broker lanes. The rules match
    /// <see cref="Resolve"/>: bounded count, links never followed, inactive/expired/invalid records skipped.
    /// An agent claimed by more than one record, sharing its sandbox with another record or not stored as
    /// <c>&lt;agent&gt;.json</c> maps to null: refused for that agent, never resolved to either record.
    /// </summary>
    public static IReadOnlyDictionary<string, StockActiveRecord?> Active(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new StockIdentityDenied("authority_mapping_invalid");
        var records = new List<StockActiveRecord>();
        foreach (var path in Records(directory))
        {
            try
            {
                if (new FileInfo(path).LinkTarget is not null) continue; // never follow a link out of the trusted directory
                records.Add(new(path, StockEnrollmentAuthority.Load(path)));
            }
            catch (StockIdentityDenied) { } // inactive, expired or invalid records never match
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new StockIdentityDenied("authority_unavailable"); }
        }
        var sandboxes = records.ToLookup(r => r.Record.SandboxId, StringComparer.Ordinal);
        return records.GroupBy(r => r.Record.Agent, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => g.Count() == 1 && sandboxes[g.First().Record.SandboxId].Count() == 1
                && Path.GetFileName(g.First().Path) == g.First().Record.Agent + ".json" ? g.First() : null,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the directory holds any entry for the agent's record, active or not.</summary>
    public static bool HasEntry(string directory, string agent)
    {
        if (!ValidAgent(agent)) return false;
        var info = new FileInfo(Path.Combine(directory, agent + ".json"));
        try { return info.Exists || info.LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // unknown is not absent
    }

    /// <summary>The outside <c>control</c>/<c>observer</c> broker credential beside an agent's record. Refuses links.</summary>
    public static string RolePassword(string directory, string agent, string role)
    {
        if (!Path.IsPathFullyQualified(directory) || !ValidAgent(agent) || role is not ("control" or "observer"))
            throw new StockIdentityDenied("broker_configuration");
        var path = Path.Combine(directory, $"{agent}.{role}.password");
        if (new FileInfo(path).LinkTarget is not null) throw new StockIdentityDenied("broker_credential_unavailable");
        return path;
    }

    /// <summary>Same agent-name rule as <see cref="StockEnrollmentAuthority.Load"/>; also a safe file-name component.</summary>
    public static bool ValidAgent(string agent) => agent.Length is > 0 and <= 64
        && agent.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static List<string> Records(string directory)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(directory, "*.json").Take(MaxRecords + 1).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StockIdentityDenied("authority_unavailable"); }
        if (files.Count > MaxRecords) throw new StockIdentityDenied("authority_directory_size");
        return files;
    }

    /// <summary>The unverified sandbox claim of an assertion, used only to select a record to verify against.</summary>
    public static string SandboxHint(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new StockIdentityDenied("assertion_shape");
            using var claims = JsonDocument.Parse(StockRequestProof.Decode(parts[1]));
            return claims.RootElement.GetProperty("Sandbox").GetString() is { Length: > 0 and <= 256 } sandbox
                ? sandbox : throw new StockIdentityDenied("assertion_binding");
        }
        catch (StockIdentityDenied) { throw; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new StockIdentityDenied("assertion_invalid"); }
    }
}

/// <summary>An active managed record and the trusted path it was read from.</summary>
public sealed record StockActiveRecord(string Path, StockEnrollmentAuthority Record)
{
    /// <summary>Same file and the same fields <see cref="StockBrokerIdentity.Current"/> pins a connection to.</summary>
    public bool SameBinding(StockActiveRecord other) => Path == other.Path && Record.Agent == other.Record.Agent
        && Record.Epoch == other.Record.Epoch && Record.SandboxId == other.Record.SandboxId
        && Record.BrokerHost == other.Record.BrokerHost && Record.MemoryHost == other.Record.MemoryHost
        && Record.PolicySha256 == other.Record.PolicySha256 && Record.StockCommit == other.Record.StockCommit;
}
