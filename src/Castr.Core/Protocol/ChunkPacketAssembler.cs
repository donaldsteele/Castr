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

    /// <summary>
    /// Default cap on distinct concurrent pending (incomplete) chunks.
    ///
    /// <para>Was 1024, mirroring <see cref="PacketReassembler"/>'s group cap — a figure inherited from the 8 KiB
    /// chunk regime, where 1024 partials was ~8 MB. At the 256 KiB default it is <b>~16x more partial chunks than
    /// the 4096-slot transport inbox can hold in flight at once</b>, so it bounded nothing a legitimate transfer
    /// would ever reach while sizing the worst case for an attacker. 64 is comfortably above any real concurrency
    /// (the carousel sends chunks in order, so a lossless transfer keeps 1-2 partials open) and cuts the
    /// pathological ceiling 16x.</para>
    /// </summary>
    public const int DefaultMaxPendingChunks = 64;

    /// <summary>
    /// Smallest fragment size a peer is assumed to have sliced with, used to bound <c>PacketCount</c>. Defaults to
    /// an <b>8x tolerance</b> below the shipped datagram budget. See <see cref="MinFragmentBytesFor"/> — and note
    /// that tolerance is about <i>admitting</i> a legitimate packet count, not about making mismatched budgets
    /// interoperate, which they do not.
    /// </summary>
    public const int DefaultMinFragmentBytes = WirePacketizer.DefaultMaxDatagramPayload / 8;

    private readonly int _maxCiphertextLength;
    private readonly int _maxPendingChunks;
    private readonly int _minFragmentBytes;
    private readonly Dictionary<(int File, int Chunk), Partial> _partials = [];
    private long _sequence;

    /// <summary>
    /// Bounds every attacker-controlled sizing field before it is used to allocate:
    /// <paramref name="maxCiphertextLength"/> caps a single chunk's reassembled ciphertext,
    /// <paramref name="minFragmentBytes"/> caps the <i>packet count</i> that ciphertext could legitimately have
    /// been split into, and <paramref name="maxPendingChunks"/> caps how many distinct incomplete chunks may be
    /// buffered at once (oldest evicted first). A session that knows the manifest's chunk size passes it (plus
    /// AEAD tag) as the tight per-chunk bound; the default is the 16 MiB hard ceiling.
    /// </summary>
    public ChunkPacketAssembler(
        int maxCiphertextLength = DefaultMaxCiphertextLength,
        int maxPendingChunks = DefaultMaxPendingChunks,
        int minFragmentBytes = DefaultMinFragmentBytes)
    {
        if (maxCiphertextLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCiphertextLength));
        if (maxPendingChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingChunks));
        if (minFragmentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minFragmentBytes));
        _maxCiphertextLength = maxCiphertextLength;
        _maxPendingChunks = maxPendingChunks;
        _minFragmentBytes = minFragmentBytes;
    }

    /// <summary>The manifest-independent AEAD tag overhead, so a session can turn a known chunk size into a ciphertext bound.</summary>
    public static int CiphertextBoundForChunkSize(int chunkSize) => chunkSize + AeadTagOverhead;

    /// <summary>
    /// Smallest fragment a peer is credited with having sliced at, for a session running on
    /// <paramref name="maxDatagramPayload"/>. Deliberately <c>/8</c> rather than the true per-packet size:
    /// <see cref="ChunkPacketizer.Split"/> gives packet 0
    /// <c>maxDatagramPayload - FixedEnvelopeOverhead - ProofEncodedSize(proof)</c> bytes and every later packet
    /// <c>maxDatagramPayload - FixedEnvelopeOverhead</c>, and the proof term grows with the file's chunk count, so
    /// the exact figure is not knowable from the receiving side without the proof in hand.
    ///
    /// <para><b>Correction (M9): this margin does NOT make mismatched datagram budgets interoperate</b>, and an
    /// earlier version of this comment implied it did. This bound only decides whether a claimed
    /// <c>PacketCount</c> is admissible at all (a DoS guard). A budget mismatch fails earlier and
    /// unconditionally, on <see cref="Offer"/>'s <c>PacketCount</c> equality check against the first packet seen
    /// for that chunk. The margin's real job is to admit a legitimate relay whose *whole* chunk arrives from one
    /// smaller-budget source; it cannot rescue a chunk already partially held at another slicing.</para>
    /// </summary>
    public static int MinFragmentBytesFor(int maxDatagramPayload) => Math.Max(1, maxDatagramPayload / 8);

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
        // `new byte[PacketCount][]`).
        //
        // The third clause is the one that matters for the packet count, and it exists because the second is far
        // too loose to be a real bound. "A legitimate split never produces more packets than ciphertext bytes" is
        // true but nearly vacuous: the ciphertext bound is manifest-derived, so with a legitimate
        // `--chunk-size 16777216` (Castr.Cli.CastrPaths.MaxChunkSize) it admits a claim of 16,777,232 packets —
        // `new byte[16777232][]` is ~134 MB of references from ONE small datagram, and across the pending-chunk
        // cap that is tens of GB. This is reachable on any tree that allows a large chunk size, not something the
        // 256 KiB default introduced; the default only changes how much a *typical* session exposes.
        //
        // The structural truth is tighter by exactly one factor — the per-packet fragment size:
        //   PacketCount <= ceil(CiphertextLength / perPacket)
        // ChunkPacketizer.Split gives every packet after packet 0 the full maxDatagramPayload -
        // FixedEnvelopeOverhead (1429 bytes at the shipped 1472-byte budget; only packet 0 also reserves
        // ProofEncodedSize), so a 256 KiB chunk is ~184 legitimate packets. We cannot recompute that exactly here
        // (the budget the sender used is not on the wire, and the proof term for packet 0 depends on the file's
        // chunk count), so _minFragmentBytes is deliberately 8x below the real figure, plus one packet of slack
        // for the remainder. At the shipped budget that admits <=1,426 packets against a legitimate ~184:
        // ~7.8x headroom for correct senders, ~180x off the attack.
        //
        // This clause does not change mixed-budget behaviour either way: the consistency check below already
        // rejects any packet whose PacketCount disagrees with the first one seen for that chunk, so mixed-budget
        // sources cannot interoperate *within* a chunk regardless. (Stated precisely because the reverse — "the
        // 8x tolerance absorbs a budget mismatch" — was believed during M8/M9 and is false: the tolerance gates
        // this DoS bound only, and the mismatch fails earlier and unconditionally below.)
        //
        // Note the severity this closes is not "a flood": ~1024 datagrams (~1.2 MB) forcing GBs of long-lived
        // LOH allocation is a ~1800x-asymmetric request that terminates the process, where a flood degrades only
        // while it lasts. Still deferred and tracked in wiki/synthesis/roadmap.md: pre-sizing from the claimed
        // count at all, rather than holding fragments in a dictionary keyed by packet index so memory tracks
        // fragments actually *received*. That is a data-structure change on a hot path and wants its own review;
        // tightening a bound that already exists does not.
        if (packet.CiphertextLength > _maxCiphertextLength
            || packet.PacketCount > Math.Max(1, packet.CiphertextLength)
            || packet.PacketCount > Math.Max(1, (packet.CiphertextLength + _minFragmentBytes - 1) / _minFragmentBytes) + 1)
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
            // Inconsistent metadata for this chunk: the two sources sliced it differently (e.g. peers running
            // different datagram budgets). Never splice mismatched fragments together — but do NOT keep the old
            // buffer and drop the new packet either, which is what this did until M9 QA measured the consequence:
            //
            //   whichever source arrived FIRST pinned the buffer, and every packet of every other slicing was
            //   dropped from then on. Forget() is called from exactly one place — after a *successful* assembly —
            //   so a poisoned partial had no recovery path except LRU eviction, which needs _maxPendingChunks
            //   other distinct chunks to go pending. A lossless transfer keeps 1-2 partials open, so in ordinary
            //   operation that eviction never happens and the chunk is STRANDED for the rest of the transfer:
            //   a full, correct re-delivery of all its packets could not complete it.
            //
            // So the newest slicing wins: drop the stale partial and re-establish the buffer from this packet.
            // The property that matters is that a chunk is always completable by *some* source re-sending it in
            // full, which is exactly what chunk-level repair does. The cost is that two mismatched sources
            // transmitting the same chunk *simultaneously* can reset each other's progress; that is a
            // throughput-shaped failure that repair retries out of, where stranding was terminal for the chunk.
            //
            // Adversarially this is an improvement, not a new hole. Before, one crafted packet could pin a chunk's
            // buffer to a slicing nobody else uses and block every legitimate packet for that chunk PERMANENTLY
            // (nothing releases the partial short of eviction). Now a hostile peer can only reset progress while
            // it keeps transmitting — the "degrades only while the attack lasts" class, which is what the bounds
            // above already accept, rather than a lasting denial from a single datagram.
            //
            // The structural fix — which retires this whole class rather than choosing a winner — is to key
            // fragments by BYTE OFFSET rather than packet index, making two slicings of the same ciphertext
            // interchangeable. Tracked with the assembler rewrite in wiki/synthesis/roadmap.md.
            _partials.Remove(key);
            partial = new Partial(packet.PacketCount, packet.CiphertextLength, ++_sequence);
            _partials[key] = partial;
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
