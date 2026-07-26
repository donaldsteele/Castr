using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Castr.Core.Transport.Udp;

namespace Castr.Cli;

/// <summary>
/// Resolves the <c>--interface</c> flag (a NIC name) to the IPv4 address a multicast socket should join on.
/// When no interface is named, callers pass <c>null</c> so the transport falls back to <see cref="IPAddress.Any"/>.
/// </summary>
internal static class NetworkInterfaces
{
    /// <summary>Resolves a NIC name to its first IPv4 address, or throws <see cref="InvalidInterfaceException"/> if unknown.</summary>
    public static IPAddress Resolve(string interfaceName)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, interfaceName, StringComparison.OrdinalIgnoreCase));

        var address = nic?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

        if (address is null)
            throw new InvalidInterfaceException(interfaceName, MulticastInterfaces.GetCandidateAddresses());

        return address;
    }

}

internal sealed class InvalidInterfaceException(string interfaceName, IReadOnlyList<IPAddress> candidates)
    : Exception($"No multicast-capable IPv4 interface named '{interfaceName}'. " +
                $"Available addresses: {(candidates.Count == 0 ? "(none)" : string.Join(", ", candidates))}.")
{
    public string InterfaceName { get; } = interfaceName;
}
