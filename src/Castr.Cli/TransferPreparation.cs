using System.Security.Cryptography;
using NSec.Cryptography;
using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;

namespace Castr.Cli;

/// <summary>Everything a <see cref="SenderSession"/> needs to serve one file, assembled from disk.</summary>
internal sealed record PreparedTransfer(
    SignedManifest Signed,
    IReadOnlyDictionary<int, IFileSource> Sources,
    IReadOnlyDictionary<int, MerkleTree> Trees,
    Key SenderEncryptionKey,
    ContentKey ContentKey) : IDisposable
{
    public SenderSession CreateSession(
        Castr.Core.Transport.IMulticastTransport transport, int sendWindowSize = SenderSession.DefaultSendWindowSize) =>
        new(Signed, Sources, Trees, transport, SenderEncryptionKey, ContentKey,
            // BENCH (temporary M7 instrumentation): MaxDatagramOverride is -1 unless CASTR_BENCH_MAXDGRAM is
            // set, so this is WirePacketizer.DefaultMaxDatagramPayload in every normal run.
            maxDatagramPayloadBytes: Castr.Core.Diagnostics.BenchMetrics.MaxDatagramOverride > 0
                ? Castr.Core.Diagnostics.BenchMetrics.MaxDatagramOverride
                : WirePacketizer.DefaultMaxDatagramPayload,
            sendWindowSize: sendWindowSize);

    public void Dispose()
    {
        foreach (var source in Sources.Values)
            (source as IDisposable)?.Dispose();
        SenderEncryptionKey.Dispose();
        ContentKey.Dispose();
    }
}

/// <summary>
/// Builds the signed manifest, per-transfer content key, encrypted-chunk Merkle tree, and file source for a
/// single real file on disk. The Merkle tree is built over <b>ciphertext</b> chunk hashes (ADR-0003), exactly
/// as Core's own tests do. (Mirrors the GUI's TransferBuilder; the CLI cannot depend on Castr.Gui.)
/// </summary>
internal static class TransferPreparation
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
