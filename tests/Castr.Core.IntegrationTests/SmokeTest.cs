using System.Net;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;

namespace Castr.Core.IntegrationTests;

public class SmokeTest
{
    [Fact]
    public async Task RealMulticastLoopback_SendThenReceive_Works()
    {
        var group = IPAddress.Parse("239.192.55.60");
        const int port = 45001;

        await using IMulticastTransport a = new UdpMulticastTransport(group, port, multicastLoopback: true);
        await using IMulticastTransport b = new UdpMulticastTransport(group, port, multicastLoopback: true);

        await a.SendAsync("smoke-test"u8.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var packet in b.ReceiveAsync(cts.Token))
        {
            Assert.Equal("smoke-test"u8.ToArray(), packet.Payload);
            return;
        }

        Assert.Fail("Did not receive the multicast packet within the timeout.");
    }
}
