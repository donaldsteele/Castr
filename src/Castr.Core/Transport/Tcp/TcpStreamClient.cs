using System.Net;
using System.Net.Sockets;

namespace Castr.Core.Transport.Tcp;

/// <summary>
/// Real TCP client side of the unicast-swarm stream transport: opens a socket to a peer's
/// <see cref="Endpoint"/> and hands back a connected <see cref="TcpStreamConnection"/>. Stateless — one
/// instance can dial many peers.
/// </summary>
public sealed class TcpStreamClient : IStreamClient
{
    public async ValueTask<IStreamConnection> ConnectAsync(Endpoint endpoint, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            var address = await ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
            return new TcpStreamConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
        return addresses.Length > 0
            ? addresses[0]
            : throw new SocketException((int)SocketError.HostNotFound);
    }
}
