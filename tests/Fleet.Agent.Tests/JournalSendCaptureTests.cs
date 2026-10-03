using System.Text.Json;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace Fleet.Agent.Tests;
public sealed class JournalSendCaptureTests
{
    // Frozen attachment-field allowlist from the pre-feature parser at 205a55f.
    private static readonly string[] OldFields = ["ordinal", "kind", "mimeType", "byteSize", "fileName", "fileUniqueId", "uploadId", "sha256", "notArchivedReason"];
    [Theory]
    [InlineData("photo", "sendPhoto")]
    [InlineData("video", "sendVideo")]
    [InlineData("audio", "sendAudio")]
    [InlineData("voice", "sendVoice")]
    [InlineData("video_note", "sendVideoNote")]
    [InlineData("animation", "sendAnimation")]
    [InlineData("sticker", "sendSticker")]
    [InlineData("document", "sendDocument")]
    [InlineData("other", "sendDocument")]
    public async Task FileIdTransport_GoldenContainsOnlyDestinationAndFile(string kind, string method)
    {
        var root = Path.Combine(Path.GetTempPath(), "send-form-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rig = JournalCaptureTests.Rig.Build(root, journal: false);
            // The fake records every request, then refuses this new request type without sending.
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transport.SendFileIdAsync(1001, kind, "SYNTHETIC", default));
            var request = Assert.IsAssignableFrom<FileRequestBase<Message>>(Assert.Single(rig.Bot.Requests));
            Assert.Equal(method, ((global::Telegram.Bot.Requests.Abstractions.IRequest<Message>)request).MethodName);
            using var content = request.ToHttpContent()!;
            Assert.Equal("chat_id=1001&" + (kind == "other" ? "document" : kind) + "=SYNTHETIC", await content.ReadAsStringAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("inbound-photo")]
    [InlineData("reply-photo")]
    [InlineData("tts-voice")]
    public void PreFeatureGoldens_KeepExactBytesAndOldFieldAllowlist(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journal-send", name + ".json"));
        using var document = JsonDocument.Parse(bytes);
        var kind = name == "tts-voice" ? JournalAttachmentKind.Voice : JournalAttachmentKind.Photo;
        var record = new JournalRecord { EventId = "01K00000000000000000000000", Telegram = new() { BotId = 5005, ChatId = 1001, ChatKind = JournalChatKind.Private, MessageId = 42 },
            Direction = name == "inbound-photo" ? JournalDirection.Inbound : JournalDirection.Outbound,
            Origin = name == "inbound-photo" ? JournalRecordOrigin.TelegramUpdate : JournalRecordOrigin.AgentRuntime,
            Sender = new() { Kind = name == "inbound-photo" ? JournalSenderKind.Human : JournalSenderKind.Agent, Id = "1001" },
            SentAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Text = null,
            Attachments = [new() { Ordinal = 0, Kind = kind, MimeType = kind == JournalAttachmentKind.Photo ? "image/jpeg" : "audio/ogg", ByteSize = 9,
                FileUniqueId = "synthetic-unique", NotArchivedReason = JournalNotArchivedReason.MediaDisabled }] };
        Assert.Equal(bytes, JournalRecordJson.Serialize(record));
        foreach (var field in document.RootElement.GetProperty("attachments")[0].EnumerateObject()) Assert.Contains(field.Name, OldFields);
        Assert.DoesNotContain("fileId", OldFields); Assert.DoesNotContain("copiedFrom", OldFields);
    }
    [Theory]
    [InlineData("inbound-photo")]
    [InlineData("reply-photo")]
    [InlineData("tts-voice")]
    public void CaptureWithoutSendGrant_IsByteIdenticalToFrozenRecords(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-golden-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new JournalSpool(root);
            var capture = new JournalCapture(spool, new(), new(Options.Create(new TelegramOptions { AllowedUserIds = [1001] })), Options.Create(new JournalOptions()), NullLogger<JournalCapture>.Instance);
            var inbound = name == "inbound-photo"; var voice = name == "tts-voice";
            var message = new JournalMessage { BotId = 5005, ChatId = 1001, ChatType = "private", MessageId = 42,
                SenderKind = inbound ? JournalSenderKind.Human : JournalSenderKind.Agent, SenderId = "1001", Date = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Media = [new(voice ? JournalAttachmentKind.Voice : JournalAttachmentKind.Photo, voice ? "audio/ogg" : "image/jpeg", 9, null, "synthetic-unique", FileId: "MUST-NOT-EMIT")] };
            if (inbound) capture.Inbound(message); else { var batch = capture.Outbound(OutboundOrigin.Human); batch.Add(message); batch.Flush(); }
            var record = Assert.Single(spool.Pending()).Record; record["eventId"] = "01K00000000000000000000000";
            Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journal-send", name + ".json")), record.ToJsonString(JournalRecordJson.StoredOptions));
            foreach (var field in record["attachments"]![0]!.AsObject()) Assert.Contains(field.Key, OldFields);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_EmitsFileIdAndCopyLinkOnlyWithSendEnabled(bool send)
    {
        var root = Path.Combine(Path.GetTempPath(), "copy-capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new JournalSpool(root);
            var capture = new JournalCapture(spool, new(), new(Options.Create(new TelegramOptions { AllowedUserIds = [1001] })),
                Options.Create(new JournalOptions { SendEnabled = send }), NullLogger<JournalCapture>.Instance);
            var copy = capture.Outbound(OutboundOrigin.Human, JournalRecordOrigin.AgentCopy);
            copy.Add(new() { BotId = 5005, ChatId = 1001, ChatType = "private", MessageId = 42, SenderKind = JournalSenderKind.Agent, SenderId = "5005",
                Media = [new(JournalAttachmentKind.Document, "application/pdf", 9, "synthetic.pdf", "unique", FileId: "synthetic-file",
                    CopiedFrom: new() { MessageId = "01K00000000000000000000000", Ordinal = 0 })] });
            copy.Flush(); copy.Flush(); Assert.Equal(send, copy.SpoolSucceeded);
            if (!send) { Assert.Empty(spool.Pending()); return; }
            var entry = Assert.Single(spool.Pending()); Assert.Equal("agent_copy", entry.Record["origin"]!.GetValue<string>());
            Assert.Null(entry.Record["text"]); var attachment = entry.Record["attachments"]![0]!;
            Assert.Equal("copied", attachment["notArchivedReason"]!.GetValue<string>());
            Assert.Equal("synthetic-file", attachment["fileId"]!.GetValue<string>());
            Assert.Equal("01K00000000000000000000000", attachment["copiedFrom"]!["messageId"]!.GetValue<string>());
            Assert.Empty(entry.MediaOrdinals);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
