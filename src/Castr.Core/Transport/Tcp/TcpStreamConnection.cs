using System.Net;
using System.Net.Sockets;

namespace Castr.Core.Transport.Tcp;

/// <summary>
/// A single connected TCP socket exposed as an <see cref="IStreamConnection"/>. Wraps a
/// <see cref="System.Net.Sockets.Socket"/> directly (not <see cref="TcpClient"/>) so both the client-dialed
/// and server-accepted sides share one implementation. Disabling Nagle keeps the small request/response
/// control messages of the swarm-pull protocol prompt.
/// </summary>
public sealed class TcpStreamConnection : IStreamConnection
{
    private readonly Socket _socket;

    internal TcpStreamConnection(Socket socket)
    {
        _socket = socket;
        try { _socket.NoDelay = true; } catch (SocketException) { /* best-effort; not fatal */ }
        RemoteEndpoint = _socket.RemoteEndPoint is IPEndPoint ip
            ? Endpoint.FromIPEndPoint(ip)
            : new Endpoint("unknown", 0);
    }

    public Endpoint RemoteEndpoint { get; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        int sent = 0;
        while (sent < data.Length)
            sent += await _socket.SendAsync(data[sent..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // A reset/aborted connection reads as end-of-stream to the caller, not an exception through the
            // framing loop — the framer surfaces truncation as a clean disconnect.
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _socket.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { /* already closed */ }
        catch (ObjectDisposedException) { /* already disposed */ }
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
