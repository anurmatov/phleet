using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed class AutonomyConfigurationTests
{
    [Theory]
    [InlineData(false, false, 14, false)]
    [InlineData(true, false, 14, true)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, true, -1, true)]
    [InlineData(true, true, 14, false)]
    public void Startup_DisablesMisconfigurationOnlyOnce(bool autonomy, bool grants, int days, bool misconfigured)
    {
        var options = new EpicGrantOptions { AutonomyEnabled = autonomy, Enabled = grants, MaxDays = days };
        Assert.Equal(misconfigured, options.DisableMisconfiguredAutonomy());
        Assert.Equal(autonomy && !misconfigured, options.AutonomyEnabled);
        Assert.False(options.DisableMisconfiguredAutonomy());
    }


    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Autonomy_BindsOnlyFromItsOwnConfigurationSection(string value, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["EpicGrants:AutonomyEnabled"] = value
        }).Build();
        Assert.Equal(expected, config.GetSection(EpicGrantOptions.Section).Get<EpicGrantOptions>()!.AutonomyEnabled);
    }

    [Theory]
    [InlineData("/api/epic-grants/example/admission", true)]
    [InlineData("/API/EPIC-GRANTS/example/ADMISSION", true)]
    [InlineData("/Api/Epic-Grants/example/Admission", true)]
    [InlineData("/api/epic-grants", false)]
    [InlineData("/api/epic-grants/example", false)]
    public void DashboardProxy_AdmissionBlockIsCaseInsensitiveAndPrecedesTheApiProxy(string path, bool refused)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src/fleet-dashboard/nginx.conf"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "src/fleet-dashboard/nginx.conf"));
        var pattern = Regex.Match(source, @"location ~\* (\^/api/epic-grants/[^ ]+) \{\s*return 404;");
        Assert.True(pattern.Success);
        Assert.True(pattern.Index < source.IndexOf("location /api/", StringComparison.Ordinal));
        Assert.Equal(refused, Regex.IsMatch(path, pattern.Groups[1].Value, RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Autonomy_DefaultsOff() => Assert.False(new EpicGrantOptions().AutonomyEnabled);
}
