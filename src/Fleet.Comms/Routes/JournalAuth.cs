using System.Collections.Concurrent;
using System.Text;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fleet.Comms.Routes;

/// <summary>
/// Authentication and the per-subject concurrency cap for the journal listener.
/// </summary>
/// <remarks>
/// <para>
/// MIDDLEWARE, before routing and before any body byte is read, on EVERY path — matched or not —
/// for the reason <see cref="SouthEndpoints"/> gives: an endpoint filter runs after binding, and an
/// anonymous caller could then probe shapes by watching the status change.
/// </para>
/// <para>
/// Absent, malformed, bad-MAC and wrong-purpose tokens, and any path that is not a journal route,
/// all get the same bytes. The south bearer is not a journal token and verifies as nothing here.
/// </para>
/// </remarks>
public static class JournalAuth
{
    /// <summary>In-flight requests one subject may hold. A constant, not a setting.</summary>
    public const int MaxInFlightPerSubject = 8;

    public const string SubjectItem = "journal.subject";

    /// <summary>The one 401 body. Byte-identical for every refusal.</summary>
    internal static readonly byte[] UnauthorizedBody = Encoding.UTF8.GetBytes("{\"error\":\"unauthorized\"}");

    /// <summary>
    /// The 404 an upload route answers for "no such upload" and "not yours".
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Byte-identical to the 401.</b> The status says "not found" and the body says nothing
    /// more: a foreign-owned upload id and a fabricated one produce the same bytes, so the route is
    /// not an oracle for whether someone else has opened an upload. The status differs from the
    /// middleware's 401 only because a PUT to a path that resolves needs a 404, not a 401.
    /// </remarks>
    public static IResult Unauthorized() => Results.Content(
        Encoding.UTF8.GetString(UnauthorizedBody), "application/json",
        statusCode: StatusCodes.Status404NotFound);

    internal static readonly byte[] TooManyRequestsBody = Encoding.UTF8.GetBytes("{\"error\":\"too_many_requests\"}");

    public static void Use(WebApplication app, IReadOnlyList<byte[]> keys, JournalRuntimeStats stats)
    {
        var inFlight = new ConcurrentDictionary<string, InFlight>(StringComparer.Ordinal);

        app.Use(async (context, next) =>
        {
            var purpose = RequiredPurpose(context.Request);
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";

            // Verified even when the path names no purpose, so an unknown path costs what a known one
            // does, and refused afterwards.
            if (!JournalTokens.TryVerify(token, purpose ?? "", keys, out var subject) || purpose is null)
            {
                stats.Rejected("unauthorized");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(UnauthorizedBody);
                return;
            }

            // After auth, because the cap is per subject; still before the body is read.
            var slot = inFlight.GetOrAdd(subject, _ => new InFlight());
            if (Interlocked.Increment(ref slot.Count) > MaxInFlightPerSubject)
            {
                Interlocked.Decrement(ref slot.Count);
                stats.Rejected("too_many_requests");
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "1";
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(TooManyRequestsBody);
                return;
            }

            try
            {
                context.Items[SubjectItem] = subject;
                await next(context);
            }
            finally
            {
                Interlocked.Decrement(ref slot.Count);
            }
        });
    }

    /// <summary>The token purpose a request needs, by exact method and path; null for anything else.</summary>
    /// <remarks>
    /// The two upload routes need <c>ingest</c> too (#388), and the PUT is matched by prefix rather
    /// than by an exact path because the id is in it. <b>Prefix matching is safe here and nowhere
    /// else</b>: the check is the whole route table, so a path that starts like an upload route but
    /// is not one still resolves to <c>ingest</c> and then 404s from the router — after being
    /// authenticated, which is the order that must not change.
    /// </remarks>
    private static string? RequiredPurpose(HttpRequest request)
    {
        var path = request.Path.Value;

        if (HttpMethods.IsPost(request.Method) && string.Equals(path, JournalEndpoints.MessagesPath, StringComparison.Ordinal))
            return JournalTokens.PurposeIngest;

        if (HttpMethods.IsPost(request.Method) && path == JournalUploadEndpoints.UploadsPath)
            return JournalTokens.PurposeIngest;

        if (HttpMethods.IsPut(request.Method)
            && path?.StartsWith(JournalUploadEndpoints.UploadsPath + "/", StringComparison.Ordinal) == true)
            return JournalTokens.PurposeIngest;

        if (HttpMethods.IsGet(request.Method) && string.Equals(path, JournalEndpoints.StatusPath, StringComparison.Ordinal))
            return JournalTokens.PurposeStatus;

        // The read tools (#394), on the exact path only. GET and DELETE are the other two verbs of
        // streamable HTTP; they need a read token too, so an unauthenticated caller gets the one 401
        // on every verb and an authenticated one gets the route's 405 — never a 401 that would send
        // an MCP client into an OAuth discovery flow.
        if ((HttpMethods.IsPost(request.Method) || HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
            && string.Equals(path, JournalMcp.Path, StringComparison.Ordinal))
            return JournalTokens.PurposeRead;

        return null;
    }

    private sealed class InFlight
    {
        public int Count;
    }
}
