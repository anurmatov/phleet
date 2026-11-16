using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

/// <summary>
/// The session file: what it records, how it is written, and the rule a provider process start
/// reads from it. Resume only for the same provider, the same instructions and the same place the
/// model runs; everything else is a new session.
/// </summary>
public sealed class ProviderSessionFileTests : IDisposable
{
    private const string Instructions = "You are a synthetic agent.\nAnswer briefly.\n";

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"fleet-session-file-{Guid.NewGuid():N}");

    public ProviderSessionFileTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* teardown only */ }
    }

    private ProviderSessionFile File_(string provider = "claude", string runsOn = ProviderSessionFile.Cloud) =>
        new(_workDir, provider, runsOn, NullLogger.Instance);

    private string FilePath => Path.Combine(_workDir, ".fleet", "session.json");

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void TheFile_IsWhereTheSharedDefinitionSaysItIs()
    {
        Assert.Equal(Path.Combine(_workDir, AgentSessionFile.RelativePath), File_().FilePath);
        Assert.Equal(FilePath, File_().FilePath);
    }

    [Fact]
    public void TheHash_IsTheSha256OfTheExactInstructions()
    {
        Assert.Equal(Sha256Hex(Instructions), ProviderSessionFile.HashInstructions(Instructions));
        Assert.NotEqual(
            ProviderSessionFile.HashInstructions(Instructions),
            ProviderSessionFile.HashInstructions(Instructions + " "));
    }

    [Fact]
    public void Save_RecordsTheProviderTheIdAndTheHash_AndNeverTheInstructions()
    {
        File_("codex", "ollama").Save("thread-1", ProviderSessionFile.HashInstructions(Instructions));

        var text = File.ReadAllText(FilePath);
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("codex", (string?)json["provider"]);
        Assert.Equal("thread-1", (string?)json["sessionId"]);
        Assert.Equal(Sha256Hex(Instructions), (string?)json["instructionsSha256"]);
        Assert.Equal("ollama", (string?)json["runsOn"]);
        Assert.Equal(4, json.Count);
        Assert.DoesNotContain("synthetic agent", text);
    }

    /// <summary>
    /// Atomic means a reader never sees half a file: the new content is written beside the target
    /// and takes its place by rename. A handle opened on the old file therefore still reads the
    /// old content afterwards; a rewrite in place would have changed what it reads.
    /// </summary>
    [Fact]
    public void Save_ReplacesTheFileByRename_LeavingNoTemporaryFile()
    {
        var file = File_();
        file.Save("session-old", "hash-old");
        var before = File.ReadAllText(FilePath);

        using (var oldFile = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            file.Save("session-new", "hash-new");

            using var reader = new StreamReader(oldFile);
            Assert.Equal(before, reader.ReadToEnd());
        }

        Assert.Contains("session-new", File.ReadAllText(FilePath));
        Assert.Equal(["session.json"], Directory.GetFiles(Path.GetDirectoryName(FilePath)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Decide_Resumes_ForTheSameProviderInstructionsAndPlace()
    {
        var hash = ProviderSessionFile.HashInstructions(Instructions);
        File_().Save("session-1", hash);

        var decision = File_().Decide(hash);

        Assert.Equal("session-1", decision.ResumeId);
        Assert.Null(decision.ReplacedId);
    }

    [Fact]
    public void Decide_IsNew_WithNoFile()
    {
        var decision = File_().Decide("hash");

        Assert.Null(decision.ResumeId);
        Assert.Equal("no file", decision.Reason);
        Assert.Null(decision.ReplacedId);
    }

    [Fact]
    public void Decide_IsNew_ForAnotherProvidersSession_AndDoesNotNameItAsReplaced()
    {
        var hash = ProviderSessionFile.HashInstructions(Instructions);
        File_("claude").Save("session-1", hash);

        var decision = File_("codex").Decide(hash);

        Assert.Null(decision.ResumeId);
        Assert.Equal("other provider", decision.Reason);
        Assert.Null(decision.ReplacedId);
    }

    [Fact]
    public void Decide_IsNew_WhenTheInstructionsChanged_AndNamesTheSessionItReplaces()
    {
        File_().Save("session-1", ProviderSessionFile.HashInstructions(Instructions));

        var decision = File_().Decide(ProviderSessionFile.HashInstructions(Instructions + "One more rule.\n"));

        Assert.Null(decision.ResumeId);
        Assert.Equal("instructions changed", decision.Reason);
        Assert.Equal("session-1", decision.ReplacedId);
    }

    [Fact]
    public void Decide_IsNew_WhenTheModelRunsElsewhere()
    {
        var hash = ProviderSessionFile.HashInstructions(Instructions);
        File_("codex", ProviderSessionFile.Cloud).Save("thread-1", hash);

        var decision = File_("codex", "ollama").Decide(hash);

        Assert.Null(decision.ResumeId);
        Assert.Equal("model runs elsewhere", decision.Reason);
        Assert.Equal("thread-1", decision.ReplacedId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"provider":"claude"}""")]
    [InlineData("""{"provider":"claude","sessionId":"","instructionsSha256":"x"}""")]
    public void Decide_IsNew_ForAFileThatDoesNotHoldASession(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        var decision = File_().Decide("hash");

        Assert.Null(decision.ResumeId);
        Assert.Equal("unreadable file", decision.Reason);
    }

    [Fact]
    public void Remove_DeletesTheFile_AndAMissingFileIsNotAFault()
    {
        var file = File_();
        file.Save("session-1", "hash");

        Assert.True(file.Remove());
        Assert.False(File.Exists(FilePath));
        Assert.True(file.Remove());
        Assert.Equal("no file", file.Decide("hash").Reason);
    }

    /// <summary>A removed file is written again by the next save, even for the same session.</summary>
    [Fact]
    public void Save_AfterRemove_WritesTheFileAgain()
    {
        var file = File_();
        file.Save("session-1", "hash");
        file.Remove();

        file.Save("session-1", "hash");

        Assert.True(File.Exists(FilePath));
    }
}
