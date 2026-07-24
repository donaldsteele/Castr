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
    // +ciphertextLength(4)+fragment length prefix(4)+proof-present flag(1) = 47.
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
    /// Splits <paramref name="ciphertext"/> into ordered <see cref="ChunkPacketMessage"/> packets, each of which
    /// encodes to at most <paramref name="maxDatagramPayload"/> bytes. The Merkle <paramref name="proof"/> rides
    /// only on packet 0.
    /// </summary>
    public static IReadOnlyList<ChunkPacketMessage> Split(
        byte[] sessionId, int fileIndex, int chunkIndex, byte[] ciphertext, MerkleProof proof, int maxDatagramPayload)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        // Size every fragment against the worst-case (proof-carrying, packet 0) envelope so that even packet 0
        // fits; a few spare bytes on later packets is a negligible cost for uniform, deterministic slicing.
        int perPacket = maxDatagramPayload - FixedEnvelopeOverhead - ProofEncodedSize(proof);
        if (perPacket < 1)
            throw new ArgumentOutOfRangeException(
                nameof(maxDatagramPayload), "Datagram budget too small to carry a chunk packet with its Merkle proof.");

        int count = ciphertext.Length == 0 ? 1 : (ciphertext.Length + perPacket - 1) / perPacket;
        var packets = new ChunkPacketMessage[count];
        for (int i = 0; i < count; i++)
        {
            int offset = i * perPacket;
            int length = Math.Min(perPacket, ciphertext.Length - offset);
            var fragment = new byte[length];
            Array.Copy(ciphertext, offset, fragment, 0, length);
            packets[i] = new ChunkPacketMessage(
                sessionId, fileIndex, chunkIndex, i, count, ciphertext.Length, fragment, i == 0 ? proof : null);
        }
        return packets;
    }
}
