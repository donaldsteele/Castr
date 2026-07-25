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
        var withProof = new ChunkPacketMessage(Session, 3, 4, 0, 5, 6000, Bytes(1100, 1), ProofFor(8, 4));
        var withoutProof = new ChunkPacketMessage(Session, 3, 4, 1, 5, 6000, Bytes(1100, 2), Proof: null);

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
    public void Offer_HugePacketCount_IsRejected_WithoutAllocating()
    {
        // The QA-found memory-exhaustion vector: a single crafted packet claims a huge PacketCount, which used to
        // size `new byte[PacketCount][]` (20,000,000 => ~152 MB; int.MaxValue => ~16 GB / uncaught OOM) before any
        // trust/crypto check. It must now be dropped cleanly (no throw, no completion, nothing buffered).
        var assembler = new ChunkPacketAssembler();
        var proof = ProofFor(8, 0);

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex152Mb = Record.Exception(() => result =
            assembler.Offer(new ChunkPacketMessage(Session, 0, 0, PacketIndex: 0, PacketCount: 20_000_000, CiphertextLength: 200, Bytes(1, 1), proof)));
        Assert.Null(ex152Mb);
        Assert.Null(result);

        var exMax = Record.Exception(() => result =
            assembler.Offer(new ChunkPacketMessage(Session, 0, 0, PacketIndex: 0, PacketCount: int.MaxValue, CiphertextLength: 200, Bytes(1, 1), proof)));
        Assert.Null(exMax);
        Assert.Null(result);

        Assert.Equal(0, assembler.PendingChunkCount); // nothing was buffered/allocated
    }

    [Fact]
    public void Offer_HugeCiphertextLength_IsRejected_WithoutAllocating()
    {
        // The other half: a modest packet count but a gigantic claimed ciphertext length, which used to size
        // `new byte[CiphertextLength]` on completion. Rejected up front against the per-session ceiling.
        var assembler = new ChunkPacketAssembler();

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex = Record.Exception(() => result =
            assembler.Offer(new ChunkPacketMessage(Session, 0, 0, PacketIndex: 0, PacketCount: 1, CiphertextLength: int.MaxValue, Bytes(1, 1), ProofFor(8, 0))));

        Assert.Null(ex);
        Assert.Null(result);
        Assert.Equal(0, assembler.PendingChunkCount);
    }

    [Fact]
    public void Offer_ConsistentButOversizedMetadata_IsRejected_AgainstTightManifestBound()
    {
        // Mirrors how ReceiverSession bounds the assembler to the transfer's known chunk size: even a
        // self-consistent (PacketCount <= CiphertextLength) but oversized claim is rejected, so an attacker cannot
        // force the 128 MB `new byte[PacketCount][]` worst case that the loose 16 MiB default would still permit.
        var assembler = new ChunkPacketAssembler(ChunkPacketAssembler.CiphertextBoundForChunkSize(8192));

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex = Record.Exception(() => result =
            assembler.Offer(new ChunkPacketMessage(Session, 0, 0, PacketIndex: 0, PacketCount: 16_000_000, CiphertextLength: 16_000_000, Bytes(1, 1), ProofFor(8, 0))));

        Assert.Null(ex);
        Assert.Null(result);
        Assert.Equal(0, assembler.PendingChunkCount);
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
    public void Offer_PacketCountFarAboveAnyLegitimateSplit_IsRejected_EvenWhenCiphertextLengthIsInBounds()
    {
        // The packet-count clause, isolated. This claim passes BOTH older guards — CiphertextLength is exactly a
        // legitimate 256 KiB chunk's ciphertext, and PacketCount <= CiphertextLength — so before the
        // minFragmentBytes bound existed it was accepted and sized `new byte[262160][]`, ~2 MB of references from
        // this one small datagram. Across the pending-chunk cap that is the whole attack.
        var assembler = new ChunkPacketAssembler(
            ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144),
            minFragmentBytes: ChunkPacketAssembler.MinFragmentBytesFor(1200));

        (byte[] Ciphertext, MerkleProof Proof)? result = null;
        var ex = Record.Exception(() => result = assembler.Offer(new ChunkPacketMessage(
            Session, 0, 0, PacketIndex: 0, PacketCount: 262_160, CiphertextLength: 262_160, Bytes(1, 1), ProofFor(512, 0))));

        Assert.Null(ex);           // dropped safely, never thrown out of the receive loop
        Assert.Null(result);
        Assert.Equal(0, assembler.PendingChunkCount); // and nothing was buffered
    }

    [Fact]
    public void Offer_PeerRelayingOnAMuchSmallerDatagramBudget_IsStillAccepted()
    {
        // Guards the other direction: the packet-count bound must not be so tight that it breaks a peer relaying
        // a chunk it sliced on a smaller datagram budget. 600 bytes is half the shipped budget and yields ~760
        // packets for a 256 KiB chunk, against a bound of ~1,749 derived from the 1200-byte budget — i.e. the
        // deliberate 8x tolerance in MinFragmentBytesFor is doing real work, not just sitting there.
        var ciphertext = Bytes(262_160, seed: 7);
        var proof = ProofFor(512, 0);
        var packets = ChunkPacketizer.Split(Session, 0, 0, ciphertext, proof, maxDatagramPayload: 600);

        var assembler = new ChunkPacketAssembler(
            ChunkPacketAssembler.CiphertextBoundForChunkSize(262_144),
            minFragmentBytes: ChunkPacketAssembler.MinFragmentBytesFor(1200));

        Assert.True(packets.Count > 700, $"fixture must produce a many-packet split, got {packets.Count}");
        (byte[] Ciphertext, MerkleProof Proof)? completed = null;
        foreach (var packet in packets)
            completed ??= assembler.Offer(packet);

        Assert.NotNull(completed);
        Assert.Equal(ciphertext, completed!.Value.Ciphertext);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
