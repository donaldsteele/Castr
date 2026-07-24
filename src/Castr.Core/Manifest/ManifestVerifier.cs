using NSec.Cryptography;

namespace Castr.Core.Manifest;

public static class ManifestVerifier
{
    /// <summary>
    /// Verifies the signature only — it does NOT consult a trust store. Whether <paramref name="signed"/>'s
    /// sender is actually trusted is a separate, policy-level decision (see Castr.Core.Trust).
    /// </summary>
    public static bool VerifySignature(SignedManifest signed)
    {
        try
        {
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, signed.SenderPublicKey, KeyBlobFormat.RawPublicKey);
            var encoded = ManifestCodec.Encode(signed.Manifest);
            return SignatureAlgorithm.Ed25519.Verify(publicKey, encoded, signed.Signature);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Malformed public key / signature bytes (e.g. wrong length from a hostile or buggy peer) — not trusted, not a crash.
            return false;
        }
    }

    /// <summary>Verifies a single chunk against the manifest's per-file Merkle root using its inclusion proof — the mechanism that lets an untrusted peer's repair chunk be trusted without trusting the peer.</summary>
    public static bool VerifyChunk(Chunking.ChunkHash fileRoot, Chunking.ChunkHash chunkHash, MerkleProof proof) =>
        MerkleProof.Verify(fileRoot, chunkHash, proof);
}
