namespace Castr.Core.Transport;

public interface IUnicastTransport : IAsyncDisposable
{
    Endpoint LocalEndpoint { get; }

    ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default);
}
