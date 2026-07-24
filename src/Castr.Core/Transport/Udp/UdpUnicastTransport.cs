using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Castr.Core.Transport.Udp;

/// <summary>Real unicast UDP, used for repair fallback on the desktop tier and as the sole transport on the mobile tier (see wiki/concepts/repair-protocol.md).</summary>
public sealed class UdpUnicastTransport : IUnicastTransport
{
    private readonly Socket _socket;
    private readonly EndPoint _receiveTemplate = new IPEndPoint(IPAddress.Any, 0);

    public UdpUnicastTransport(IPAddress bindAddress, int port = 0)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(bindAddress, port));
        LocalEndpoint = Endpoint.FromIPEndPoint((IPEndPoint)_socket.LocalEndPoint!);
    }

    public Endpoint LocalEndpoint { get; }

    public async ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var target = new IPEndPoint(IPAddress.Parse(destination.Host), destination.Port);
        await _socket.SendToAsync(payload, SocketFlags.None, target, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[65_507];
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
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
