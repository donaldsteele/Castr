using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Castr.Core.Transport.Udp;

/// <summary>
/// Real unicast UDP: the datagram counterpart to <see cref="UdpMulticastTransport"/>, for directed repair
/// traffic (see wiki/concepts/repair-protocol.md).
///
/// <para><b>Nothing in the shipped composition wires this up today.</b> The desktop tier serves repair over the
/// multicast socket, and the mobile swarm tier is unicast <i>TCP</i>
/// (<c>TcpStreamClient</c> / <c>SwarmServeListener</c>), not this. Stated plainly because this type's summary
/// used to claim it was "the sole transport on the mobile tier", which is what put a suspected M6-class defect
/// on the backlog against a path that does not exist.</para>
/// </summary>
/// <remarks>
/// <para><b>The receive shape is M6's, not the one that predates it.</b> A dedicated loop drains the socket
/// into a bounded channel and <see cref="ReceiveAsync"/> enumerates that channel, so the next
/// <c>ReceiveFromAsync</c> is issued no matter how slow the consumer's own chain is. Until M11 this class still
/// had the shape M6 removed from the multicast tier: <see cref="ReceiveAsync"/>'s own iteration <i>was</i> the
/// read loop, so the kernel receive buffer drained only as fast as the caller finished handling the previous
/// datagram, and anything arriving in the meantime was dropped by the kernel with no record of it.</para>
///
/// <para>That was latent rather than live, precisely because nothing enumerates this transport in production.
/// It is fixed anyway: two sibling datagram transports that differ in their concurrency model is the kind of
/// asymmetry that gets copied, and the defect would arrive fully grown the moment someone wired repair
/// fallback to it.</para>
///
/// <para>The multicast sibling additionally applies a <c>DatagramFilter</c> in its reader loop; there is no
/// equivalent here because a unicast socket receives only what was addressed to it, so there is no
/// wrong-role traffic to discard before it costs an allocation.</para>
/// </remarks>
public sealed class UdpUnicastTransport : IUnicastTransport
{
    /// <summary>Capacity of the internal channel, matching <see cref="UdpMulticastTransport.InboxCapacity"/>.</summary>
    public const int InboxCapacity = UdpMulticastTransport.InboxCapacity;

    private readonly Socket _socket;
    private readonly EndPoint _receiveTemplate = new IPEndPoint(IPAddress.Any, 0);
    private readonly Channel<ReceivedPacket> _inbox;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerLoopTask;
    private bool _disposed;

    public UdpUnicastTransport(IPAddress bindAddress, int port = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(bindAddress, port));
        LocalEndpoint = Endpoint.FromIPEndPoint((IPEndPoint)_socket.LocalEndPoint!);

        _inbox = Channel.CreateBounded<ReceivedPacket>(new BoundedChannelOptions(InboxCapacity)
        {
            SingleWriter = true, // only ReceiveLoopAsync ever writes
            SingleReader = true, // only one logical consumer per transport instance in this codebase
        });
        _readerLoopTask = Task.Run(() => ReceiveLoopAsync(_readerCts.Token));
    }

    public Endpoint LocalEndpoint { get; }

    public async ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var target = new IPEndPoint(IPAddress.Parse(destination.Host), destination.Port);
        await _socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerates packets already drained off the socket by the reader loop. Cancelling
    /// <paramref name="cancellationToken"/> stops only this enumeration; the reader loop keeps draining until
    /// the transport is disposed.
    /// </summary>
    public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _inbox.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Runs for the transport's whole lifetime: pulls datagrams off the socket with no per-packet processing in between.</summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[65_507]; // max theoretical UDP/IPv4 payload
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await TryReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received is null)
                    break; // socket cancelled/disposed/reset — shutting down
                await _inbox.Writer.WriteAsync(received, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown (DisposeAsync cancelled us, or the bounded channel's WriteAsync observed cancellation)
        }
        catch (Exception ex)
        {
            // Complete WITH the exception so a genuine fault surfaces to whoever is enumerating, rather than
            // being absorbed here and looking like an ordinary end-of-stream.
            _inbox.Writer.TryComplete(ex);
        }
        finally
        {
            _inbox.Writer.TryComplete();
        }
    }

    private async ValueTask<ReceivedPacket?> TryReceiveAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _receiveTemplate, cancellationToken).ConfigureAwait(false);
            return new ReceivedPacket(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), Endpoint.FromIPEndPoint((IPEndPoint)result.RemoteEndPoint));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Cancellation, or the socket being torn down / reset during shutdown — end the loop, don't throw through it.
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Kept tolerant of repeated calls, as it was before the reader loop existed: _readerCts.Dispose() below
        // would otherwise make a second call throw ObjectDisposedException at Cancel().
        if (_disposed)
            return;
        _disposed = true;

        _readerCts.Cancel();
        _socket.Dispose(); // unblocks a pending ReceiveFromAsync immediately (caught in TryReceiveAsync)
        try { await _readerLoopTask.ConfigureAwait(false); }
        catch
        {
            // ReceiveLoopAsync already handles every shutdown path without rethrowing; this is a safety net so a
            // disposed transport never faults the caller's shutdown sequence.
        }
        _readerCts.Dispose();
    }
}
