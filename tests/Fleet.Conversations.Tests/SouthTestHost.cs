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
    /// <summary>The agent every conversation in these suites is with, and the subject of <see cref="Token"/>.</summary>
    public const string Agent = "test-agent";

    /// <summary>The shared key the host is configured with. Not a secret; this is a test fixture.</summary>
    public const string Key = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA";

    /// <summary>
    /// <see cref="Agent"/>'s own south token, as provisioning mints it (#530): it acts only on that
    /// agent's conversations.
    /// </summary>
    public static readonly string Token = TokenFor(Agent);

    /// <summary>Another agent's token under the same key.</summary>
    public static string TokenFor(string agent) =>
        Fleet.Conversations.Journal.JournalTokens.Mint(
            Fleet.Conversations.Journal.JournalTokens.ParseKeys(Key)[0], Fleet.Shared.SignIn.AgentTokenPurpose, agent);

    private readonly WebApplication _app;

    private SouthTestHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>A client carrying NO credential. Every authorised call goes through the helpers.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// A fresh handler onto the same in-process listener.
    /// </summary>
    /// <remarks>
    /// The agent's typed south client owns its own <see cref="HttpClient"/> — it sets a base address
    /// and an Authorization header — so it needs a handler rather than a configured client. Handing
    /// it this one is what lets the round-trip suite exercise the agent against the REAL listener,
    /// bearer check included, instead of against a stub that would agree with whatever the agent
    /// sent.
    /// </remarks>
    public HttpMessageHandler CreateHandler() => _app.GetTestServer().CreateHandler();

    public static async Task<SouthTestHost> StartAsync(IConversationStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = CommsApp.BuildSouthApp(builder, store, new CommsOptions
        {
            ConversationConnectionString = "configured",
            Journal = new() { TokenKeys = Key },
        });

        await app.StartAsync();
        return new SouthTestHost(app, app.GetTestClient());
    }

    /// <summary>POST with the correct credential.</summary>
    public Task<HttpResponseMessage> PostAsync<T>(string path, T body) =>
        Send(path, body, Token);

    /// <summary>POST with a given credential: another agent's token, or a token of another purpose.</summary>
    public Task<HttpResponseMessage> PostAsAsync<T>(string path, T body, string token) =>
        Send(path, body, token);

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
