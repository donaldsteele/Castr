using System.Threading.Channels;

namespace Castr.Core.Transport.InMemory;

public sealed class InMemoryUnicastTransport(InMemoryNetwork network, Endpoint self) : IUnicastTransport
{
    private readonly Channel<ReceivedPacket> _channel = Channel.CreateUnbounded<ReceivedPacket>();

    public Endpoint LocalEndpoint => self;

    internal ChannelWriter<ReceivedPacket> Inbox => _channel.Writer;

    public ValueTask SendAsync(Endpoint destination, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        network.SendUnicast(self, destination, payload.ToArray());
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<ReceivedPacket> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        network.Unregister(self);
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
