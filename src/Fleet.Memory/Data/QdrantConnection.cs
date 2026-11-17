using Fleet.Memory.Configuration;
using Qdrant.Client;

namespace Fleet.Memory.Data;

/// <summary>
/// A Qdrant configuration that cannot be used. The message names settings and never the key.
/// </summary>
public sealed class QdrantConfigurationException(string message) : Exception(message);

/// <summary>
/// How fleet-memory reaches Qdrant, and the one place its client is constructed
/// (<c>IndexConnectionTests</c> fails on a Qdrant client constructed anywhere else in this service).
///
/// Without <c>Qdrant:ApiKey</c> the client is built as it always was: the URL's host and port, TLS
/// exactly when the scheme is <c>https</c>. With a key the URL must be <c>https</c>, and the server's
/// certificate is validated the platform's normal way: there is no setting that relaxes it. A key
/// with any other URL is refused; the key is never sent over a connection that is not encrypted,
/// and never dropped to connect without it.
/// </summary>
public sealed class QdrantConnection
{
    private QdrantConnection(string host, int port, bool https, string? apiKey)
    {
        Host = host;
        Port = port;
        Https = https;
        ApiKey = apiKey;
    }

    public string Host { get; }
    public int Port { get; }
    public bool Https { get; }

    /// <summary>The key sent with every call, or null when none is configured.</summary>
    public string? ApiKey { get; }

    /// <exception cref="QdrantConfigurationException">A key is configured and the URL is not <c>https</c>.</exception>
    public static QdrantConnection From(QdrantOptions options)
    {
        if (DescribeFault(options.Url, options.ApiKey) is { } fault)
            throw new QdrantConfigurationException(fault);

        var uri = new Uri(options.Url);
        return new QdrantConnection(uri.Host, uri.Port, uri.Scheme == "https", KeyOrNull(options.ApiKey));
    }

    /// <summary>
    /// What makes the two settings unusable together, or null. Checked once at startup, before
    /// anything connects, and again by <see cref="From"/>. Without a key there is nothing to refuse:
    /// the URL is used as it always was.
    /// </summary>
    public static string? DescribeFault(string? url, string? apiKey)
    {
        if (KeyOrNull(apiKey) is null) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") return null;

        return $"{QdrantOptions.Section}:ApiKey is set and {QdrantOptions.Section}:Url is not an https URL. "
             + "The API key is sent only over TLS: use an https URL for a Qdrant that requires a key, or remove the key. "
             + "Nothing was connected.";
    }

    private static string? KeyOrNull(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    /// <summary>The client. No custom certificate validation in either branch.</summary>
    public QdrantClient CreateClient() =>
        ApiKey is null
            ? new QdrantClient(Host, Port, https: Https)
            : new QdrantClient(Host, Port, https: true, apiKey: ApiKey);

    /// <summary>For a log line: whether a key is in use, never the key.</summary>
    public override string ToString() =>
        $"{(Https ? "https" : "http")}://{Host}:{Port}, {(ApiKey is null ? "no API key" : "with an API key")}";
}
