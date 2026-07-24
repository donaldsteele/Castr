using Castr.Core.Chunking;

namespace Castr.Core.Manifest;

public sealed record ManifestFileEntry(
    string RelativePath,
    long Size,
    int ChunkSize,
    int ChunkCount,
    ChunkHash MerkleRoot);

/// <summary>
/// The unsigned content of a Castr transfer offer. Only <see cref="ManifestFileEntry.MerkleRoot"/> per file
/// travels here — never a flat per-chunk hash list — so the manifest stays a fixed, cheap size to
/// re-broadcast regardless of file size. Each per-file Merkle root is computed over chunk <b>ciphertext</b>
/// (see wiki/synthesis/adr-0003-payload-encryption.md). <see cref="SenderEncryptionPublicKey"/> is the
/// sender's X25519 public key, carried here (and thus covered by the Ed25519 signature) so a receiver can
/// perform the JOIN_REQUEST/KEY_GRANT handshake without a separate, unauthenticated key exchange. See
/// wiki/concepts/wire-protocol.md.
/// </summary>
public sealed record TransferManifest(
    byte[] SessionId,
    string TransferName,
    DateTimeOffset IssuedAt,
    byte[] SenderEncryptionPublicKey,
    IReadOnlyList<ManifestFileEntry> Files)
{
    public const int SessionIdSize = 16;

    /// <summary>Raw X25519 public key length.</summary>
    public const int EncryptionPublicKeySize = 32;
}
