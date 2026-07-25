using System.Threading.Channels;
using Castr.Core.Transport;

namespace Castr.Core.Discovery.InMemory;

/// <summary>
/// A simulated LAN for discovery unit tests: an in-process registry of advertised Castr peers plus a
/// pub/sub bus, mirroring <c>Castr.Core.Transport.InMemory.InMemoryNetwork</c>. Lets tests exercise
/// the <see cref="IServiceDiscovery"/> contract deterministically with no real network, platform API,
/// or timing. A browser subscribing sees a snapshot of everything currently advertised and then every
/// subsequent advertisement live.
/// </summary>
public sealed class InMemoryDiscoveryNetwork
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Endpoint, DiscoveredPeer> _advertised = [];
    private readonly List<Channel<DiscoveredPeer>> _subscribers = [];

    /// <summary>Registers (or replaces) an advertisement and pushes it to all live browsers.</summary>
    internal void Advertise(DiscoveredPeer peer)
    {
        Channel<DiscoveredPeer>[] targets;
        lock (_lock)
        {
            _advertised[peer.Endpoint] = peer;
            targets = [.. _subscribers];
        }

        foreach (var target in targets)
            target.Writer.TryWrite(peer);
    }

    /// <summary>Removes an advertisement (e.g. when an advertiser is disposed).</summary>
    internal void Withdraw(Endpoint endpoint)
    {
        lock (_lock) { _advertised.Remove(endpoint); }
    }

    /// <summary>
    /// Subscribes a browser: seeds the returned channel with the current snapshot of advertised peers,
    /// then streams every future advertisement until <paramref name="unsubscribe"/> is invoked.
    /// </summary>
    internal ChannelReader<DiscoveredPeer> Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateUnbounded<DiscoveredPeer>();
        lock (_lock)
        {
            foreach (var peer in _advertised.Values)
                channel.Writer.TryWrite(peer);
            _subscribers.Add(channel);
        }

        unsubscribe = () =>
        {
            lock (_lock) { _subscribers.Remove(channel); }
            channel.Writer.TryComplete();
        };
        return channel.Reader;
    }
}
