using System.Net;
using System.Net.Sockets;
using Openshell.Sandbox.V1;

namespace Fleet.Orchestrator.Services;

public static class OpenShellEndpointCompiler
{
    // Bootstrap permits one inspected route, never ordinary MCP/admin/runtime intake.
    public static void ValidateBootstrap(NetworkEndpoint endpoint)
    {
        if (Uri.CheckHostName(endpoint.Host) != UriHostNameType.Dns || endpoint.Host.Contains('*')
            || endpoint.Port == 0 || endpoint.Ports.Count != 0 || endpoint.AllowedIps.Count == 0
            || endpoint.Protocol != "rest" || endpoint.Path != "/identity/ready"
            || endpoint.Tls != NetworkTlsMode.Unspecified || endpoint.Enforcement != NetworkEnforcementMode.Enforce
            || endpoint.Access != NetworkAccessPreset.Unspecified || endpoint.Rules.Count != 1
            || endpoint.Rules[0].Allow is not { Method: "POST", Path: "/identity/ready" } allow || allow.Query.Count != 0
            || endpoint.CredentialBinding is not null || endpoint.AllowUninspectedCredentials
            || endpoint.RequestBodyCredentialRewrite || endpoint.WebsocketCredentialRewrite)
            throw new OpenShellStateConflictException("bootstrap_destination_unsupported");
        foreach (var pin in endpoint.AllowedIps)
            if (!IPAddress.TryParse(pin, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
                || IPAddress.IsLoopback(ip) || ip.GetAddressBytes()[0] is 0 or >= 224
                || ip.GetAddressBytes() is [169, 254, _, _])
                throw new OpenShellStateConflictException("bootstrap_destination_unpinned");
    }
}
