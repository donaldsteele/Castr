using System.Runtime.CompilerServices;
using Castr.Core.Protocol;
using Castr.Core.Transport;

namespace Castr.Core.Tests.TestSupport;

/// <summary>Test-only decorator that deterministically drops specific decoded messages before they reach a receiver — used to simulate targeted packet loss without relying on probabilistic chaos.</summary>
internal sealed class FilteringMulticastTransport(IMulticastTransport inner, Func<object, bool> shouldDeliver) : IMulticastTransport
{
    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        inner.SendAsync(payload, cancellationToken);

    public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in inner.ReceiveAsync(cancellationToken))
        {
            object? decoded;
            try { decoded = MessageCodec.Decode(packet.Payload); }
            catch { decoded = null; }

            if (decoded is not null && !shouldDeliver(decoded))
                continue;

            yield return packet;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
