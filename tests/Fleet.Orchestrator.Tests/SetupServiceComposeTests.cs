using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

// ── SetupService.UpsertEnvLines — multi-key batch writes ─────────────────────

public class SetupServiceUpsertEnvLinesTests
{
    [Fact]
    public void UpsertEnvLines_UpdatesExistingKey()
    {
        var lines = new[] { "FOO=old", "BAR=keep" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["FOO"] = "new" });
        Assert.Contains("FOO=new", result);
        Assert.Contains("BAR=keep", result);
        Assert.DoesNotContain("FOO=old", result);
    }

    [Fact]
    public void UpsertEnvLines_AppendsNewKey()
    {
        var lines = new[] { "EXISTING=val" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["NEW"] = "newval" });
        Assert.Contains("NEW=newval", result);
        Assert.Equal("NEW=newval", result.Last());
    }

    [Fact]
    public void UpsertEnvLines_PreservesComments()
    {
        var lines = new[] { "# comment", "KEY=old" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["KEY"] = "new" });
        Assert.Contains("# comment", result);
        Assert.Contains("KEY=new", result);
    }

    [Fact]
    public void UpsertEnvLines_ThrowsOnNewlineInValue()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SetupService.UpsertEnvLines([], new Dictionary<string, string> { ["KEY"] = "val\ninjection" }));
        Assert.Contains("newline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpsertEnvLines_MultipleKeys_AllWritten()
    {
        var lines = new[] { "A=1", "B=2" };
        var result = SetupService.UpsertEnvLines(lines, new Dictionary<string, string> { ["A"] = "10", ["B"] = "20" });
        Assert.Contains("A=10", result);
        Assert.Contains("B=20", result);
    }
}
