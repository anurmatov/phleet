namespace Fleet.Conversations.Journal;

/// <summary>
/// What the object store needs, as the store sees it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT <c>Fleet.Comms.Configuration.MediaOptions</c>: <c>Fleet.Conversations</c> does
/// not reference <c>Fleet.Comms</c> and must not start to for a settings type — the Comms test
/// suite builds its store hosts from this assembly, and a dependency the other way would make the
/// store untestable outside the deployable. Comms maps its options onto this at the one place the
/// two meet.
/// </para>
/// <para>
/// ⚠️ <see cref="SecretKey"/> is a credential. Nothing here logs, echoes or returns it.
/// </para>
/// </remarks>
public sealed record JournalMediaOptions
{
    public required string Endpoint { get; init; }

    public required string Bucket { get; init; }

    public required string AccessKey { get; init; }

    public required string SecretKey { get; init; }

    public required string Region { get; init; }

    /// <summary>Per-request budget. A hung bucket is a store failure, never a held-open request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The guard the store applies to its own configuration, so a caller that constructed it from a
    /// half-read settings section fails here rather than at the first request.
    /// </summary>
    /// <remarks>
    /// The message names the field and never its value: <see cref="SecretKey"/> reaching an
    /// exception message reaches a container log.
    /// </remarks>
    /// <exception cref="ArgumentException">A field is missing or unusable.</exception>
    public void Validate(bool requireFields = true)
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(
                "media_endpoint_invalid: the object store endpoint is not an absolute http:// or https:// URL.");

        if (!requireFields) return;

        if (string.IsNullOrWhiteSpace(Bucket))
            throw new ArgumentException("media_bucket_invalid: the object store bucket is required.");

        if (string.IsNullOrWhiteSpace(AccessKey))
            throw new ArgumentException("media_access_key_invalid: the object store access key is required.");

        if (string.IsNullOrWhiteSpace(SecretKey))
            throw new ArgumentException("media_secret_key_invalid: the object store secret key is required.");

        if (string.IsNullOrWhiteSpace(Region))
            throw new ArgumentException("media_region_invalid: the object store region is required.");
    }
}
