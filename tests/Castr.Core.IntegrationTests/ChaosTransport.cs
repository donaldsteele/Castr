using System.Runtime.CompilerServices;
using Castr.Core.Transport;

namespace Castr.Core.IntegrationTests;

/// <summary>
/// Wraps a real transport with seeded-RNG fault injection (loss/duplication/latency), so integration
/// tests get both real-OS-socket fidelity and reproducible failure scenarios — complementary to
/// Castr.Core.Tests' InMemoryNetwork, which is deterministic but never touches a real socket.
/// </summary>
internal sealed class ChaosTransport(
    IMulticastTransport inner, double lossProbability = 0, double duplicateProbability = 0, int seed = 0) : IMulticastTransport
{
    private readonly Random _random = new(seed);

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        inner.SendAsync(payload, cancellationToken);

    public async IAsyncEnumerable<ReceivedPacket> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in inner.ReceiveAsync(cancellationToken))
        {
            if (lossProbability > 0 && _random.NextDouble() < lossProbability)
                continue;

            yield return packet;

            if (duplicateProbability > 0 && _random.NextDouble() < duplicateProbability)
                yield return packet;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
