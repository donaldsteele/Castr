using System.Net;
using System.Net.Sockets;

namespace Castr.Cli.Tests;

/// <summary>
/// Asks the OS for an ephemeral UDP port that is actually bindable right now, instead of hardcoding one.
/// See <c>Castr.Core.IntegrationTests.TestPorts</c> for the full rationale — in short, a Windows system service
/// can hold a dynamic-range port (observed: <c>DnsService</c> on UDP 46101) and the bind then fails with
/// <c>WSAEACCES</c>, a machine-dependent failure unrelated to the behaviour under test.
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
