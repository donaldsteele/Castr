using System.Buffers.Binary;
using Castr.Core.Protocol;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;

namespace Castr.Core.Tests.Protocol;

public class LengthPrefixedFramerTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsWholeMessages_InOrder()
    {
        var (client, server) = await ConnectedPairAsync();
        var writer = new LengthPrefixedFramer(client);
        var reader = new LengthPrefixedFramer(server);

        await writer.WriteFrameAsync(new byte[] { 1, 2, 3 });
        await writer.WriteFrameAsync(new byte[] { 9, 8, 7, 6, 5 });

        Assert.Equal([1, 2, 3], await reader.ReadFrameAsync());
        Assert.Equal([9, 8, 7, 6, 5], await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_ReassemblesAcrossPartialSocketReads()
    {
        // A framer must not assume one ReceiveAsync yields a whole frame — a real socket splits arbitrarily.
        var payload = new byte[10_000];
        new Random(7).NextBytes(payload);

        var (client, server) = await ConnectedPairAsync();
        await new LengthPrefixedFramer(client).WriteFrameAsync(payload);

        // A connection that hands back at most 13 bytes per read, to force the ReadExactly loop to iterate.
        var reader = new LengthPrefixedFramer(new ChoppyConnection(server, maxPerRead: 13));
        Assert.Equal(payload, await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task EmptyFrame_RoundTrips()
    {
        var (client, server) = await ConnectedPairAsync();
        await new LengthPrefixedFramer(client).WriteFrameAsync(ReadOnlyMemory<byte>.Empty);

        Assert.Equal([], await new LengthPrefixedFramer(server).ReadFrameAsync());
    }

    [Fact]
    public async Task CleanCloseAtFrameBoundary_ReturnsNull()
    {
        var (client, server) = await ConnectedPairAsync();
        await client.DisposeAsync(); // no frame written, then closed

        Assert.Null(await new LengthPrefixedFramer(server).ReadFrameAsync());
    }

    [Fact]
    public async Task OversizedLengthPrefix_IsRejectedBeforeAllocation()
    {
        // The core memory-safety property: a crafted 4-byte length prefix far larger than the cap must be
        // rejected (throwing) rather than driving a giant `new byte[length]` allocation.
        var (client, server) = await ConnectedPairAsync();

        var evilPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(evilPrefix, 0x7FFF_FFFF); // ~2 GiB claimed
        await client.SendAsync(evilPrefix);

        var reader = new LengthPrefixedFramer(server, maxFrameLength: 4096);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadFrameAsync().AsTask());
    }

    [Fact]
    public async Task LengthPrefixExactlyAtLimit_IsAccepted()
    {
        var (client, server) = await ConnectedPairAsync();
        var payload = new byte[4096];
        new Random(1).NextBytes(payload);

        await new LengthPrefixedFramer(client, maxFrameLength: 4096).WriteFrameAsync(payload);

        Assert.Equal(payload, await new LengthPrefixedFramer(server, maxFrameLength: 4096).ReadFrameAsync());
    }

    [Fact]
    public async Task WriteFrame_OverLimit_Throws_WithoutSending()
    {
        var (client, _) = await ConnectedPairAsync();
        var framer = new LengthPrefixedFramer(client, maxFrameLength: 8);

        await Assert.ThrowsAsync<InvalidDataException>(() => framer.WriteFrameAsync(new byte[9]).AsTask());
    }

    [Fact]
    public async Task TruncatedFrame_MidPayload_Throws()
    {
        var (client, server) = await ConnectedPairAsync();

        // Declare 100 bytes but send only 10, then close.
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, 100);
        await client.SendAsync(prefix);
        await client.SendAsync(new byte[10]);
        await client.DisposeAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => new LengthPrefixedFramer(server).ReadFrameAsync().AsTask());
    }

    private static async Task<(IStreamConnection Client, IStreamConnection Server)> ConnectedPairAsync()
    {
        var network = new InMemoryStreamNetwork();
        var listener = network.CreateListener(new Endpoint("server", 1));
        var client = network.CreateClient(new Endpoint("client", 1));

        var clientConn = await client.ConnectAsync(new Endpoint("server", 1));
        var serverConn = await listener.AcceptAsync();
        return (clientConn, serverConn);
    }

    /// <summary>Decorator that caps each ReceiveAsync at <paramref name="maxPerRead"/> bytes, simulating a socket that fragments reads.</summary>
    private sealed class ChoppyConnection(IStreamConnection inner, int maxPerRead) : IStreamConnection
    {
        public Endpoint RemoteEndpoint => inner.RemoteEndpoint;

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            inner.SendAsync(data, cancellationToken);

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(buffer[..Math.Min(maxPerRead, buffer.Length)], cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
