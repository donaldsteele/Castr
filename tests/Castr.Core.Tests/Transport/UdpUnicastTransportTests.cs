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
}
