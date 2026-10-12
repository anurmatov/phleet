using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fleet.Orchestrator.Services;

internal sealed record EpicPreflightVisibility(
    bool Valid, string? Failure, IReadOnlyDictionary<string, string> Visibility);

/// <summary>Stored-evidence checks shared by the autonomous preflight readers.</summary>
internal static class EpicGrantPreflight
{
    // Callers must audit the returned failure and refuse before using any cached evidence.
    // Never normalize the stored JSON before hashing: whitespace is part of its evidence.
    public static EpicPreflightVisibility ReadStoredVisibility(string? json, string? sha256)
    {
        if (json is null || sha256 is null) return Refuse("missing");
        var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (!string.Equals(actual, sha256, StringComparison.Ordinal)) return Refuse("mismatch");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("visibility", out var visibility)
                || visibility.ValueKind != JsonValueKind.Object) return Refuse("malformed");
            var repos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var repo in visibility.EnumerateObject())
            {
                if (repo.Value.ValueKind != JsonValueKind.String
                    || repo.Value.GetString() is not ("public" or "private")
                    || !repos.TryAdd(repo.Name, repo.Value.GetString()!)) return Refuse("malformed");
            }
            return new(true, null, new ReadOnlyDictionary<string, string>(repos));
        }
        catch (JsonException) { return Refuse("malformed"); }
    }

    private static EpicPreflightVisibility Refuse(string failure) =>
        new(false, failure, new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));
}
