using Castr.Core.Protocol;
using Castr.Core.Time;
using Castr.Core.Transport;

namespace Castr.Core.Tests.Protocol;

/// <summary>Covers the M4 IPeerTable.ObserveDiscovered extension: mobile mDNS registers a peer with no chunk bitmap, ranked as unknown-completeness.</summary>
public class PeerTableDiscoveryTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
    private static readonly Endpoint OriginalSender = new("sender-host", 9000);

    [Fact]
    public void ObserveDiscovered_RegistersPeer_WithUnknownPopCountSentinel()
    {
        var table = new PeerTable();
        var endpoint = new Endpoint("10.0.0.7", 5001);

        table.ObserveDiscovered(endpoint, Epoch);

        var all = table.All();
        Assert.Single(all);
        Assert.Equal(endpoint, all[0].Endpoint);
        Assert.Equal(PeerInfo.UnknownChunkPopCount, all[0].ChunkPopCount);
        Assert.Equal(-1, PeerInfo.UnknownChunkPopCount);
    }

    [Fact]
    public void GetPeersWithChunk_DiscoveredPeer_IsIncludedForAnyChunk_ButRankedLast()
    {
        var table = new PeerTable();
        // A gossip-confirmed holder of chunk 0 with a couple of chunks, plus a discovery-only peer.
        table.Observe(new PeerHaveMessage(Id16(), Id16(1), 0, [0b0000_0011], "gossip-peer", 5000), Epoch);
        table.ObserveDiscovered(new Endpoint("discovered-peer", 5001), Epoch);

        var ranked = table.GetPeersWithChunk(fileIndex: 0, chunkIndex: 0);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("gossip-peer", ranked[0].Endpoint.Host);       // known completeness ranks above
        Assert.Equal("discovered-peer", ranked[1].Endpoint.Host);   // unknown completeness ranks last
        Assert.Equal(PeerInfo.UnknownChunkPopCount, ranked[1].ChunkPopCount);
    }

    [Fact]
    public void GetPeersWithChunk_OnlyDiscoveredPeers_AreStillTried_NotExcluded()
    {
        var table = new PeerTable();
        table.ObserveDiscovered(new Endpoint("only-peer", 5001), Epoch);

        // Even for an arbitrary chunk index we have no bitmap for, the discovered peer is a candidate.
        var ranked = table.GetPeersWithChunk(fileIndex: 2, chunkIndex: 99);

        Assert.Single(ranked);
        Assert.Equal("only-peer", ranked[0].Endpoint.Host);
    }

    [Fact]
    public void GetPeersWithChunk_GossipConfirmedLack_BeatsDiscovery_ForThatFile()
    {
        var table = new PeerTable();
        // Same peer both discovered AND gossiping a bitmap that lacks chunk 3 for file 0.
        var endpoint = new Endpoint("dual-peer", 5000);
        table.ObserveDiscovered(endpoint, Epoch);
        table.Observe(new PeerHaveMessage(Id16(), Id16(1), 0, [0b0000_0001], "dual-peer", 5000), Epoch); // only chunk 0

        // For file 0 chunk 3, the gossip entry says "no" — so it is excluded (a confirmed no beats unknown).
        // (The discovered entry is keyed separately by endpoint, so it may still surface; assert the gossip
        // entry specifically is not offered as a holder of chunk 3.)
        var ranked = table.GetPeersWithChunk(fileIndex: 0, chunkIndex: 3);

        Assert.DoesNotContain(ranked, p => p.ChunkPopCount >= 0); // no gossip-confirmed holder of chunk 3
    }

    [Fact]
    public void ObserveDiscovered_SameEndpointTwice_DoesNotDuplicate_RefreshesLastSeen()
    {
        var table = new PeerTable();
        var endpoint = new Endpoint("10.0.0.7", 5001);

        table.ObserveDiscovered(endpoint, Epoch);
        table.ObserveDiscovered(endpoint, Epoch.AddSeconds(5));

        Assert.Single(table.All());
        Assert.Equal(Epoch.AddSeconds(5), table.All()[0].LastSeen);
    }

    [Fact]
    public void RemoveExpired_AgesOutDiscoveredPeers()
    {
        var table = new PeerTable(ttl: TimeSpan.FromSeconds(15));
        table.ObserveDiscovered(new Endpoint("10.0.0.7", 5001), Epoch);

        table.RemoveExpired(Epoch + TimeSpan.FromSeconds(16));

        Assert.Empty(table.All());
    }

    [Fact]
    public void RepairCoordinator_UsesDiscoveredPeer_WhenNoGossipHolderIsKnown()
    {
        var table = new PeerTable();
        table.ObserveDiscovered(new Endpoint("discovered-peer", 5001), Epoch);
        // InitialRequestJitter off: this test is about which TARGET is picked, not about when the first request
        // fires, and the shipped 500 ms jitter would otherwise defer the first plan and mask the ranking.
        var coordinator = new RepairCoordinator(
            table, new FakeClock(Epoch),
            new RepairOptions(TimeSpan.FromSeconds(5), RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

        var plans = coordinator.PlanRepairs(fileIndex: 0, missingChunkIndices: [4], OriginalSender, NonceFactory);

        // The discovered peer is tried in preference to falling back to the original sender.
        Assert.Single(plans);
        Assert.Equal(new Endpoint("discovered-peer", 5001), plans[0].Target);
    }

    [Fact]
    public void RepairCoordinator_PrefersGossipConfirmedHolder_OverDiscoveredPeer()
    {
        var table = new PeerTable();
        table.Observe(new PeerHaveMessage(Id16(), Id16(1), 0, [0b0001_0000], "gossip-peer", 5000), Epoch); // has chunk 4
        table.ObserveDiscovered(new Endpoint("discovered-peer", 5001), Epoch);
        // InitialRequestJitter off: this test is about which TARGET is picked, not about when the first request
        // fires, and the shipped 500 ms jitter would otherwise defer the first plan and mask the ranking.
        var coordinator = new RepairCoordinator(
            table, new FakeClock(Epoch),
            new RepairOptions(TimeSpan.FromSeconds(5), RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

        var plans = coordinator.PlanRepairs(0, [4], OriginalSender, NonceFactory);

        Assert.Single(plans);
        Assert.Equal(new Endpoint("gossip-peer", 5000), plans[0].Target);
    }

    private static byte[] NonceFactory() => Guid.NewGuid().ToByteArray();
    private static byte[] Id16() => new byte[16];
    private static byte[] Id16(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
