using Castr.Core.Chunking;

namespace Castr.Core.Security;

/// <summary>
/// Computes the per-chunk BLAKE3 leaf hashes a transfer's Merkle tree is built from — over each chunk's
/// <b>ciphertext</b>, not its plaintext (see wiki/synthesis/adr-0003-payload-encryption.md). Encryption is
/// deterministic given the content key and <c>(fileIndex, chunkIndex)</c>, so the ciphertext hashed here is
/// byte-identical to what <c>SenderSession</c> puts on the wire — the Merkle proof therefore verifies the
/// exact ciphertext a receiver (or relaying peer) delivers.
/// </summary>
public static class EncryptedChunkHasher
{
    public static async Task<ChunkHash[]> ComputeAsync(
        IFileSource source, int chunkSize, byte[] sessionId, int fileIndex, ContentKey contentKey, CancellationToken cancellationToken = default)
    {
        int count = ChunkLayout.ComputeChunkCount(source.Length, chunkSize);
        var hashes = new ChunkHash[count];

        foreach (var range in ChunkLayout.EnumerateRanges(source.Length, chunkSize))
        {
            var plaintext = await Chunker.ReadChunkAsync(source, chunkSize, range.Index, cancellationToken).ConfigureAwait(false);
            var ciphertext = contentKey.EncryptChunk(sessionId, fileIndex, range.Index, plaintext);
            hashes[range.Index] = ChunkHash.Compute(ciphertext);
        }

        return hashes;
    }
}
