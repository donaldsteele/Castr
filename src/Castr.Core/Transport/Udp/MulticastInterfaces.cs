using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Castr.Core.Transport.Udp;

/// <summary>
/// Interface selection for multicast sockets. Auto-selecting "the default route" is unreliable on macOS
/// when a VPN or virtual adapter is present (see wiki/concepts/tech-stack.md) — so this exposes the
/// candidate list explicitly rather than silently picking one, and callers (ultimately the CLI) should
/// let the user override the choice.
/// </summary>
public static class MulticastInterfaces
{
    public static IReadOnlyList<IPAddress> GetCandidateAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.SupportsMulticast)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(addr => addr.Address)];
}
