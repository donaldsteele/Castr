using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Castr.Core.Diagnostics; // BENCH (temporary M7 instrumentation)

namespace Castr.Core.Transport.Udp;

/// <summary>
/// Real IP multicast over a raw <see cref="Socket"/> (not <see cref="UdpClient"/>, which doesn't expose
/// the explicit interface-index control, <see cref="SocketOptionName.MulticastLoopback"/>, and
/// <see cref="SocketOptionName.ReuseAddress"/> this needs). See wiki/concepts/tech-stack.md for the
/// per-platform quirks this constructor works around (Windows requires bind-then-join; Linux permits
/// binding directly to the group address but this portable path avoids relying on that).
/// </summary>
public sealed class UdpMulticastTransport : IMulticastTransport
{
    /// <summary>
    /// Explicit OS socket buffer size (both directions), set in the constructor rather than left at the
    /// platform default (which can be as low as ~256 KB on Linux/macOS/BSD). Chosen for the same reason
    /// mature UDP multicast implementations (e.g. uftp-multicast, github.com/digarok/uftp-multicast) treat
    /// <c>SO_RCVBUF</c>/<c>SO_SNDBUF</c> sizing as a primary throughput lever: a bigger kernel-level buffer
    /// gives a receiver's decode/verify/write chain more slack to fall behind a burst without the OS silently
    /// dropping datagrams. This is complementary to, not a substitute for, <see cref="ReceiveLoopAsync"/>'s
    /// channel-based decoupling below — a bigger buffer only delays the same overflow if the consumer stays
    /// slower than the arrival rate; the real fix is draining the socket independently of how slow downstream
    /// processing is. See the M6 write-up in wiki/synthesis/roadmap.md for the measurements behind both.
    /// Best-effort: some platforms silently clamp this to a lower ceiling (e.g. Linux's <c>net.core.rmem_max</c>)
    /// rather than erroring, so no particular value is guaranteed to take effect.
    /// </summary>
    public const int SocketBufferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Capacity of the internal channel <see cref="ReceiveLoopAsync"/> feeds and <see cref="ReceiveAsync"/>
    /// drains. Bounded (not unbounded) so a consumer that falls arbitrarily far behind can't grow this queue
    /// without limit; sized well above <see cref="PacketReassembler"/>'s/<see cref="ChunkPacketAssembler"/>'s
    /// own 1024-group caps as a second, cheaper-to-drain layer of slack in front of them.
    /// </summary>
    public const int InboxCapacity = 4096;

    private readonly Socket _socket;
    private readonly IPEndPoint _groupEndpoint;
    private readonly EndPoint _receiveTemplate = new IPEndPoint(IPAddress.Any, 0);
    private readonly Channel<ReceivedPacket> _inbox;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerLoopTask;
    private bool _disposed;

