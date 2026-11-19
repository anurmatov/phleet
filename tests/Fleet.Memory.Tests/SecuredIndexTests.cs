using Fleet.Memory.Configuration;
using Fleet.Memory.Data;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

/// <summary>
/// For the tests that talk to a Qdrant that requires TLS and an API key. They run when
/// <see cref="UrlVariable"/> and <see cref="KeyVariable"/> are set and are skipped, with this
/// reason, when they are not. <c>bash tests/memory-qdrant/secured.sh</c> starts such a Qdrant with a
/// certificate from an authority made for the run, gives the test process that authority as a
/// trusted root the platform's normal way (<c>SSL_CERT_FILE</c>, which .NET reads on Linux), runs
/// them and refuses a run that skipped any.
///
/// The type names here deliberately do not contain the word the other real-server tests are
/// selected by, so neither script runs (and then counts as skipped) the other's tests.
/// </summary>
internal static class SecuredIndex
{
    public const string UrlVariable = "FLEET_MEMORY_TEST_SECURED_URL";
    public const string KeyVariable = "FLEET_MEMORY_TEST_SECURED_API_KEY";

    public const string Reason =
        "needs a Qdrant that requires TLS and an API key: run it with one, bash tests/memory-qdrant/secured.sh";

    public static string? Url => Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? url : null;
    public static string Key => Environment.GetEnvironmentVariable(KeyVariable) ?? "";
}

public sealed class SecuredIndexFactAttribute : FactAttribute
{
    public SecuredIndexFactAttribute() { if (SecuredIndex.Url is null) Skip = SecuredIndex.Reason; }
}

/// <summary>
/// The real <see cref="VectorStore"/>, built from <c>Qdrant:Url</c> and <c>Qdrant:ApiKey</c> the way
/// the service builds it, against a real Qdrant that answers only over TLS and only to its key:
/// the configured key is what the server receives, over a connection whose certificate was
/// validated the normal way. Nothing here relaxes that validation; the authority is trusted
/// because the process was given it as a root.
/// </summary>
public sealed class SecuredIndexTests
{
    private const int Dimensions = 8;

    private static VectorStore Store(string url, string apiKey, string collection) =>
        new(Options.Create(new QdrantOptions { Url = url, ApiKey = apiKey, Collection = collection }),
            Options.Create(new EmbeddingOptions { Dimensions = Dimensions }),
            NullLogger<VectorStore>.Instance);

    private static string NewCollection() => $"fleet_memtest_secured_{Guid.NewGuid():N}";

    [SecuredIndexFact]
    public async Task WithTheConfiguredKey_OverTls_TheStoreCreatesAndReadsItsCollection()
    {
        var collection = NewCollection();
        var store = Store(SecuredIndex.Url!, SecuredIndex.Key, collection);
        using var client = QdrantConnection
            .From(new QdrantOptions { Url = SecuredIndex.Url!, ApiKey = SecuredIndex.Key })
            .CreateClient();
        try
        {
            await store.EnsureCollectionAsync();

            Assert.Contains(collection, await client.ListCollectionsAsync());
            // A second start finds what the first made: a read with the key, not only a write.
            await store.EnsureCollectionAsync();
            Assert.Equal(0UL, await client.CountAsync(collection));
        }
        finally
        {
            try { await client.DeleteCollectionAsync(collection); } catch { /* best effort */ }
        }
    }

    /// <summary>The server is what the script says it is: without the key it answers nothing.</summary>
    [SecuredIndexFact]
    public async Task WithoutAKey_TheServerRefuses()
    {
        var store = Store(SecuredIndex.Url!, "", NewCollection());

        var error = await Assert.ThrowsAsync<RpcException>(() => store.EnsureCollectionAsync());

        Assert.Contains(error.StatusCode, new[] { StatusCode.Unauthenticated, StatusCode.PermissionDenied });
    }

    [SecuredIndexFact]
    public async Task WithAnotherKey_TheServerRefuses()
    {
        var store = Store(SecuredIndex.Url!, "synthetic-wrong-key-0123456789abcdef", NewCollection());

        var error = await Assert.ThrowsAsync<RpcException>(() => store.EnsureCollectionAsync());

        Assert.Contains(error.StatusCode, new[] { StatusCode.Unauthenticated, StatusCode.PermissionDenied });
    }

