using Castr.Core.Transport;

namespace Castr.Core.Protocol;

/// <summary>
/// A candidate repair source. <see cref="ChunkPopCount"/> is a cheap proxy for "how complete is this peer's
/// copy of the file" (higher = prefer), used for ranking — it doesn't require knowing the file's total chunk
/// count, just the peer's most recently announced bitmap.
/// <para>
/// The special value <see cref="UnknownChunkPopCount"/> (-1) marks a peer registered via mobile mDNS
/// discovery (<see cref="IPeerTable.ObserveDiscovered"/>) whose chunk completeness is not yet known: unlike
/// multicast gossip, discovery reveals only "a peer exists at this endpoint," never a bitmap. -1 is chosen
/// over 0 deliberately — 0 is a legitimate, rankable count meaning "known to hold zero chunks," so reusing it
/// would conflate "known empty" with "unknown." Because <see cref="RepairCoordinator"/> ranks most-complete
/// first (descending), -1 sorts strictly below every real count, making unknown-completeness peers the
/// lowest-priority candidates — still tried when nothing better is known, never preferred over a peer gossip
/// confirms holds the chunk. This keeps the ranking logic identical across both transport tiers.
/// </para>
/// </summary>
public sealed record PeerInfo(byte[] ReceiverId, Endpoint Endpoint, DateTimeOffset LastSeen, int ChunkPopCount)
{
    /// <summary>Sentinel <see cref="ChunkPopCount"/> for an mDNS-discovered peer of unknown completeness.</summary>
    public const int UnknownChunkPopCount = -1;
}

/// <summary>
/// Tracks peer receivers seen via PEER_HAVE gossip, per file, with TTL-based expiry so a peer that goes
/// silent ages out of consideration on its own (see wiki/concepts/repair-protocol.md). Populated
/// differently per transport tier (multicast gossip on desktop, mDNS+gossip on mobile — deferred to M4)
/// but consumed identically by <see cref="RepairCoordinator"/>: this is the cross-tier seam.
/// </summary>
public interface IPeerTable
{
    void Observe(PeerHaveMessage message, DateTimeOffset now);

    /// <summary>
    /// Registers a peer learned from mobile mDNS discovery, which yields only an endpoint (no chunk bitmap).
    /// The peer is upserted with <see cref="PeerInfo.UnknownChunkPopCount"/> completeness so
    /// <see cref="RepairCoordinator"/> can still consider it — at lowest priority — for any chunk, keeping the
    /// cross-tier ranking uniform. If the same endpoint is later confirmed by gossip (a real bitmap), that
    /// takes precedence for ranking. A no-op refresh of <see cref="PeerInfo.LastSeen"/> if already known.
    /// </summary>
    void ObserveDiscovered(Endpoint endpoint, DateTimeOffset now);

    void RemoveExpired(DateTimeOffset now);

    /// <summary>Peers (other than the caller) known to have this specific chunk, most-complete-file first.</summary>
    IReadOnlyList<PeerInfo> GetPeersWithChunk(int fileIndex, int chunkIndex);

    IReadOnlyList<PeerInfo> All();
}
