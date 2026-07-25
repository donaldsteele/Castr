using System.Net;
using Castr.Core.Protocol;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;

namespace Castr.Gui.Services;

/// <summary>
/// Real IP-multicast transport factory used by the desktop head. Sender and receiver bind the same
/// group/port (ReuseAddress + multicast loopback), so a Send and a Receive flow can even reach each other on
/// one box — the local proxy for "one send, many LAN receivers."
/// </summary>
public sealed class UdpTransportFactory(IPAddress? group = null, int port = 45010) : ITransportFactory
{
    public const int DefaultPort = 45010;

    // A site-local administratively-scoped multicast group (RFC 2365 239.192/14), matching the range the
    // Core integration tests exercise.
    private static readonly IPAddress DefaultGroup = IPAddress.Parse("239.192.55.60");

    private readonly IPAddress _group = group ?? DefaultGroup;
    private readonly int _port = port;

    // Both roles pass a role-appropriate DatagramFilter for exactly the reason this factory's own summary
    // describes: multicast loopback is on, so a sender's socket hands back everything the sender just sent.
    // Without the filter that echo is copied, queued, and fully decoded before being discarded — starving the
    // real CHUNK_REQUEST/JOIN_REQUEST control traffic. See Castr.Core.Protocol.DatagramFilters.
    public IMulticastTransport CreateSenderTransport() =>
        new UdpMulticastTransport(_group, _port, datagramFilter: DatagramFilters.Sender);

    public IMulticastTransport CreateReceiverTransport() =>
        new UdpMulticastTransport(_group, _port, datagramFilter: DatagramFilters.Receiver);
}
