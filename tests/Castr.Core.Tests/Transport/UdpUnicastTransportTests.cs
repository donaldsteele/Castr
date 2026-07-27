using System.Net;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;

namespace Castr.Core.Tests.Transport;

public class UdpUnicastTransportTests
{
    [Fact]
    public async Task SendThenReceive_OverRealLoopbackSocket_DeliversPayload()
    {
        await using var a = new UdpUnicastTransport(IPAddress.Loopback);
        await using var b = new UdpUnicastTransport(IPAddress.Loopback);

        await a.SendAsync(b.LocalEndpoint, "hello over real UDP"u8.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReceivedPacket? received = null;
        await foreach (var packet in b.ReceiveAsync(cts.Token))
        {
            received = packet;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal("hello over real UDP"u8.ToArray(), received.Payload);
        Assert.Equal(a.LocalEndpoint, received.From);
    }

    [Fact]
    public async Task Constructor_BindsToEphemeralPort_WhenPortNotSpecified()
    {
        await using var transport = new UdpUnicastTransport(IPAddress.Loopback);

        Assert.NotEqual(0, transport.LocalEndpoint.Port);
        Assert.Equal("127.0.0.1", transport.LocalEndpoint.Host);
    }

    [Fact]
    public async Task TwoTransports_GetDifferentEphemeralPorts()
    {
        await using var a = new UdpUnicastTransport(IPAddress.Loopback);
        await using var b = new UdpUnicastTransport(IPAddress.Loopback);

        Assert.NotEqual(a.LocalEndpoint.Port, b.LocalEndpoint.Port);
    }

    [Fact]
    public async Task DisposeAsync_ThenReceive_EndsEnumerationWithoutThrowing()
    {
        var transport = new UdpUnicastTransport(IPAddress.Loopback);
        var receiveTask = Task.Run(async () =>
        {
            await foreach (var _ in transport.ReceiveAsync()) { }
        });

        await transport.DisposeAsync();

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);
        await receiveTask; // rethrow if it actually faulted
    }

    [Fact]
    public async Task DatagramsArrivingBeforeAnyoneEnumerates_AreStillDelivered()
    {
        // M6's property, applied to the unicast sibling in M11. Until then ReceiveAsync's own iteration WAS the
        // read loop: no socket read was issued until someone enumerated, so anything arriving in the meantime
        // sat in the kernel receive buffer and was dropped once that filled — silently, since a UDP socket does
        // not tell you. Sending well past the default receive buffer before enumerating is what separates the
        // two shapes; a burst that fits in the kernel buffer would pass either way.
        const int datagramCount = 300;
        var payload = new byte[1400]; // 420 KB total, far past the ~64 KB default SO_RCVBUF

        await using var sender = new UdpUnicastTransport(IPAddress.Loopback);
        await using var receiver = new UdpUnicastTransport(IPAddress.Loopback);

        // Paced, and that matters. An unpaced 420 KB burst also measures whether the reader loop wins its share
        // of a loaded CPU, which made this fail under a full parallel test run for a reason that has nothing to
        // do with the property. Pacing removes that without weakening the control at all: the pre-M11 shape
        // issues no socket read until someone enumerates, so it loses everything past the kernel buffer however
        // slowly the datagrams arrive.
        for (int i = 0; i < datagramCount; i++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(), i);
            await sender.SendAsync(receiver.LocalEndpoint, payload);
            if (i % 20 == 19)
                await Task.Delay(1);
        }

        await Task.Delay(500); // let the reader loop finish draining before anyone asks for a packet

        int received = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await foreach (var _ in receiver.ReceiveAsync(cts.Token))
            {
                if (++received >= datagramCount)
                    break;
            }
        }
        catch (OperationCanceledException) { /* drained whatever was buffered */ }

        // Deliberately not "all 300": loopback UDP may still drop under a burst, and the point being asserted is
        // the shape, not lossless delivery. The pre-M11 shape caps out near the kernel buffer, ~46 datagrams.
        Assert.True(received >= 150, $"only {received} of {datagramCount} datagrams survived; the socket is not being drained ahead of the consumer");
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var transport = new UdpUnicastTransport(IPAddress.Loopback);

        await transport.DisposeAsync();
        await transport.DisposeAsync(); // the reader-loop CTS makes a naive second call throw
    }
}
