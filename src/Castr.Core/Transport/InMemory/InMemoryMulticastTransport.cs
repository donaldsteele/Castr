using System.Threading.Channels;

namespace Castr.Core.Transport.InMemory;

public sealed class InMemoryMulticastTransport(InMemoryNetwork network, Endpoint self) : IMulticastTransport
{
    private readonly Channel<ReceivedPacket> _channel = Channel.CreateUnbounded<ReceivedPacket>();

    internal ChannelWriter<ReceivedPacket> Inbox => _channel.Writer;

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        network.PublishMulticast(self, payload.ToArray());
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        network.Unregister(this);
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
