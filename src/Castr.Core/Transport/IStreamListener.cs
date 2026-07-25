namespace Castr.Core.Transport;

/// <summary>
/// Server side of the unicast-swarm stream transport: accepts inbound peer connections, each surfaced as a
/// bidirectional <see cref="IStreamConnection"/> to be handled independently. Mirrors the real+in-memory split
/// (real: <see cref="Tcp.TcpStreamListener"/>; test fake: <see cref="InMemory.InMemoryStreamNetwork"/>).
/// </summary>
public interface IStreamListener : IAsyncDisposable
{
    /// <summary>The address this listener accepts connections on (its port is assigned once <see cref="AcceptAsync"/>-ready).</summary>
    Endpoint LocalEndpoint { get; }

    /// <summary>Waits for and returns the next inbound connection. Throws <see cref="OperationCanceledException"/> on cancellation.</summary>
    ValueTask<IStreamConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
