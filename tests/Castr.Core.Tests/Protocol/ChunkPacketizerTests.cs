using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Unit tests for the chunk-aware transport packetization (<see cref="ChunkPacketizer"/> /
/// <see cref="ChunkPacketAssembler"/>): a chunk's ciphertext splits into MTU-safe packets and reassembles
/// exactly; reassembly waits for every packet, tolerates reordering/duplication, and — the property that
/// makes large-chunk repair viable — accumulates a chunk's packets across separate retransmission rounds.
/// </summary>
public class ChunkPacketizerTests
{
    private static readonly byte[] Session = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    private static MerkleProof ProofFor(int leafCount, int leafIndex) =>
        MerkleTree.Build(Enumerable.Range(0, leafCount).Select(i => ChunkHash.Compute(BitConverter.GetBytes(i))).ToArray())
            .GetProof(leafIndex);

    [Theory]
    [InlineData(8208, 1200)]      // ~8 KB default chunk ciphertext
    [InlineData(262_160, 1200)]   // ~256 KB documented chunk ciphertext
    [InlineData(1_048_592, 1200)] // ~1 MB documented chunk ciphertext
    public void SplitThenReassemble_RoundTripsExactly(int ciphertextLength, int maxDatagram)
    {
        var ciphertext = Bytes(ciphertextLength, seed: ciphertextLength);
        var proof = ProofFor(64, 3);

        var packets = ChunkPacketizer.Split(Session, fileIndex: 0, chunkIndex: 3, ciphertext, proof, maxDatagram);

        Assert.True(packets.Count > 1);
        Assert.All(packets, p => Assert.True(MessageCodec.Encode(p).Length <= maxDatagram, "packet exceeds datagram budget"));
        // Proof rides only on packet 0.
        Assert.NotNull(packets[0].Proof);
        Assert.All(packets.Skip(1), p => Assert.Null(p.Proof));

        var assembler = new ChunkPacketAssembler();
        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        for (int i = 0; i < packets.Count; i++)
        {
            var result = assembler.Offer(packets[i]);
            if (i < packets.Count - 1)
                Assert.Null(result);
            else
                completed = result;
        }

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
        Assert.Equal(proof, completed.Value.Proof);
    }

    [Fact]
    public void Reassembly_HandlesOutOfOrderAndDuplicatePackets()
    {
        var ciphertext = Bytes(20_000, seed: 7);
        var packets = ChunkPacketizer.Split(Session, 1, 2, ciphertext, ProofFor(16, 2), 1200).ToList();

        var jumbled = packets
            .Concat(packets) // every packet delivered twice
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        var assembler = new ChunkPacketAssembler();
        var completions = jumbled.Select(assembler.Offer).Where(r => r is not null).ToList();

        Assert.Single(completions);
        Assert.Equal(ciphertext, completions[0]!.Value.Ciphertext);
    }

    [Fact]
    public void IncompleteChunk_IsNeverSurfaced()
    {
        var ciphertext = Bytes(30_000, seed: 8);
        var packets = ChunkPacketizer.Split(Session, 0, 0, ciphertext, ProofFor(8, 0), 1200).ToList();

        var assembler = new ChunkPacketAssembler();
        for (int i = 0; i < packets.Count; i++)
        {
            if (i == 2)
                continue; // one packet permanently lost this round
            Assert.Null(assembler.Offer(packets[i]));
        }

        Assert.Equal(1, assembler.PendingChunkCount); // buffered but never completed => "not received"
    }

    [Fact]
    public void Packets_AreDeterministic_AcrossRetransmissions()
    {
        // The core accumulation guarantee depends on every re-send producing byte-identical packets.
        var ciphertext = Bytes(50_000, seed: 9);
        var proof = ProofFor(32, 5);

        var first = ChunkPacketizer.Split(Session, 2, 5, ciphertext, proof, 1200);
        var second = ChunkPacketizer.Split(Session, 2, 5, ciphertext, proof, 1200);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(MessageCodec.Encode(first[i]), MessageCodec.Encode(second[i]));
    }

    [Fact]
    public void Reassembly_AccumulatesAcrossRepairRounds()
    {
        // Models the real failure the design must survive: at high per-packet loss no single round delivers a
        // large chunk in full, but identity-keyed packets from later repair rounds fill the gaps left by
        // earlier ones until the chunk is complete.
        var ciphertext = Bytes(120_000, seed: 10); // many packets
        var proof = ProofFor(64, 1);
        var assembler = new ChunkPacketAssembler();

        var rng = new Random(1234);
        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        int round = 0;
        while (completed is null && round++ < 100)
        {
            // Each round re-sends the whole chunk (deterministic packets), but ~40% are dropped in transit.
            var packets = ChunkPacketizer.Split(Session, 0, 1, ciphertext, proof, 1200);
            foreach (var packet in packets)
            {
                if (rng.NextDouble() < 0.40)
                    continue; // dropped this round
                completed = assembler.Offer(packet);
                if (completed is not null)
                    break; // real receivers stop feeding a chunk once it completes (bitmap guard)
            }
        }

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
        Assert.Equal(0, assembler.PendingChunkCount); // buffer released on completion
    }

    [Fact]
    public void ChunkPacket_RoundTrips_ViaCodec_WithAndWithoutProof()
    {
        var withProof = new ChunkPacketMessage(Session, 3, 4, 0, 6000, Bytes(1100, 1), ProofFor(8, 4));
        var withoutProof = new ChunkPacketMessage(Session, 3, 4, 1100, 6000, Bytes(1100, 2), Proof: null);

        var d0 = (ChunkPacketMessage)MessageCodec.Decode(MessageCodec.Encode(withProof));
        var d1 = (ChunkPacketMessage)MessageCodec.Decode(MessageCodec.Encode(withoutProof));

        Assert.Equal(withProof.Fragment, d0.Fragment);
        Assert.Equal(withProof.CiphertextLength, d0.CiphertextLength);
        Assert.NotNull(d0.Proof);
        Assert.Equal(withProof.Proof!.LeafIndex, d0.Proof!.LeafIndex);
        Assert.Equal(withProof.Proof.LeafCount, d0.Proof.LeafCount);
        Assert.Equal(withProof.Proof.Steps, d0.Proof.Steps); // MerkleProofStep is a value type => element-wise
        Assert.Null(d1.Proof);
        Assert.Equal(withoutProof.Fragment, d1.Fragment);
    }

    [Fact]
    public void Offer_HugeCiphertextLength_IsRejected_WithoutAllocating()
    {
        // A gigantic claimed ciphertext length, which sizes the partial's buffer. Rejected up front against the
        // per-session ceiling. This is now the ONLY wire field that sizes an allocation: the claimed-packet-count
        // vector (`new byte[PacketCount][]`, ~152 MB at 20,000,000 and an uncaught OOM at int.MaxValue) is gone
        // with the field, not merely bounded.
        var assembler = new ChunkPacketAssembler();

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex = Record.Exception(() => result =
            assembler.Offer(new ChunkPacketMessage(Session, 0, 0, FragmentOffset: 0, CiphertextLength: int.MaxValue, Bytes(1, 1), ProofFor(8, 0))));

        Assert.Null(ex);
        Assert.Null(result);
        Assert.Equal(0, assembler.PendingChunkCount);
    }

    [Fact]
    public void Offer_CiphertextLengthOverTheTightManifestBound_IsRejected()
    {
        // Mirrors how ReceiverSession bounds the assembler to the transfer's known chunk size: a claim that is
        // internally consistent but larger than any chunk this transfer can contain is still rejected, so the
        // per-partial allocation is capped at what a legitimate chunk of THIS transfer costs.
        var assembler = new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(8192));

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex = Record.Exception(() => result =
            assembler.Offer(new ChunkPacketMessage(Session, 0, 0, FragmentOffset: 0, CiphertextLength: 16_000_000, Bytes(1, 1), ProofFor(8, 0))));

        Assert.Null(ex);
        Assert.Null(result);
        Assert.Equal(0, assembler.PendingChunkCount);
    }

    [Theory]
    [InlineData(-1, 10)]        // negative offset
    [InlineData(200, 10)]       // starts past the end
    [InlineData(195, 10)]       // starts inside but runs past the end
    [InlineData(int.MaxValue, 1)]
    public void Offer_FragmentOutsideTheClaimedCiphertext_IsRejected(int offset, int fragmentLength)
    {
        // Offset keying moves the attacker's lever from "how many packets" to "where does this one go", so the
        // placement itself is what must be bounded: every byte written has to land inside [0, CiphertextLength).
        var assembler = new ChunkPacketAssembler();

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex = Record.Exception(() => result = assembler.Offer(new ChunkPacketMessage(
            Session, 0, 0, FragmentOffset: offset, CiphertextLength: 200, Bytes(fragmentLength, 1), ProofFor(8, 0))));

        Assert.Null(ex);                              // dropped safely, never thrown out of the receive loop
        Assert.Null(result);
        Assert.Equal(0, assembler.PendingChunkCount); // and nothing was buffered
    }

    [Fact]
    public void Offer_ScatteredTinyFragments_CannotFragmentCoverageWithoutBound()
    {
        // The resource guard that replaced the packet-count bound. A peer sending one-byte fragments at
        // deliberately non-adjacent offsets would otherwise grow the coverage list one entry per datagram; the
        // minFragmentBytes-derived cap stops it, and the cap is expressed in the same units the old bound was.
        int ciphertextLength = 262_160;
        var assembler = new ChunkPacketAssembler(
            ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144),
            minFragmentBytes: ChunkPacketAssembler.MinFragmentBytesFor(WirePacketizer.DefaultMaxDatagramPayload));
        int cap = (ciphertextLength + ChunkPacketAssembler.MinFragmentBytesFor(WirePacketizer.DefaultMaxDatagramPayload) - 1)
            / ChunkPacketAssembler.MinFragmentBytesFor(WirePacketizer.DefaultMaxDatagramPayload) + 2;

        // Every third byte, so no two fragments are ever adjacent and each would open its own range.
        for (int offset = 0; offset + 1 <= ciphertextLength; offset += 3)
        {
            var ex = Record.Exception(() => assembler.Offer(new ChunkPacketMessage(
                Session, 0, 0, FragmentOffset: offset, ciphertextLength, Bytes(1, offset), offset == 0 ? ProofFor(512, 0) : null)));
            Assert.Null(ex);
        }

        Assert.Equal(1, assembler.PendingChunkCount);
        Assert.True(assembler.CoverageRangeCount(0, 0) <= cap,
            $"coverage fragmented to {assembler.CoverageRangeCount(0, 0)} ranges, above the cap of {cap}");
    }

    [Fact]
    public void PendingChunkCap_BoundsConcurrentIncompleteChunks()
    {
        // An attacker can also open many pending chunks, each individually in-bounds, to exhaust memory. Cap the
        // number of distinct concurrent incomplete chunks (oldest evicted), mirroring PacketReassembler's group cap.
        var assembler = new ChunkPacketAssembler(maxPendingChunks: 4);

        for (int chunk = 0; chunk < 20; chunk++)
        {
            // Only the first packet of each distinct chunk => each stays incomplete and buffered.
            var packets = ChunkPacketizer.Split(Session, 0, chunk, Bytes(5000, 200 + chunk), ProofFor(16, chunk % 16), 1200);
            Assert.Null(assembler.Offer(packets[0]));
        }

        Assert.True(assembler.PendingChunkCount <= 4, $"pending {assembler.PendingChunkCount} exceeded cap");
    }

    [Fact]
    public void Offer_PeerRelayingOnAMuchSmallerDatagramBudget_IsStillAccepted()
    {
        // Guards the other direction: the coverage-fragmentation bound must not be so tight that it breaks a peer
        // relaying a chunk it sliced on a smaller datagram budget. 600 bytes is well under half the shipped budget
        // and yields ~470 packets for a 256 KiB chunk, against a cap of ~1,427 derived from the shipped budget —
        // i.e. the deliberate 8x tolerance in MinFragmentBytesFor is doing real work, not just sitting there.
        var ciphertext = Bytes(262_160, seed: 7);
        var proof = ProofFor(512, 0);
        var packets = ChunkPacketizer.Split(Session, 0, 0, ciphertext, proof, maxDatagramPayload: 600);

        var assembler = new ChunkPacketAssembler(
            ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144),
            minFragmentBytes: ChunkPacketAssembler.MinFragmentBytesFor(WirePacketizer.DefaultMaxDatagramPayload));

        Assert.True(packets.Count > 400, $"fixture must produce a many-packet split, got {packets.Count}");
        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        foreach (var packet in packets)
            completed ??= assembler.Offer(packet);

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
    }

    // ---- M9: proof space is reserved on packet 0 only ----

    /// <summary>
    /// ChunkPacketizer.FixedEnvelopeOverhead, restated here because it is internal to Castr.Core:
    /// version(1)+type(1)+sessionId(16)+fileIndex(4)+chunkIndex(4)+fragmentOffset(4)
    /// +ciphertextLength(4)+fragment length prefix(4)+proof-present flag(1) = <b>39</b>. (Was 43 while the message
    /// carried packetIndex AND packetCount; offset keying replaced both with one field. Its own comment in
    /// Castr.Core said 47 until M9; the encoded sizes asserted below are what settle it either way.)
    /// </summary>
    private const int EnvelopeOverhead = 39;

    /// <summary>
    /// The pre-M11 envelope, kept so <see cref="SplitTheOldUniformWay"/> reproduces the exact fragment shape the
    /// M8 sniffer capture counted (309 packets of 850 bytes for a 256 KiB chunk at a 1200-byte budget). It models
    /// a differently-sliced source, which is the only thing those tests need it for.
    /// </summary>
    private const int LegacyEnvelopeOverhead = 43;

    private static int ProofBytes(MerkleProof proof) => 4 + 4 + 2 + (proof.Steps.Length * (ChunkHash.Size + 1));

    [Theory]
    [InlineData(1200)]
    [InlineData(WirePacketizer.DefaultMaxDatagramPayload)]
    public void Split_ReservesProofSpaceOnPacketZeroOnly(int budget)
    {
        // The defect this locks down: every packet used to be sized against packet 0's proof-carrying envelope,
        // so every packet after the first wasted ProofEncodedSize(proof) bytes of its datagram. Packet 0 is short
        // BECAUSE it carries the proof; every later packet is full.
        var ciphertext = Bytes(262_160, seed: 11);
        var proof = ProofFor(400, 7);                       // 100 MiB at 256 KiB chunks => depth 9 => 307-byte proof
        Assert.Equal(307, ProofBytes(proof));                // the fixture is the shipped configuration, not a guess

        var packets = ChunkPacketizer.Split(Session, 0, 7, ciphertext, proof, budget);

        Assert.Equal(budget - EnvelopeOverhead - ProofBytes(proof), packets[0].Fragment.Length);
        Assert.Equal(budget, MessageCodec.Encode(packets[0]).Length);   // packet 0 is exactly full, proof included
        foreach (var packet in packets.Skip(1).SkipLast(1))
        {
            Assert.Equal(budget - EnvelopeOverhead, packet.Fragment.Length);
            Assert.Equal(budget, MessageCodec.Encode(packet).Length);   // no proof reservation => exactly full
        }
        Assert.All(packets, p => Assert.True(MessageCodec.Encode(p).Length <= budget, "packet exceeds datagram budget"));
        Assert.Equal(ciphertext.Length, packets.Sum(p => p.Fragment.Length));
    }

    [Theory]
    // The predictions this stage was commissioned against, derived from the code and asserted here so a future
    // change to either the envelope or the budget has to restate them:
    //   packet 0 = budget − 39 − 307, later = budget − 39, count = 1 + ceil((262160 − first) / later).
    // Both figures survive M11's 4-byte envelope saving unchanged — the extra payload per packet is not quite
    // enough to drop a packet at either budget — so the M9 measurements they anchor still describe shipped code.
    [InlineData(1200, 227)]                                          // old slicing at this budget: 309
    [InlineData(WirePacketizer.DefaultMaxDatagramPayload, 184)]      // shipped: 309 -> 184, a 1.68x reduction
    public void Split_ShippedConfiguration_ProducesThePredictedPacketCount(int budget, int expected)
    {
        var packets = ChunkPacketizer.Split(Session, 0, 0, Bytes(262_160, seed: 12), ProofFor(400, 0), budget);

        Assert.Equal(expected, packets.Count);
    }

    [Fact]
    public void Split_EmptyCiphertext_StillProducesExactlyOneProofCarryingPacket()
    {
        var packets = ChunkPacketizer.Split(Session, 0, 0, [], ProofFor(64, 0), WirePacketizer.DefaultMaxDatagramPayload);

        Assert.Single(packets);
        Assert.Empty(packets[0].Fragment);
        Assert.NotNull(packets[0].Proof);
    }

    [Fact]
    public void Reassembly_AcceptsAnOldStyleUniformlySlicedChunk_Unchanged()
    {
        // The claim that makes this change wire-compatible, tested rather than asserted: ChunkPacketAssembler
        // sums each fragment's ACTUAL length against CiphertextLength and never assumed uniform fragments, so a
        // chunk sliced the OLD way (every packet sized against packet 0's proof envelope) still reassembles
        // byte-exactly on a receiver running the new code.
        var ciphertext = Bytes(262_160, seed: 13);
        var proof = ProofFor(400, 3);
        var oldStyle = SplitTheOldUniformWay(ciphertext, proof, maxDatagramPayload: 1200); // the pre-M9 shipped pair

        Assert.Equal(309, oldStyle.Count);   // 850-byte fragments: the exact shape the M8 sniffer table counted
        var assembler = new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144));
        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        foreach (var packet in oldStyle)
            completed ??= assembler.Offer(packet);

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
        Assert.Equal(proof, completed.Value.Proof);
    }

    // ---- M11: fragments are keyed by byte offset ----

    [Fact]
    public void Reassembly_CombinesTwoDifferentSlicings_NeitherOfWhichIsCompleteAlone()
    {
        // The headline property offset keying buys, stated so it cannot pass by accident: each source delivers a
        // strict subset of its own slicing, so NEITHER could complete the chunk on its own, and the chunk still
        // reassembles byte-exactly from the union of their byte ranges.
        //
        // Through M9 this was structurally impossible. Packet indices only mean something relative to the slicing
        // that produced them, so the assembler compared PacketCounts and rejected one side outright — and
        // whichever slicing established the buffer first pinned it, which is the stranding QA measured.
        var ciphertext = Bytes(262_160, seed: 15);
        var proof = ProofFor(400, 9);

        var wide = ChunkPacketizer.Split(Session, 0, 9, ciphertext, proof, WirePacketizer.DefaultMaxDatagramPayload);
        var narrow = ChunkPacketizer.Split(Session, 0, 9, ciphertext, proof, maxDatagramPayload: 900);
        Assert.NotEqual(wide.Count, narrow.Count); // genuinely different slicings, not the same one twice

        // Split the chunk in two and let each source cover one side. The sides overlap at the seam by exactly one
        // fragment from each, because their boundaries do not line up — which is the case worth testing.
        int seam = ciphertext.Length / 2;
        var head = wide.Where(p => p.FragmentOffset < seam).ToList();
        var tail = narrow.Where(p => p.FragmentOffset + p.Fragment.Length > seam).ToList();
        Assert.True(head.Count < wide.Count, "the wide source must be delivering a strict subset");
        Assert.True(tail.Count < narrow.Count, "the narrow source must be delivering a strict subset");

        var assembler = new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144));

        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        foreach (var packet in head)
            completed ??= assembler.Offer(packet);
        Assert.Null(completed); // the head alone can never complete the chunk
        Assert.Equal(1, assembler.PendingChunkCount);

        // The narrow-budget peer relays the tail. Its fragments do not line up with the first source's at all —
        // different lengths, different boundaries — but they describe the same bytes of the same chunk.
        foreach (var packet in tail)
            completed ??= assembler.Offer(packet);

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
        Assert.Equal(proof, completed.Value.Proof);
        Assert.Equal(0, assembler.PendingChunkCount);
    }

    [Fact]
    public void Reassembly_ThreeSlicingsInterleaved_ConvergeWithoutAnySourceGettingACleanPass()
    {
        // The M9 contract said two mismatched sources transmitting at once "reset each other's buffer" and the
        // chunk completes only when one of them gets a clean pass. That cost is gone: here three budgets
        // interleave, each dropping two thirds of its own packets, and the union still converges — no single
        // source ever delivers a complete slicing.
        var ciphertext = Bytes(262_160, seed: 14);
        var proof = ProofFor(400, 5);

        var slicings = new[] { WirePacketizer.DefaultMaxDatagramPayload, 1200, 700 }
            .Select(budget => ChunkPacketizer.Split(Session, 0, 5, ciphertext, proof, budget))
            .ToList();

        // One third of the chunk from each source, and no source delivers more than its third. The thirds are
        // measured in BYTES, so each source's own packet boundaries fall wherever its budget puts them.
        int firstSeam = ciphertext.Length / 3;
        int secondSeam = 2 * ciphertext.Length / 3;
        var contributions = new List<IReadOnlyList<ChunkPacketMessage>>
        {
            slicings[0].Where(p => p.FragmentOffset < firstSeam).ToList(),
            slicings[1].Where(p => p.FragmentOffset + p.Fragment.Length > firstSeam && p.FragmentOffset < secondSeam).ToList(),
            slicings[2].Where(p => p.FragmentOffset + p.Fragment.Length > secondSeam).ToList(),
        };
        for (int i = 0; i < 3; i++)
            Assert.True(contributions[i].Count < slicings[i].Count, $"source {i} must deliver a strict subset of its own slicing");

        var assembler = new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144));
        (byte[] Ciphertext, MerkleProof Proof)? completed = null;

        // Interleaved on the wire, so no source ever gets an uninterrupted run.
        int longest = contributions.Max(c => c.Count);
        for (int i = 0; i < longest; i++)
            foreach (var contribution in contributions)
                if (i < contribution.Count)
                    completed ??= assembler.Offer(contribution[i]);

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
        Assert.Equal(proof, completed.Value.Proof);
        Assert.Equal(0, assembler.PendingChunkCount);
    }

    [Fact]
    public void Reassembly_OverlappingFragments_FirstWriterWins_SoAPoisonedByteRangeCannotOverwriteGoodBytes()
    {
        // Overlap is the price of letting slicings mix, so the resolution rule has to be stated. First writer
        // wins: once a byte range is covered, later fragments claiming it are ignored. A hostile peer therefore
        // cannot rewrite bytes a good source already delivered — it can only fill holes nobody has filled yet,
        // which produces a chunk that fails Merkle verification and is dropped whole.
        var ciphertext = Bytes(40_000, seed: 21);
        var proof = ProofFor(64, 3);
        var honest = ChunkPacketizer.Split(Session, 0, 3, ciphertext, proof, WirePacketizer.DefaultMaxDatagramPayload);

        var assembler = new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(40_000));

        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        foreach (var packet in honest.SkipLast(1))
            completed ??= assembler.Offer(packet);
        Assert.Null(completed);

        // A hostile peer re-sends every delivered fragment with the bytes flipped, then fills the one hole.
        foreach (var packet in honest.SkipLast(1))
        {
            var poisoned = packet.Fragment.Select(b => (byte)~b).ToArray();
            completed ??= assembler.Offer(packet with { Fragment = poisoned });
        }
        completed ??= assembler.Offer(honest[^1]);

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext); // the honest bytes survived the overwrite attempt
    }

    /// <summary>
    /// Reproduces the pre-M9 slicing exactly — every fragment sized against packet 0's proof-carrying envelope —
    /// so the tests above can model a peer (or an older build) that still slices that way. Deliberately built on
    /// <see cref="LegacyEnvelopeOverhead"/> rather than the current envelope, so the fragment shape it produces
    /// stays the one the M8 sniffer capture counted.
    /// </summary>
    private static IReadOnlyList<ChunkPacketMessage> SplitTheOldUniformWay(
        byte[] ciphertext, MerkleProof proof, int maxDatagramPayload, int chunkIndex = 0)
    {
        int perPacket = maxDatagramPayload - LegacyEnvelopeOverhead - ProofBytes(proof);
        int count = ciphertext.Length == 0 ? 1 : ((ciphertext.Length + perPacket - 1) / perPacket);

        var packets = new ChunkPacketMessage[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * perPacket;
            int length = Math.Min(perPacket, ciphertext.Length - offset);
            packets[i] = new ChunkPacketMessage(
                Session, 0, chunkIndex, offset, ciphertext.Length,
                ciphertext.AsSpan(offset, length).ToArray(), i == 0 ? proof : null);
        }
        return packets;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
