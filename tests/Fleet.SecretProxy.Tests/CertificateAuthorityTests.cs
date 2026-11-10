using System.Security.Cryptography.X509Certificates;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.SecretProxy.Tests;

public sealed class CertificateAuthorityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-secrets-ca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Certificate => Path.Combine(_directory, SecretProxyState.CaCertificateFile);
    private string Key => Path.Combine(_directory, SecretProxyState.CaKeyFile);

    [Fact]
    public void FirstStart_CreatesACaWithAPrivateKeyFile_AndTheNextStartLoadsTheSameOne()
    {
        using var first = CertificateAuthority.LoadOrCreate(_directory, NullLogger.Instance);
        using var firstPublic = first.PublicCertificate;
        using var second = CertificateAuthority.LoadOrCreate(_directory, NullLogger.Instance);
        using var secondPublic = second.PublicCertificate;

        Assert.Equal(firstPublic.Thumbprint, secondPublic.Thumbprint);
        Assert.False(firstPublic.HasPrivateKey);
        Assert.Contains("BEGIN CERTIFICATE", File.ReadAllText(Certificate));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Certificate));
        Assert.Contains("PRIVATE KEY", File.ReadAllText(Key));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Key));

        var constraints = firstPublic.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyOneOfTheTwoFiles_IsAStartupFault_NotASilentNewCa(bool removeKey)
    {
        CertificateAuthority.LoadOrCreate(_directory, NullLogger.Instance).Dispose();
        var kept = File.ReadAllText(removeKey ? Certificate : Key);
        File.Delete(removeKey ? Key : Certificate);

        var error = Assert.Throws<InvalidOperationException>(() => CertificateAuthority.LoadOrCreate(_directory, NullLogger.Instance));

        Assert.Contains("secret_proxy_ca_incomplete", error.Message);
        Assert.Equal(kept, File.ReadAllText(removeKey ? Certificate : Key));
        Assert.False(File.Exists(removeKey ? Key : Certificate));
    }

    [Fact]
    public void Leaf_NamesOneHost_IsAServerCertificate_ChainsToTheCa_AndIsCached()
    {
        using var ca = CertificateAuthority.LoadOrCreate(_directory, NullLogger.Instance);
        using var root = ca.PublicCertificate;

        var leaf = ca.LeafFor("api.example.test");

        Assert.Same(leaf, ca.LeafFor("api.example.test"));
        Assert.NotEqual(leaf.Thumbprint, ca.LeafFor("other.example.test").Thumbprint);
        Assert.True(leaf.HasPrivateKey);
        Assert.True(leaf.MatchesHostname("api.example.test"));
        Assert.False(leaf.MatchesHostname("sub.api.example.test"));
        Assert.False(leaf.MatchesHostname("example.test"));
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal("1.3.6.1.5.5.7.3.1",
            leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages[0].Value);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        using var leafPublic = X509CertificateLoader.LoadCertificate(leaf.RawData);
        Assert.True(chain.Build(leafPublic));
    }

    [Fact]
    public void Leaf_IsReissuedWhenItNearsExpiry()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        using var ca = CertificateAuthority.LoadOrCreate(_directory, NullLogger.Instance, time);
        var first = ca.LeafFor("api.example.test");

        time.Advance(TimeSpan.FromDays(20));
        Assert.Same(first, ca.LeafFor("api.example.test"));

        time.Advance(TimeSpan.FromDays(5));
        var renewed = ca.LeafFor("api.example.test");
        Assert.NotSame(first, renewed);
        Assert.True(renewed.NotAfter > first.NotAfter);
    }
}
