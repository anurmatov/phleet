using System.Text;
using System.Text.Json.Nodes;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;

namespace Fleet.Comms.Tests;

public sealed class JournalCopyRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Source = "01K00000000000000000000000";

    private static JsonObject Record() => JsonNode.Parse("""
        {"eventId":"01K00000000000000000000001","channel":"telegram",
         "telegram":{"botId":5005,"chatId":1001,"chatKind":"private","messageId":1},
         "direction":"outbound","sender":{"kind":"agent","id":"5005"},
         "sentAt":"2026-01-01T00:00:00Z","text":null,"origin":"agent_copy",
         "attachments":[{"ordinal":0,"kind":"document","mimeType":"application/pdf",
         "byteSize":9,"fileId":"FILE-SYNTH-1","notArchivedReason":"copied",
         "copiedFrom":{"messageId":"01K00000000000000000000000","ordinal":0}}]}
        """)!.AsObject();

    private static JournalRecord? Parse(JsonObject node, out JournalRecordParser.Failure? failure) =>
        JournalRecordParser.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()), Now, out failure);

    [Fact]
    public void CopyRecord_Valid_ParsesAuditLinkAndFileId()
    {
        var record = Parse(Record(), out var failure);
        Assert.Null(failure);
        Assert.NotNull(record);
        var attachment = Assert.Single(record.Attachments);
        Assert.Equal("FILE-SYNTH-1", attachment.FileId);
        Assert.Equal(Source, attachment.CopiedFrom!.MessageId);
        Assert.Equal(0, attachment.CopiedFrom.Ordinal);
        Assert.Equal(JournalNotArchivedReason.Copied, attachment.NotArchivedReason);
        Assert.Equal(JournalRecordOrigin.AgentCopy, record.Origin);
    }

    [Theory]
    [InlineData("direction", "inbound")]
    [InlineData("origin", "agent_runtime")]
    public void CopyRecord_InvalidOriginPairing_IsRefused(string field, string value)
    {
        var node = Record();
        node[field] = value;
        Assert.Null(Parse(node, out var failure));
        Assert.Equal("invalid_record", failure!.Error);
    }

    [Theory]
    [InlineData("copiedFrom")]
    [InlineData("notArchivedReason")]
    public void CopyRecord_MissingRequiredMetadata_IsRefused(string field)
    {
        var node = Record();
        node["attachments"]![0]!.AsObject().Remove(field);
        Assert.Null(Parse(node, out var failure));
        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void CopyRecord_SourceOrdinalOutsideByteRange_IsRefused(int ordinal)
    {
        var node = Record();
        node["attachments"]![0]!["copiedFrom"]!["ordinal"] = ordinal;
        Assert.Null(Parse(node, out var failure));
        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void FileId_AsciiLengthBoundary_IsEnforced(int length, bool accepted)
    {
        var node = Record();
        node["attachments"]![0]!["fileId"] = new string('X', length);
        Assert.Equal(accepted, Parse(node, out _) is not null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unicode-\u0430")]
    [InlineData("control-\n")]
    public void FileId_Invalid_IsRefused(string fileId)
    {
        var node = Record();
        node["attachments"]![0]!["fileId"] = fileId;
        Assert.Null(Parse(node, out var failure));
        Assert.NotNull(failure);
    }

    [Fact]
    public void Fingerprint_FileIdAndCopyLink_DoNotChangeIdentity()
    {
        var first = Parse(Record(), out _)!;
        var attachment = first.Attachments.Single();
        var second = first with { Attachments = [attachment with { FileId = "OTHER", CopiedFrom = null }] };
        Assert.Equal(JournalFingerprint.Compute("synthetic", first), JournalFingerprint.Compute("synthetic", second));
    }
}
