using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Fleet.Shared;

/// <summary>
/// Shared helper for creating RabbitMQ connections with exponential backoff retry.
/// </summary>
public static class RabbitMqConnectionHelper
{
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60),
    ];

    /// <summary>
    /// Creates a RabbitMQ connection, retrying with exponential backoff on failure.
    /// Throws if all retry attempts are exhausted or cancellation is requested.
    /// </summary>
    public static async Task<IConnection> ConnectWithRetryAsync(
        ConnectionFactory factory, ILogger logger, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await factory.CreateConnectionAsync(ct);
            }
            catch (Exception ex) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                var delay = RetryDelays[attempt];
                logger.LogWarning(ex, "RabbitMQ connection failed (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt + 1, RetryDelays.Length + 1, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }
}
