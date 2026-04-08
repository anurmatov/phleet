using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Client;

namespace Fleet.Temporal.Services;

/// <summary>
/// Hosted service that creates configured Temporal namespaces on startup if they don't exist.
/// Runs before the Temporal workers register, ensuring namespaces are available.
/// </summary>
public sealed class NamespaceInitializer(
    IOptions<TemporalBridgeOptions> options,
    ILogger<NamespaceInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (opts.Namespaces is not { Count: > 0 })
        {
            logger.LogWarning("No namespaces configured — skipping Temporal namespace initialization");
            return;
        }

        ITemporalClient client;
        try
        {
            client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(opts.TemporalAddress)
            {
                Namespace = "default"
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not connect to Temporal for namespace initialization — skipping");
            return;
        }

        foreach (var ns in opts.Namespaces)
        {
            try
            {
                await client.Connection.WorkflowService.RegisterNamespaceAsync(
                    new Temporalio.Api.WorkflowService.V1.RegisterNamespaceRequest
                    {
                        Namespace = ns,
                        WorkflowExecutionRetentionPeriod = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromDays(30))
                    });
                logger.LogInformation("Created Temporal namespace: {Namespace}", ns);
            }
            catch (Temporalio.Exceptions.RpcException ex) when (ex.Message.Contains("already exists") || ex.Message.Contains("NamespaceAlreadyExistsError"))
            {
                logger.LogDebug("Temporal namespace already exists: {Namespace}", ns);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create Temporal namespace {Namespace} — it may already exist or server may not support registration", ns);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
