using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Turns the attachment ids on a dispatched command into <see cref="MessageImage"/>s the executor
/// already knows how to carry (#308 D7).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole per-provider answer, and it is why the design chose this shape.</b> The
/// bytes go to <c>TaskManager.StartTask(..., images: …)</c> — a parameter that already exists and is
/// already wired for every provider:
/// </para>
/// <list type="table">
///   <item><term>Claude</term><description>a native <c>type:"image"</c> base64 content block</description></item>
///   <item><term>Codex</term><description>a <c>local_image</c> block built from <c>FilePath</c></description></item>
///   <item><term>Gemini</term><description>an <c>@&lt;path&gt;</c> reference</description></item>
/// </list>
/// <para>
/// No executor changes. Both the native-block and the path-based providers work on day one through
/// the mechanism the chat channel has used since the document-attachment work.
/// </para>
/// <para>
/// ⚠️ Two couplings severed on purpose:
/// </para>
/// <list type="bullet">
///   <item><c>Telegram:PersistAttachments</c> does NOT gate this. The directory is shared; the
///   switch is not (MUST NOT 15). A deployment that turned Telegram persistence off still gets
///   conversation images.</item>
///   <item>The local file path is internal and never leaves this process in a client event. The
///   <c>attachmentId</c> is the only identifier that may (protocol Constraints 4 and 5).</item>
/// </list>
/// </remarks>
public sealed class ConversationAttachmentFetcher(
    ConversationSouthClient south,
    IOptions<TelegramOptions> telegram,
    ILogger<ConversationAttachmentFetcher> logger)
{
    /// <summary>What one dispatch's attachments resolved to.</summary>
    /// <param name="Images">Everything that was fetched. May be shorter than what was asked for.</param>
    /// <param name="Unavailable">
    /// How many could not be fetched. Non-zero means the caller owes the client a
    /// <c>turn.notice</c> — the turn still runs with its text, and it must never answer as though no
    /// image was sent (MUST NOT / AC-29).
    /// </param>
    public sealed record Fetched(IReadOnlyList<MessageImage> Images, int Unavailable)
    {
        public static readonly Fetched None = new([], 0);
    }

    /// <summary>
    /// Fetch every attachment named by a command, and persist what arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An image is never allowed to abandon a turn.</b> Every failure path here returns rather
    /// than throws: a 404 or a 410 from the south route, a connection refused, a timeout, a disk that
    /// will not take the write. The caller reports what is missing and runs the turn on its text.
    /// </para>
    /// <para>
    /// A failed local write is not fatal either. The bytes are still in hand, so Claude gets its
    /// content block from memory; the path-based providers get the notice path instead. That is a
    /// real degradation and it is reported, not hidden.
    /// </para>
    /// </remarks>
    public async Task<Fetched> FetchAsync(
        IReadOnlyList<string> attachmentIds, CancellationToken ct)
    {
        if (attachmentIds.Count == 0) return Fetched.None;

        var images = new List<MessageImage>(attachmentIds.Count);
        var unavailable = 0;

        foreach (var attachmentId in attachmentIds)
        {
            try
            {
                // Bounded retry INSIDE the dispatch budget. A store that is briefly unreachable
                // should not cost the image, and a store that is properly down should not hold the
                // turn open — so the retry is the client's ordinary one and then it gives up.
                var fetched = await south.ExecuteWithRetryAsync(
                    token => south.FetchAttachmentAsync(attachmentId, token),
                    label: "attachment-fetch", attempts: 3, ct);

                if (fetched is null)
                {
                    // 404 or 410 — the attachment is not coming. Counted, never retried: asking the
                    // same question again gets the same answer while the turn waits.
                    unavailable++;
                    continue;
                }

                images.Add(Persist(attachmentId, fetched.Value.Bytes, fetched.Value.ContentType));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Type only. A transport error message can carry a host, and this one is logged.
                logger.LogWarning(
                    "conversation attachment could not be fetched: {Error}", e.GetType().Name);
                unavailable++;
            }
        }

        return new Fetched(images, unavailable);
    }

    /// <summary>
    /// Write the bytes beside the Telegram attachments and build the image.
    /// </summary>
    /// <remarks>
    /// The same directory, deliberately — one place on disk that holds "files an agent may read", so
    /// the retention sweep and the provider hints already cover it. The <b>switch</b> is not shared:
    /// <c>PersistAttachments</c> governs Telegram's own downloads and says nothing about this path.
    /// </remarks>
    private MessageImage Persist(string attachmentId, byte[] bytes, string contentType)
    {
        var directory = telegram.Value.AttachmentDir;

        if (string.IsNullOrWhiteSpace(directory))
            return new MessageImage(bytes, contentType);

        try
        {
            Directory.CreateDirectory(directory);

            // The id names the file, and the id is a ULID. Nothing client-supplied reaches a path.
            var path = Path.Combine(directory, $"conv-{attachmentId}{ExtensionFor(contentType)}");
            File.WriteAllBytes(path, bytes);

            return new MessageImage(bytes, contentType) { FilePath = path };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Non-fatal, and reported honestly: Claude still gets the bytes from memory, and the
            // path-based providers will not see this image.
            logger.LogWarning(
                "conversation attachment could not be persisted: {Error}", e.GetType().Name);
            return new MessageImage(bytes, contentType);
        }
    }

    /// <summary>
    /// The extension for one of the four accepted types.
    /// </summary>
    /// <remarks>
    /// Derived from the SNIFFED type the service served, never from a client-supplied filename — and
    /// it matters: <c>AttachmentSweeper.BuildHints</c> classifies by extension, so a wrong one turns
    /// an image into a <c>[file attachment: …]</c> the vision path ignores.
    /// </remarks>
    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".bin",
    };
}
