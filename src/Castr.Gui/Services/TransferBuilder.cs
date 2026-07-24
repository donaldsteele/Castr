using System.Security.Cryptography;
using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;

namespace Castr.Gui.Services;

/// <summary>Everything a <see cref="SenderSession"/> needs to serve one file, assembled from disk.</summary>
public sealed record PreparedTransfer(
    SignedManifest Signed,
    IReadOnlyDictionary<int, IFileSource> Sources,
    IReadOnlyDictionary<int, MerkleTree> Trees,
    Key SenderEncryptionKey,
    ContentKey ContentKey)
{
    public SenderSession CreateSession(Castr.Core.Transport.IMulticastTransport transport) =>
        new(Signed, Sources, Trees, transport, SenderEncryptionKey, ContentKey);
}

/// <summary>
/// Builds the signed manifest, per-transfer content key, encrypted-chunk Merkle tree, and file source for a
/// single real file on disk — the sender-side counterpart of what the receiver reconstructs from the wire.
/// The Merkle tree is built over <b>ciphertext</b> chunk hashes (ADR-0003), exactly as Core's own tests do.
/// </summary>
public static class TransferBuilder
{
    public static async Task<PreparedTransfer> PrepareFileAsync(
        string filePath, Key signingKey, int chunkSize, CancellationToken cancellationToken = default)
    {
        var sessionId = RandomNumberGenerator.GetBytes(TransferManifest.SessionIdSize);
        var relativePath = Path.GetFileName(filePath);

        var source = new FileSystemFileSource(filePath);
        var contentKey = ContentKey.Generate();
        var senderEncryptionKey = EncryptionKeys.Create();

        var hashes = await EncryptedChunkHasher
            .ComputeAsync(source, chunkSize, sessionId, fileIndex: 0, contentKey, cancellationToken)
            .ConfigureAwait(false);
        var tree = MerkleTree.Build(hashes);
        int chunkCount = ChunkLayout.ComputeChunkCount(source.Length, chunkSize);

        var manifest = new TransferManifest(
            sessionId,
            relativePath,
            DateTimeOffset.UtcNow,
            EncryptionKeys.ExportPublicKey(senderEncryptionKey),
            [new ManifestFileEntry(relativePath, source.Length, chunkSize, chunkCount, tree.Root)]);
        var signed = ManifestSigner.Sign(manifest, signingKey);

        return new PreparedTransfer(
            signed,
            new Dictionary<int, IFileSource> { [0] = source },
            new Dictionary<int, MerkleTree> { [0] = tree },
            senderEncryptionKey,
            contentKey);
    }
}
