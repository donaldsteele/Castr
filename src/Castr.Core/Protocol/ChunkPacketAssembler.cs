using Castr.Core.Manifest;

namespace Castr.Core.Protocol;

/// <summary>
/// Receive-side buffer for <see cref="ChunkPacketMessage"/> wire packets, keyed by chunk identity
/// (file index, chunk index). Because packets for a given chunk are deterministic and identity-keyed, this
/// accumulates them <b>across repair rounds</b>: a packet lost on the carousel and re-sent during repair drops
/// into the same buffer, so a large chunk converges even when independent per-packet loss makes any single
/// round incomplete. A chunk is only surfaced once every byte of its ciphertext is covered — a partially-arrived
/// chunk stays buffered and "not yet received", leaving the existing chunk-level repair to re-request it.
///
/// <para><b>Fragments are placed by byte offset, not by packet index (M11).</b> Each <see cref="Partial"/> holds
/// one ciphertext-sized buffer plus the set of byte ranges written into it so far. That single change retires
/// three separate problems at once:</para>
/// <list type="bullet">
/// <item><description><b>Mixed datagram budgets now interoperate.</b> A packet index only means something
/// relative to the slicing that produced it, so two peers on different <c>--datagram-size</c> values described
/// mutually unintelligible layouts and the assembler had to reject one of them outright. Byte ranges are a
/// property of the ciphertext, so any mix of slicings combines. The "the budget must match on every peer"
/// contract — which held by documentation alone, with no wire enforcement and no diagnostic when it was
/// violated — is gone.</description></item>
/// <item><description><b>The mixed-slicing stranding class is retired</b> rather than mitigated. M9's fix chose
/// a winner (newest slicing resets the buffer) because splicing two slicings was impossible; there is no longer
/// anything to choose between, so neither source can reset the other's progress.</description></item>
/// <item><description><b>Nothing is sized from a claimed packet count.</b> The buffer is sized from
/// <c>CiphertextLength</c>, which a session bounds against the manifest's chunk size, so a pending chunk costs
/// exactly what a legitimate chunk of that transfer costs and no more.</description></item>
/// </list>
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
    ///
    /// <para>This is now the <i>only</i> multiplier on pending-chunk memory: with offset keying a partial costs
    /// its ciphertext length flat, so the worst case is <c>maxPendingChunks x maxCiphertextLength</c> — 16 MB at
    /// the shipped 256 KiB chunk, and reached only by an attacker opening 64 chunks at once.</para>
    /// </summary>
    public const int DefaultMaxPendingChunks = 64;

    /// <summary>
    /// Smallest fragment size a peer is assumed to have sliced with. Defaults to an <b>8x tolerance</b> below the
    /// shipped datagram budget. See <see cref="MinFragmentBytesFor"/>.
    /// </summary>
    public const int DefaultMinFragmentBytes = WirePacketizer.DefaultMaxDatagramPayload / 8;

    private readonly int _maxCiphertextLength;
    private readonly int _maxPendingChunks;
    private readonly int _minFragmentBytes;
    private readonly Dictionary<(int File, int Chunk), Partial> _partials = [];
    private long _sequence;

    /// <summary>
    /// Bounds every attacker-controlled sizing field before it is used to allocate:
    /// <paramref name="maxCiphertextLength"/> caps a single chunk's buffer, <paramref name="minFragmentBytes"/>
    /// caps how <i>fragmented</i> a partial's coverage may become, and <paramref name="maxPendingChunks"/> caps
    /// how many distinct incomplete chunks may be buffered at once (oldest-established evicted first). A session
    /// that knows the manifest's chunk size passes it (plus AEAD tag) as the tight per-chunk bound; the default
    /// is the 16 MiB hard ceiling.
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
    /// <para><b>What this bounds, precisely.</b> Since M11 it is not a bound on a packet count — no count is on
    /// the wire any more — but on how many <i>disjoint byte ranges</i> a partial's coverage may be broken into.
    /// The two are the same order for an honest sender (each lost packet leaves at most one hole), and the point
    /// of the bound is the same: a peer sending one-byte fragments at scattered offsets must not be able to grow
    /// an unbounded interval list. Once the cap is reached a partial still accepts every packet that <i>extends
    /// or fills</i> its existing coverage, which is every packet a real transfer produces; only a packet that
    /// would open yet another disjoint hole is dropped.</para>
    ///
    /// <para>An earlier version of this comment claimed the 8x margin let mismatched datagram budgets
    /// interoperate. It did not — through M9 mixed budgets could not interoperate at all. They can now, but by
    /// offset keying, not by this margin, which remains purely a resource guard.</para>
    /// </summary>
    public static int MinFragmentBytesFor(int maxDatagramPayload) => Math.Max(1, maxDatagramPayload / 8);

    /// <summary>Number of chunks with at least one buffered-but-incomplete packet. Exposed for testing.</summary>
    public int PendingChunkCount => _partials.Count;

    /// <summary>
    /// Disjoint covered byte ranges currently held for a chunk, or 0 if nothing is buffered for it. Exposed so
    /// the resource-bound tests can assert on fragmentation directly rather than inferring it.
    /// </summary>
    public int CoverageRangeCount(int fileIndex, int chunkIndex) =>
        _partials.TryGetValue((fileIndex, chunkIndex), out var partial) ? partial.CoverageRangeCount : 0;

    /// <summary>
    /// Buffers <paramref name="packet"/> and, if it completes its chunk, returns the fully reassembled
    /// ciphertext together with the chunk's Merkle proof; otherwise returns <c>null</c>. Duplicate, reordered,
    /// overlapping, inconsistent, and oversized (attacker-controlled) packets are all dropped safely without
    /// throwing — this feeds a shared multicast receive loop that must survive one bad actor or corrupt packet.
    /// </summary>
    public (byte[] Ciphertext, MerkleProof Proof)? Offer(ChunkPacketMessage packet)
    {
        // Reject before allocating anything sized from the wire. Two fields are attacker-controlled and both are
        // checked here: CiphertextLength (which sizes the buffer) and FragmentOffset (which indexes into it).
        //
        // The packet-count bound this replaced was the larger of the two hazards, and it is now structurally
        // absent rather than tightened: through M9 a single small datagram could claim `PacketCount` up to the
        // ciphertext length and size `new byte[PacketCount][]` — ~134 MB of references for a legitimate 16 MiB
        // chunk size, and tens of GB across the pending-chunk cap, from ~1024 crafted datagrams. Nothing on the
        // wire sizes an array by count any more; a partial costs exactly CiphertextLength bytes.
        if (packet.CiphertextLength < 0
            || packet.CiphertextLength > _maxCiphertextLength
            || packet.FragmentOffset < 0
            || packet.Fragment.Length > packet.CiphertextLength
            || packet.FragmentOffset > packet.CiphertextLength - packet.Fragment.Length)
            return null;

        var key = (packet.FileIndex, packet.ChunkIndex);
        if (!_partials.TryGetValue(key, out var partial))
        {
            EvictOldestIfFull();
            partial = new Partial(packet.CiphertextLength, ++_sequence);
            _partials[key] = partial;
        }
        else if (partial.CiphertextLength != packet.CiphertextLength)
        {
            // Two sources disagree about how long this chunk's ciphertext is. Unlike a slicing disagreement —
            // which offset keying makes a non-event — this is a disagreement about the chunk itself, so at most
            // one of them is telling the truth and their bytes must never be combined.
            //
            // The newest claim wins and the stale buffer is dropped. Keeping the old one instead is what M9 QA
            // measured the cost of: whichever source arrived FIRST pinned the buffer and everything else was
            // dropped from then on, with Forget() only called after a *successful* assembly, so a poisoned
            // partial had no recovery path short of eviction under cap pressure that a lossless transfer never
            // reaches — the chunk was stranded for the rest of the transfer. Resetting keeps the property that
            // matters: a chunk is always completable by *some* source re-sending it in full, which is exactly
            // what chunk-level repair does.
            _partials.Remove(key);
            partial = new Partial(packet.CiphertextLength, ++_sequence);
            _partials[key] = partial;
        }

        partial.Add(packet.FragmentOffset, packet.Fragment, packet.Proof, MaxCoverageRangesFor(packet.CiphertextLength));

        if (!partial.TryAssemble(out var ciphertext, out var proof))
            return null;

        _partials.Remove(key);
        return (ciphertext, proof);
    }

    /// <summary>
    /// How many disjoint covered byte ranges one partial may hold. Derived from <see cref="_minFragmentBytes"/>:
    /// a sender slicing at or above that size cannot produce more ranges than this, plus slack for the remainder
    /// range and for one hole at each end.
    /// </summary>
    private int MaxCoverageRangesFor(int ciphertextLength) =>
        Math.Max(1, (ciphertextLength + _minFragmentBytes - 1) / _minFragmentBytes) + 2;

    /// <summary>
    /// Caps concurrent pending chunks: when full, drop the one whose buffer was established earliest (repair
    /// re-requests it later). Deliberately FIFO by establishment and not LRU — a partial that keeps receiving
    /// packets is making progress, but so is one that has been quietly waiting for a single lost fragment, and
    /// the oldest buffer is the one least likely to still have a source transmitting into it.
    /// </summary>
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

    /// <summary>
    /// One chunk's ciphertext under construction: the bytes, plus the disjoint byte ranges written so far.
    /// Complete when a single range covers <c>[0, CiphertextLength)</c> and the proof has arrived.
    /// </summary>
    private sealed class Partial
    {
        // Sorted by Start, disjoint, and merged whenever two become adjacent — so an in-order delivery holds
        // exactly one range and the common case costs one comparison per packet.
        private readonly List<(int Start, int End)> _covered = [];
        private readonly byte[] _buffer;
        private MerkleProof? _proof;

        public Partial(int ciphertextLength, long sequence)
        {
            _buffer = new byte[ciphertextLength];
            Sequence = sequence;
        }

        public int CiphertextLength => _buffer.Length;
        public long Sequence { get; }

        /// <summary>Disjoint covered ranges. Exposed for the resource-bound tests, which assert on fragmentation.</summary>
        public int CoverageRangeCount => _covered.Count;

        /// <summary>
        /// Writes a fragment's still-uncovered bytes into the buffer. Bytes already covered are left alone:
        /// first writer wins, so a duplicate costs nothing and a hostile peer cannot overwrite bytes a good
        /// source already delivered. It can still poison bytes nobody has delivered yet — but that produces a
        /// chunk that fails Merkle verification, which the caller drops <i>and forgets</i>, so the next round
        /// starts from an empty buffer. Poisoning degrades throughput while it lasts; it cannot strand a chunk.
        /// </summary>
        public void Add(int offset, byte[] fragment, MerkleProof? proof, int maxRanges)
        {
            if (proof is not null)
                _proof ??= proof; // the proof rides on the fragment at offset 0

            if (fragment.Length == 0)
                return;

            int start = offset;
            int end = offset + fragment.Length;

            // A packet that neither touches nor overlaps any existing range would open a new hole. Refuse that
            // once the range list is at its cap, so scattered one-byte fragments cannot grow it without bound.
            if (_covered.Count >= maxRanges && !TouchesExisting(start, end))
                return;

            // Copy only the gaps. _covered is sorted and disjoint, so one forward walk finds them all.
            int cursor = start;
            int i = 0;
            while (i < _covered.Count && _covered[i].End <= cursor)
                i++;
            while (cursor < end)
            {
                if (i < _covered.Count && _covered[i].Start <= cursor)
                {
                    cursor = Math.Min(end, _covered[i].End);
                    i++;
                    continue;
                }
                int gapEnd = i < _covered.Count ? Math.Min(end, _covered[i].Start) : end;
                Array.Copy(fragment, cursor - offset, _buffer, cursor, gapEnd - cursor);
                cursor = gapEnd;
            }

            Cover(start, end);
        }

        public bool TryAssemble(out byte[] ciphertext, out MerkleProof proof)
        {
            ciphertext = [];
            proof = default!;

            var capturedProof = _proof;
            if (capturedProof is null)
                return false;
            if (CiphertextLength > 0 && (_covered.Count != 1 || _covered[0] != (0, CiphertextLength)))
                return false;

            // Handing the buffer out directly is safe: Offer removes the partial in the same step, so nothing
            // can write into it afterwards.
            ciphertext = _buffer;
            proof = capturedProof;
            return true;
        }

        private bool TouchesExisting(int start, int end)
        {
            foreach (var range in _covered)
                if (range.Start <= end && start <= range.End)
                    return true;
            return false;
        }

        /// <summary>Unions <c>[start, end)</c> into the covered set, merging every range it touches or overlaps.</summary>
        private void Cover(int start, int end)
        {
            int first = 0;
            while (first < _covered.Count && _covered[first].End < start)
                first++;

            int last = first;
            while (last < _covered.Count && _covered[last].Start <= end)
            {
                start = Math.Min(start, _covered[last].Start);
                end = Math.Max(end, _covered[last].End);
                last++;
            }

            _covered.RemoveRange(first, last - first);
            _covered.Insert(first, (start, end));
        }
    }
}
