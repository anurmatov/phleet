using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// <c>GET /api/operator-token</c> over real HTTP: the one read that checks the operator token.
/// </summary>
/// <remarks>
/// The host maps the production route and nothing else. There is deliberately no administrative
/// gate in front of it: the gate never asks a GET for the token
/// (<see cref="OrchestratorAuth.RequiresBearerToken"/>), so if this route did not check the token
/// itself, every case below that expects 401 would answer 200.
/// </remarks>
public class OperatorTokenEndpointsTests
{
    private const string Token = "synthetic-operator-token";

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(string? configuredToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:AuthToken"] = configuredToken,
        });

        var app = builder.Build();
        app.MapOperatorTokenEndpoints();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<(HttpStatusCode Status, string Body)> CheckAsync(string? configuredToken, string? authorization)
    {
        var (app, client) = await StartAsync(configuredToken);
        await using (app)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OperatorTokenEndpoints.Path);
            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }

    private static string? Field(string body, string name) =>
        JsonDocument.Parse(body).RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;

    [Fact]
    public async Task TheOperatorToken_IsAccepted()
    {
        var (status, body) = await CheckAsync(Token, $"Bearer {Token}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(OperatorTokenEndpoints.Accepted, Field(body, "status"));
    }

    [Theory]
    [InlineData("Bearer synthetic-operator-tokeN")]
    [InlineData("Bearer synthetic-operator-token-and-more")]
    [InlineData("Bearer synthetic")]
    [InlineData("Bearer ")]
    [InlineData("Basic synthetic-operator-token")]
    [InlineData("synthetic-operator-token")]
    [InlineData(null)]
    public async Task AnythingElse_IsRefused_WithTheGatesOwnAnswer(string? authorization)
    {
        var (status, body) = await CheckAsync(Token, authorization);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("Unauthorized", Field(body, "error"));
    }

    [Theory]
    [InlineData("bearer synthetic-operator-token")]
    [InlineData("BEARER synthetic-operator-token")]
    [InlineData("Bearer   synthetic-operator-token  ")]
    public async Task TheBearer_IsReadTheWayTheAdministrativeGateReadsIt(string authorization)
    {
        // A token this route accepts and the gate refuses (or the reverse) would make the check
        // worse than no check: the client would be told the opposite of what a write will do.
        var (status, body) = await CheckAsync(Token, authorization);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(OperatorTokenEndpoints.Accepted, Field(body, "status"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithNoTokenConfigured_TheAnswerSaysWritesAreNotGated_NotThatTheTokenWasChecked(string? configured)
    {
        var (status, body) = await CheckAsync(configured, "Bearer whatever-the-client-holds");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(OperatorTokenEndpoints.NotRequired, Field(body, "status"));
    }

    [Fact]
    public async Task NoAnswer_EverRepeatsAToken()
    {
        foreach (var authorization in new[] { $"Bearer {Token}", "Bearer a-wrong-token-value" })
        {
            var (_, body) = await CheckAsync(Token, authorization);

            Assert.DoesNotContain(Token, body);
            Assert.DoesNotContain("a-wrong-token-value", body);
        }
    }

    [Fact]
    public void TheRoute_IsARead_SoTheGateNeverAsksItForTheToken()
    {
        // Which is exactly why the route has to check the token itself. If this ever turns true,
        // the route is behind the gate and its own check is dead code worth deleting.
        Assert.False(OrchestratorAuth.RequiresBearerToken("GET", OperatorTokenEndpoints.Path));
    }
}
