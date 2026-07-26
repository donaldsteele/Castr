using System.Net;
using System.Net.Sockets;

namespace Castr.Core.IntegrationTests;

/// <summary>
/// Asks the OS for an ephemeral UDP port that is actually bindable right now, instead of hardcoding one.
///
/// <para>A fixed port is not safe on Windows: an unrelated system service can hold one in the dynamic range
/// (observed on this project's dev host: <c>DnsService</c> on UDP <b>46101</b>), and a bind then fails with
/// <c>WSAEACCES</c> — a machine-dependent failure with nothing to do with the behaviour under test. M7 introduced
/// this probe for <c>UdpMulticastTransportTests</c>; M9 extended it to the real-socket tests here, which had kept
/// hardcoded ports 45001-45006 (an M9 benchmark harness hit exactly this failure twice, once on 46101 itself).</para>
///
/// <para>The port is released before it is returned; two <c>UdpMulticastTransport</c> instances can then both bind
/// it because the transport sets <c>ReuseAddress</c>.</para>
/// </summary>
internal static class TestPorts
{
    public static int FreeUdp()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
