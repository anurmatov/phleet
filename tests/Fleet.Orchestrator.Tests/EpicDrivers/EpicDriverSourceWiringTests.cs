using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Tests.EpicDrivers;

public class EpicDriverSourceWiringTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable");
    }
    private static string Read(string path) => File.ReadAllText(Path.Combine(Root(), path));

    [Fact]
    public void Configuration_FreshAndUpgradeDefaultsAreCompleteAndDoNotOverwriteExistingValues()
    {
        var env = Read(".env.example"); var compose = Read("docker-compose.example.yml"); var setup = Read("setup.sh"); var upgrade = Read("upgrade.sh");
        Assert.Contains("FLEET_EPIC_WATCHER_ENABLED=false", env); Assert.Contains("FLEET_EPIC_WATCHER_INTERVAL_SECONDS=300", env);
        Assert.Contains("EpicDriverWatcher__Enabled=${FLEET_EPIC_WATCHER_ENABLED:-false}", compose);
        Assert.Contains("EpicDriverWatcher__IntervalSeconds=${FLEET_EPIC_WATCHER_INTERVAL_SECONDS:-300}", compose);
        Assert.Contains("FLEET_EPIC_WATCHER_ENABLED=false FLEET_EPIC_WATCHER_INTERVAL_SECONDS=300", setup);
        Assert.Contains("add_missing_env_var FLEET_EPIC_WATCHER_ENABLED false", upgrade);
        Assert.Contains("add_missing_env_var FLEET_EPIC_WATCHER_INTERVAL_SECONDS 300", upgrade);
        Assert.Contains("AddHostedService<EpicDriverWatcherService>", Read("src/Fleet.Orchestrator/Program.cs"));
        Assert.Contains("app.MapEpicDriverEndpoints()", Read("src/Fleet.Orchestrator/Program.cs"));
    }
    [Fact]
    public void Proxy_InternalStateIsRefusedBeforeApiProxy_CaseInsensitively()
    {
        var nginx = Read("src/fleet-dashboard/nginx.conf");
        Assert.True(nginx.IndexOf("location ~* ^/api/epic-drivers/", StringComparison.Ordinal) < nginx.IndexOf("location /api/", StringComparison.Ordinal));
        Assert.Matches(@"location ~\* \^/api/epic-drivers/\s*\{\s*return 404;\s*\}", nginx);
        foreach (var path in new[] { "/api/epic-drivers/x/state", "/API/Epic-Drivers/x/state" })
            Assert.True(Regex.IsMatch(path, "^/api/epic-drivers/", RegexOptions.IgnoreCase));
        Assert.False(Regex.IsMatch("/api/epic-grants", "^/api/epic-drivers/", RegexOptions.IgnoreCase));
    }
}
