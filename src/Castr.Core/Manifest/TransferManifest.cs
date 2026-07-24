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
/// re-broadcast regardless of file size. See wiki/concepts/wire-protocol.md.
/// </summary>
public sealed record TransferManifest(
    byte[] SessionId,
    string TransferName,
    DateTimeOffset IssuedAt,
    IReadOnlyList<ManifestFileEntry> Files)
{
    public const int SessionIdSize = 16;
}
