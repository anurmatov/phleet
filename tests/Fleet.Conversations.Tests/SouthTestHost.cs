using Fleet.Comms;
using Fleet.Comms.Configuration;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The south application, in process, over <see cref="TestServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Built through <see cref="CommsApp.BuildSouthApp"/> — the same composition <c>Program</c> runs —
/// so these tests exercise the production graph rather than a hand-wired approximation. A host that
/// mapped the endpoints itself would prove the endpoints can be mapped, which is not the question:
/// the question is what the deployment's listener does with a request.
/// </para>
/// <para>
/// The store behind it is the real <see cref="MySqlConversationStore"/> against a real database.
/// A double would prove the routes deserialize into something; it would not prove that what they
/// deserialize into is what the store accepts, and that seam is where the wire form of an enum
/// stops being a detail.
/// </para>
/// </remarks>
internal sealed class SouthTestHost : IAsyncDisposable
{
    /// <summary>The credential the host is configured with. Not a secret; this is a test fixture.</summary>
    public const string Token = "south-suite-bearer-token";

    private readonly WebApplication _app;

    private SouthTestHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>A client carrying NO credential. Every authorised call goes through the helpers.</summary>
    public HttpClient Client { get; }

    public static async Task<SouthTestHost> StartAsync(IConversationStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = CommsApp.BuildSouthApp(builder, store, new CommsOptions
        {
            SouthBearerToken = Token,
            AgentName = "test-agent",
            ConversationConnectionString = "configured",
        });

        await app.StartAsync();
        return new SouthTestHost(app, app.GetTestClient());
    }

    /// <summary>POST with the correct credential.</summary>
    public Task<HttpResponseMessage> PostAsync<T>(string path, T body) =>
        Send(path, body, Token);

    /// <summary>POST with a credential that is present and wrong.</summary>
    public Task<HttpResponseMessage> PostWithWrongTokenAsync<T>(string path, T body) =>
        Send(path, body, Token + "-not");

    /// <summary>POST with no Authorization header at all.</summary>
    public Task<HttpResponseMessage> PostAnonymouslyAsync<T>(string path, T body) =>
        Send(path, body, bearer: null);

    private Task<HttpResponseMessage> Send<T>(string path, T body, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            // Serialized through the protocol's options, because that is what the agent on the
            // other end of this listener will send.
            Content = new StringContent(
                FleetProtocolJson.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };

        if (bearer is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");

        return Client.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return FleetProtocolJson.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"could not read a {typeof(T).Name} from: {json}");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
