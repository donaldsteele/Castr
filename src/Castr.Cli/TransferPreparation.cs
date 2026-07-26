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
        Castr.Core.Transport.IMulticastTransport transport,
        int sendWindowSize = SenderSession.DefaultSendWindowSize,
        int datagramSize = WirePacketizer.DefaultMaxDatagramPayload) =>
        new(Signed, Sources, Trees, transport, SenderEncryptionKey, ContentKey,
            maxDatagramPayloadBytes: datagramSize, sendWindowSize: sendWindowSize);

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
    /// <summary>
    /// Thrown when <c>--datagram-size</c> and <c>--chunk-size</c> are individually legal but jointly impossible for
    /// this file: the Merkle proof for its chunk count does not leave packet 0 a single payload byte. Named after
    /// both options because either one can be the culprit, and the file's size is the third term.
    /// </summary>
    internal sealed class DatagramBudgetTooSmallException(int required, int configured, int chunkCount)
        : Exception(
            $"--datagram-size {configured} is too small for this transfer: at --chunk-size granularity this file is " +
            $"{chunkCount} chunks, whose Merkle proof needs at least {required} bytes of datagram to travel with the " +
            $"first packet of a chunk. Raise --datagram-size to at least {required}, or raise --chunk-size (fewer, " +
            $"larger chunks mean a shallower tree and a smaller proof).");

    public static async Task<PreparedTransfer> PrepareFileAsync(
        string filePath, Key signingKey, int chunkSize, CancellationToken cancellationToken = default)
        => await PrepareFileAsync(
            filePath, signingKey, chunkSize, WirePacketizer.DefaultMaxDatagramPayload, cancellationToken).ConfigureAwait(false);

    public static async Task<PreparedTransfer> PrepareFileAsync(
        string filePath, Key signingKey, int chunkSize, int maxDatagramPayloadBytes, CancellationToken cancellationToken = default)
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

        // Fail here, before a single datagram is sent, on a (datagram-size, chunk-size, file-size) combination that
        // ChunkPacketizer.Split would otherwise reject *mid-carousel*. The budget cannot be validated on its own:
        // proof size grows with chunk count, so this is the first point at which all three terms are known. Every
        // proof in a tree has the same step count (MerkleTree.GetProof walks every level), so leaf 0's proof is the
        // exact worst case, not a sample.
        int requiredBudget = ChunkPacketizer.MinDatagramPayloadFor(tree.GetProof(0));
        if (maxDatagramPayloadBytes < requiredBudget)
        {
            source.Dispose();
            contentKey.Dispose();
            senderEncryptionKey.Dispose();
            throw new DatagramBudgetTooSmallException(requiredBudget, maxDatagramPayloadBytes, chunkCount);
        }

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
