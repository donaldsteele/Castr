using System.Net;
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

    public IMulticastTransport CreateSenderTransport() => new UdpMulticastTransport(_group, _port);

    public IMulticastTransport CreateReceiverTransport() => new UdpMulticastTransport(_group, _port);
}