    /// <summary>And only over TLS: the same port asked in plaintext gives no answer to act on.</summary>
    [SecuredIndexFact]
    public async Task InPlaintext_TheServerIsNotReached()
    {
        var plaintext = new UriBuilder(SecuredIndex.Url!) { Scheme = "http" }.Uri.ToString();
        var store = Store(plaintext, "", NewCollection());

        var error = await Assert.ThrowsAnyAsync<Exception>(() => store.EnsureCollectionAsync());

        Assert.False(error is RpcException { StatusCode: StatusCode.OK or StatusCode.Unimplemented });
    }
}

/// <summary>
/// For the tests that talk to a Qdrant that requires an API key over plaintext gRPC, reached by an
/// explicit <c>http</c> URL: the configuration for an operator-trusted private network. Set and
/// skipped like <see cref="SecuredIndex"/>; <c>bash tests/memory-qdrant/secured.sh</c> starts that
/// Qdrant beside the TLS one, with a key of its own, and refuses a run that skipped any.
/// </summary>
internal static class SecuredIndexOverHttp
{
    public const string UrlVariable = "FLEET_MEMORY_TEST_SECURED_HTTP_URL";
    public const string KeyVariable = "FLEET_MEMORY_TEST_SECURED_HTTP_API_KEY";

    public const string Reason =
        "needs a Qdrant that requires an API key over http: run it with one, bash tests/memory-qdrant/secured.sh";

    public static string? Url => Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url ? url : null;
    public static string Key => Environment.GetEnvironmentVariable(KeyVariable) ?? "";
}

public sealed class SecuredIndexOverHttpFactAttribute : FactAttribute
{
    public SecuredIndexOverHttpFactAttribute() { if (SecuredIndexOverHttp.Url is null) Skip = SecuredIndexOverHttp.Reason; }
}

/// <summary>
/// The real <see cref="VectorStore"/> with <c>Qdrant:ApiKey</c> and an explicit <c>http</c>
/// <c>Qdrant:Url</c>, against a real Qdrant that answers only to its key: the configured key is
/// what the server receives, in plaintext, and the server refuses a call without it or with another.
/// </summary>
public sealed class SecuredIndexOverHttpTests
{
    private const int Dimensions = 8;

    private static VectorStore Store(string apiKey, string collection) =>
        new(Options.Create(new QdrantOptions { Url = SecuredIndexOverHttp.Url!, ApiKey = apiKey, Collection = collection }),
            Options.Create(new EmbeddingOptions { Dimensions = Dimensions }),
            NullLogger<VectorStore>.Instance);

    private static string NewCollection() => $"fleet_memtest_secured_http_{Guid.NewGuid():N}";

    [SecuredIndexOverHttpFact]
    public async Task WithTheConfiguredKey_OverExplicitHttp_TheStoreCreatesAndReadsItsCollection()
    {
        var url = SecuredIndexOverHttp.Url!;
        Assert.StartsWith("http://", url);
        var collection = NewCollection();
        var store = Store(SecuredIndexOverHttp.Key, collection);
        var connection = QdrantConnection.From(new QdrantOptions { Url = url, ApiKey = SecuredIndexOverHttp.Key });
        Assert.False(connection.Https);
        using var client = connection.CreateClient();
        try
        {
            await store.EnsureCollectionAsync();

            Assert.Contains(collection, await client.ListCollectionsAsync());
            // A second start finds what the first made: a read with the key, not only a write.
            await store.EnsureCollectionAsync();
            Assert.Equal(0UL, await client.CountAsync(collection));
        }
        finally
        {
            try { await client.DeleteCollectionAsync(collection); } catch { /* best effort */ }
        }
    }

    [SecuredIndexOverHttpFact]
    public async Task WithoutAKey_TheServerRefuses()
    {
        var store = Store("", NewCollection());

        var error = await Assert.ThrowsAsync<RpcException>(() => store.EnsureCollectionAsync());

        Assert.Contains(error.StatusCode, new[] { StatusCode.Unauthenticated, StatusCode.PermissionDenied });
    }

    [SecuredIndexOverHttpFact]
    public async Task WithAnotherKey_TheServerRefuses()
    {
        var store = Store("synthetic-wrong-key-0123456789abcdef", NewCollection());

        var error = await Assert.ThrowsAsync<RpcException>(() => store.EnsureCollectionAsync());

        Assert.Contains(error.StatusCode, new[] { StatusCode.Unauthenticated, StatusCode.PermissionDenied });
    }
}
