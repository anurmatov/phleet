using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Data;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class OpenShellHostConfigTests
{
    [Fact]
    public void ProvisioningRejectsReservedReferencesEvenAfterPrivilegedPersistenceBypass()
    {
        const string key = "FLEET_MEMORY_IDENTITY_CONNECTION_STRING";
        var agent = new Agent { Name = "agent1", DisplayName = "Agent 1", Role = "test", Model = "test", ContainerName = "test",
            EnvRefs = [new() { EnvKeyName = key }] };
        var build = typeof(ContainerProvisioningService).GetMethod("BuildEnv", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Assert.Throws<TargetInvocationException>(() => build.Invoke(null,
            [agent, new Dictionary<string, string> { [key] = "synthetic-value" }, "", ""]));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task ReceiverSettingsStayOutsideSetupReadWriteAndDeleteApis()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string key = "FLEET_MEMORY_IDENTITY_CONNECTION_STRING";
            await File.WriteAllTextAsync(path, key + "=synthetic-value\nVISIBLE=ok\n");
            using var services = new ServiceCollection().BuildServiceProvider();
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Provisioning:EnvFilePath"] = path }).Build();
            var setup = new SetupService(config, null!, null!, scopes, NullLogger<SetupService>.Instance, null!);
            Assert.DoesNotContain(await setup.GetEnvVarsAsync(scopes), x => x.Key == key);
            Assert.Null(setup.RevealEnvVar(key));
            await Assert.ThrowsAsync<ArgumentException>(() => setup.SetEnvVarAsync(key, "changed"));
            await Assert.ThrowsAsync<ArgumentException>(() => setup.DeleteEnvVarAsync(key));
            Assert.Contains("synthetic-value", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
}
