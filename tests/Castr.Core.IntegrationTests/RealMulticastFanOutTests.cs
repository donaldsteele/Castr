using System.Net;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;

namespace Castr.Core.IntegrationTests;

/// <summary>Multiple real UDP multicast sockets bound to the same group/port on one box (via ReuseAddress) — the local proxy for "hundreds of LAN receivers, one send."</summary>
public class RealMulticastFanOutTests
{
    [Fact]
    public async Task OneSend_MultipleRealSockets_AllReceiveIt()
    {
        var group = IPAddress.Parse("239.192.55.61");
        int port = TestPorts.FreeUdp();
        const int receiverCount = 5;

        await using IMulticastTransport sender = new UdpMulticastTransport(group, port);
        var receivers = new List<IMulticastTransport>();
        try
        {
            for (int i = 0; i < receiverCount; i++)
                receivers.Add(new UdpMulticastTransport(group, port));

            await sender.SendAsync("fan-out-payload"u8.ToArray());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var receiveTasks = receivers.Select(r => ReceiveOneAsync(r, cts.Token)).ToArray();
            var payloads = await Task.WhenAll(receiveTasks);

            Assert.All(payloads, p => Assert.Equal("fan-out-payload"u8.ToArray(), p));
        }
        finally
        {
            foreach (var r in receivers)
                await r.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultipleSequentialSends_ArriveInOrder_OnRealLoopback()
    {
        var group = IPAddress.Parse("239.192.55.62");
        int port = TestPorts.FreeUdp();

        await using IMulticastTransport sender = new UdpMulticastTransport(group, port);
        await using IMulticastTransport receiver = new UdpMulticastTransport(group, port);

        for (int i = 0; i < 20; i++)
            await sender.SendAsync(BitConverter.GetBytes(i));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int expected = 0;
        await foreach (var packet in receiver.ReceiveAsync(cts.Token))
        {
            Assert.Equal(expected, BitConverter.ToInt32(packet.Payload));
            expected++;
            if (expected == 20)
                break;
        }
    }

    private static async Task<byte[]> ReceiveOneAsync(IMulticastTransport transport, CancellationToken cancellationToken)
    {
        await foreach (var packet in transport.ReceiveAsync(cancellationToken))
            return packet.Payload;
        throw new TimeoutException("No packet received within the test timeout.");
    }
}
