using System.Net;
using System.Text;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static Fleet.SecretProxy.Tests.ProxyHarness;

namespace Fleet.SecretProxy.Tests;

/// <summary>The secret reaches the bound host and nothing else: not the agent, not a log line.</summary>
public sealed class LeakTests
{
    [Fact]
    public async Task LogsOfSuccessesAndRefusals_ContainNoSecretPlaceholderOrUrl()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        const string privatePath = "/v1/private-path-4c1d";

        // Success, slot mismatch, destination mismatch, unknown grant, URL placeholder, upstream failure.
        using var ok = await client.SendAsync(Get($"https://{HostA}{privatePath}?q=private-query-9a2b", "Bearer " + proxy.PlaceholderA));
        using var slot = Get($"https://{HostA}{privatePath}");
        slot.Headers.TryAddWithoutValidation("X-Debug", proxy.PlaceholderA);
        using var slotResponse = await client.SendAsync(slot);
        using var destination = await client.SendAsync(Get($"https://{HostB}{privatePath}", "Bearer " + proxy.PlaceholderA));
        var unknown = SecretPlaceholders.Create();
        using var unknownResponse = await client.SendAsync(Get($"https://{HostA}{privatePath}", "Bearer " + unknown));
        using var inUrl = await client.SendAsync(Get($"https://{HostA}{privatePath}?t={proxy.PlaceholderA}"));
        proxy.Upstream.Respond = _ => throw new HttpRequestException("synthetic upstream failure " + SecretA);
        using var failed = await client.SendAsync(Get($"https://{HostA}{privatePath}", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);

        var logs = proxy.Logs.All;
        Assert.Contains("SecretProxy host=" + HostA, logs);
        Assert.Contains("key=" + KeyA, logs);
        Assert.Contains("agent=agent1", logs);
        Assert.DoesNotContain(SecretA, logs);
        Assert.DoesNotContain(SecretB, logs);
        Assert.DoesNotContain(proxy.PlaceholderA, logs);
        Assert.DoesNotContain(unknown, logs);
        Assert.DoesNotContain(SecretPlaceholders.Hash(proxy.PlaceholderA), logs);
        Assert.DoesNotContain("private-path-4c1d", logs);
        Assert.DoesNotContain("private-query-9a2b", logs);
    }

    [Fact]
    public async Task RefusalBodiesAndHeaders_ContainNoSecretOrPlaceholder()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var responses = new List<HttpResponseMessage>
        {
            await client.SendAsync(Get($"https://{HostB}/x", "Bearer " + proxy.PlaceholderA)),
            await client.SendAsync(Get($"https://{HostA}/x?t={proxy.PlaceholderA}")),
            await client.SendAsync(Get($"https://{HostA}/x", "Bearer " + SecretPlaceholders.Create())),
        };

        foreach (var response in responses)
        {
            Assert.True(response.Headers.Contains(InjectingForwarder.ErrorHeader));
            var text = await response.Content.ReadAsStringAsync()
                + string.Join("\n", response.Headers.SelectMany(h => h.Value));
            Assert.DoesNotContain(SecretA, text);
            Assert.DoesNotContain(SecretB, text);
            Assert.DoesNotContain(proxy.PlaceholderA, text);
            Assert.DoesNotContain("fleet_placeholder_", text);
            response.Dispose();
        }
    }

    [Fact]
    public async Task UpstreamThatEchoesTheCredentialInAResponseHeader_IsWithheld()
    {
        await using var proxy = await StartAsync();
        proxy.Upstream.Respond = call =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("echo") };
            response.Headers.TryAddWithoutValidation("X-Echo", call.Header("Authorization"));
            return response;
        };
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/headers", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("secret_reflected", response.Headers.GetValues(InjectingForwarder.ErrorHeader).Single());
        Assert.False(response.Headers.Contains("X-Echo"));
        Assert.DoesNotContain(SecretA, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnexpectedFailureInsideTheForwarder_IsA500_LoggedAsATypeNameOnly()
    {
        await using var proxy = await StartAsync();
        const string detail = "synthetic failure detail quoting " + SecretA;
        proxy.Upstream.Respond = _ => throw new InvalidOperationException(detail);
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(SecretA, await response.Content.ReadAsStringAsync());
        Assert.Contains("forwarder_error:InvalidOperationException", proxy.Logs.All);
        Assert.DoesNotContain(SecretA, proxy.Logs.All);
        Assert.DoesNotContain("synthetic failure detail", proxy.Logs.All);
    }

    [Fact]
    public async Task SuccessfulResponse_GivesTheAgentNoSecret()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        var text = await response.Content.ReadAsStringAsync()
            + string.Join("\n", response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value));
        Assert.DoesNotContain(SecretA, text);
    }

    [Fact]
    public async Task HealthListener_IsLoopbackOnly_AndSaysNothingAboutTheState()
    {
        // The harness disables the health listener; the handler is exercised through a plain host.
        var directory = Path.Combine(Path.GetTempPath(), "fleet-secrets-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var port = FreePort();
        var app = SecretProxyApp.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProxy:StateFile"] = Path.Combine(directory, "state.json"),
                ["SecretProxy:CaDirectory"] = Path.Combine(directory, "ca"),
                ["SecretProxy:ListenAddress"] = Loopback,
                ["SecretProxy:Port"] = "0",
                ["SecretProxy:HealthPort"] = port.ToString(),
            });
            builder.Logging.ClearProviders();
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            var body = await client.GetStringAsync($"http://127.0.0.1:{port}/health");
            Assert.Contains("\"status\":\"healthy\"", body);
            Assert.Contains("\"state\":\"state_missing\"", body);
            using var other = await client.GetAsync($"http://127.0.0.1:{port}/v1/items");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
