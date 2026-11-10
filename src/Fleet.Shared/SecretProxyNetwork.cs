using System.Net;
using System.Net.Sockets;

namespace Fleet.Shared;

/// <summary>
/// The internal Docker network granted agents share with <c>fleet-secrets</c>, and the fixed address
/// each side has on it. Addresses are derived, never stored: the forwarder sits at a configured
/// address and an agent at <see cref="FirstAgentOffset"/> plus its database id, so an agent keeps its
/// address across restarts and no two agents can share one.
/// </summary>
public sealed class SecretProxyNetwork
{
    public const string NetworkName = "fleet-secrets-net";
    public const string DefaultSubnet = "10.213.96.0/22";
    public const string DefaultAddress = "10.213.96.2";

    /// <summary>Offsets below this are left to the gateway, the forwarder and operator use.</summary>
    public const int FirstAgentOffset = 16;

    private readonly uint _network;
    private readonly uint _broadcast;

    public string Subnet { get; }
    public IPAddress ProxyAddress { get; }

    private SecretProxyNetwork(string subnet, uint network, uint broadcast, IPAddress proxyAddress)
    {
        Subnet = subnet;
        _network = network;
        _broadcast = broadcast;
        ProxyAddress = proxyAddress;
    }

    /// <summary>
    /// Parses an IPv4 CIDR subnet and the forwarder's address inside it. Returns the fault, or null
    /// with <paramref name="network"/> set. A /16 to /28 subnet only: wider is a typo, narrower has no
    /// room for agents.
    /// </summary>
    public static string? TryParse(string? subnet, string? proxyAddress, out SecretProxyNetwork? network)
    {
        network = null;
        subnet = (subnet ?? "").Trim();
        proxyAddress = (proxyAddress ?? "").Trim();

        var slash = subnet.IndexOf('/');
        if (slash <= 0
            || !IPAddress.TryParse(subnet[..slash], out var baseAddress)
            || baseAddress.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(subnet[(slash + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 16 or > 28)
            return "secret_proxy_subnet_invalid: expected an IPv4 subnet between /16 and /28, such as " + DefaultSubnet;

        var mask = uint.MaxValue << (32 - prefix);
        var value = ToUInt32(baseAddress);
        if ((value & mask) != value)
            return "secret_proxy_subnet_invalid: the address has bits set outside the prefix";

        if (!IPAddress.TryParse(proxyAddress, out var proxy) || proxy.AddressFamily != AddressFamily.InterNetwork)
            return "secret_proxy_address_invalid: expected an IPv4 address inside the subnet, such as " + DefaultAddress;

        var broadcast = value | ~mask;
        var proxyValue = ToUInt32(proxy);
        if (proxyValue <= value + 1 || proxyValue >= value + FirstAgentOffset)
            return $"secret_proxy_address_invalid: the forwarder address must be host 2 to {FirstAgentOffset - 1} of the subnet";

        network = new SecretProxyNetwork(subnet, value, broadcast, proxy);
        return null;
    }

    /// <summary>The fixed address of the agent with this database id, or null when the subnet is too small for it.</summary>
    public IPAddress? AgentAddress(int agentId)
    {
        if (agentId <= 0) return null;
        var candidate = (ulong)_network + FirstAgentOffset + (ulong)agentId;
        return candidate >= _broadcast ? null : FromUInt32((uint)candidate);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
