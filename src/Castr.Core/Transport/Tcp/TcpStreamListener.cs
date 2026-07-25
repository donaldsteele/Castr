using System.Net;
using System.Net.Sockets;

namespace Castr.Core.Transport.Tcp;

/// <summary>
/// Real TCP server side of the unicast-swarm stream transport, backed by <see cref="TcpListener"/>. Bind to
/// port 0 to let the OS pick a free port (read it back from <see cref="LocalEndpoint"/> after construction) —
/// the pattern the loopback integration tests use.
/// </summary>
public sealed class TcpStreamListener : IStreamListener
{
    private readonly TcpListener _listener;

    public TcpStreamListener(IPAddress address, int port, int backlog = 128)
    {
        _listener = new TcpListener(new IPEndPoint(address, port));
        _listener.Start(backlog);
        LocalEndpoint = Endpoint.FromIPEndPoint((IPEndPoint)_listener.LocalEndpoint);
    }

    public Endpoint LocalEndpoint { get; }

    public async ValueTask<IStreamConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        return new TcpStreamConnection(socket);
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
