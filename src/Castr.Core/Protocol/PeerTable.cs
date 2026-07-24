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
            if (!entry.FileBitmaps.TryGetValue(fileIndex, out var bitmapBytes))
                continue;

            if (!HasBit(bitmapBytes, chunkIndex))
                continue;

            result.Add(new PeerInfo(entry.ReceiverId, entry.Endpoint, entry.LastSeen, PopCount(bitmapBytes)));
        }

        // Most-complete-file first; ties broken by RepairCoordinator's own jitter, not here.
        return [.. result.OrderByDescending(p => p.ChunkPopCount)];
    }

    public IReadOnlyList<PeerInfo> All() =>
        [.. _peers.Values.Select(e => new PeerInfo(
            e.ReceiverId, e.Endpoint, e.LastSeen,
            e.FileBitmaps.Count > 0 ? e.FileBitmaps.Values.Max(PopCount) : 0))];

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

    private sealed class PeerEntry(byte[] receiverId)
    {
        public byte[] ReceiverId { get; } = receiverId;
        public Endpoint Endpoint { get; set; } = new("", 0);
        public DateTimeOffset LastSeen { get; set; }
        public Dictionary<int, byte[]> FileBitmaps { get; } = [];
    }
}
