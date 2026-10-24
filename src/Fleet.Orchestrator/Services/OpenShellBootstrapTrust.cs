using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Fleet.Shared.Security;

namespace Fleet.Orchestrator.Services;

public sealed class OpenShellBootstrapTrust : IDisposable
{
    private readonly X509Certificate2 _ca;
    private readonly string _dnsName;
    public int Port { get; }
    public FleetRequestAssertionVerifier Assertions { get; }
    public OpenShellBootstrapTrust(OpenShellBootstrapReceiverOptions options)
    {
        if (options.Port is < 1 or > 65535 or 3600 || string.IsNullOrWhiteSpace(options.IngressDnsName))
            throw new InvalidOperationException("A distinct bootstrap listener and pinned ingress name are required.");
        Port = options.Port;
        var pem = File.ReadAllText(options.AssertionPublicKeyPath);
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidOperationException("Only a public assertion key is allowed.");
        using var key = ECDsa.Create(); key.ImportFromPem(pem);
        Assertions = new(key.ExportSubjectPublicKeyInfo(), options.Issuer, "orchestrator:/identity/ready", options.KeyId);
        var caPem = File.ReadAllText(options.IngressCaPath);
        if (caPem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidOperationException("Only the public ingress CA is allowed.");
        _ca = X509Certificate2.CreateFromPem(caPem);
        _dnsName = options.IngressDnsName;
    }
    public bool AcceptCertificate(X509Certificate2? certificate)
    {
        if (certificate is null) return false;
        try
        {
            var san = certificate.Extensions["2.5.29.17"];
            if (san is null) return false;
            var names = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
            var matches = false;
            while (names.HasData)
            {
                if (names.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    matches |= names.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)) == _dnsName;
                else names.ReadEncodedValue();
            }
            if (!matches) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(_ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            return chain.Build(certificate);
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException) { return false; }
    }
    public void Dispose() => _ca.Dispose();
}
