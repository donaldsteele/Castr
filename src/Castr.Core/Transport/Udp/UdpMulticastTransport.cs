using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

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
    private readonly Socket _socket;
    private readonly IPEndPoint _groupEndpoint;
    private readonly EndPoint _receiveTemplate = new IPEndPoint(IPAddress.Any, 0);

    public UdpMulticastTransport(
        IPAddress groupAddress, int port, IPAddress? interfaceAddress = null, bool multicastLoopback = true, short timeToLive = 1)
    {
        if (groupAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 multicast groups are supported currently.", nameof(groupAddress));

        _groupEndpoint = new IPEndPoint(groupAddress, port);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        await _socket.SendToAsync(payload, SocketFlags.None, _groupEndpoint, cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[65_507]; // max theoretical UDP/IPv4 payload
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = await TryReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (received is null)
                yield break;
            yield return received;
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
            // Cancellation, or the socket being torn down / reset during shutdown — end the stream, don't throw through the caller's enumeration.
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
