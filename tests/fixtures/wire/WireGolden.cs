using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Fleet.WireFixtures;

/// <summary>
/// The exact bytes one fleet service puts on the wire for another, kept as files under
/// <c>tests/fixtures/wire/</c> and compiled into every test project that owns one end of a pair.
/// </summary>
/// <remarks>
/// <para>
/// A producer's test serializes through the production code and compares with the file. The
/// consumer's test, in a different test project, reads the same file through the consumer's own
/// handler. Neither test project references the other service, so the file is what holds the two
/// ends to one shape.
/// </para>
/// <para>
/// The comparison is on text, not on parsed JSON: property order, casing and whether a null is
/// written or left out are all part of what an older or newer peer sees.
/// </para>
/// <para>
/// Values that differ on every run are replaced before comparing: a timestamp by <c>&lt;ts&gt;</c>,
/// a generated correlation id by <c>&lt;id&gt;</c>, and the three heartbeat fields that come from
/// the clock or the container's environment.
/// </para>
/// <para>
/// To record a shape that was changed on purpose, run the producer's test once with
/// <c>FLEET_WIRE_GOLDEN_RECORD=1</c> and read the diff. A changed file is a wire change: a peer
/// built before it sends or expects the old one.
/// </para>
/// </remarks>
internal static partial class WireGolden
{
    /// <summary>What <c>&lt;ts&gt;</c> becomes when a consumer reads a file.</summary>
    public const string SampleTimestamp = "2026-01-02T03:04:05+00:00";

    /// <summary>What <c>&lt;id&gt;</c> becomes when a consumer reads a file.</summary>
    public const string SampleId = "0123456789abcdef0123456789abcdef";

    /// <summary>What <c>&lt;sha&gt;</c> becomes when a consumer reads a file.</summary>
    public const string SampleSha = "4a5b6c7d8e9f00112233445566778899aabbccddeeff00112233445566778899";

    private static readonly Lazy<string> Directory = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fleet.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, "could not locate the repository root");
        return Path.Combine(directory!.FullName, "tests", "fixtures", "wire");
    });

    /// <summary>The file as stored, placeholders included.</summary>
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(Directory.Value, name)).TrimEnd('\r', '\n');

    /// <summary>The file as a peer would have sent it: placeholders filled with sample values.</summary>
    public static string Sample(string name) =>
        Read(name).Replace("<ts>", SampleTimestamp, StringComparison.Ordinal)
            .Replace("<id>", SampleId, StringComparison.Ordinal)
            .Replace("<sha>", SampleSha, StringComparison.Ordinal);

    public static byte[] SampleBytes(string name) => Encoding.UTF8.GetBytes(Sample(name));

    /// <summary>Fails unless what the producer wrote is the stored file, byte for byte.</summary>
    public static void AssertMatches(string name, ReadOnlySpan<byte> produced) =>
        AssertMatches(name, Encoding.UTF8.GetString(produced));

    public static void AssertMatches(string name, string produced)
    {
        var normalized = Normalize(produced);
        var path = Path.Combine(Directory.Value, name);

        if (Environment.GetEnvironmentVariable("FLEET_WIRE_GOLDEN_RECORD") == "1")
        {
            File.WriteAllText(path, normalized + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"no wire sample {name}; record it with FLEET_WIRE_GOLDEN_RECORD=1");
        Assert.Equal(Read(name), normalized);
    }

    /// <summary>Replaces the values that differ on every run.</summary>
    public static string Normalize(string json)
    {
        json = TimestampValue().Replace(json, "${key}\"<ts>\"");
        json = GeneratedId().Replace(json, "${key}\"<id>\"");
        json = PlanSha().Replace(json, "${key}\"<sha>\"");
        json = UptimeSeconds().Replace(json, "\"UptimeSeconds\":0");
        json = EnvironmentValue().Replace(json, "\"${key}\":\"<env>\"");
        return json;
    }

    [GeneratedRegex(@"(?<key>""(?:Timestamp|QueuedAt)"":)""[^""]*""")]
    private static partial Regex TimestampValue();

    [GeneratedRegex(@"(?<key>""CorrelationId"":)""[0-9a-f]{32}""")]
    private static partial Regex GeneratedId();

    /// <summary>The harness plan hash, which changes with any harness default.</summary>
    [GeneratedRegex(@"(?<key>""PlanSha256"":)""[0-9a-f]{64}""")]
    private static partial Regex PlanSha();

    [GeneratedRegex(@"""UptimeSeconds"":\d+")]
    private static partial Regex UptimeSeconds();

    /// <summary>The build commit and container name a heartbeat reads from the environment.</summary>
    [GeneratedRegex(@"""(?<key>Version|ContainerName)"":(?:null|""[^""]*"")")]
    private static partial Regex EnvironmentValue();
}
