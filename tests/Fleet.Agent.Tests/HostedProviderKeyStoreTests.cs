using Fleet.Agent.Services.HostedProviders;

namespace Fleet.Agent.Tests;

/// <summary>#335 D5.4 / AC4: the key is read once, the file is deleted, the value is held in memory.</summary>
public sealed class HostedProviderKeyStoreTests : IDisposable
{
    private const string KeyEnv = "ZAI_CODING_PLAN_API_KEY";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hosted-key-{Guid.NewGuid():N}");
    private string KeyPath => Path.Combine(_dir, "key");

    public HostedProviderKeyStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* teardown only */ }
    }

    [Fact]
    public void Load_ReadsTheKey_DeletesTheFile_HoldsTheValue()
    {
        File.WriteAllText(KeyPath, "key-store-test-value\n");
        var store = new HostedProviderKeyStore(KeyPath);

        store.Load(KeyEnv);

        Assert.True(store.IsLoaded);
        Assert.Equal("key-store-test-value", store.Key);
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void Load_IsIdempotentOnceLoaded()
    {
        File.WriteAllText(KeyPath, "key-store-test-value");
        var store = new HostedProviderKeyStore(KeyPath);
        store.Load(KeyEnv);

        store.Load(KeyEnv);

        Assert.Equal("key-store-test-value", store.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("<secret>")]
    public void Load_RejectsAnUnusableKey_NamingTheVariable_AndStillDeletesTheFile(string content)
    {
        File.WriteAllText(KeyPath, content);
        var store = new HostedProviderKeyStore(KeyPath);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Load(KeyEnv));

        Assert.Contains(KeyEnv, ex.Message);
        Assert.False(store.IsLoaded);
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void Load_MissingFile_ThrowsNamingTheVariable()
    {
        var store = new HostedProviderKeyStore(KeyPath);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Load(KeyEnv));

        Assert.Contains(KeyEnv, ex.Message);
    }

    [Fact]
    public void Key_BeforeLoad_Throws()
    {
        var store = new HostedProviderKeyStore(KeyPath);

        Assert.Throws<InvalidOperationException>(() => store.Key);
    }
}
