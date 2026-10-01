using System.Net;
using System.Text;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class CommsJournalStatusProxyTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task Success_forwards_a_status_token_and_marks_body_available()
    {
        HttpRequestMessage? captured = null;
        var proxy = Create(new Handler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"spoolDepth\":3}", Encoding.UTF8, "application/json"),
            };
        }));

        var result = await proxy.GetAsync();

        Assert.Equal("available", result["status"]!.GetValue<string>());
        Assert.Equal(3, result["spoolDepth"]!.GetValue<int>());
        Assert.StartsWith("cj1.status.orchestrator.", captured!.Headers.Authorization!.Parameter);
        Assert.Equal("Bearer", captured.Headers.Authorization.Scheme);
    }

    [Fact]
    public async Task Unreachable_returns_unavailable_instead_of_throwing()
    {
        var proxy = Create(new Handler(_ => throw new HttpRequestException("secret details")));

        var result = await proxy.GetAsync();

        Assert.Equal("unavailable", result["status"]!.GetValue<string>());
        Assert.Equal("HttpRequestException", result["errorClass"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_key_returns_disabled()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var proxy = new CommsJournalStatusProxy(
            new Factory(new Handler(_ => throw new InvalidOperationException("must not call"))), config,
            new JournalTokenService(config), NullLogger<CommsJournalStatusProxy>.Instance);

        var result = await proxy.GetAsync();

        Assert.Equal("disabled", result["status"]!.GetValue<string>());
        Assert.Single(result);
    }

    [Fact]
    public async Task Invalid_key_remains_unavailable_without_calling_comms()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Journal:TokenKey"] = "invalid",
        }).Build();
        var proxy = new CommsJournalStatusProxy(
            new Factory(new Handler(_ => throw new InvalidOperationException("must not call"))), config,
            new JournalTokenService(config), NullLogger<CommsJournalStatusProxy>.Instance);
        var result = await proxy.GetAsync();
        Assert.Equal("unavailable", result["status"]!.GetValue<string>());
        Assert.Equal("journal_key_invalid", result["errorClass"]!.GetValue<string>());
    }

    private static CommsJournalStatusProxy Create(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Journal:TokenKey"] = Key,
            ["Journal:StatusUrl"] = "http://fleet-comms:8083/journal/v1/status",
        }).Build();
        return new CommsJournalStatusProxy(new Factory(handler), config,
            new JournalTokenService(config), NullLogger<CommsJournalStatusProxy>.Instance);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
