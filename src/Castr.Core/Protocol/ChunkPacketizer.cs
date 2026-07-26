using Castr.Core.Chunking;
using Castr.Core.Manifest;

namespace Castr.Core.Protocol;

/// <summary>
/// Splits a single chunk's encrypted payload into ordered, MTU-safe <see cref="ChunkPacketMessage"/> wire
/// packets, and (via <see cref="ChunkPacketAssembler"/>) reassembles them back into the original ciphertext.
///
/// Pure and deterministic: given the same ciphertext, proof, and datagram budget it always produces the same
/// packets, so the sender's carousel send and any later repair re-send (from the sender or a relaying peer)
/// yield byte-identical packets that a receiver can accumulate across rounds. Packetization is a purely
/// post-encryption transport concern — it slices already-encrypted bytes and never touches the content key,
/// nonce, or AEAD tag; the receiver reassembles the whole ciphertext before the existing Merkle-proof +
/// AEAD-decrypt path runs, exactly as for a chunk that arrived in one datagram.
/// </summary>
public static class ChunkPacketizer
{
    // version(1)+type(1)+sessionId(16)+fileIndex(4)+chunkIndex(4)+packetIndex(4)+packetCount(4)
    // +ciphertextLength(4)+fragment length prefix(4)+proof-present flag(1) = 43.
    // (This comment said "= 47" until M9 — an arithmetic slip in the comment only; the expression below has
    // always summed to 43, and 43 is what the wire shows. Corrected because the wrong figure was propagated into
    // a derived datagram-count prediction, where it disagreed with a sniffer capture that the correct figure
    // reconciles with exactly.)
    internal const int FixedEnvelopeOverhead = 1 + 1 + TransferManifest.SessionIdSize + 4 + 4 + 4 + 4 + 4 + 4 + 1;

    /// <summary>Encoded size of a Merkle proof: leafIndex(4)+leafCount(4)+stepCount(2)+steps*(hash+side).</summary>
    private static int ProofEncodedSize(MerkleProof proof) => 4 + 4 + 2 + proof.Steps.Length * (ChunkHash.Size + 1);

    /// <summary>
    /// True when <paramref name="ciphertextLength"/>'s whole <see cref="ChunkDataMessage"/> envelope would not
    /// fit in a single datagram of <paramref name="maxDatagramPayload"/> bytes and must therefore be packetized.
    /// </summary>
    public static bool RequiresPacketization(int ciphertextLength, MerkleProof proof, int maxDatagramPayload)
    {
        // Whole ChunkDataMessage: version+type+sessionId+fileIndex+chunkIndex + ciphertext(varbytes) + proof.
        int wholeSize = 1 + 1 + TransferManifest.SessionIdSize + 4 + 4 + (4 + ciphertextLength) + ProofEncodedSize(proof);
        return wholeSize > maxDatagramPayload;
    }

    /// <summary>
    /// Bytes of fragment payload packet 0 can carry on a <paramref name="maxDatagramPayload"/>-byte budget: it is
    /// the only packet that carries <paramref name="proof"/>, so it is the only one that pays for it.
    /// </summary>
    internal static int FirstFragmentBytes(MerkleProof proof, int maxDatagramPayload) =>
        maxDatagramPayload - FixedEnvelopeOverhead - ProofEncodedSize(proof);

    /// <summary>
    /// Bytes of fragment payload every packet after packet 0 can carry: the full budget less the fixed envelope,
    /// with no proof reservation. Independent of the proof, and therefore of the file's chunk count.
    /// </summary>
    internal static int LaterFragmentBytes(int maxDatagramPayload) => maxDatagramPayload - FixedEnvelopeOverhead;

    /// <summary>
    /// Smallest datagram budget that can carry <paramref name="proof"/> plus at least one payload byte on packet 0
    /// — i.e. the smallest budget for which <see cref="Split"/> will not throw.
    ///
    /// <para><b>Why this is a public API and not an internal detail.</b> The budget alone cannot be validated: the
    /// proof grows with the file's chunk count, which is <c>fileSize / chunkSize</c>, so whether a given budget
    /// works is a joint property of <c>--datagram-size</c>, <c>--chunk-size</c> and the file. A caller that lets an
    /// operator choose the budget must check it against the transfer it is about to start, <b>before</b> the
    /// carousel begins — otherwise <see cref="Split"/> throws mid-transfer, out of the send loop, on a
    /// configuration that passed every startup check. See <c>Castr.Cli.TransferPreparation</c> for the check.</para>
    ///
    /// <para>Worked example of the reachable failure: <c>--datagram-size 548</c> (the floor) with
    /// <c>--chunk-size 8192</c> on a 1 GB file is 131,072 chunks, a depth-17 tree, a 571-byte proof, and
    /// <c>548 − 43 − 571 = −66</c>. It was unreachable while the budget was pinned at 1200
    /// (<c>1200 − 43 − 571 = 586</c>), which is exactly why exposing the budget is what made it reachable.</para>
    /// </summary>
    public static int MinDatagramPayloadFor(MerkleProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return FixedEnvelopeOverhead + ProofEncodedSize(proof) + 1;
    }

