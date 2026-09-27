using System.Text;
using System.Text.Json.Nodes;
using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client.Tests;

public sealed class JournalOptionsTests
{
    [Fact]
    public void A_blank_token_is_off_and_never_a_fault()
    {
        var options = new JournalOptions { IngestToken = "  ", BaseUrl = "nonsense", ExcludedChatIds = "x" };

        Assert.False(options.Enabled);
        Assert.Null(options.DescribeFault());
    }

    [Fact]
    public void A_well_formed_token_starts()
    {
        var options = new JournalOptions { IngestToken = Records.Token, ExcludedChatIds = "-1001, ,0" };

        Assert.True(options.Enabled);
        Assert.Null(options.DescribeFault());
        Assert.Equal("agent1", options.Subject);
        Assert.Equal("http://fleet-comms:8083", options.BaseUrl);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("cj1.notifier.agent1.AAAA")]
    [InlineData("cj1.observer.agent1.AAAA")]
    [InlineData("cj1.ingest.agent1")]
    public void A_token_of_the_wrong_shape_fails_startup_without_echoing_it(string token)
    {
        var fault = new JournalOptions { IngestToken = token }.DescribeFault();

        Assert.NotNull(fault);
        Assert.Contains("Journal:IngestToken", fault, StringComparison.Ordinal);
        Assert.DoesNotContain(token, fault, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fleet-comms:8083")]
    [InlineData("ftp://fleet-comms")]
    [InlineData("/journal")]
    public void A_base_url_that_is_not_absolute_http_fails(string url) =>
        Assert.Contains("Journal:BaseUrl", new JournalOptions { IngestToken = Records.Token, BaseUrl = url }.DescribeFault());

    [Fact]
    public void Exclusions_ignore_blanks_and_zero_and_refuse_garbage()
    {
        Assert.Equal(new HashSet<long> { -1001, 5 }, JournalOptions.ParseExcludedChatIds(" -1001 ,, 0, 5,5"));
        Assert.Contains("ExcludedChatIds",
            new JournalOptions { IngestToken = Records.Token, ExcludedChatIds = "-1001,abc" }.DescribeFault());
    }
}

public sealed class JournalRecordJsonTests
{
    [Fact]
    public void The_wire_form_omits_what_is_absent_and_keeps_cyrillic_as_utf8()
    {
        var bytes = JournalRecordJson.Serialize(Records.Record(1, text: "привет"));
        var json = Encoding.UTF8.GetString(bytes);
        var node = JsonNode.Parse(bytes)!;

        Assert.Contains("привет", json, StringComparison.Ordinal);
        Assert.Equal("telegram", node["channel"]!.GetValue<string>());
        Assert.Equal("plain", node["textFormat"]!.GetValue<string>());
        Assert.Equal("telegram_update", node["origin"]!.GetValue<string>());
        Assert.Equal("2026-09-27T10:00:00.000Z", node["sentAt"]!.GetValue<string>());
        Assert.Null(node["transcript"]);
        Assert.Null(node["sendGroup"]);
        Assert.Null(node["telegram"]!["mediaGroupId"]);
        Assert.Empty(node["attachments"]!.AsArray());
    }

    [Fact]
    public void A_record_without_text_carries_no_text_format()
    {
        var node = JsonNode.Parse(JournalRecordJson.Serialize(Records.Record(1, text: null)))!;

        Assert.Null(node["text"]);
        Assert.Null(node["textFormat"]);
    }

    [Fact]
    public void A_long_transcript_is_cut_and_marked_but_text_is_never_touched()
    {
        var text = new string('t', 70_000);
        var record = Records.Record(1, text: text) with { Transcript = new string('я', 40_000) };

        var node = JsonNode.Parse(JournalRecordJson.Serialize(record))!;

        Assert.Equal(text, node["text"]!.GetValue<string>());
        Assert.True(Encoding.UTF8.GetByteCount(node["transcript"]!.GetValue<string>()) <= JournalRecordJson.MaxFieldBytes);
        Assert.True(node["transcriptTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Bounded_fields_are_clamped_and_ids_that_would_be_refused_are_dropped()
    {
        var record = Records.Record(1) with
        {
            Telegram = Records.Record(1).Telegram with { ChatTitle = new string('c', 300), MediaGroupId = "bad id with spaces" },
            Sender = new JournalSender { Kind = JournalSenderKind.Human, Id = "1", Display = new string('d', 200) },
            Attachments = [new JournalAttachment
            {
                Ordinal = 0, Kind = JournalAttachmentKind.Document, MimeType = "not a mime",
                FileName = new string('f', 300), FileUniqueId = "ok_id-1",
                NotArchivedReason = JournalNotArchivedReason.MediaDisabled,
            }],
        };

        var node = JsonNode.Parse(JournalRecordJson.Serialize(record))!;

        Assert.Equal(256, node["telegram"]!["chatTitle"]!.GetValue<string>().Length);
        Assert.Null(node["telegram"]!["mediaGroupId"]);
        Assert.Equal(128, node["sender"]!["display"]!.GetValue<string>().Length);
        var attachment = node["attachments"]![0]!;
        Assert.Equal("application/octet-stream", attachment["mimeType"]!.GetValue<string>());
        Assert.Equal(255, attachment["fileName"]!.GetValue<string>().Length);
        Assert.Equal("ok_id-1", attachment["fileUniqueId"]!.GetValue<string>());
        Assert.Equal("media_disabled", attachment["notArchivedReason"]!.GetValue<string>());
    }
}
