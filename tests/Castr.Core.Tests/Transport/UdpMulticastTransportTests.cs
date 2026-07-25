using System.Net;
using Castr.Core.Transport;
using Castr.Core.Transport.Udp;

namespace Castr.Core.Tests.Transport;

/// <summary>
/// Targeted coverage for <see cref="UdpMulticastTransport"/>'s M6-round-2 producer/consumer split (a
/// dedicated reader task feeds a bounded channel independently of how fast <see cref="IMulticastTransport.ReceiveAsync"/>
/// is drained — see the M6 write-up in wiki/synthesis/roadmap.md) and its dispose lifecycle. Real loopback UDP
/// sockets throughout, matching <see cref="UdpUnicastTransportTests"/>'s convention of exercising this tier as
/// a fast real-socket test rather than moving it to Castr.Core.IntegrationTests.
/// </summary>
public class UdpMulticastTransportTests
{
    [Fact]
    public async Task SendThenReceive_OverRealLoopbackSocket_DeliversPayload()
    {
        var group = IPAddress.Parse("239.192.56.10");
        int port = FreeUdpPort();

        await using IMulticastTransport a = new UdpMulticastTransport(group, port);
        await using IMulticastTransport b = new UdpMulticastTransport(group, port);

        await a.SendAsync("hello over real multicast UDP"u8.ToArray());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ReceivedPacket? received = null;
        await foreach (var packet in b.ReceiveAsync(cts.Token))
        {
            received = packet;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal("hello over real multicast UDP"u8.ToArray(), received.Payload);
    }

    [Fact]
    public async Task ReaderLoop_BuffersPacketsBeforeAnyConsumerCalls_ReceiveAsync()
    {
        // The reader task starts in the constructor (not lazily on first ReceiveAsync call), so packets sent
        // before anyone ever enumerates ReceiveAsync should already be sitting in the internal channel by the
        // time a consumer shows up — the whole point of decoupling the socket drain from consumer speed.
        var group = IPAddress.Parse("239.192.56.11");
        int port = FreeUdpPort();

        await using IMulticastTransport sender = new UdpMulticastTransport(group, port);
        await using IMulticastTransport receiver = new UdpMulticastTransport(group, port);

        for (int i = 0; i < 5; i++)
            await sender.SendAsync(BitConverter.GetBytes(i));

        // Give the receiver's reader loop time to actually drain the socket into its channel before this test
        // ever calls ReceiveAsync.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<int>();
        await foreach (var packet in receiver.ReceiveAsync(cts.Token))
        {
            received.Add(BitConverter.ToInt32(packet.Payload));
            if (received.Count == 5)
                break;
        }

        Assert.Equal([0, 1, 2, 3, 4], received.OrderBy(x => x));
    }

    [Fact]
    public async Task DatagramFilter_RejectedDatagrams_NeverReachTheConsumer()
    {
        // The filter is applied in the reader loop before a ReceivedPacket is allocated or a channel slot is
        // taken, so a rejected datagram must be indistinguishable from one the network never delivered.
        var group = IPAddress.Parse("239.192.56.14");
        int port = FreeUdpPort();

        await using IMulticastTransport sender = new UdpMulticastTransport(group, port);
        await using IMulticastTransport receiver = new UdpMulticastTransport(
            group, port, datagramFilter: d => d.Length > 0 && d[0] == 0x01);

        await sender.SendAsync(new byte[] { 0x02, 0xAA }); // rejected
        await sender.SendAsync(new byte[] { 0x01, 0xBB }); // accepted
        await sender.SendAsync(new byte[] { 0x02, 0xCC }); // rejected

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<byte[]>();
        await foreach (var packet in receiver.ReceiveAsync(cts.Token))
        {
            received.Add(packet.Payload);
            break; // only the accepted one should ever arrive; anything else would have arrived first
        }

        Assert.Single(received);
        Assert.Equal(new byte[] { 0x01, 0xBB }, received[0]);
    }

    [Fact]
    public async Task NullDatagramFilter_AcceptsEverything_UnchangedFromBeforeTheFilterExisted()
    {
        var group = IPAddress.Parse("239.192.56.15");
        int port = FreeUdpPort();

        await using IMulticastTransport sender = new UdpMulticastTransport(group, port);
        await using IMulticastTransport receiver = new UdpMulticastTransport(group, port, datagramFilter: null);

        await sender.SendAsync(new byte[] { 0x02, 0xAA });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var packet in receiver.ReceiveAsync(cts.Token))
        {
            Assert.Equal(new byte[] { 0x02, 0xAA }, packet.Payload);
            return;
        }

        Assert.Fail("a null filter must accept every datagram");
    }

    [Fact]
    public async Task DisposeAsync_ThenReceive_EndsEnumerationWithoutThrowing()
    {
        var group = IPAddress.Parse("239.192.56.12");
        const int port = 46103;
        var transport = new UdpMulticastTransport(group, port);

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
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        // Regression test: the M6-round-2 rewrite introduced a CancellationTokenSource that DisposeAsync both
        // cancels and disposes, which made a second call throw ObjectDisposedException at _readerCts.Cancel()
        // — a real regression from the pre-round-2 version (just _socket.Dispose(), naturally idempotent) and
        // from the convention IAsyncDisposable implementations are expected to follow (the sibling
        // UdpUnicastTransport's DisposeAsync still tolerates repeated calls).
        var group = IPAddress.Parse("239.192.56.13");
        const int port = 46104;
        var transport = new UdpMulticastTransport(group, port);

        await transport.DisposeAsync();
        await transport.DisposeAsync(); // must not throw
    }

    /// <summary>
    /// Asks the OS for an ephemeral UDP port that is actually bindable right now, instead of hardcoding one.
    /// A fixed port is not safe here: Windows lets an unrelated system service (observed: <c>DnsService</c> on
    /// UDP 46101) hold a port in the dynamic range, and <see cref="UdpMulticastTransport"/>'s <c>Bind</c> then
    /// fails with <c>WSAEACCES</c> — a machine-dependent failure that has nothing to do with the behavior under
    /// test. The port is released before it is returned; two <see cref="UdpMulticastTransport"/> instances can
    /// then both bind it because the transport sets <c>ReuseAddress</c>.
    /// </summary>
    private static int FreeUdpPort()
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
