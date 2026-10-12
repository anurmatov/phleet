using System.Security.Cryptography;
using System.Text;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.EpicGrants;

public sealed class AutonomyCharterTests
{
    private const string Valid = """{"items":[],"visibility":{"example-org/example-repo":"private","example-org/public-repo":"public"}}""";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreflightIntegrity_ExactStoredBytesPreserveRecordedVisibility(bool whitespace)
    {
        var stored = whitespace ? " \n" + Valid + "\n " : Valid;
        var result = EpicGrantPreflight.ReadStoredVisibility(stored, Hash(stored));
        Assert.True(result.Valid);
        Assert.Null(result.Failure);
        Assert.Equal("private", result.Visibility["example-org/example-repo"]);
        Assert.Equal("public", result.Visibility["example-org/public-repo"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "hash")]
    [InlineData("json", null)]
    public void PreflightIntegrity_MissingEvidenceReturnsNoVisibility(string? json, string? hash)
    {
        var result = EpicGrantPreflight.ReadStoredVisibility(json, hash);
        Assert.False(result.Valid);
        Assert.Equal("missing", result.Failure);
        Assert.Empty(result.Visibility);
    }

    [Theory]
    [InlineData("byte")]
    [InlineData("whitespace")]
    [InlineData("uppercase_hash")]
    public void PreflightIntegrity_ChangedBytesOrHashFailBeforeParsing(string change)
    {
        var json = change == "byte" ? Valid.Replace("private", "public") : change == "whitespace" ? " " + Valid : Valid;
        var hash = change == "uppercase_hash" ? Hash(Valid).ToUpperInvariant() : Hash(Valid);
        var result = EpicGrantPreflight.ReadStoredVisibility(json, hash);
        Assert.False(result.Valid);
        Assert.Equal("mismatch", result.Failure);
        Assert.Empty(result.Visibility);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"visibility\":null}")]
    [InlineData("{\"visibility\":[]}")]
    [InlineData("{\"visibility\":{\"example-org/example-repo\":\"internal\"}}")]
    [InlineData("{\"visibility\":{\"example-org/example-repo\":\"Public\"}}")]
    [InlineData("{\"visibility\":{\"example-org/example-repo\":true}}")]
    public void PreflightIntegrity_MatchingHashWithBadShapeReturnsNoVisibility(string json)
    {
        var result = EpicGrantPreflight.ReadStoredVisibility(json, Hash(json));
        Assert.False(result.Valid);
        Assert.Equal("malformed", result.Failure);
        Assert.Empty(result.Visibility);
    }
}
