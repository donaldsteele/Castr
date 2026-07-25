using System.Numerics;
using Castr.Core.Transport;

namespace Castr.Core.Protocol;

public sealed class PeerTable(TimeSpan? ttl = null) : IPeerTable
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly Dictionary<string, PeerEntry> _peers = [];

    public void Observe(PeerHaveMessage message, DateTimeOffset now)
    {
        string key = KeyOf(message.ReceiverId);
        if (!_peers.TryGetValue(key, out var entry))
        {
            entry = new PeerEntry(message.ReceiverId);
            _peers[key] = entry;
        }

        entry.Endpoint = new Endpoint(message.EndpointHost, message.EndpointPort);
        entry.LastSeen = now;
        entry.FileBitmaps[message.FileIndex] = message.ChunkBitmap;
    }

    public void ObserveDiscovered(Endpoint endpoint, DateTimeOffset now)
    {
        // Discovery has no receiver-id, so discovered peers are keyed by endpoint (prefixed to avoid ever
        // colliding with a gossip key, which is the hex of a 16-byte receiver-id). ReceiverId is a stable
        // synthetic value derived from the endpoint — its length (UTF-8 of "host:port") differs from a real
        // 16-byte id, so it never masquerades as one when RepairCoordinator compares against the caller's id.
        string key = DiscoveredKeyOf(endpoint);
        if (!_peers.TryGetValue(key, out var entry))
        {
            entry = new PeerEntry(System.Text.Encoding.UTF8.GetBytes(endpoint.ToString())) { Discovered = true };
            _peers[key] = entry;
        }

        entry.Endpoint = endpoint;
        entry.LastSeen = now;
    }

    public void RemoveExpired(DateTimeOffset now)
    {
        var expiredKeys = _peers.Where(kv => now - kv.Value.LastSeen > _ttl).Select(kv => kv.Key).ToList();
        foreach (var key in expiredKeys)
            _peers.Remove(key);
    }

    public IReadOnlyList<PeerInfo> GetPeersWithChunk(int fileIndex, int chunkIndex)
    {
        var result = new List<PeerInfo>();
        foreach (var entry in _peers.Values)
        {
            if (entry.FileBitmaps.TryGetValue(fileIndex, out var bitmapBytes))
            {
                // Gossip-confirmed: we know exactly whether this peer holds the chunk. If it doesn't, it is
                // excluded even if it was also discovered — a confirmed "no" beats an "unknown."
                if (HasBit(bitmapBytes, chunkIndex))
                    result.Add(new PeerInfo(entry.ReceiverId, entry.Endpoint, entry.LastSeen, PopCount(bitmapBytes)));
            }
            else if (entry.Discovered)
            {
                // mDNS-discovered, no bitmap for this file: completeness unknown, so try it as a last resort
                // (ranked below every gossip-confirmed holder via the -1 sentinel).
                result.Add(new PeerInfo(entry.ReceiverId, entry.Endpoint, entry.LastSeen, PeerInfo.UnknownChunkPopCount));
            }
        }

        // Most-complete-file first; ties broken by RepairCoordinator's own jitter, not here. The -1 sentinel
        // naturally sorts unknown-completeness discovered peers last.
        return [.. result.OrderByDescending(p => p.ChunkPopCount)];
    }

    public IReadOnlyList<PeerInfo> All() =>
        [.. _peers.Values.Select(e => new PeerInfo(
            e.ReceiverId, e.Endpoint, e.LastSeen,
            e.FileBitmaps.Count > 0 ? e.FileBitmaps.Values.Max(PopCount) : (e.Discovered ? PeerInfo.UnknownChunkPopCount : 0)))];

    private static bool HasBit(byte[] bitmapBytes, int index)
    {
        int byteIndex = index / 8;
        if (byteIndex < 0 || byteIndex >= bitmapBytes.Length)
            return false;
        return (bitmapBytes[byteIndex] & (1 << (index % 8))) != 0;
    }

    private static int PopCount(byte[] bytes)
    {
        int count = 0;
        foreach (var b in bytes)
            count += BitOperations.PopCount(b);
        return count;
    }

    private static string KeyOf(byte[] receiverId) => Convert.ToHexString(receiverId);

    private static string DiscoveredKeyOf(Endpoint endpoint) => "disc:" + endpoint;

    private sealed class PeerEntry(byte[] receiverId)
    {
        public byte[] ReceiverId { get; } = receiverId;
        public Endpoint Endpoint { get; set; } = new("", 0);
        public DateTimeOffset LastSeen { get; set; }
        public Dictionary<int, byte[]> FileBitmaps { get; } = [];

        /// <summary>True for a peer learned only via mDNS discovery (no gossip bitmap) — completeness unknown.</summary>
        public bool Discovered { get; init; }
    }
}
