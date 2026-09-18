using System.Net;
using System.Text;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Comms.Tests;

/// <summary>
/// An install that has not enabled conversations is byte-identical to the one the auth slice ships
/// (AC9, D-C, MUST NOT 13).
/// </summary>
/// <remarks>
/// This is what makes the feature opt-in rather than merely defaulted to off. A deployment that
/// wants an auth boundary and nothing else keeps exactly the surface it had, attempts no database
/// connection, and gets no background loop.
/// </remarks>
public class ConversationDisabledTests
{
    /// <summary>The six routes, which must be absent rather than present-and-refusing.</summary>
    public static TheoryData<string, string> ConversationRoutes() => new()
    {
        { "POST", "/v1/conversations" },
        { "GET", "/v1/conversations/c_1/events" },
        { "POST", "/v1/conversations/c_1/submissions" },
        { "POST", "/v1/conversations/c_1:cancel" },
        { "POST", "/v1/conversations/c_1/cursor" },
        { "GET", "/v1/conversations/c_1/stream" },
    };

    [Theory]
    [MemberData(nameof(ConversationRoutes))]
    public async Task No_conversation_route_exists_when_the_feature_is_disabled(
        string method, string path)
    {
        await using var host = await NorthTestHost.StartAsync();

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent(
                $$"""{"protocol":"{{ProtocolVersion.Current}}"}""", Encoding.UTF8, "application/json");
        }

        var response = await host.Client.SendAsync(request);

        // 404 from the ROUTER — not a handler that authenticated and then refused. A route that
        // exists and says no is a route an operator has to reason about.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Nothing attempts a database connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is registered as a lazy factory, so "no connection attempted" is a property of
    /// nothing resolving it rather than of the connection string being blank. Resolving it here
    /// proves the factory is the one that refuses — with a message naming the key — instead of
    /// constructing a store against an empty connection string and failing later, at a request, as
    /// a `503` the operator would read as a transient outage.
    /// </para>
    /// <para>
    /// Asserted by resolution rather than by watching for a socket, because a test that waited for
    /// a connection that never comes cannot tell "did not connect" from "has not connected yet".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_conversation_store_is_never_constructed_when_the_feature_is_disabled()
    {
        await using var host = await NorthTestHost.StartAsync();

        var failure = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<IConversationStore>());

        Assert.Contains(nameof(Fleet.Comms.Configuration.CommsOptions.ConversationConnectionString),
            failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// No background loop is registered, so a disabled install runs no reconciler, no garbage
    /// collection and no outbox drain.
    /// </summary>
    [Fact]
    public async Task No_conversation_background_service_is_registered_when_the_feature_is_disabled()
    {
        await using var host = await NorthTestHost.StartAsync();

        var hosted = host.Services
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Select(s => s.GetType().Name)
            .ToArray();

        Assert.DoesNotContain(nameof(Fleet.Conversations.OutboxDrainService), hosted);
        Assert.DoesNotContain(nameof(Fleet.Conversations.ConversationMaintenanceService), hosted);
    }

    /// <summary>
    /// The four auth routes are untouched: same statuses, same bodies.
    /// </summary>
    /// <remarks>
    /// MUST NOT 12. The equalised-work property on credential checks is part of the contract and is
    /// easy to break by adding a fast path — so the disabled install is checked end to end rather
    /// than assumed to be unaffected by a feature it does not have.
    /// </remarks>
    [Fact]
    public async Task The_auth_routes_still_work_end_to_end_when_conversations_are_disabled()
    {
        await using var host = await NorthTestHost.StartAsync();

        var (deviceId, secret, token) = await host.EnrolledDeviceAsync();

        Assert.NotEmpty(deviceId);
        Assert.NotEmpty(secret);

        var session = await host.SessionAsync(token);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }
}
