using Castr.Core.Manifest;

namespace Castr.Core.Protocol;

/// <summary>
/// Receive-side buffer for <see cref="ChunkPacketMessage"/> wire packets, keyed by chunk identity
/// (file index, chunk index). Because packets for a given chunk are deterministic and identity-keyed, this
/// accumulates them <b>across repair rounds</b>: a packet lost on the carousel and re-sent during repair drops
/// into the same buffer, so a large chunk converges even when independent per-packet loss makes any single
/// round incomplete. A chunk is only surfaced once every packet is present and the reassembled ciphertext
/// length matches — a partially-arrived chunk stays buffered and "not yet received", leaving the existing
/// chunk-level repair to re-request it.
///
/// Not thread-safe: a session owns one instance and drives it from its single receive loop.
/// </summary>
public sealed class ChunkPacketAssembler
{
    /// <summary>AEAD (Poly1305) tag bytes appended to each chunk's plaintext, so a chunk's ciphertext is plaintext+16.</summary>
    private const int AeadTagOverhead = 16;

    /// <summary>
    /// Hard ceiling on a single chunk's reassembled ciphertext length when no tighter, manifest-derived bound is
    /// supplied. Kept consistent with the CLI's advertised maximum chunk size (Castr.Cli.CastrPaths.MaxChunkSize =
    /// 16 MiB) plus the fixed AEAD tag, so an attacker cannot force an allocation larger than a legitimate 16 MiB
    /// chunk would. A session that knows the manifest's chunk size for the file passes a much tighter bound.
    /// </summary>
    public const int DefaultMaxCiphertextLength = 16 * 1024 * 1024 + AeadTagOverhead;

    /// <summary>Default cap on distinct concurrent pending (incomplete) chunks, mirroring <see cref="PacketReassembler"/>'s group cap.</summary>
    public const int DefaultMaxPendingChunks = 1024;

    private readonly int _maxCiphertextLength;
    private readonly int _maxPendingChunks;
    private readonly Dictionary<(int File, int Chunk), Partial> _partials = [];
    private long _sequence;

