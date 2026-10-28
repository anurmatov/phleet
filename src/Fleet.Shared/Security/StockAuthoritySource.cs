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
        List<string> files;
        try { files = Directory.EnumerateFiles(directory, "*.json").Take(MaxRecords + 1).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StockIdentityDenied("authority_unavailable"); }
        if (files.Count > MaxRecords) throw new StockIdentityDenied("authority_directory_size");
        StockEnrollmentAuthority? match = null;
        foreach (var path in files)
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
