using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Fleet.Identity.Configuration;

namespace Fleet.Identity.Services;

public static class ReceiverTransport
{
    public static HttpClient Create(IdentityOptions options)
    {
        var client = X509Certificate2.CreateFromPemFile(options.ClientCertificatePath, options.ClientKeyPath);
        if (!client.HasPrivateKey) throw new InvalidOperationException("Outside mTLS client key required.");
        var ca = X509Certificate2.CreateFromPemFile(options.ReceiverCaPath);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            CheckCertificateRevocationList = false };
        handler.ClientCertificates.Add(client);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
            return chain.Build(certificate);
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