    /// <summary>
    /// Bounds every attacker-controlled sizing field before it is used to allocate:
    /// <paramref name="maxCiphertextLength"/> caps a single chunk's reassembled ciphertext (and, transitively, the
    /// packet count, since a legitimate split never yields more packets than ciphertext bytes), and
    /// <paramref name="maxPendingChunks"/> caps how many distinct incomplete chunks may be buffered at once
    /// (oldest evicted first). A session that knows the manifest's chunk size passes it (plus AEAD tag) as the
    /// tight per-chunk bound; the default is the 16 MiB hard ceiling.
    /// </summary>
    public ChunkPacketAssembler(int maxCiphertextLength = DefaultMaxCiphertextLength, int maxPendingChunks = DefaultMaxPendingChunks)
    {
        if (maxCiphertextLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCiphertextLength));
        if (maxPendingChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingChunks));
        _maxCiphertextLength = maxCiphertextLength;
        _maxPendingChunks = maxPendingChunks;
    }

    /// <summary>The manifest-independent AEAD tag overhead, so a session can turn a known chunk size into a ciphertext bound.</summary>
    public static int CiphertextBoundForChunkSize(int chunkSize) => chunkSize + AeadTagOverhead;

    /// <summary>Number of chunks with at least one buffered-but-incomplete packet. Exposed for testing.</summary>
    public int PendingChunkCount => _partials.Count;

    /// <summary>
    /// Buffers <paramref name="packet"/> and, if it completes its chunk, returns the fully reassembled
    /// ciphertext together with the chunk's Merkle proof; otherwise returns <c>null</c>. Duplicate, reordered,
    /// inconsistent, and oversized (attacker-controlled) packets are all dropped safely without throwing —
    /// this feeds a shared multicast receive loop that must survive one bad actor or corrupt packet.
    /// </summary>
    public (byte[] Ciphertext, MerkleProof Proof)? Offer(ChunkPacketMessage packet)
    {
        if (packet.PacketCount <= 0
            || packet.PacketIndex < 0
            || packet.PacketIndex >= packet.PacketCount
            || packet.CiphertextLength < 0
            || packet.Fragment.Length > packet.CiphertextLength)
            return null;

        // Reject before allocating anything sized from the wire: a peer must not be able to claim a gigabyte
        // ciphertext (which would size `new byte[CiphertextLength]`) or a huge packet count (which would size
        // `new byte[PacketCount][]`). A legitimate split never produces more packets than ciphertext bytes, so
        // PacketCount is bounded by the ciphertext length, which is bounded by the per-session maximum.
        if (packet.CiphertextLength > _maxCiphertextLength
            || packet.PacketCount > Math.Max(1, packet.CiphertextLength))
            return null;

        var key = (packet.FileIndex, packet.ChunkIndex);
        if (!_partials.TryGetValue(key, out var partial))
        {
            EvictOldestIfFull();
            partial = new Partial(packet.PacketCount, packet.CiphertextLength, ++_sequence);
            _partials[key] = partial;
        }
        else if (partial.PacketCount != packet.PacketCount || partial.CiphertextLength != packet.CiphertextLength)
        {
            // Inconsistent metadata for this chunk (e.g. a peer that sliced with a different datagram budget).
            // Ignore rather than corrupt the in-progress reassembly; repair still converges from one source.
            return null;
        }

        partial.Add(packet.PacketIndex, packet.Fragment, packet.Proof);

        if (!partial.TryAssemble(out var ciphertext, out var proof))
            return null;

        _partials.Remove(key);
        return (ciphertext, proof);
    }

    /// <summary>Caps concurrent pending chunks: when full, drop the oldest still-incomplete one (repair re-requests it later).</summary>
    private void EvictOldestIfFull()
    {
        if (_partials.Count < _maxPendingChunks)
            return;

        (int File, int Chunk) oldestKey = default;
        long oldestSequence = long.MaxValue;
        foreach (var (key, value) in _partials)
        {
            if (value.Sequence < oldestSequence)
            {
                oldestSequence = value.Sequence;
                oldestKey = key;
            }
        }
        _partials.Remove(oldestKey);
    }

    /// <summary>Drops any buffered packets for a chunk — call once the chunk has been accepted (or rejected) so its buffer is released.</summary>
    public void Forget(int fileIndex, int chunkIndex) => _partials.Remove((fileIndex, chunkIndex));

    private sealed class Partial
    {
        private readonly byte[]?[] _fragments;
        private int _received;
        private MerkleProof? _proof;

        public Partial(int packetCount, int ciphertextLength, long sequence)
        {
            PacketCount = packetCount;
            CiphertextLength = ciphertextLength;
            Sequence = sequence;
            _fragments = new byte[packetCount][];
        }

        public int PacketCount { get; }
        public int CiphertextLength { get; }
        public long Sequence { get; }

        public void Add(int index, byte[] fragment, MerkleProof? proof)
        {
            if (proof is not null)
                _proof ??= proof; // proof rides on packet 0
            if (index < 0 || index >= PacketCount || _fragments[index] is not null)
                return; // out of range or duplicate — nothing new
            _fragments[index] = fragment;
            _received++;
        }

        public bool TryAssemble(out byte[] ciphertext, out MerkleProof proof)
        {
            ciphertext = [];
            proof = default!;
            var capturedProof = _proof;
            if (_received != PacketCount || capturedProof is null)
                return false;

            int total = 0;
            foreach (var fragment in _fragments)
            {
                if (fragment is null)
                    return false;
                total += fragment.Length;
            }
            if (total != CiphertextLength)
                return false;

            var result = new byte[total];
            int offset = 0;
            foreach (var fragment in _fragments)
            {
                Array.Copy(fragment!, 0, result, offset, fragment!.Length);
                offset += fragment.Length;
            }
            ciphertext = result;
            proof = capturedProof;
            return true;
        }
    }
}
