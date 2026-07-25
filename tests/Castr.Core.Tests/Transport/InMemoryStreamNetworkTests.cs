using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;

namespace Castr.Core.Tests.Transport;

public class InMemoryStreamNetworkTests
{
    private static readonly Endpoint ServerEndpoint = new("server", 1);
    private static readonly Endpoint ClientEndpoint = new("client", 1);

    [Fact]
    public async Task Connect_MatchesListener_AndDeliversBytesBothWays()
    {
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServerEndpoint);
        var client = network.CreateClient(ClientEndpoint);

        var clientConn = await client.ConnectAsync(ServerEndpoint);
        var serverConn = await listener.AcceptAsync();

        await clientConn.SendAsync(new byte[] { 1, 2, 3 });
        Assert.Equal([1, 2, 3], await ReadExactlyAsync(serverConn, 3));

        await serverConn.SendAsync(new byte[] { 4, 5 });
        Assert.Equal([4, 5], await ReadExactlyAsync(clientConn, 2));
    }

    [Fact]
    public async Task ServerSeesClientAsRemoteEndpoint()
    {
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServerEndpoint);
        var client = network.CreateClient(ClientEndpoint);

        var clientConn = await client.ConnectAsync(ServerEndpoint);
        var serverConn = await listener.AcceptAsync();

        Assert.Equal(ServerEndpoint, clientConn.RemoteEndpoint);
        Assert.Equal(ClientEndpoint, serverConn.RemoteEndpoint);
    }

    [Fact]
    public async Task Receive_HonorsPartialReads_ForOneWrite()
    {
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServerEndpoint);
        var client = network.CreateClient(ClientEndpoint);

        var clientConn = await client.ConnectAsync(ServerEndpoint);
        var serverConn = await listener.AcceptAsync();

        await clientConn.SendAsync(new byte[] { 10, 20, 30, 40 });

        // Reading into a smaller buffer returns a partial read; the remainder is buffered for the next read.
        var first = new byte[2];
        int n1 = await serverConn.ReceiveAsync(first);
        Assert.Equal(2, n1);
        Assert.Equal([10, 20], first);

        var second = new byte[10];
        int n2 = await serverConn.ReceiveAsync(second);
        Assert.Equal(2, n2);
        Assert.Equal([30, 40], second[..2]);
    }

    [Fact]
    public async Task DisposingPeer_SurfacesEndOfStream()
    {
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(ServerEndpoint);
        var client = network.CreateClient(ClientEndpoint);

        var clientConn = await client.ConnectAsync(ServerEndpoint);
        var serverConn = await listener.AcceptAsync();

        await clientConn.DisposeAsync();

        Assert.Equal(0, await serverConn.ReceiveAsync(new byte[8])); // 0 == end-of-stream
    }

    [Fact]
    public async Task Connect_NoListener_Throws()
    {
        var network = new InMemoryStreamNetwork();
        var client = network.CreateClient(ClientEndpoint);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(new Endpoint("nobody", 9)).AsTask());
    }

    [Fact]
    public void CreateListener_DuplicateEndpoint_Throws()
    {
        var network = new InMemoryStreamNetwork();
        network.CreateListener(ServerEndpoint);

        Assert.Throws<InvalidOperationException>(() => network.CreateListener(ServerEndpoint));
    }

    private static async Task<byte[]> ReadExactlyAsync(IStreamConnection connection, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await connection.ReceiveAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }
}
