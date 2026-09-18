using RabbitMQ.Client;

namespace Fleet.Conversations.Tests;

/// <summary>
/// A real broker for the round-trip suite.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This fixture FAILS when the broker is absent. It never skips</b> — the same rule
/// <see cref="MySqlFixture"/> follows, and for the same reason: a suite that skipped for want of a
/// connection string is indistinguishable from a passing one in a run summary, which is how a gate
/// that never executed gets shipped as evidence that it did.
/// </para>
/// <para>
/// The agent binds nothing and declares nothing — the publishing service owns the topology — so the
/// fixture declares the queue exactly as <see cref="RabbitMqOutboxTransport.DeclareTopologyAsync"/>
/// does, through that very method, and deletes it afterwards. Per-class agent names keep two classes
/// from draining each other's queue.
/// </para>
/// </remarks>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Variable = "FLEET_CONVERSATIONS_BROKER";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"{Variable} is not set, so the round-trip suite has no broker to run against.\n\n"
                + "  This FAILS rather than skipping on purpose: the whole point of this suite is to\n"
                + "  prove the loop closes against real infrastructure, and a skipped run proves the\n"
                + "  opposite of what its green summary claims.\n\n"
                + "  CI supplies a rabbitmq:4 service container. Locally, point it at any broker:\n"
                + $"    export {Variable}='amqp://guest:guest@127.0.0.1:5672/'");

        ConnectionString = configured;

        // Fail here, at fixture time, rather than inside the first test: an unreachable broker is a
        // missing prerequisite, not a failing assertion, and the two should not look alike.
        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Delete a queue this suite created, so a rerun starts from an empty one.</summary>
    public async Task DeleteQueueAsync(string queue)
    {
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(ConnectionString) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
        }
        catch (Exception)
        {
            // A queue that is already gone is the desired end state.
        }
    }

    /// <summary>Messages currently waiting on a queue. Used to assert an ack actually happened.</summary>
    public async Task<uint> DepthAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Passive: never create the queue as a side effect of measuring it.
        var declared = await channel.QueueDeclarePassiveAsync(queue);
        return declared.MessageCount;
    }
}

/// <summary>
/// The round-trip collection: one MySQL schema and one broker, shared by the classes in it.
/// </summary>
[CollectionDefinition("south-round-trip")]
public sealed class SouthRoundTripCollection : ICollectionFixture<MySqlFixture>, ICollectionFixture<RabbitMqFixture>;
