namespace Castr.Core.Transport;

/// <summary>
/// A connected, bidirectional, reliable, ordered byte stream between two participants — the unicast-swarm
/// counterpart to the datagram-oriented <see cref="IMulticastTransport"/>. Backed by TCP in production
/// (<see cref="Tcp.TcpStreamConnection"/>) and by an in-process pipe in tests
/// (<see cref="InMemory.InMemoryStreamNetwork"/>). Unlike a datagram transport it carries no message
/// framing of its own: bytes in equal bytes out, and a higher layer (<see cref="Protocol.LengthPrefixedFramer"/>)
/// imposes message boundaries. Not thread-safe for concurrent reads or concurrent writes; the swarm sessions
/// drive each direction from a single logical flow.
/// </summary>
public interface IStreamConnection : IAsyncDisposable
{
    /// <summary>The remote peer this connection is talking to (for logging/diagnostics; never a trust input).</summary>
    Endpoint RemoteEndpoint { get; }

    /// <summary>Writes every byte of <paramref name="data"/> to the stream in order.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads up to <paramref name="buffer"/>.Length bytes, returning how many were read. Like a raw socket
    /// read it may return fewer than requested; returns 0 only at end-of-stream (the peer closed cleanly).
    /// </summary>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
}
