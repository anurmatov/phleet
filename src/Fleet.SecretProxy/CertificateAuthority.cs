using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleet.Shared;

namespace Fleet.SecretProxy;

/// <summary>
/// The local certificate authority that lets <c>fleet-secrets</c> answer TLS for a bound host. It is
/// created on first start and trusted only inside agent containers, where the orchestrator mounts
/// its public certificate.
/// </summary>
/// <remarks>
/// The private key never leaves <see cref="SecretProxyOptions.CaDirectory"/>. Whoever holds it can
/// impersonate any host to an agent, so only this service mounts that directory writable, and the
/// orchestrator binds the certificate file alone into agents.
/// </remarks>
internal sealed class CertificateAuthority : IDisposable
{
    private static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan LeafRenewBefore = TimeSpan.FromDays(7);

    private readonly X509Certificate2 _ca;
    private readonly ConcurrentDictionary<string, X509Certificate2> _leaves = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    private CertificateAuthority(X509Certificate2 ca, TimeProvider time)
    {
        _ca = ca;
        _time = time;
    }

    /// <summary>The CA certificate, without its key: what an agent trusts.</summary>
    public X509Certificate2 PublicCertificate => X509CertificateLoader.LoadCertificate(_ca.RawData);

    /// <summary>
    /// Loads the CA from <paramref name="directory"/>, creating it when both files are absent. One
    /// file without the other is a fault: regenerating would silently invalidate every agent's trust.
    /// </summary>
    public static CertificateAuthority LoadOrCreate(string directory, ILogger logger, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, SecretProxyState.CaCertificateFile);
        var keyPath = Path.Combine(directory, SecretProxyState.CaKeyFile);
        var hasCertificate = File.Exists(certificatePath);
        var hasKey = File.Exists(keyPath);

        if (hasCertificate != hasKey)
            throw new InvalidOperationException(
                $"secret_proxy_ca_incomplete: {directory} holds only one of {SecretProxyState.CaCertificateFile} and "
                + $"{SecretProxyState.CaKeyFile}. Restore the missing file, or remove both to create a new CA "
                + "(then reprovision every agent that holds a placeholder).");

        if (!hasCertificate)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=fleet-secrets local CA", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var now = time.GetUtcNow();
            using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

            // Key first, private from the first byte; the certificate last, so a crash in between
            // leaves the "only one file" fault above rather than a certificate with no key.
            WritePrivate(keyPath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(certificatePath, created.ExportCertificatePem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(certificatePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            logger.LogWarning(
                "SecretProxy created a new local CA in {Directory}. Agents trust it from their next provision.", directory);
        }

        var ca = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        if (ca.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true })
        {
            ca.Dispose();
            throw new InvalidOperationException("secret_proxy_ca_invalid: the stored certificate is not a CA certificate.");
        }
        if (ca.NotAfter.ToUniversalTime() <= time.GetUtcNow().UtcDateTime)
        {
            ca.Dispose();
            throw new InvalidOperationException("secret_proxy_ca_expired: remove both CA files to create a new one, then reprovision every agent.");
        }
        return new CertificateAuthority(ca, time);
    }

    /// <summary>A server certificate for exactly <paramref name="host"/>, cached until it nears expiry.</summary>
    public X509Certificate2 LeafFor(string host)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_leaves.TryGetValue(host, out var cached) && cached.NotAfter.ToUniversalTime() - now > LeafRenewBefore)
            return cached;

        var created = CreateLeaf(host);
        _leaves[host] = created;
        return created;
    }

    private X509Certificate2 CreateLeaf(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = _time.GetUtcNow();
        var notAfter = now + LeafLifetime;
        if (notAfter.UtcDateTime > _ca.NotAfter.ToUniversalTime())
            notAfter = new DateTimeOffset(_ca.NotAfter.ToUniversalTime(), TimeSpan.Zero);

        Span<byte> serial = stackalloc byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;

        using var signed = request.Create(_ca, now.AddHours(-1), notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Through PKCS#12 so the key is usable by the TLS stack on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    private static void WritePrivate(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var writer = new StreamWriter(path, options);
        writer.Write(content);
    }

    public void Dispose()
    {
        foreach (var leaf in _leaves.Values)
            leaf.Dispose();
        _ca.Dispose();
    }
}
