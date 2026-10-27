using Fleet.Orchestrator.Configuration;
using Fleet.Shared.Security;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Orchestrator.Services;

/// <summary>Optional, independently connected observer. Failure never stops ordinary consumption.</summary>
public sealed class StockHeartbeatObserverService(
    IOptions<RabbitMqOptions> options,
    AgentRegistry registry,
    AgentConfigPublisherService publisher,
    IConfiguration configuration,
    StockRuntimeMutationHold hold,
    ILogger<HeartbeatConsumerService> consumerLogger,
    ILogger<StockHeartbeatObserverService> logger) : BackgroundService
{
    internal Func<ConnectionFactory, CancellationToken, Task<IConnection>>? Connect { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HeartbeatConsumerService? receiver = null;
        try
        {
            var identity = StockBrokerIdentity.Optional(configuration["StockOpenShell:AuthorityFile"],
                configuration["StockOpenShell:BrokerPasswordFile"], "observer");
            if (identity is null) return;
            // Select the enrolled endpoint without mutating the ordinary consumer's options.
            // Configure still checks this exact host against fresh authority before connecting.
            var observerOptions = Options.Create(new RabbitMqOptions
            {
                Host = identity.BrokerHost,
                Exchange = options.Value.Exchange
            });
            receiver = new(observerOptions, registry, publisher, configuration, consumerLogger, hold, identity) { Connect = Connect };
            await receiver.StartAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Do not expose credentials or exception details, retry, or fall back to the default broker.
            logger.LogWarning("Stock observer stopped; manual reconciliation required");
        }
        finally
        {
            if (receiver is not null)
            {
                try { await receiver.DisposeAsync(); }
                catch (Exception) { logger.LogWarning("Stock observer cleanup failed; manual reconciliation required"); }
            }
        }
    }
}
