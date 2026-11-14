using System.Text.Json;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Reads the spec of a running container from <c>docker inspect</c> and compares it with the one
/// provisioning would create. Reads only; a preview changes nothing.
/// </summary>
internal static class ContainerSpecDiff
{
    /// <summary>The spec of a running container, from the root of its <c>docker inspect</c> document.</summary>
    internal static ContainerSpec FromInspect(JsonElement root)
    {
        var image  = root.GetProperty("Config").GetProperty("Image").GetString() ?? "";
        var memory = root.GetProperty("HostConfig").GetProperty("Memory").GetInt64();

        var env = new List<string>();
        if (root.GetProperty("Config").TryGetProperty("Env", out var envArr))
            foreach (var e in envArr.EnumerateArray())
                env.Add(e.GetString() ?? "");

        var binds = new List<string>();
        if (root.GetProperty("HostConfig").TryGetProperty("Binds", out var bindsEl)
            && bindsEl.ValueKind == JsonValueKind.Array)
            foreach (var b in bindsEl.EnumerateArray())
                binds.Add(b.GetString() ?? "");

        var networks = new List<string>();
        if (root.TryGetProperty("NetworkSettings", out var ns)
            && ns.TryGetProperty("Networks", out var netsEl))
            foreach (var n in netsEl.EnumerateObject())
                networks.Add(n.Name);

        var extraHosts = new List<string>();
        if (root.GetProperty("HostConfig").TryGetProperty("ExtraHosts", out var hostsEl)
            && hostsEl.ValueKind == JsonValueKind.Array)
            foreach (var h in hostsEl.EnumerateArray())
                extraHosts.Add(h.GetString() ?? "");

        return new ContainerSpec(image, memory, env, binds, networks) { ExtraHosts = extraHosts };
    }

    public static List<string> ComputeDiff(ContainerSpec desired, ContainerSpec actual)
    {
        var diffs = new List<string>();

        // Image
        if (!string.Equals(desired.Image, actual.Image, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"image: desired '{desired.Image}' vs actual '{actual.Image}'");

        // Memory
        if (desired.MemoryBytes != actual.MemoryBytes)
            diffs.Add($"memory: desired {FormatBytes(desired.MemoryBytes)} vs actual {FormatBytes(actual.MemoryBytes)}");

        // Networks
        var desiredNets = new HashSet<string>(desired.Networks, StringComparer.OrdinalIgnoreCase);
        var actualNets  = new HashSet<string>(actual.Networks,  StringComparer.OrdinalIgnoreCase);
        foreach (var n in desiredNets.Except(actualNets))
            diffs.Add($"network missing: '{n}'");
        foreach (var n in actualNets.Except(desiredNets))
            diffs.Add($"network extra: '{n}'");

        // Binds — compare container-side paths only (host paths differ between environments)
        var desiredContainerPaths = desired.Binds
            .Select(ParseContainerPath)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var actualContainerPaths = actual.Binds
            .Select(ParseContainerPath)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var p in desiredContainerPaths.Except(actualContainerPaths))
            diffs.Add($"mount missing (container path): '{p}'");
        foreach (var p in actualContainerPaths.Except(desiredContainerPaths))
            diffs.Add($"mount extra (container path): '{p}'");

        // Env — compare keys only (values may differ due to secrets)
        // Exclude base-image env vars that are injected by Docker/the base image and are not
        // part of the agent's desired config.
        var baseImageEnvPrefixes = new[] { "DOTNET_", "ASPNETCORE_" };
        var baseImageEnvKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "PATH", "APP_UID", "HOME", "HOSTNAME", "TERM",
            "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_VERSION",
        };
        bool IsBaseImageEnv(string key) =>
            baseImageEnvKeys.Contains(key) ||
            baseImageEnvPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

        var desiredEnvKeys = desired.Env.Select(ParseEnvKey).Where(k => !IsBaseImageEnv(k)).ToHashSet(StringComparer.Ordinal);
        var actualEnvKeys  = actual.Env.Select(ParseEnvKey).Where(k => !IsBaseImageEnv(k)).ToHashSet(StringComparer.Ordinal);
        foreach (var k in desiredEnvKeys.Except(actualEnvKeys))
            diffs.Add($"env missing: '{k}'");
        foreach (var k in actualEnvKeys.Except(desiredEnvKeys))
            diffs.Add($"env extra: '{k}'");

        // Host steering of bound secrets: a host points at the forwarder only after a reprovision.
        var desiredHosts = new HashSet<string>(desired.ExtraHosts, StringComparer.OrdinalIgnoreCase);
        var actualHosts  = new HashSet<string>(actual.ExtraHosts,  StringComparer.OrdinalIgnoreCase);
        foreach (var h in desiredHosts.Except(actualHosts))
            diffs.Add($"host entry missing: '{h}'");
        foreach (var h in actualHosts.Except(desiredHosts))
            diffs.Add($"host entry extra: '{h}'");

        return diffs;
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        0           => "unlimited / unset",
        >= 1 << 30  => $"{bytes / (1 << 30)}GB",
        >= 1 << 20  => $"{bytes / (1 << 20)}MB",
        _           => $"{bytes}B",
    };

    private static string? ParseContainerPath(string bind)
    {
        // bind format: "host:container" or "host:container:options"
        var parts = bind.Split(':', 3);
        return parts.Length >= 2 ? parts[1] : null;
    }

    internal static string ParseEnvKey(string envEntry)
    {
        var idx = envEntry.IndexOf('=');
        return idx >= 0 ? envEntry[..idx] : envEntry;
    }
}
