namespace Castr.Core.Chunking;

/// <summary>Write-only, random-access byte sink. Abstracts the filesystem so receiver logic is unit-testable without disk I/O.</summary>
public interface IFileSink
{
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IFileSink"/> that can also read back bytes it has already been given. Deliberately a separate,
/// <b>optional</b> interface rather than a widening of <see cref="IFileSink"/>: the write-only contract is the
/// right one for a sink, and an external implementation that cannot read must keep compiling.
///
/// <para><b>Why this exists.</b> <c>ReceiverSession</c> keeps verified chunk ciphertext so it can relay it to
/// peers during repair. Retaining every chunk for the whole transfer is a hard memory wall (a 5 GB transfer
/// retains ~5 GB, all of it on the large-object heap at the 256 KiB default chunk size), so the ciphertext cache
/// is byte-bounded and evicts LRU. The cold path for an evicted chunk is to read the <i>plaintext</i> back from
/// this sink and re-encrypt it: <see cref="Castr.Core.Security.ContentKey.EncryptChunk"/> is a deterministic
/// function of (key, sessionId, fileIndex, chunkIndex, plaintext) — the nonce is
/// <c>fileIndex|chunkIndex|0000</c> and the AAD is <c>sessionId|fileIndex|chunkIndex</c>, both fixed — so the
/// re-encrypted ciphertext is byte-identical to the one that was verified and evicted, and the Merkle proof
/// retained alongside it still applies unchanged.</para>
///
/// <para>A sink that does not implement this simply makes evicted chunks unservable; the receiver degrades to
/// "cannot answer that CHUNK_REQUEST", which the repair protocol already tolerates (the requester falls back to
/// another peer or the sender).</para>
/// </summary>
public interface IReadableFileSink : IFileSink
{
    /// <summary>
    /// Reads back up to <paramref name="buffer"/>.Length bytes previously written at
    /// <paramref name="offset"/>, returning the number of bytes actually read (0 if the range is unavailable).
    /// Must be safe to call concurrently with itself and with <see cref="IFileSink.WriteAsync"/> for
    /// non-overlapping ranges.
    /// </summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
}
