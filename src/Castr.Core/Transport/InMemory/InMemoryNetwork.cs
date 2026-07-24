using System.Threading.Channels;

namespace Castr.Core.Transport.InMemory;

/// <summary>
/// A simulated LAN for pure unit tests: an in-process pub/sub bus that <see cref="InMemoryMulticastTransport"/>
/// and <see cref="InMemoryUnicastTransport"/> instances publish to and receive from, with optional
/// <see cref="ChaosOptions"/> fault injection (loss, duplication, latency/reordering) via a seeded RNG so
/// repair-protocol tests are both realistic and deterministic.
/// </summary>
public sealed class InMemoryNetwork(ChaosOptions? chaos = null)
{
    private readonly ChaosOptions _chaos = chaos ?? ChaosOptions.None;
    private readonly Random _random = chaos?.RandomSeed is { } seed ? new Random(seed) : new Random();
    private readonly Lock _lock = new();
    private readonly List<InMemoryMulticastTransport> _multicastSubscribers = [];
    private readonly Dictionary<Endpoint, InMemoryUnicastTransport> _unicastSubscribers = [];

    public IMulticastTransport CreateMulticastTransport(Endpoint self)
    {
        var transport = new InMemoryMulticastTransport(this, self);
        lock (_lock) { _multicastSubscribers.Add(transport); }
        return transport;
    }

    public IUnicastTransport CreateUnicastTransport(Endpoint self)
    {
        var transport = new InMemoryUnicastTransport(this, self);
        lock (_lock)
        {
            if (!_unicastSubscribers.TryAdd(self, transport))
                throw new InvalidOperationException($"Endpoint {self} is already registered on this network.");
        }
        return transport;
    }

    internal void PublishMulticast(Endpoint from, byte[] payload)
    {
        InMemoryMulticastTransport[] targets;
        lock (_lock) { targets = [.. _multicastSubscribers]; }

        foreach (var target in targets)
            DeliverWithChaos(target.Inbox, new ReceivedPacket(payload, from));
    }

    internal void SendUnicast(Endpoint from, Endpoint to, byte[] payload)
    {
        InMemoryUnicastTransport? target;
        lock (_lock) { _unicastSubscribers.TryGetValue(to, out target); }

        if (target is not null)
            DeliverWithChaos(target.Inbox, new ReceivedPacket(payload, from));
    }

    internal void Unregister(InMemoryMulticastTransport transport)
    {
        lock (_lock) { _multicastSubscribers.Remove(transport); }
    }

    internal void Unregister(Endpoint endpoint)
    {
        lock (_lock) { _unicastSubscribers.Remove(endpoint); }
    }

    private void DeliverWithChaos(ChannelWriter<ReceivedPacket> inbox, ReceivedPacket packet)
    {
        bool lost;
        bool duplicate;
        TimeSpan latency;

        lock (_lock)
        {
            lost = _chaos.LossProbability > 0 && _random.NextDouble() < _chaos.LossProbability;
            duplicate = _chaos.DuplicateProbability > 0 && _random.NextDouble() < _chaos.DuplicateProbability;
            latency = _chaos.MaxLatency > _chaos.MinLatency
                ? _chaos.MinLatency + (_chaos.MaxLatency - _chaos.MinLatency) * _random.NextDouble()
                : _chaos.MinLatency;
        }

        if (lost)
            return;

        int deliveries = duplicate ? 2 : 1;
        for (int i = 0; i < deliveries; i++)
            _ = DeliverAfterDelayAsync(inbox, packet, latency);
    }

    private static async Task DeliverAfterDelayAsync(ChannelWriter<ReceivedPacket> inbox, ReceivedPacket packet, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay).ConfigureAwait(false);
        inbox.TryWrite(packet);
    }
}
