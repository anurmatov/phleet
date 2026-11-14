namespace Fleet.Protocol;

/// <summary>
/// Attachments that are not one of the four image types: any other file a person sends.
/// </summary>
/// <remarks>
/// <para>
/// An image is sniffed, bounded by pixels and handed to a model as a picture. A file is none of
/// those things: the chat service stores its bytes without reading them, records a type it chose
/// itself, and the agent gets it as a path it may open with its own tools.
/// </para>
/// <para>
/// <b>Nothing the client declared is ever stored or served as a type.</b> A file is
/// <see cref="GenericContentType"/>, or <see cref="PdfContentType"/> when its first bytes say so —
/// both read from the bytes. The download route adds <c>Content-Disposition: attachment</c>,
/// <c>nosniff</c> and a sandboxing policy, so a file is saved, never rendered, in the one origin
/// that holds the owner's session.
/// </para>
/// <para>
/// The file's NAME is the one client-supplied thing that travels, and only two reduced forms of it
/// leave: the extension, when it is on <see cref="Extensions"/>, so the agent's tools know what
/// they are opening; and a name with every separator and control character removed, for the saved
/// download. Neither ever reaches a filesystem path on the server, which is named by the
/// attachment's id alone.
/// </para>
/// </remarks>
public static class AttachmentFiles
{
    /// <summary>The <c>kind</c> a client reserves a non-image file with.</summary>
    public const string FileKind = "file";

    /// <summary>What every file is recorded and served as, unless its bytes are a PDF's.</summary>
    public const string GenericContentType = "application/octet-stream";

    public const string PdfContentType = "application/pdf";

    /// <summary>
    /// South response header carrying the extension the agent names its copy with. Always a member
    /// of <see cref="Extensions"/> or <see cref="UnknownExtension"/>, and checked again on arrival.
    /// </summary>
    public const string ExtensionHeader = "X-Fleet-Attachment-Extension";

    /// <summary>The extension of a file whose own is not on the list.</summary>
    public const string UnknownExtension = ".bin";

    /// <summary>
    /// Extensions an agent's copy may carry. A list, not a pattern: an extension decides which tool
    /// opens a file and how a shell treats it, so only ones someone chose are passed on.
    /// </summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.Ordinal)
    {
        // text and data
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".jsonl", ".ndjson", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".conf", ".diff", ".patch", ".tex", ".rst",
        // documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".rtf", ".odt", ".ods", ".odp", ".epub",
        // code
        ".html", ".htm", ".css", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".ipynb", ".cs", ".csproj",
        ".go", ".rs", ".java", ".kt", ".swift", ".rb", ".php", ".sh", ".sql", ".c", ".h", ".cpp", ".hpp",
        ".proto", ".tf", ".gradle", ".lua", ".r",
        // archives
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".7z",
        // media a model cannot be shown here but a tool can open
        ".mp3", ".wav", ".m4a", ".ogg", ".oga", ".flac", ".mp4", ".mov", ".webm", ".mkv",
        ".heic", ".heif", ".svg", ".bmp", ".tif", ".tiff", ".avif",
    };

    /// <summary>
    /// The extension of <paramref name="fileName"/>, lower-cased, when it is on the list; otherwise
    /// <see cref="UnknownExtension"/>.
    /// </summary>
    public static string SafeExtension(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return UnknownExtension;

        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return UnknownExtension;

        var extension = fileName[dot..].ToLowerInvariant();
        return Extensions.Contains(extension) ? extension : UnknownExtension;
    }

    /// <summary>
    /// <paramref name="extension"/> when it is one this system issues; otherwise
    /// <see cref="UnknownExtension"/>. What the receiving side applies to a header it did not write.
    /// </summary>
    public static string KnownExtensionOrUnknown(string? extension) =>
        extension is not null && Extensions.Contains(extension) ? extension : UnknownExtension;

    /// <summary>
    /// A name safe to offer a browser for the saved file: no path, no control characters, no quotes,
    /// at most 120 characters. Null when nothing usable is left.
    /// </summary>
    public static string? SafeDownloadName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        // The last path segment only: a name is not a place.
        var name = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];

        var kept = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsControl(c) || c is '"' or '\'' or ';' or '%' or '<' or '>' or ':' or '|' or '?' or '*') continue;
            kept.Append(c);
        }

        var cleaned = kept.ToString().Trim().TrimStart('.');
        if (cleaned.Length == 0) return null;

        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    /// <summary>True when the bytes begin as a PDF does.</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> prefix) => prefix.StartsWith("%PDF-"u8);
}
