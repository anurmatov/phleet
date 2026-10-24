using System.Net;
using Fleet.Orchestrator.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Endpoints;

public sealed class OpenShellAuthorizationTests
{
    private const string Token = "synthetic-operator-token";

    public static IEnumerable<object[]> ProtectedRequests()
    {
        string[] paths = ["/api/agents", "/api/agents/agent1", "/api/agents/reprovision-running",
            "/api/agents/reprovision-running/unknown", "/api/agents/agent1/start", "/api/agents/agent1/stop", "/api/agents/agent1/restart",
            "/api/agents/agent1/restart-with-version", "/api/agents/agent1/reprovision",
            "/api/agents/agent1/openshell", "/api/agents/agent1/openshell/unknown",
            "/api/agents/agent1/secret-bindings/unknown", "/api/agents/agent1/execution-policy",
            "/API/AGENTS/agent1/START/unknown", "/api/services/restart"];
        foreach (var path in paths)
        foreach (var method in new[] { "GET", "HEAD", "OPTIONS", "POST", "DELETE" })
            yield return [method, path];
    }

    [Theory, MemberData(nameof(ProtectedRequests))]
    public async Task ProtectedPath_AllVerbsRejectBeforeBackend(string method, string path)
    {
        var result = await Send(Token, method, path, []);
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal(0, result.BackendCalls);
    }

    [Theory]
    [InlineData("")] [InlineData(" ")]
    public async Task MissingConfiguredCredential_FailsClosed(string configured)
    {
        var result = await Send(configured, "POST", "/api/agents/agent1/start", ["Bearer " + Token]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Status);
        Assert.Equal(0, result.BackendCalls);
    }

    [Theory]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer config-token")]
    [InlineData("Basic synthetic")]
    [InlineData("Bearer ")]
    [InlineData("Bearer synthetic-operator-token,other")]
    public async Task MalformedOrWrongCredential_NoBackend(string value)
    {
        var result = await Send(Token, "POST", "/api/agents/agent1/openshell/migrate", [value]);
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal(0, result.BackendCalls);
    }

    [Fact]
    public async Task RawWhitespaceCredential_RejectsBeforeBackend()
    {
        // HttpClient's typed Authorization parser normalizes spaces. Exercise the raw server
        // boundary separately rather than pretending a normalized HTTP value is malformed.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Path = "/api/agents/agent1/start";
        context.Request.Headers.Authorization = "Bearer  " + Token;
        context.Response.Body = new MemoryStream();
        var pipeline = new ApplicationBuilder(app.Services);
        pipeline.UseOpenShellOperatorAuthorization(Token);
        var calls = 0;
        pipeline.Run(_ => { calls++; return Task.CompletedTask; });
        await pipeline.Build()(context);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DuplicateOrOversizeAuthorization_NoBackend()
    {
        foreach (var headers in new[] { new[] { "Bearer " + Token, "Bearer " + Token }, new[] { "Bearer " + new string('x', 4096) } })
        {
            var result = await Send(Token, "POST", "/api/agents/agent1/start", headers);
            Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
            Assert.Equal(0, result.BackendCalls);
        }
    }

    [Fact]
    public async Task QueryIdentityCannotSubstituteForCredential()
    {
        var result = await Send(Token, "GET", "/api/agents/agent1/secret-bindings?agent=agent1&token=" + Token, []);
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal(0, result.BackendCalls);
    }

    [Fact]
    public async Task ExactOperatorCredential_ReachesBackend()
    {
        var result = await Send(Token, "POST", "/api/agents/agent1/start", ["Bearer " + Token]);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(1, result.BackendCalls);
    }

    [Theory]
    [InlineData("/health")] [InlineData("/mcp")] [InlineData("/internal/agent-project-access")]
    [InlineData("/api/agents/agent1/config")] [InlineData("/api/agents/agent1/history")]
    public async Task UnrelatedRoutes_KeepTheirExistingAuthorization(string path)
    {
        var result = await Send("", "GET", path, []);
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(1, result.BackendCalls);
    }

    private static async Task<(HttpStatusCode Status, int BackendCalls)> Send(string configured,
        string method, string path, string[] authorization)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseOpenShellOperatorAuthorization(configured);
        var calls = 0;
        app.Run(context => { Interlocked.Increment(ref calls); return context.Response.WriteAsync("backend"); });
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, calls);
    }
}
