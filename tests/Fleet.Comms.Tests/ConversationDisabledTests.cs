using System.Net;
using System.Text;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Comms.Tests;

/// <summary>
/// An install that has not enabled conversations serves no north route (AC9, D-C, MUST NOT 13).
/// </summary>
/// <remarks>
/// This is what makes the feature opt-in rather than merely defaulted to off. Such a deployment
/// attempts no database connection, gets no background loop and never asks the orchestrator
/// anything; sign-in is the orchestrator's (#530).
/// </remarks>
public class ConversationDisabledTests
{
    /// <summary>The six routes, which must be absent rather than present-and-refusing.</summary>
    public static TheoryData<string, string> ConversationRoutes() => new()
    {
        { "GET", "/v1/session" },
        { "GET", "/v1/conversations" },
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
    /// Nothing asks the orchestrator, and nothing needs the shared key: the sign-in check is never
    /// built when no route could use it.
    /// </summary>
    [Fact]
    public async Task The_sign_in_check_is_never_asked_when_the_feature_is_disabled()
    {
        await using var host = await NorthTestHost.StartAsync();
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var session = await host.SessionAsync(token);

        Assert.Equal(HttpStatusCode.NotFound, session.StatusCode);
        Assert.Empty(host.SignIn.Asked);
    }
}
