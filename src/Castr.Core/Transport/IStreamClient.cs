namespace Castr.Core.Transport;

/// <summary>
/// Client side of the unicast-swarm stream transport: dials a peer <see cref="Endpoint"/> and returns a
/// connected <see cref="IStreamConnection"/>. Mirrors the real+in-memory split used everywhere else in
/// Castr.Core (real: <see cref="Tcp.TcpStreamClient"/>; test fake: <see cref="InMemory.InMemoryStreamNetwork"/>).
/// </summary>
public interface IStreamClient
{
    ValueTask<IStreamConnection> ConnectAsync(Endpoint endpoint, CancellationToken cancellationToken = default);
}
