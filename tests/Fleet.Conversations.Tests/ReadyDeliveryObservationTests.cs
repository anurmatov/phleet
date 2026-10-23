namespace Fleet.Conversations.Tests;

// Owned observations only. These tests never construct the MySQL or RabbitMQ fixtures.
public class ReadyDeliveryObservationTests
{
    [Fact]
    public async Task TransientZeroAfterShutdown_WaitsForTheRequeuedDelivery()
    {
        var calls = 0;
        var depth = await SouthRoundTripTests.WaitForRequeuedDeliveryAsync(
            _ => Task.FromResult(++calls == 1 ? 0u : 1u), TimeSpan.FromSeconds(2));
        Assert.Equal(1u, depth);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StillEmptyAtDeadline_IsNotSuccessfulRequeue()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SouthRoundTripTests.WaitForRequeuedDeliveryAsync(_ => Task.FromResult(0u), TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task ReadError_IsNotAnEmptyQueueOrSuccessfulRequeue()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SouthRoundTripTests.WaitForRequeuedDeliveryAsync(_ =>
            {
                calls++;
                return Task.FromException<uint>(new InvalidOperationException("owned read error"));
            }, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReadThatIgnoresCancellation_CannotOutliveTheObservationDeadline()
    {
        var unresolved = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SouthRoundTripTests.WaitForRequeuedDeliveryAsync(_ => unresolved.Task, TimeSpan.FromMilliseconds(30)));
        }
        finally { unresolved.TrySetResult(0); }
    }
}
