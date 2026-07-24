namespace Castr.Core.Transport;

public interface IMulticastTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default);
}
