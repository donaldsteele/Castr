using Castr.Core.Chunking;

namespace Castr.Core.Manifest;

/// <summary>
/// Structural bounds every accepted manifest must satisfy, checked once at admission.
///
/// <para><b>Why this is not redundant with the signature.</b> A manifest's fields are covered by the sender's
/// Ed25519 signature, so reaching this code needs a sender the receiver already trusts — which is exactly why
/// the gap was robustness rather than a remote hole, and exactly why it still had to be closed. A trusted
/// sender that is buggy or compromised was previously able to hand over a <c>ChunkSize</c> anywhere in
/// <see cref="int"/>, and the receiver would act on it: a value near <see cref="int.MaxValue"/> made
/// <c>ChunkPacketAssembler.CiphertextBoundForChunkSize</c> overflow to a negative bound and throw
/// <see cref="ArgumentOutOfRangeException"/> straight out of the receive loop (which does not wrap manifest
/// handling), and any large-but-not-overflowing value re-opened the per-chunk allocation ceiling that bound
/// exists to close. Signing something does not make it well-formed.</para>
///
/// <para>The checks are deliberately structural — they say nothing about whether the transfer is one the user
/// wants, only that its own fields agree with each other and stay inside what the rest of the system is built
/// to allocate for.</para>
/// </summary>
public static class ManifestLimits
{
    /// <summary>
    /// Largest accepted chunk size, 16 MiB. Matches the CLI's advertised <c>--chunk-size</c> ceiling and the
    /// bound <c>ChunkPacketAssembler.DefaultMaxCiphertextLength</c> is derived from, so a manifest can never ask
    /// a receiver to size a reassembly buffer larger than one the CLI would let an operator produce.
    /// </summary>
    public const int MaxChunkSize = 16 * 1024 * 1024;

    /// <summary>True when every file entry is self-consistent and within bounds.</summary>
    public static bool IsWellFormed(TransferManifest manifest) => Validate(manifest) is null;

    /// <summary>Returns the first structural problem with <paramref name="manifest"/>, or null if there is none.</summary>
    public static string? Validate(TransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SessionId.Length != TransferManifest.SessionIdSize)
            return $"session id must be {TransferManifest.SessionIdSize} bytes, got {manifest.SessionId.Length}.";

        for (int i = 0; i < manifest.Files.Count; i++)
        {
            var file = manifest.Files[i];

            if (string.IsNullOrEmpty(file.RelativePath))
                return $"file {i} has an empty path.";
            if (file.Size < 0)
                return $"file {i} ('{file.RelativePath}') has a negative size ({file.Size}).";
            if (file.ChunkSize <= 0 || file.ChunkSize > MaxChunkSize)
                return $"file {i} ('{file.RelativePath}') has chunk size {file.ChunkSize}, outside [1, {MaxChunkSize}].";
            if (file.ChunkCount < 0)
                return $"file {i} ('{file.RelativePath}') has a negative chunk count ({file.ChunkCount}).";

            // Computed the long way rather than through ChunkLayout.ComputeChunkCount, which throws OverflowException
            // on a (size, chunkSize) pair whose count exceeds int — and a manifest that produced one would be
            // rejected here anyway, so throwing to find that out would be the wrong shape.
            long expected = file.Size == 0 ? 0 : (file.Size + file.ChunkSize - 1) / file.ChunkSize;
            if (expected != file.ChunkCount)
                return $"file {i} ('{file.RelativePath}') claims {file.ChunkCount} chunks; {file.Size} bytes at {file.ChunkSize} is {expected}.";
        }

        return null;
    }
}
