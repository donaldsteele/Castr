using System.Threading.Channels;

namespace Castr.Core.Transport.InMemory;

/// <summary>
/// A simulated connection-oriented LAN for pure unit tests — the stream-transport analogue of
/// <see cref="InMemoryNetwork"/>. Listeners register by <see cref="Endpoint"/>; a client dial finds the
/// matching listener, mints a wired pair of <see cref="InMemoryStreamConnection"/> (one per side, each
/// direction its own in-process channel), and hands the server side to the listener's accept queue. No
/// sockets, no ports, fully deterministic. Thread-safe registration mirrors <see cref="InMemoryNetwork"/>.
/// </summary>
public sealed class InMemoryStreamNetwork
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Endpoint, InMemoryStreamListener> _listeners = [];

    public IStreamListener CreateListener(Endpoint endpoint)
    {
        var listener = new InMemoryStreamListener(this, endpoint);
        lock (_lock)
        {
            if (!_listeners.TryAdd(endpoint, listener))
                throw new InvalidOperationException($"Endpoint {endpoint} already has a listener on this network.");
        }
        return listener;
    }

    /// <summary>A client that dials peers on this network. <paramref name="self"/> is the address the server side sees as its remote peer.</summary>
    public IStreamClient CreateClient(Endpoint self) => new InMemoryStreamClient(this, self);

    internal ValueTask<IStreamConnection> ConnectAsync(Endpoint from, Endpoint to, CancellationToken cancellationToken)
    {
        InMemoryStreamListener? listener;
        lock (_lock) { _listeners.TryGetValue(to, out listener); }

        if (listener is null)
            throw new InvalidOperationException($"Connection refused: no listener at {to}.");

        // Two independent one-directional channels: client writes to c2s, server reads it; server writes to s2c,
        // client reads it. Each side is handed the reader for its inbound direction and the writer for its outbound.
        var c2s = Channel.CreateUnbounded<byte[]>();
        var s2c = Channel.CreateUnbounded<byte[]>();

        var clientSide = new InMemoryStreamConnection(remote: to, inbound: s2c.Reader, outbound: c2s.Writer);
        var serverSide = new InMemoryStreamConnection(remote: from, inbound: c2s.Reader, outbound: s2c.Writer);

        listener.Enqueue(serverSide);
        return ValueTask.FromResult<IStreamConnection>(clientSide);
    }

    internal void Unregister(Endpoint endpoint)
    {
        lock (_lock) { _listeners.Remove(endpoint); }
    }

    private sealed class InMemoryStreamClient(InMemoryStreamNetwork network, Endpoint self) : IStreamClient
    {
        public ValueTask<IStreamConnection> ConnectAsync(Endpoint endpoint, CancellationToken cancellationToken = default) =>
            network.ConnectAsync(self, endpoint, cancellationToken);
    }

    private sealed class InMemoryStreamListener(InMemoryStreamNetwork network, Endpoint endpoint) : IStreamListener
    {
        private readonly Channel<IStreamConnection> _accepts = Channel.CreateUnbounded<IStreamConnection>();

        public Endpoint LocalEndpoint => endpoint;

        internal void Enqueue(IStreamConnection connection) => _accepts.Writer.TryWrite(connection);

        public async ValueTask<IStreamConnection> AcceptAsync(CancellationToken cancellationToken = default) =>
            await _accepts.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            network.Unregister(endpoint);
            _accepts.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// One end of an in-memory stream pair. Reads pull whole written segments off an inbound channel, buffering
/// any leftover so <see cref="ReceiveAsync"/> honors the "up to N bytes, may return fewer" contract of a real
/// socket. Disposing completes the outbound channel, surfacing end-of-stream to the peer's reads.
/// </summary>
public sealed class InMemoryStreamConnection : IStreamConnection
{
    private readonly ChannelReader<byte[]> _inbound;
    private readonly ChannelWriter<byte[]> _outbound;
    private byte[]? _leftover;
    private int _leftoverOffset;

    internal InMemoryStreamConnection(Endpoint remote, ChannelReader<byte[]> inbound, ChannelWriter<byte[]> outbound)
    {
        RemoteEndpoint = remote;
        _inbound = inbound;
        _outbound = outbound;
    }

    public Endpoint RemoteEndpoint { get; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!data.IsEmpty)
            _outbound.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;

        if (_leftover is null)
        {
            try
            {
                if (!await _inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0; // peer disposed => channel completed => end-of-stream
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            if (!_inbound.TryRead(out _leftover) || _leftover is null)
                return 0;
            _leftoverOffset = 0;
        }

        int available = _leftover.Length - _leftoverOffset;
        int toCopy = Math.Min(available, buffer.Length);
        _leftover.AsMemory(_leftoverOffset, toCopy).CopyTo(buffer);
        _leftoverOffset += toCopy;

        if (_leftoverOffset >= _leftover.Length)
        {
            _leftover = null;
            _leftoverOffset = 0;
        }

        return toCopy;
    }

    public ValueTask DisposeAsync()
    {
        _outbound.TryComplete();
        return ValueTask.CompletedTask;
    }
}
