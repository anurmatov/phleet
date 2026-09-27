using System.Security.Cryptography;
using System.Text;
using Fleet.Conversations.Journal;

namespace Fleet.Comms.Tests;

/// <summary>Journal token mint and verify (#375 Tokens, AC11, AC12).</summary>
public sealed class JournalTokenTests
{
    private static readonly byte[] KeyA = JournalTokens.ParseKeys(JournalTestHost.KeyA)[0];
    private static readonly byte[] KeyB = JournalTokens.ParseKeys(JournalTestHost.KeyB)[0];

    /// <summary>The format is exactly <c>cj1.purpose.subject.mac</c> with the MAC the issue defines.</summary>
    [Fact]
    public void A_minted_token_has_the_documented_format()
    {
        var token = JournalTokens.Mint(KeyA, "ingest", "agent1");

        var expectedMac = Convert.ToBase64String(HMACSHA256.HashData(KeyA, Encoding.UTF8.GetBytes("cj1|ingest|agent1")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal($"cj1.ingest.agent1.{expectedMac}", token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void A_token_verifies_for_its_own_purpose_and_yields_its_subject()
    {
        var token = JournalTokens.Mint(KeyA, "ingest", "agent1");

        Assert.True(JournalTokens.TryVerify(token, "ingest", [KeyA], out var subject));
        Assert.Equal("agent1", subject);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("read")]
    [InlineData("ingest-service")]
    [InlineData("")]
    public void A_token_never_verifies_for_another_purpose(string required)
    {
        var token = JournalTokens.Mint(KeyA, "ingest", "agent1");

        Assert.False(JournalTokens.TryVerify(token, required, [KeyA], out var subject));
        Assert.Equal(string.Empty, subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cj1")]
    [InlineData("cj1.ingest.agent1")]
    [InlineData("cj2.ingest.agent1.AAAA")]
    [InlineData("cj1.ingest.agent.1.AAAA")]
    [InlineData("cj1.INGEST.agent1.AAAA")]
    [InlineData("cj1.ingest.agent 1.AAAA")]
    [InlineData("cj1.ingest.agent1.!!!!")]
    public void A_malformed_token_is_refused(string? token)
    {
        Assert.False(JournalTokens.TryVerify(token, "ingest", [KeyA], out _));
    }

    /// <summary>Changing the subject or purpose inside a valid token breaks its MAC.</summary>
    [Theory]
    [InlineData("cj1.ingest.agent2.")]
    [InlineData("cj1.status.agent1.")]
    public void The_mac_binds_purpose_and_subject(string forgedPrefix)
    {
        var mac = JournalTokens.Mint(KeyA, "ingest", "agent1").Split('.')[3];

        Assert.False(JournalTokens.TryVerify(forgedPrefix + mac, forgedPrefix.Split('.')[1], [KeyA], out _));
    }

    [Fact]
    public void Every_listed_key_verifies_and_a_removed_key_does_not()
    {
        var oldToken = JournalTokens.Mint(KeyA, "ingest", "agent1");
        var newToken = JournalTokens.Mint(KeyB, "ingest", "agent1");

        Assert.True(JournalTokens.TryVerify(oldToken, "ingest", [KeyB, KeyA], out _));
        Assert.True(JournalTokens.TryVerify(newToken, "ingest", [KeyB, KeyA], out _));

        Assert.False(JournalTokens.TryVerify(oldToken, "ingest", [KeyB], out _));
        Assert.True(JournalTokens.TryVerify(newToken, "ingest", [KeyB], out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void An_unusable_key_list_is_refused_without_echoing_it(string keys)
    {
        var error = Assert.Throws<FormatException>(() => JournalTokens.ParseKeys(keys));

        if (keys.Trim().Length > 0)
            Assert.DoesNotContain(keys.Trim(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_are_comma_separated_and_trimmed()
    {
        var keys = JournalTokens.ParseKeys($" {JournalTestHost.KeyB} ,{JournalTestHost.KeyA},");

        Assert.Equal(2, keys.Count);
        Assert.Equal(KeyB, keys[0]);
        Assert.Equal(KeyA, keys[1]);
    }

    [Theory]
    [InlineData("agent1", true)]
    [InlineData("Agent_runtime-7", true)]
    [InlineData("", false)]
    [InlineData("has.dot", false)]
    [InlineData("has space", false)]
    public void A_subject_is_a_plain_identifier(string subject, bool valid)
    {
        Assert.Equal(valid, JournalTokens.IsValidSubject(subject));
        Assert.True(JournalTokens.IsValidSubject(new string('a', 128)));
        Assert.False(JournalTokens.IsValidSubject(new string('a', 129)));
    }
}
