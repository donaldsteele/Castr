using Castr.Core.Protocol;

namespace Castr.Core.Tests.Protocol;

public class PeerTableTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-07-24T00:00:00Z");

    [Fact]
    public void Observe_ThenGetPeersWithChunk_FindsPeerThatHasIt()
    {
        var table = new PeerTable();
        var peerId = Id(1);

        // Chunk index 3 set => bit 3 of byte 0 => 0b0000_1000
        table.Observe(new PeerHaveMessage(SessionId(), peerId, FileIndex: 0, ChunkBitmap: [0b0000_1000], "10.0.0.1", 5000), Epoch);

        var peers = table.GetPeersWithChunk(fileIndex: 0, chunkIndex: 3);

        Assert.Single(peers);
        Assert.Equal(peerId, peers[0].ReceiverId);
    }

    [Fact]
    public void GetPeersWithChunk_PeerDoesNotHaveThatBitSet_IsExcluded()
    {
        var table = new PeerTable();
        table.Observe(new PeerHaveMessage(SessionId(), Id(1), 0, [0b0000_0001], "10.0.0.1", 5000), Epoch); // only chunk 0

        Assert.Empty(table.GetPeersWithChunk(fileIndex: 0, chunkIndex: 3));
    }

    [Fact]
    public void GetPeersWithChunk_DifferentFileIndex_IsExcluded()
    {
        var table = new PeerTable();
        table.Observe(new PeerHaveMessage(SessionId(), Id(1), FileIndex: 0, ChunkBitmap: [0xFF], "10.0.0.1", 5000), Epoch);

        Assert.Empty(table.GetPeersWithChunk(fileIndex: 1, chunkIndex: 0));
    }

    [Fact]
    public void GetPeersWithChunk_RanksMostCompleteFirst()
    {
        var table = new PeerTable();
        // peer A has only chunk 0 set; peer B has chunks 0-3 set (more complete)
        table.Observe(new PeerHaveMessage(SessionId(), Id(1), 0, [0b0000_0001], "peer-a", 1), Epoch);
        table.Observe(new PeerHaveMessage(SessionId(), Id(2), 0, [0b0000_1111], "peer-b", 1), Epoch);

        var ranked = table.GetPeersWithChunk(fileIndex: 0, chunkIndex: 0);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("peer-b", ranked[0].Endpoint.Host); // more chunks set => ranked first
        Assert.Equal("peer-a", ranked[1].Endpoint.Host);
    }

    [Fact]
    public void Observe_SamePeerTwice_UpdatesInPlace_DoesNotDuplicate()
    {
        var table = new PeerTable();
        var peerId = Id(1);
        table.Observe(new PeerHaveMessage(SessionId(), peerId, 0, [0b0000_0001], "old-endpoint", 1), Epoch);
        table.Observe(new PeerHaveMessage(SessionId(), peerId, 0, [0b0000_0011], "new-endpoint", 2), Epoch.AddSeconds(1));

        Assert.Single(table.All());
        Assert.Equal("new-endpoint", table.All()[0].Endpoint.Host);
    }

    [Fact]
    public void RemoveExpired_DropsPeersOlderThanTtl()
    {
        var table = new PeerTable(ttl: TimeSpan.FromSeconds(15));
        table.Observe(new PeerHaveMessage(SessionId(), Id(1), 0, [0xFF], "peer", 1), Epoch);

        table.RemoveExpired(Epoch + TimeSpan.FromSeconds(16));

        Assert.Empty(table.All());
    }

    [Fact]
    public void RemoveExpired_KeepsPeersWithinTtl()
    {
        var table = new PeerTable(ttl: TimeSpan.FromSeconds(15));
        table.Observe(new PeerHaveMessage(SessionId(), Id(1), 0, [0xFF], "peer", 1), Epoch);

        table.RemoveExpired(Epoch + TimeSpan.FromSeconds(10));

        Assert.Single(table.All());
    }

    [Fact]
    public void GetPeersWithChunk_ExpiredPeerNotRemoved_StillIncludedUntilRemoveExpiredCalled()
    {
        // RemoveExpired is explicit (clock-driven), not automatic on every read — consistent with the
        // pure-state-machine design where nothing happens on a hidden timer.
        var table = new PeerTable(ttl: TimeSpan.FromSeconds(15));
        table.Observe(new PeerHaveMessage(SessionId(), Id(1), 0, [0xFF], "peer", 1), Epoch);

        var peers = table.GetPeersWithChunk(0, 0); // called with a "now" 100s later conceptually, but RemoveExpired wasn't invoked

        Assert.Single(peers);
    }

    private static byte[] SessionId() => Id(16);

    private static byte[] Id(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
