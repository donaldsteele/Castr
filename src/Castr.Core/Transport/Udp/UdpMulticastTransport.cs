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
        _socket.SetSocketOption(
            SocketOptionLevel.IP, SocketOptionName.AddMembership,
            new MulticastOption(groupAddress, interfaceAddress ?? IPAddress.Any));
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
