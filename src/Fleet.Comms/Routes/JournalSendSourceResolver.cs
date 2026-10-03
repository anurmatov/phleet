using System.Text;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
namespace Fleet.Comms.Routes;
public sealed record JournalSendResolution(JournalSendSource? Source, long BotId, bool Cross, int Status = 200, string? Error = null)
{
    public override string ToString() => nameof(JournalSendResolution);
}
/// <summary>Resolve first, authorize second, disclose last. Off/error masks held lookup failures.</summary>
public sealed class JournalSendSourceResolver(IJournalSendSource? store, JournalBindingScope bindings, IJournalCrossChatAuthorization auth)
{
    public async Task<JournalSendResolution> ResolveAsync(string subject, JournalAttachmentRequest request, bool crossRoute, CancellationToken ct)
    {
        static JournalSendResolution Failure(int status, string error) => new(null, 0, false, status, error);
        static JournalSendResolution Denied(string reason) => Failure(403, JsonSerializer.Serialize(new { error = "source_denied", reason }));
        var bound = bindings.Get(subject);
        if (bound is null) return Failure(409, JournalAttachmentRequest.Unavailable("no_bound_conversation"));
        if (bound.ChatKind != "private") return Failure(409, JournalAttachmentRequest.Unavailable("not_private_binding"));
        var key = JournalKeys.ConversationKey(new JournalTelegramRef { BotId = bound.BotId!.Value, ChatId = bound.ChatId!.Value, ChatKind = JournalChatKind.Private, MessageId = 0 });
        JournalSendSource? row = null;
        JournalSendResolution? held = bindings.IsExcluded(bound.ChatId.Value)
            ? Failure(409, JournalAttachmentRequest.Unavailable("conversation_not_journaled")) : null;
        try
        {
            if (store is null) throw new JournalStoreUnavailableException("send_attachment");
            row = await store.FindSendSourceAsync(subject, key, bound.ChatId.Value, request.MessageId, request.TelegramMessageId, request.Ordinal, ct);
        }
        catch (JournalStoreUnavailableException) { held = Failure(503, "{\"error\":\"store_unavailable\",\"retryable\":true}"); }
        var cross = row is not null && row.ConversationKey != key;
        held ??= row is null ? Failure(404, "{\"error\":\"not_found\"}")
            : bindings.IsExcluded(row.ChatId) ? Failure(409, JournalAttachmentRequest.Unavailable("conversation_not_journaled"))
            : !cross ? null
            : !crossRoute ? Denied("cross_chat_disabled")
            : row.ChatKind == "private" ? Denied("private_source")
            : row.ChatKind is not ("group" or "supergroup") ? Denied("unsupported_source")
            : !row.RequesterWasPresent ? Denied("requester_not_participant") : null;
        if (crossRoute)
        {
            var authorized = await auth.CheckAsync(subject, bound.BotId.Value,
                cross && held is null ? row!.ChatId : null, cross && held is null ? bound.ChatId : null, ct);
            if (!authorized.Available) return Failure(503, "{\"error\":\"authorization_unavailable\"}");
            if (!authorized.Effective) return Failure(401, Encoding.UTF8.GetString(JournalAuth.UnauthorizedBody));
            if (held is null && cross)
            {
                if (authorized.Member == "not_member") held = Denied("requester_not_member");
                else if (authorized.Member != "member") held = Denied("membership_unverified");
            }
        }
        return held ?? new(row, bound.BotId.Value, cross);
    }
}
