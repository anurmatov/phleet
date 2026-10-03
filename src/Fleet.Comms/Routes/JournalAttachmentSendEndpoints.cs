using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Conversations.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Fleet.Comms.Routes;
public static class JournalAttachmentSendEndpoints
{
    public const string HandlePath = "/journal/v1/attachments/send-handle";
    public const string CrossHandlePath = "/journal/v1/attachments/cross-chat/send-handle";
    public const string CrossContentPath = "/journal/v1/attachments/cross-chat/content";
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static void Map(WebApplication app, JournalSendSourceResolver resolver)
    {
        app.MapPost(HandlePath, (Delegate)Handle);
        app.MapPost(CrossHandlePath, (Delegate)Handle);
        async Task<IResult> Handle(HttpContext context)
        {
            var result = "unavailable";
            try
            {
                var response = await HandleCore(context);
                result = response is IStatusCodeHttpResult status ? (status.StatusCode ?? 200).ToString(System.Globalization.CultureInfo.InvariantCulture) : "200";
                return response;
            }
            finally
            {
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JournalAttachmentSend")
                    .LogInformation("Journal send handle: route={route} result={result}",
                        context.Request.Path == CrossHandlePath ? "cross" : "same", result);
            }
        }
        async Task<IResult> HandleCore(HttpContext context)
        {
            var subject = (string)context.Items[JournalAuth.SubjectItem]!;
            var cross = context.Request.Path == CrossHandlePath;
            var bytes = new byte[1025]; var count = 0;
            while (count < bytes.Length) { var n = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted); if (n == 0) break; count += n; }
            JournalAttachmentRequest? request = null;
            if (count <= 1024) try { request = JsonSerializer.Deserialize<JournalAttachmentRequest>(bytes.AsSpan(0, count), Json); } catch (JsonException) { }
            if (request is null) return Results.Content(JournalAttachmentRequest.Invalid("body"), "application/json", statusCode: 400);
            if (request.Error() is { } error) return Results.Content(error, "application/json", statusCode: 400);
            if (cross && request.TelegramMessageId is not null) return Results.Content(JournalAttachmentRequest.Invalid("telegram_message_id"), "application/json", statusCode: 400);
            var result = await resolver.ResolveAsync(subject, request, cross, context.RequestAborted);
            if (result.Error is not null) return Results.Content(result.Error, "application/json", statusCode: result.Status);
            var row = result.Source!; var file = row.Attachment;
            context.Response.Headers["X-Journal-Message-Id"] = file.MessageId;
            var body = new Dictionary<string, object?> { ["kind"] = file.Kind, ["mime_type"] = file.MimeType,
                ["byte_size"] = file.ByteSize, ["file_name"] = file.FileName, ["archived"] = file.AttachmentState is "committed" or "archived",
                ["source"] = result.Cross ? "cross" : "same" };
            if (row.FileIdBotId == result.BotId && row.FileId is not null) body["file_id"] = row.FileId;
            return Results.Json(body, Json);
        }
    }
}
