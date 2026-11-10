using System.Net;
using Fleet.Shared;
using static Fleet.SecretProxy.Tests.ProxyHarness;

namespace Fleet.SecretProxy.Tests;

/// <summary>
/// A change to the state file takes effect on the next request, including on a connection that was
/// already open. Every refusal here happens with no upstream call.
/// </summary>
public sealed class RevocationTests
{
    private static string Code(HttpResponseMessage response) =>
        response.Headers.TryGetValues(InjectingForwarder.ErrorHeader, out var values) ? values.Single() : "";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedGrant_IsRefusedOnTheNextRequest_OnTheSameConnection(bool http2)
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient(http2);

        using var before = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Single(proxy.Upstream.Calls);

        var state = proxy.DefaultState();
        proxy.WriteState(state with { Grants = [.. state.Grants.Where(g => g.Key != KeyA)] });

        using var after = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("secret_grant_unknown", Code(after));
        Assert.Single(proxy.Upstream.Calls);

        // The other grant of the same agent is untouched.
        using var other = Get($"https://{HostB}/status");
        other.Headers.TryAddWithoutValidation("X-Api-Key", proxy.PlaceholderB);
        using var stillWorks = await client.SendAsync(other);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task Reprovision_KillsTheOldPlaceholder_AndTheNewOneWorks()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var replacement = SecretPlaceholders.Create();
        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Grants = [.. state.Grants.Select(g => g.Key == KeyA ? g with { PlaceholderSha256 = SecretPlaceholders.Hash(replacement) } : g)],
        });

        using var old = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        using var fresh = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + replacement));

        Assert.Equal("secret_grant_unknown", Code(old));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal("Bearer " + SecretA, Assert.Single(proxy.Upstream.Calls).Header("Authorization"));
    }

    [Fact]
    public async Task RotatedSecret_IsUsedOnTheNextRequest_WithTheSamePlaceholder()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var first = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        const string rotated = "synthetic-rotated-value-55d0";
        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Bindings = [.. state.Bindings.Select(b => b.Key == KeyA ? b with { Value = rotated } : b)],
        });
        using var second = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        var calls = proxy.Upstream.Calls.ToArray();
        Assert.Equal("Bearer " + SecretA, calls[0].Header("Authorization"));
        Assert.Equal("Bearer " + rotated, calls[1].Header("Authorization"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<secret>")]
    public async Task SecretWithNoUsableValue_IsRefused_AndThePlaceholderIsNotForwarded(string? value)
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Bindings = [.. state.Bindings.Select(b => b.Key == KeyA ? b with { Value = value } : b)],
        });

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("secret_unavailable", Code(response));
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task ChangedBindingHosts_MoveTheSecretAtOnce()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var state = proxy.DefaultState();
        // Key A now belongs to a third host; host A stays bound through a new, unrelated binding.
        proxy.WriteState(state with
        {
            Bindings =
            [
                .. state.Bindings.Select(b => b.Key == KeyA ? b with { Hosts = ["third.example.test"] } : b),
                new SecretProxyBinding("UNRELATED_KEY", SecretBindingRules.KindStatic, [HostA], "Authorization", "synthetic-unrelated"),
            ],
        });

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        Assert.Equal("secret_destination_mismatch", Code(response));
        Assert.Empty(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task RemovedBinding_UnbindsItsHost_OnAnOpenConnectionAndForNewOnes()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var before = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Bindings = [.. state.Bindings.Where(b => b.Key != KeyA)],
            Grants = [.. state.Grants.Where(g => g.Key != KeyA)],
        });

        using var open = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal("secret_host_mismatch", Code(open));

        using var fresh = proxy.CreateClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => fresh.SendAsync(Get($"https://{HostA}/v1/items")));
        Assert.Single(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task MissingStateFile_RefusesEverything_AndRecoversWhenItReturns()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var before = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        proxy.DeleteState();
        using var withPlaceholder = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        using var without = await client.SendAsync(Get($"https://{HostA}/public"));
        Assert.Equal("secret_state_unavailable", Code(withPlaceholder));
        Assert.Equal("secret_state_unavailable", Code(without));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, without.StatusCode);
        Assert.Single(proxy.Upstream.Calls);

        proxy.WriteState(proxy.DefaultState());
        using var after = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"version":2,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[],"grants":[]}""")]
    [InlineData("""{"version":1,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[{"key":"K","kind":"static","hosts":["*.example.test"],"header":"Authorization","value":"v","appId":null}],"grants":[]}""")]
    [InlineData("""{"version":1,"generatedAtUtc":"2026-01-01T00:00:00Z","bindings":[],"grants":[],"extra":true}""")]
    public async Task InvalidStateFile_RefusesEverything(string content)
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        using var before = await client.SendAsync(Get($"https://{HostA}/public"));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        proxy.WriteRawState(content);

        using var after = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, after.StatusCode);
        Assert.Equal("secret_state_unavailable", Code(after));
        Assert.Single(proxy.Upstream.Calls);
    }

    [Fact]
    public async Task StartedWithoutAStateFile_RefusesTheHandshake_ThenServesOnceTheFileAppears()
    {
        await using var proxy = await StartAsync(writeState: false);
        using var early = proxy.CreateClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => early.SendAsync(Get($"https://{HostA}/public")));

        proxy.WriteState(proxy.DefaultState());

        using var client = proxy.CreateClient();
        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TwoGrantsClaimingOnePlaceholder_AreBothIgnored()
    {
        await using var proxy = await StartAsync();
        using var client = proxy.CreateClient();
        var state = proxy.DefaultState();
        proxy.WriteState(state with
        {
            Grants = [.. state.Grants, new SecretProxyGrant("agent2", Loopback, KeyB, SecretPlaceholders.Hash(proxy.PlaceholderA))],
        });

        using var response = await client.SendAsync(Get($"https://{HostA}/v1/items", "Bearer " + proxy.PlaceholderA));

        Assert.Equal("secret_grant_unknown", Code(response));
        Assert.Empty(proxy.Upstream.Calls);
    }
}
