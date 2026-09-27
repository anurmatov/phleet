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
    private static string? RequiredPurpose(HttpRequest request)
    {
        var path = request.Path.Value;

        if (HttpMethods.IsPost(request.Method) && string.Equals(path, JournalEndpoints.MessagesPath, StringComparison.Ordinal))
            return JournalTokens.PurposeIngest;

        if (HttpMethods.IsGet(request.Method) && string.Equals(path, JournalEndpoints.StatusPath, StringComparison.Ordinal))
            return JournalTokens.PurposeStatus;

        return null;
    }

    private sealed class InFlight
    {
        public int Count;
    }
}
