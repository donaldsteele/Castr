using Castr.Core.Protocol;
using Castr.Core.Time;
using Castr.Core.Transport;

namespace Castr.Core.Tests.Protocol;

public class RepairCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
    private static readonly Endpoint OriginalSender = new("sender-host", 9000);

    [Fact]
    public void PlanRepairs_NoPeersAvailable_FallsBackToOriginalSender()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock);

        var plans = coordinator.PlanRepairs(fileIndex: 0, missingChunkIndices: [1, 2, 3], OriginalSender, NonceFactory);

        Assert.Single(plans);
        Assert.Equal(OriginalSender, plans[0].Target);
        Assert.Equal([1, 2, 3], plans[0].ChunkIndices);
    }

    [Fact]
    public void PlanRepairs_PeerHasChunk_PrefersPeerOverOriginalSender()
    {
        var clock = new FakeClock(Epoch);
        var peerTable = new PeerTable();
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(1), 0, [0b0000_0010], "peer-1", 5000), Epoch); // has chunk 1

        var coordinator = new RepairCoordinator(peerTable, clock);
        var plans = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);

        Assert.Single(plans);
        Assert.Equal(new Endpoint("peer-1", 5000), plans[0].Target);
    }

    [Fact]
    public void PlanRepairs_MixOfPeerAndFallback_SplitsAcrossTargets()
    {
        var clock = new FakeClock(Epoch);
        var peerTable = new PeerTable();
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(1), 0, [0b0000_0001], "peer-1", 5000), Epoch); // only chunk 0

        var coordinator = new RepairCoordinator(peerTable, clock);
        var plans = coordinator.PlanRepairs(0, [0, 5], OriginalSender, NonceFactory); // chunk 5 has no peer

        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.Target == new Endpoint("peer-1", 5000) && p.ChunkIndices.Contains(0));
        Assert.Contains(plans, p => p.Target == OriginalSender && p.ChunkIndices.Contains(5));
    }

    [Fact]
    public void PlanRepairs_ChunkAlreadyInFlight_IsNotReplannedBeforeTimeout()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, new RepairOptions(TimeSpan.FromSeconds(5)));

        var first = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        Assert.Single(first);

        clock.Advance(TimeSpan.FromSeconds(2)); // still within timeout
        var second = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);

        Assert.Empty(second);
    }

    [Fact]
    public void PlanRepairs_RequestTimesOut_IsReplanned()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, new RepairOptions(TimeSpan.FromSeconds(5)));

        coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        clock.Advance(TimeSpan.FromSeconds(6)); // past timeout

        var replanned = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);

        Assert.Single(replanned);
    }

    [Fact]
    public void MarkFulfilled_AllowsImmediateReplanning_WithoutWaitingForTimeout()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, new RepairOptions(TimeSpan.FromSeconds(5)));

        coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        Assert.True(coordinator.IsPending(0, 1));

        coordinator.MarkFulfilled(0, 1);

        Assert.False(coordinator.IsPending(0, 1));
        var replanned = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        Assert.Single(replanned); // e.g. it turned out corrupt on arrival and is missing again
    }

    [Fact]
    public void PlanRepairs_NoMissingChunks_ReturnsEmpty()
    {
        var coordinator = new RepairCoordinator(new PeerTable(), new FakeClock(Epoch));

        Assert.Empty(coordinator.PlanRepairs(0, [], OriginalSender, NonceFactory));
    }

    [Fact]
    public void PlanRepairs_MultiplePeersWithSameCompleteness_JitterPicksAmongThem_Deterministically()
    {
        var clock = new FakeClock(Epoch);
        var peerTable = new PeerTable();
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(1), 0, [0b0000_0001], "peer-1", 1), Epoch);
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(2), 0, [0b0000_0001], "peer-2", 1), Epoch);

        var coordinatorA = new RepairCoordinator(peerTable, clock, random: new Random(123));
        var coordinatorB = new RepairCoordinator(peerTable, clock, random: new Random(123));

        var planA = coordinatorA.PlanRepairs(0, [0], OriginalSender, NonceFactory);
        var planB = coordinatorB.PlanRepairs(0, [0], OriginalSender, NonceFactory);

        Assert.Equal(planA[0].Target, planB[0].Target); // same seed => same jitter choice
    }

    private static byte[] NonceFactory() => Guid.NewGuid().ToByteArray();
    private static byte[] SessionId() => new byte[16];
    private static byte[] PeerId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
