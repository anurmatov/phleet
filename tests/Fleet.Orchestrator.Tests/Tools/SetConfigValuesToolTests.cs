using System.Text.Json;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// Unit tests for SetConfigValuesTool — auth, validation, classification, and exception paths.
/// ConfigService is replaced by a mocked IConfigWriter so tests require no real .env file.
/// </summary>
public sealed class SetConfigValuesToolTests
{
    private const string ValidToken = "test-config-token";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SetConfigValuesTool BuildTool(
        IConfigWriter? configWriter = null,
        string? configToken = ValidToken,
        string? bearerToken = ValidToken)
    {
        configWriter ??= Substitute.For<IConfigWriter>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("Orchestrator:ConfigToken", configToken ?? "")
            ])
            .Build();

        var httpContext = new DefaultHttpContext();
        if (bearerToken is not null)
            httpContext.Request.Headers.Authorization = $"Bearer {bearerToken}";

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        return new SetConfigValuesTool(
            configWriter,
            config,
            accessor,
            NullLogger<SetConfigValuesTool>.Instance);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── Auth ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingBearer_ReturnsUnauthorized()
    {
        var tool = BuildTool(bearerToken: null);
        var result = Parse(await tool.SetConfigValuesAsync("{\"FOO\":\"bar\"}"));
        Assert.Equal("unauthorized", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WrongBearer_ReturnsUnauthorized()
    {
        var tool = BuildTool(bearerToken: "wrong-token");
        var result = Parse(await tool.SetConfigValuesAsync("{\"FOO\":\"bar\"}"));
        Assert.Equal("unauthorized", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfigTokenNotConfigured_ReturnsConfigApiUnavailable()
    {
        var tool = BuildTool(configToken: "");
        var result = Parse(await tool.SetConfigValuesAsync("{\"FOO\":\"bar\"}"));
        Assert.Equal("config_api_unavailable", result.GetProperty("error").GetString());
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidJson_ReturnsInvalidJson()
    {
        var tool = BuildTool();
        var result = Parse(await tool.SetConfigValuesAsync("not-json"));
        Assert.Equal("invalid_json", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task EmptyObject_ReturnsEmptyPayload()
    {
        var tool = BuildTool();
        var result = Parse(await tool.SetConfigValuesAsync("{}"));
        Assert.Equal("empty_payload", result.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("lower_case")]
    [InlineData("1STARTS_WITH_DIGIT")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS-DASH")]
    public async Task InvalidKeyFormat_ReturnsInvalidKeys_NothingWritten(string badKey)
    {
        var writer = Substitute.For<IConfigWriter>();
        var tool = BuildTool(configWriter: writer);

        var result = Parse(await tool.SetConfigValuesAsync(JsonSerializer.Serialize(
            new Dictionary<string, string> { [badKey] = "v" })));

        Assert.Equal("invalid_keys", result.GetProperty("error").GetString());
        Assert.Contains(badKey, result.GetProperty("detail").GetString());
        await writer.DidNotReceive().PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DenylistedKey_ReturnsDenylisted_NothingWritten()
    {
        var writer = Substitute.For<IConfigWriter>();
        var tool = BuildTool(configWriter: writer);

        // ORCHESTRATOR_AUTH_TOKEN is on the exact denylist
        var result = Parse(await tool.SetConfigValuesAsync("{\"ORCHESTRATOR_AUTH_TOKEN\":\"x\"}"));

        Assert.Equal("denylisted", result.GetProperty("error").GetString());
        await writer.DidNotReceive().PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullValue_ReturnsNullValueError_NothingWritten()
    {
        var writer = Substitute.For<IConfigWriter>();
        var tool = BuildTool(configWriter: writer);

        var result = Parse(await tool.SetConfigValuesAsync("{\"GOOD_KEY\":\"val\",\"BAD_KEY\":null}"));

        Assert.Equal("null_value", result.GetProperty("error").GetString());
        var keys = result.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("BAD_KEY", keys);
        await writer.DidNotReceive().PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    // ── Classification ────────────────────────────────────────────────────────

    [Fact]
    public async Task NewKey_ReturnsCreated_BroadcastTrue()
    {
        var writer = Substitute.For<IConfigWriter>();
        writer.GetExistingKeys(Arg.Any<IEnumerable<string>>()).Returns(new HashSet<string>());
        writer.PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(["NEW_KEY"]);

        var tool = BuildTool(configWriter: writer);
        var result = Parse(await tool.SetConfigValuesAsync("{\"NEW_KEY\":\"v\"}"));

        Assert.Contains("NEW_KEY", result.GetProperty("created").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(result.GetProperty("updated").EnumerateArray());
        Assert.Empty(result.GetProperty("unchanged").EnumerateArray());
        Assert.True(result.GetProperty("broadcast").GetBoolean());
    }

    [Fact]
    public async Task ExistingKeyWithNewValue_ReturnsUpdated_BroadcastTrue()
    {
        var writer = Substitute.For<IConfigWriter>();
        writer.GetExistingKeys(Arg.Any<IEnumerable<string>>())
            .Returns(new HashSet<string>(["EXISTING_KEY"], StringComparer.Ordinal));
        writer.PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(["EXISTING_KEY"]);

        var tool = BuildTool(configWriter: writer);
        var result = Parse(await tool.SetConfigValuesAsync("{\"EXISTING_KEY\":\"new-value\"}"));

        Assert.Empty(result.GetProperty("created").EnumerateArray());
        Assert.Contains("EXISTING_KEY", result.GetProperty("updated").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(result.GetProperty("unchanged").EnumerateArray());
        Assert.True(result.GetProperty("broadcast").GetBoolean());
    }

    [Fact]
    public async Task IdenticalValue_ReturnsUnchanged_BroadcastFalse()
    {
        var writer = Substitute.For<IConfigWriter>();
        writer.GetExistingKeys(Arg.Any<IEnumerable<string>>())
            .Returns(new HashSet<string>(["STABLE_KEY"], StringComparer.Ordinal));
        // PutValuesAsync returns empty list — no actual change detected
        writer.PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var tool = BuildTool(configWriter: writer);
        var result = Parse(await tool.SetConfigValuesAsync("{\"STABLE_KEY\":\"same-value\"}"));

        Assert.Empty(result.GetProperty("created").EnumerateArray());
        Assert.Empty(result.GetProperty("updated").EnumerateArray());
        Assert.Contains("STABLE_KEY", result.GetProperty("unchanged").EnumerateArray().Select(e => e.GetString()));
        Assert.False(result.GetProperty("broadcast").GetBoolean());
    }

    [Fact]
    public async Task MixedBatch_CorrectClassification()
    {
        var writer = Substitute.For<IConfigWriter>();
        // EXISTING_KEY was already in .env; NEW_KEY was not; STABLE_KEY was but value unchanged
        writer.GetExistingKeys(Arg.Any<IEnumerable<string>>())
            .Returns(new HashSet<string>(["EXISTING_KEY", "STABLE_KEY"], StringComparer.Ordinal));
        writer.PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(["NEW_KEY", "EXISTING_KEY"]);   // STABLE_KEY not in changed list

        var tool = BuildTool(configWriter: writer);
        var json = "{\"NEW_KEY\":\"v1\",\"EXISTING_KEY\":\"v2\",\"STABLE_KEY\":\"v3\"}";
        var result = Parse(await tool.SetConfigValuesAsync(json));

        var created   = result.GetProperty("created").EnumerateArray().Select(e => e.GetString()).ToList();
        var updated   = result.GetProperty("updated").EnumerateArray().Select(e => e.GetString()).ToList();
        var unchanged = result.GetProperty("unchanged").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["NEW_KEY"], created);
        Assert.Equal(["EXISTING_KEY"], updated);
        Assert.Equal(["STABLE_KEY"], unchanged);
        Assert.True(result.GetProperty("broadcast").GetBoolean());
    }

    // ── Exception paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task DenylistedExceptionFromWriter_ReturnsDenylisted()
    {
        var writer = Substitute.For<IConfigWriter>();
        writer.GetExistingKeys(Arg.Any<IEnumerable<string>>()).Returns(new HashSet<string>());
        writer.PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DenylistedException("blocked by service-side check"));

        var tool = BuildTool(configWriter: writer);
        var result = Parse(await tool.SetConfigValuesAsync("{\"VALID_KEY\":\"v\"}"));

        Assert.Equal("denylisted", result.GetProperty("error").GetString());
        Assert.Contains("blocked by service-side check", result.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TimeoutExceptionFromWriter_ReturnsWriteTimeout()
    {
        var writer = Substitute.For<IConfigWriter>();
        writer.GetExistingKeys(Arg.Any<IEnumerable<string>>()).Returns(new HashSet<string>());
        writer.PutValuesAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("mutex held by another writer"));

        var tool = BuildTool(configWriter: writer);
        var result = Parse(await tool.SetConfigValuesAsync("{\"VALID_KEY\":\"v\"}"));

        Assert.Equal("write_timeout", result.GetProperty("error").GetString());
    }
}
