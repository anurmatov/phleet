using System.Net;

namespace Fleet.Comms.Configuration;

/// <summary>
/// The loopback-only invariant for the operations listener, enforced rather than defaulted.
///
/// <para><c>/ready</c> reports whether the auth store is answering. On a reachable address that is
/// an availability oracle for the owner's own boundary, which is why the contract says it must
/// never be published or proxied. Passing <see cref="CommsOptions.OpsUrl"/> straight to
/// <c>UseUrls</c> made that property true of the <b>default value</b> and of nothing else: a
/// wildcard or LAN address in configuration simply published it.</para>
///
/// <para>Checked before either host starts, so a misconfigured deployment is a process that refuses
/// to run rather than one that quietly exposes the endpoint.</para>
/// </summary>
public static class OpsListenerAddress
{
    /// <summary>
    /// Throw unless every configured address binds a loopback interface.
    ///
    /// <para><c>UseUrls</c> accepts a semicolon-separated list, so every entry is checked — one
    /// good address does not excuse a wildcard beside it.</para>
    /// </summary>
    public static void EnsureLoopbackOnly(string? opsUrl)
    {
        if (string.IsNullOrWhiteSpace(opsUrl))
        {
            throw new InvalidOperationException(
                $"{CommsOptions.SectionName}:{nameof(CommsOptions.OpsUrl)} is required.");
        }

        foreach (var candidate in opsUrl.Split(';', StringSplitOptions.RemoveEmptyEntries
                                                    | StringSplitOptions.TrimEntries))
        {
            if (!IsLoopback(candidate))
            {
                throw new InvalidOperationException(
                    $"{CommsOptions.SectionName}:{nameof(CommsOptions.OpsUrl)} must bind loopback " +
                    $"only; '{candidate}' does not. The readiness endpoint reports auth-store " +
                    "state and must never be reachable off the container.");
            }
        }
    }

    private static bool IsLoopback(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;

        // Kestrel's wildcards. `*` and `+` bind every interface, and neither parses as an address,
        // so they have to be rejected by name rather than by IPAddress.TryParse returning false.
        var host = uri.Host;
        if (host is "*" or "+" or "[::]")
            return false;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
            return IPAddress.IsLoopback(address);

        // A name, not an address. Only the conventional loopback names are accepted: anything else
        // resolves at bind time to whatever DNS says, which is not a property this can check.
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
