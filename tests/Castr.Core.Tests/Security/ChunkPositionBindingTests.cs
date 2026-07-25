using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Security;

namespace Castr.Core.Tests.Security;

/// <summary>
/// Adversarial coverage for the ADR-0003 "two independent checks" claim: a chunk's Merkle inclusion proof
/// binds its ciphertext to the signed transfer, while the AEAD tag + AAD independently bind that ciphertext
/// to its exact (session, file, chunk) position. Neither substitutes for the other. The existing primitive
/// tests (PayloadEncryptionTests / MerkleTreeTests) check each layer alone; this proves the *composed*
/// defense against a peer that relays a genuine, Merkle-valid chunk under a lied-about position.
/// </summary>
public class ChunkPositionBindingTests
{
    private static readonly byte[] SessionId = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task GenuineCiphertext_RelabeledToWrongPosition_PassesMerkleButFailsAead()
    {
        using var contentKey = ContentKey.Generate();
        var bytes = new byte[5000];
        new Random(99).NextBytes(bytes);
        const int chunkSize = 1000; // 5 chunks
        var source = new MemoryFileSource(bytes);

        var hashes = await EncryptedChunkHasher.ComputeAsync(source, chunkSize, SessionId, fileIndex: 0, contentKey);
        var tree = MerkleTree.Build(hashes);

        // A genuine ciphertext + genuine inclusion proof for chunk index 2 of file 0.
        var range2 = ChunkLayout.GetRange(bytes.Length, chunkSize, 2);
        var plaintext2 = bytes.AsSpan((int)range2.Offset, range2.Length).ToArray();
        var ciphertext2 = contentKey.EncryptChunk(SessionId, fileIndex: 0, chunkIndex: 2, plaintext2);
        var proof2 = tree.GetProof(2);

        // The Merkle proof verifies this ciphertext against the signed root — but it only proves MEMBERSHIP.
        // It is position-agnostic w.r.t. whatever (file, chunk) an attacker CLAIMS in the wire envelope.
        Assert.True(ManifestVerifier.VerifyChunk(tree.Root, ChunkHash.Compute(ciphertext2), proof2));

        // Decrypting at the genuine position recovers the plaintext...
        Assert.Equal(plaintext2, contentKey.TryDecryptChunk(SessionId, fileIndex: 0, chunkIndex: 2, ciphertext2));

        // ...but a peer relaying this genuine, Merkle-valid ciphertext under a DIFFERENT claimed position is
        // caught by the AEAD AAD binding: decryption returns null, so nothing is ever written at that offset.
        // The AEAD — not the Merkle proof — is what stops chunk-position substitution.
        Assert.Null(contentKey.TryDecryptChunk(SessionId, fileIndex: 0, chunkIndex: 4, ciphertext2)); // wrong chunk index
        Assert.Null(contentKey.TryDecryptChunk(SessionId, fileIndex: 1, chunkIndex: 2, ciphertext2)); // wrong file index
    }

    [Fact]
    public void GenuineProof_WithRewrittenLeafIndex_IsRejectedByVerify()
    {
        // MerkleProof.LeafIndex is a plaintext, wire-mutable field. A relaying peer that keeps a genuine chunk's
        // Steps (which still reproduce the real root) but rewrites LeafIndex to match a forged claimed position
        // must NOT verify — otherwise a session's "proof.LeafIndex == claimed index" guard is meaningless and
        // the chunk-index-swap permanent-stall DoS reopens. Verify binds LeafIndex to the Steps' committed
        // position, so the tampered proof is rejected even though its ciphertext and Steps are genuine.
        var leaves = Enumerable.Range(0, 8).Select(i => ChunkHash.Compute([(byte)i])).ToArray();
        var tree = MerkleTree.Build(leaves);
        var genuine = tree.GetProof(2);

        Assert.True(MerkleProof.Verify(tree.Root, leaves[2], genuine));               // genuine proof verifies
        var relabeled = genuine with { LeafIndex = 5 };                               // lie about position only
        Assert.False(MerkleProof.Verify(tree.Root, leaves[2], relabeled));            // ...now rejected
    }
}
