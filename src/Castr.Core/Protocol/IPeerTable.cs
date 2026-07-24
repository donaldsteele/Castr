using Castr.Core.Transport;

namespace Castr.Core.Protocol;

/// <summary>
/// A candidate repair source discovered via <see cref="PeerHaveMessage"/> gossip. <see cref="ChunkPopCount"/>
/// is a cheap proxy for "how complete is this peer's copy of the file" (higher = prefer), used for ranking
/// — it doesn't require knowing the file's total chunk count, just the peer's most recently announced bitmap.
/// </summary>
public sealed record PeerInfo(byte[] ReceiverId, Endpoint Endpoint, DateTimeOffset LastSeen, int ChunkPopCount);

/// <summary>
/// Tracks peer receivers seen via PEER_HAVE gossip, per file, with TTL-based expiry so a peer that goes
/// silent ages out of consideration on its own (see wiki/concepts/repair-protocol.md). Populated
/// differently per transport tier (multicast gossip on desktop, mDNS+gossip on mobile — deferred to M4)
/// but consumed identically by <see cref="RepairCoordinator"/>: this is the cross-tier seam.
/// </summary>
public interface IPeerTable
{
    void Observe(PeerHaveMessage message, DateTimeOffset now);

    void RemoveExpired(DateTimeOffset now);

    /// <summary>Peers (other than the caller) known to have this specific chunk, most-complete-file first.</summary>
    IReadOnlyList<PeerInfo> GetPeersWithChunk(int fileIndex, int chunkIndex);

    IReadOnlyList<PeerInfo> All();
}
