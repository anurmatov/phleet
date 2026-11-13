using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Comms.Auth;
using Fleet.Comms.Contracts;
using Fleet.Protocol;
using Fleet.Shared;

namespace Fleet.Comms.Tests;

/// <summary>
/// The north routes on orchestrator sign-in, with chat for any chat-enabled agent (#530 D6, D7, D9,
/// MUST NOT 2, 3 and 5).
/// </summary>
public sealed class ChatRouteTests
{
    // ── a conversation names its agent ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    public async Task Opening_without_a_usable_agent_name_is_a_client_error(string? agent)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.SendAsync(Bearer(Post("/v1/conversations", new
        {
            protocol = ProtocolVersion.Current,
            externalRef = "main-thread",
            agent,
        }), token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Opened);
    }

    [Fact]
    public async Task Opening_with_an_agent_that_does_not_accept_chat_is_agent_unavailable_and_opens_nothing()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.SendAsync(Bearer(Post("/v1/conversations", new
        {
            protocol = ProtocolVersion.Current,
            externalRef = "main-thread",
            agent = "not-a-chat-agent",
        }), token));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_unavailable", await ErrorCodeAsync(response));
        Assert.Empty(store.Opened);

        // The question named the agent, so the orchestrator — not this service — decided.
        Assert.Contains(host.SignIn.Asked, q => q.Agent == "not-a-chat-agent");
    }

    [Fact]
    public async Task The_principal_comes_from_the_credential_and_never_from_the_body()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync("p_real");

        var response = await host.Client.SendAsync(Bearer(Post("/v1/conversations", new
        {
            protocol = ProtocolVersion.Current,
            externalRef = "main-thread",
            agent = NorthTestHost.ChatAgent,
            principalId = "p_forged",
        }), token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("p_real", Assert.Single(store.Opened).PrincipalId);
    }

    // ── the list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_is_the_callers_own_conversations_with_their_agents()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, mine) = await host.EnrolledDeviceAsync();
        var (_, _, theirs) = await host.EnrolledDeviceAsync("p_someone_else");

        var own = await ReadAsync<ConversationListResponse>(
            await host.Client.SendAsync(Bearer(new HttpRequestMessage(HttpMethod.Get, "/v1/conversations"), mine)));

        var item = Assert.Single(own.Conversations);
        Assert.Equal(store.ConversationId, item.ConversationId);
        Assert.Equal(NorthTestHost.ChatAgent, item.Agent);

        var other = await ReadAsync<ConversationListResponse>(
            await host.Client.SendAsync(Bearer(new HttpRequestMessage(HttpMethod.Get, "/v1/conversations"), theirs)));

        Assert.Empty(other.Conversations);
    }

    // ── every write checks the chat switch (MUST NOT 5) ──────────────────────

    public static TheoryData<string, string> WriteRoutes() => new()
    {
        { "/submissions", """{"protocol":"PROTOCOL","type":"create","submissionId":"s_1","text":"hi"}""" },
        { ":cancel", """{"protocol":"PROTOCOL","scope":"current"}""" },
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_to_a_conversation_whose_agent_stopped_accepting_chat_is_refused_and_publishes_nothing(
        string suffix, string body)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.SignIn.ChatAgents[NorthTestHost.ChatAgent] = false;

        var response = await host.Client.SendAsync(
            Bearer(Raw($"/v1/conversations/{store.ConversationId}{suffix}", body), token));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("agent_unavailable", await ErrorCodeAsync(response));
        Assert.Empty(store.Accepted);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_to_someone_elses_conversation_is_not_found_and_publishes_nothing(
        string suffix, string body)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync("p_someone_else");

        var response = await host.Client.SendAsync(
            Bearer(Raw($"/v1/conversations/{store.ConversationId}{suffix}", body), token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("conversation_not_found", await ErrorCodeAsync(response));
        Assert.Empty(store.Accepted);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_with_the_agent_accepting_chat_is_accepted(string suffix, string body)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var response = await host.Client.SendAsync(
            Bearer(Raw($"/v1/conversations/{store.ConversationId}{suffix}", body), token));

        Assert.True(response.IsSuccessStatusCode, $"answered {(int)response.StatusCode}");
        Assert.Single(store.Accepted);
    }

    // ── a browser: the session cookie, and the write header (D6) ─────────────

    [Fact]
    public async Task A_browser_reads_with_its_session_cookie_alone()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var cookie = host.SignIn.AddBrowser();

        var response = await host.Client.SendAsync(
            Cookie(new HttpRequestMessage(HttpMethod.Get, "/v1/conversations"), cookie));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_browser_write_without_the_request_header_is_refused(string suffix, string body)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var cookie = host.SignIn.AddBrowser();

        var response = await host.Client.SendAsync(
            Cookie(Raw($"/v1/conversations/{store.ConversationId}{suffix}", body), cookie));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Accepted);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_browser_write_with_the_request_header_is_accepted(string suffix, string body)
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var cookie = host.SignIn.AddBrowser();

        var request = Cookie(Raw($"/v1/conversations/{store.ConversationId}{suffix}", body), cookie);
        request.Headers.Add(SignIn.RequestHeader, "1");

        var response = await host.Client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"answered {(int)response.StatusCode}");
        Assert.Single(store.Accepted);
    }

    [Fact]
    public async Task An_app_write_needs_no_request_header()
    {
        // A bearer is put on a request on purpose and is never ambient; the header is a browser's.
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        var (suffix, body) = ("/submissions", WriteBody("""{"protocol":"PROTOCOL","type":"create","submissionId":"s_1","text":"hi"}"""));
        var response = await host.Client.SendAsync(Bearer(Raw($"/v1/conversations/{store.ConversationId}{suffix}", body), token));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_session_cookie_is_forwarded_never_another_cookie()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var cookie = host.SignIn.AddBrowser();

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations");
        request.Headers.Add("Cookie", $"other=private-value; {SignIn.SessionCookie}={cookie}");

        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(request)).StatusCode);

        var asked = Assert.Single(host.SignIn.Asked);
        Assert.Equal(cookie, asked.Credential.SessionCookie);
        Assert.Null(asked.Credential.Authorization);
    }

    // ── an answer that could not be had is never a pass (MUST NOT 3) ─────────

    [Fact]
    public async Task Sign_in_that_cannot_be_checked_is_503_with_the_fixed_body_and_never_2xx()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.SignIn.Override = SignInAnswer.Unavailable;

        foreach (var request in new[]
                 {
                     Bearer(new HttpRequestMessage(HttpMethod.Get, "/v1/session"), token),
                     Bearer(new HttpRequestMessage(HttpMethod.Get, "/v1/conversations"), token),
                     Bearer(Raw($"/v1/conversations/{store.ConversationId}/submissions",
                         """{"protocol":"PROTOCOL","type":"create","submissionId":"s_1","text":"hi"}"""), token),
                 })
        {
            var response = await host.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("internal", await ErrorCodeAsync(response));
        }

        Assert.Empty(store.Accepted);
    }

    [Fact]
    public async Task A_throttled_sign_in_is_429_with_a_retry_after()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var (_, _, token) = await host.EnrolledDeviceAsync();

        host.SignIn.Override = SignInAnswer.Busy;

        var response = await host.Client.SendAsync(
            Bearer(new HttpRequestMessage(HttpMethod.Get, "/v1/conversations"), token));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task A_request_with_no_credential_asks_nothing()
    {
        await using var host = await NorthTestHost.StartAsync(conversations: new FakeConversationStore());

        var response = await host.Client.GetAsync("/v1/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.SignIn.Asked);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string WriteBody(string body) => body.Replace("PROTOCOL", ProtocolVersion.Current);

    private static HttpRequestMessage Raw(string path, string body) => new(HttpMethod.Post, path)
    {
        Content = new StringContent(WriteBody(body), Encoding.UTF8, "application/json"),
    };

    private static HttpRequestMessage Post(string path, object body) => new(HttpMethod.Post, path)
    {
        Content = new StringContent(FleetProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static HttpRequestMessage Bearer(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Cookie(HttpRequestMessage request, string session)
    {
        request.Headers.Add("Cookie", $"{SignIn.SessionCookie}={session}");
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), FleetProtocolJson.Options)!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }
}
