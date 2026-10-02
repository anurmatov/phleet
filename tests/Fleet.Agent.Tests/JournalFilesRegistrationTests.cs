using Fleet.Agent;
using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Fleet.Journal.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace Fleet.Agent.Tests;
public sealed class JournalFilesRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualGraphRegistersFilesOnlyWhenEnabledAndBeforeWarmup(bool enabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "files-reg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:WorkDir"] = root, ["Telegram:AttachmentDir"] = Path.Combine(root, "attachments"),
                ["Journal:IngestToken"] = "cj1.ingest.agent1.signature", ["Journal:ReadToken"] = "cj1.read.agent1.signature",
                ["Journal:FilesEnabled"] = enabled.ToString(),
            });
            builder.Services.AddAgentCoreServices(builder.Configuration); builder.Services.AddAgentDaemonServices(builder.Configuration);
            using var host = builder.Build(); var services = host.Services.GetServices<IHostedService>().ToArray();
            Assert.Equal(enabled, host.Services.GetService<JournalFilesTools>() is not null);
            if (enabled)
            {
                Assert.Same(host.Services.GetRequiredService<JournalFilesListener>(), services.OfType<JournalFilesListener>().Single());
                Assert.True(Array.FindIndex(services, s => s is JournalFilesListener) < Array.FindIndex(services, s => s is WarmupService));
                Assert.Single(services.OfType<JournalFilesSweepService>());
            }
            else Assert.DoesNotContain(services, s => s is JournalFilesListener or JournalFilesSweepService);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void ActualStartupGateRefusesFilesWithoutCaptureEvenWhenJournalIsNotRegistered()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Journal:FilesEnabled"] = "true" });
        builder.Services.AddAgentCoreServices(builder.Configuration); builder.Services.AddAgentDaemonServices(builder.Configuration);
        using var host = builder.Build();
        Assert.Throws<InvalidOperationException>(() => AgentHostRegistration.ValidateStartupConfiguration(host.Services));
        Assert.Null(host.Services.GetService<JournalFilesListener>());
    }
    [Theory]
    [InlineData("", "cj1.read.agent1.signature")]
    [InlineData("cj1.ingest.agent1.signature", "")]
    [InlineData("cj1.ingest.agent1.signature", "cj1.ingest.agent1.signature")]
    [InlineData("cj1.ingest.agent1.signature", "cj1.read.agent2.signature")]
    public void InvalidFileConfigRefusesWithoutLeakingToken(string ingest, string read)
    {
        var options = new JournalOptions { IngestToken = ingest, ReadToken = read, FilesEnabled = true };
        var fault = options.DescribeFault(); Assert.NotNull(fault); Assert.DoesNotContain("signature", fault);
    }
}