    public UdpMulticastTransport(
        IPAddress groupAddress, int port, IPAddress? interfaceAddress = null, bool multicastLoopback = true, short timeToLive = 1)
    {
        if (groupAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 multicast groups are supported currently.", nameof(groupAddress));

        _groupEndpoint = new IPEndPoint(groupAddress, port);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // Best-effort: the OS may clamp these below SocketBufferBytes rather than throw, so failures here are
        // swallowed rather than treated as fatal — a transport that can't get a bigger buffer should still work,
        // just with less slack against bursts (see SocketBufferBytes's doc comment).
        // BENCH (temporary M7 instrumentation): SocketBufferOverride is -1 unless CASTR_BENCH_SOCKBUF is set,
        // so this is exactly SocketBufferBytes in every normal run.
        int bufferBytes = BenchMetrics.SocketBufferOverride > 0 ? BenchMetrics.SocketBufferOverride : SocketBufferBytes;
        TrySetSocketOption(SocketOptionName.ReceiveBuffer, bufferBytes);
        TrySetSocketOption(SocketOptionName.SendBuffer, bufferBytes);
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));

        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, multicastLoopback);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, (int)timeToLive);
        // Multicast needs two interface decisions: which interface to *join* the group on (AddMembership /
        // IP_ADD_MEMBERSHIP, receive side) and which interface a multicast sendto() leaves by
        // (IP_MULTICAST_IF, send side). On Windows and Linux both are resolved acceptably by the kernel's
        // routing-table fallback when left unset, so the historical null-interface path already works there
        // and we leave it untouched. macOS does NOT do that fallback for the send side: without
        // IP_MULTICAST_IF set, sendto() to a multicast address fails with EHOSTUNREACH ("No route to host").
        // Worse, for same-host delivery the loopback copy (IP_MULTICAST_LOOP) is delivered only to members
        // on the *outgoing* interface, so the join interface and the send interface MUST be the same one —
        // otherwise the send succeeds but no local receiver ever sees the packet. See
        // wiki/concepts/tech-stack.md.
        var joinInterface = interfaceAddress ?? IPAddress.Any;
        var sendInterface = interfaceAddress; // null => leave Windows/Linux kernel fallback in charge.

        if (interfaceAddress is null && OperatingSystem.IsMacOS())
        {
            // Resolve one interface and use it for BOTH join and send so they agree. Prefer a single
            // unambiguous candidate NIC (real single-NIC hosts, and cross-host delivery, work); otherwise
            // fall back to loopback — always present, correct for same-host multicastLoopback scenarios incl.
            // the integration tests, and the safe non-guess for multi-NIC hosts where the user is expected to
            // pass an explicit --interface. This honors MulticastInterfaces' documented no-silent-guess policy
            // (loopback is not a guess among the real NICs).
            var candidates = MulticastInterfaces.GetCandidateAddresses();
            var resolved = candidates.Count == 1 ? candidates[0] : IPAddress.Loopback;
            joinInterface = resolved;
            sendInterface = resolved;
        }

        _socket.SetSocketOption(
            SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(groupAddress, joinInterface));

        if (sendInterface is not null)
        {
            _socket.SetSocketOption(
                SocketOptionLevel.IP, SocketOptionName.MulticastInterface, sendInterface.GetAddressBytes());
        }

        // Decouple draining the OS socket from however slow a consumer's own processing chain is (decode,
        // Merkle/AEAD verify, disk write, outbound PEER_HAVE broadcast — see ReceiverSession.HandlePacketAsync).
        // Before this, ReceiveAsync's own await-foreach *was* the read loop: the next socket read was never
        // issued until the caller finished fully handling the previous packet, so the kernel receive buffer
        // only drained as fast as that whole chain ran. Now a dedicated tight loop (ReceiveLoopAsync) does
        // nothing but pull datagrams off the socket into this bounded channel; ReceiveAsync just enumerates the
        // channel, at whatever pace the caller can manage. See the M6 write-up in wiki/synthesis/roadmap.md.
        _inbox = Channel.CreateBounded<ReceivedPacket>(new BoundedChannelOptions(InboxCapacity)
        {
            SingleWriter = true, // only ReceiveLoopAsync ever writes
            SingleReader = true, // only one logical consumer per transport instance in this codebase
        });
        _readerLoopTask = Task.Run(() => ReceiveLoopAsync(_readerCts.Token));

        // BENCH (temporary M7 instrumentation) — record the buffer sizes the OS actually granted, and expose
        // the bounded channel's live depth to the sampler. No effect when CASTR_BENCH is unset.
        BenchMetrics.RegisterInbox(() => _inbox.Reader.Count, InboxCapacity);
        BenchMetrics.Meta_("requestedSocketBufferBytes", bufferBytes.ToString());
        BenchMetrics.Meta_("effectiveReceiveBufferBytes", _socket.ReceiveBufferSize.ToString());
        BenchMetrics.Meta_("effectiveSendBufferBytes", _socket.SendBufferSize.ToString());
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        BenchMetrics.OnDatagramSent(payload.Span); // BENCH
        await _socket.SendToAsync(payload, SocketFlags.None, _groupEndpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enumerates packets already drained off the socket by <see cref="ReceiveLoopAsync"/>. Cancelling <paramref name="cancellationToken"/> only stops this particular enumeration — the underlying socket-reader loop keeps running (draining the socket, buffering into the channel) until the transport itself is disposed.</summary>
    public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _inbox.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Runs for the transport's whole lifetime (started in the constructor, stopped in <see cref="DisposeAsync"/>):
    /// pulls datagrams off the socket as fast as the OS delivers them and writes each into <see cref="_inbox"/>,
    /// with no per-packet processing in between. This is the producer half of the producer/consumer split
    /// described on <see cref="_inbox"/>'s construction above.
    /// </summary>
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
                // TryReceiveAsync already copies the received bytes into ReceivedPacket's own array (see its
                // body below), so `buffer` itself is safe to reuse for the next datagram — no allocation here.
                BenchMetrics.OnDatagramReceived(received.Payload); // BENCH
                if (BenchMetrics.Enabled)
                {
                    // BENCH: distinguishes "channel sitting empty (loss/sender-bound)" from "channel full
                    // (receiver-processing-bound)" by counting the times the producer actually had to block.
                    if (_inbox.Writer.TryWrite(received))
                        continue;
                    BenchMetrics.OnChannelWriteBlocked();
                }
                await _inbox.Writer.WriteAsync(received, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown (DisposeAsync cancelled us, or the bounded channel's WriteAsync observed cancellation)
        }
        catch (Exception ex)
        {
            // A genuine, unexpected fault in this loop itself (not cancellation, and not one of the
            // socket-teardown cases TryReceiveAsync already turns into a clean "end the loop" null). Complete
            // the channel WITH the exception so it surfaces to whoever is enumerating ReceiveAsync via
            // ReadAllAsync, instead of being silently absorbed here and looking like an ordinary end-of-stream.
            _inbox.Writer.TryComplete(ex);
        }
        finally
        {
            // No-op if the catch above already completed the channel (with or without an error) — TryComplete
            // only succeeds once; this is just the normal-path completion for the common shutdown case.
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

    /// <summary>Best-effort socket option set: some platforms/kernels clamp or reject a requested buffer size rather than honoring it exactly, and that should never prevent constructing the transport.</summary>
    private void TrySetSocketOption(SocketOptionName option, int value)
    {
        try { _socket.SetSocketOption(SocketOptionLevel.Socket, option, value); }
        catch (SocketException) { /* best-effort — proceed with whatever buffer size the OS gives us */ }
    }

    public async ValueTask DisposeAsync()
    {
        // IAsyncDisposable.DisposeAsync is conventionally expected to tolerate repeated calls (the pre-round-2
        // version — just _socket.Dispose() — was naturally idempotent; the sibling UdpUnicastTransport still
        // is). _readerCts.Dispose() below means a second call would otherwise throw ObjectDisposedException at
        // _readerCts.Cancel(), so guard re-entry explicitly.
        if (_disposed)
            return;
        _disposed = true;

        _readerCts.Cancel();
        _socket.Dispose(); // unblocks a pending ReceiveFromAsync immediately (caught as ObjectDisposedException in TryReceiveAsync)
        try { await _readerLoopTask.ConfigureAwait(false); }
        catch
        {
            // ReceiveLoopAsync's own try/catch/finally already handles every shutdown path (cancellation,
            // socket disposal, channel completion) without rethrowing; this is just a safety net so a disposed
            // transport never faults the caller's shutdown sequence.
        }
        _readerCts.Dispose();
    }
}
