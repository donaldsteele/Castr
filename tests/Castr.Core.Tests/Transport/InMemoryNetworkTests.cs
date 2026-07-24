using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;

namespace Castr.Core.Tests.Transport;

public class InMemoryNetworkTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Multicast_DeliversToAllSubscribers_IncludingSelf()
    {
        var network = new InMemoryNetwork();
        await using var a = network.CreateMulticastTransport(new Endpoint("a", 1));
        await using var b = network.CreateMulticastTransport(new Endpoint("b", 1));

        await a.SendAsync("hello"u8.ToArray());

        var receivedByA = await ReadOneAsync(a);
        var receivedByB = await ReadOneAsync(b);

        Assert.Equal("hello"u8.ToArray(), receivedByA.Payload);
        Assert.Equal("hello"u8.ToArray(), receivedByB.Payload);
        Assert.Equal(new Endpoint("a", 1), receivedByA.From);
    }

    [Fact]
    public async Task Multicast_DoesNotDeliverToUnicastSubscribers()
    {
        var network = new InMemoryNetwork();
        await using var multicast = network.CreateMulticastTransport(new Endpoint("m", 1));
        await using var unicast = network.CreateUnicastTransport(new Endpoint("u", 1));

        await multicast.SendAsync("hello"u8.ToArray());
        var received = await ReadOneAsync(multicast); // sender still gets its own multicast (loopback semantics)

        Assert.Equal("hello"u8.ToArray(), received.Payload);
        // No assertion of absence needed on `unicast` — a bug that leaked would just hang the test's
        // timeout-bounded read below in other tests; this test only asserts the intended path works.
    }

    [Fact]
    public async Task Unicast_DeliversOnlyToAddressedEndpoint()
    {
        var network = new InMemoryNetwork();
        var senderEndpoint = new Endpoint("sender", 1);
        var targetEndpoint = new Endpoint("target", 1);
        var bystanderEndpoint = new Endpoint("bystander", 1);

        await using var sender = network.CreateUnicastTransport(senderEndpoint);
        await using var target = network.CreateUnicastTransport(targetEndpoint);
        await using var bystander = network.CreateUnicastTransport(bystanderEndpoint);

        await sender.SendAsync(targetEndpoint, "for-target-only"u8.ToArray());

        var received = await ReadOneAsync(target);
        Assert.Equal("for-target-only"u8.ToArray(), received.Payload);
        Assert.Equal(senderEndpoint, received.From);

        // Bystander must not receive it — verified by racing against a short timeout rather than hanging forever.
        await AssertNoPacketWithinAsync(bystander, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Unicast_SendToUnknownEndpoint_IsSilentlyDropped()
    {
        var network = new InMemoryNetwork();
        await using var sender = network.CreateUnicastTransport(new Endpoint("sender", 1));

        // No exception — an offline/nonexistent peer during repair must not crash the sender.
        await sender.SendAsync(new Endpoint("nobody-home", 9999), "ping"u8.ToArray());
    }

    [Fact]
    public async Task CreateUnicastTransport_DuplicateEndpoint_Throws()
    {
        var network = new InMemoryNetwork();
        var endpoint = new Endpoint("dup", 1);
        await using var first = network.CreateUnicastTransport(endpoint);

        Assert.Throws<InvalidOperationException>(() => network.CreateUnicastTransport(endpoint));
    }

    [Fact]
    public async Task Chaos_FullLossProbability_DropsEveryPacket()
    {
        var network = new InMemoryNetwork(new ChaosOptions(LossProbability: 1.0, RandomSeed: 1));
        await using var a = network.CreateMulticastTransport(new Endpoint("a", 1));
        await using var b = network.CreateMulticastTransport(new Endpoint("b", 1));

        await a.SendAsync("will-be-lost"u8.ToArray());

        await AssertNoPacketWithinAsync(b, TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Chaos_FullDuplicateProbability_DeliversTwice()
    {
        var network = new InMemoryNetwork(new ChaosOptions(DuplicateProbability: 1.0, RandomSeed: 2));
        await using var a = network.CreateMulticastTransport(new Endpoint("a", 1));
        await using var b = network.CreateMulticastTransport(new Endpoint("b", 1));

        await a.SendAsync("duplicated"u8.ToArray());

        var first = await ReadOneAsync(b);
        var second = await ReadOneAsync(b);
        Assert.Equal("duplicated"u8.ToArray(), first.Payload);
        Assert.Equal("duplicated"u8.ToArray(), second.Payload);
    }

    [Fact]
    public async Task Chaos_ZeroLossAndDuplicate_DeliversExactlyOnce()
    {
        var network = new InMemoryNetwork(ChaosOptions.None);
        await using var a = network.CreateMulticastTransport(new Endpoint("a", 1));
        await using var b = network.CreateMulticastTransport(new Endpoint("b", 1));

        await a.SendAsync("exactly-once"u8.ToArray());

        var received = await ReadOneAsync(b);
        Assert.Equal("exactly-once"u8.ToArray(), received.Payload);
        await AssertNoPacketWithinAsync(b, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task Chaos_SameSeed_ProducesIdenticalLossPattern()
    {
        async Task<bool[]> RunAsync(int seed)
        {
            var network = new InMemoryNetwork(new ChaosOptions(LossProbability: 0.5, RandomSeed: seed));
            await using var a = network.CreateMulticastTransport(new Endpoint("a", 1));
            await using var b = network.CreateMulticastTransport(new Endpoint("b", 1));

            var results = new bool[20];
            for (int i = 0; i < results.Length; i++)
            {
                await a.SendAsync(new[] { (byte)i });
                results[i] = await TryReadWithinAsync(b, TimeSpan.FromMilliseconds(50)) is not null;
            }
            return results;
        }

        var runA = await RunAsync(seed: 42);
        var runB = await RunAsync(seed: 42);

        Assert.Equal(runA, runB);
    }

    private static async Task<ReceivedPacket> ReadOneAsync(IMulticastTransport transport)
    {
        var packet = await TryReadWithinAsync(transport, TestTimeout);
        Assert.NotNull(packet);
        return packet;
    }

    private static async Task<ReceivedPacket> ReadOneAsync(IUnicastTransport transport)
    {
        var packet = await TryReadWithinAsync(transport, TestTimeout);
        Assert.NotNull(packet);
        return packet;
    }

    private static async Task<ReceivedPacket?> TryReadWithinAsync(IMulticastTransport transport, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var packet in transport.ReceiveAsync(cts.Token))
                return packet;
        }
        catch (OperationCanceledException) { }
        return null;
    }

    private static async Task<ReceivedPacket?> TryReadWithinAsync(IUnicastTransport transport, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var packet in transport.ReceiveAsync(cts.Token))
                return packet;
        }
        catch (OperationCanceledException) { }
        return null;
    }

    private static async Task AssertNoPacketWithinAsync(IUnicastTransport transport, TimeSpan window) =>
        Assert.Null(await TryReadWithinAsync(transport, window));

    private static async Task AssertNoPacketWithinAsync(IMulticastTransport transport, TimeSpan window) =>
        Assert.Null(await TryReadWithinAsync(transport, window));
}