    /// <summary>
    /// Splits <paramref name="ciphertext"/> into ordered <see cref="ChunkPacketMessage"/> packets, each of which
    /// encodes to at most <paramref name="maxDatagramPayload"/> bytes. The Merkle <paramref name="proof"/> rides
    /// only on packet 0.
    /// </summary>
    /// <remarks>
    /// <para><b>Packet 0 is short and every later packet is long, by design.</b> The proof rides only on packet 0,
    /// so only packet 0 reserves room for it. Sizing <i>every</i> fragment against packet 0's proof-carrying
    /// envelope — which this did until the M9 slicing fix — wastes <c>ProofEncodedSize(proof)</c> bytes on every
    /// packet after the first. That waste is not a rounding error at any shipped configuration: at a 256 KiB chunk
    /// and the old 1200-byte budget a 100 MiB file has a 307-byte proof, so 308 of every 309 datagrams per chunk
    /// went out at 893 bytes — 307 bytes of every one of them unused. Uniform slicing bought nothing the assembler
    /// needed (see below), and at the shipped 1472-byte budget the fix takes a 256 KiB chunk from 309 datagrams to
    /// <b>184</b>.</para>
    ///
    /// <para><b>The wire format does not change and neither does the receiver's contract.</b>
    /// <see cref="ChunkPacketAssembler"/> sums each fragment's <i>actual</i> length and validates the total
    /// against <c>CiphertextLength</c>; it never assumed uniform fragment sizes, so an unmodified receiver
    /// reassembles variable-length fragments correctly. What must stay true is <b>determinism</b>: this function
    /// is pure in (ciphertext, proof, budget), so a carousel send and any later repair re-send — from the sender
    /// or from a relaying peer on the same budget — produce byte-identical packets that accumulate across rounds.
    /// </para>
    ///
    /// <para><b>The one real interop consequence, stated plainly:</b> <see cref="ChunkPacketAssembler.Offer"/>
    /// rejects a packet whose <c>PacketCount</c>/<c>CiphertextLength</c> disagree with the first packet seen for
    /// that chunk. Old and new slicing produce different <c>PacketCount</c>s for the same chunk, so a chunk
    /// relayed by a peer still running the old slicing (or any peer on a different datagram budget — a
    /// pre-existing property, not one this change introduced) is dropped <i>per chunk</i>: that peer simply stops
    /// contributing to repair, and the receiver converges from the sender or from a peer that agrees. It
    /// <b>degrades, never corrupts</b> — a mismatched fragment can never be spliced into a chunk, and the
    /// Merkle proof over the reassembled ciphertext is checked regardless. This is why the change is safe to land
    /// without a format-version bump, despite M7's review classifying it as requiring sender+receiver lockstep.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChunkPacketMessage> Split(
        byte[] sessionId, int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof, int maxDatagramPayload)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        int first = FirstFragmentBytes(proof, maxDatagramPayload);
        int later = LaterFragmentBytes(maxDatagramPayload);
        if (first < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxDatagramPayload), "Datagram budget too small to carry a chunk packet with its Merkle proof.");

        // count: packet 0 takes `first` bytes, the remainder is sliced at `later` bytes each. An empty ciphertext
        // still produces exactly one (empty) packet, so the proof always has a carrier.
        int remainder = Math.Max(0, ciphertext.Length - first);
        int count = 1 + (remainder + later - 1) / later;

        var packets = new ChunkPacketMessage[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i == 0 ? 0 : first + ((i - 1) * later);
            int length = Math.Min(i == 0 ? first : later, ciphertext.Length - offset);
            var fragment = new byte[length];
            Array.Copy(ciphertext, offset, fragment, 0, length);
            packets[i] = new ChunkPacketMessage(
                sessionId, fileIndex, chunkIndex, i, count, ciphertext.Length, fragment, i == 0 ? proof : null);
        }
        return packets;
    }
}
