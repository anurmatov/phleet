using MySqlConnector;

namespace Fleet.Conversations.Tests;

[Collection("mysql")]
public sealed class MySqlPoolCleanupTests(MySqlFixture fixture)
{
    [Fact]
    public async Task Dropping_a_scratch_schema_releases_its_cached_connections()
    {
        var scratch = await fixture.CreateScratchDatabaseAsync();
        int thread;
        await using (var connection = new MySqlConnection(scratch.ConnectionString))
        {
            await connection.OpenAsync();
            thread = connection.ServerThread;
        }
        Assert.Equal("1", await MySqlFixture.ScalarRowOnAsync(fixture.MigrationConnectionString,
            $"SELECT COUNT(*) FROM information_schema.processlist WHERE ID = {thread}"));
        await scratch.DisposeAsync();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (await MySqlFixture.ScalarRowOnAsync(fixture.MigrationConnectionString,
                $"SELECT COUNT(*) FROM information_schema.processlist WHERE ID = {thread}") == "0") return;
            await Task.Delay(20);
        }
        Assert.Fail("the dropped schema still owns an idle server connection");
    }
}
