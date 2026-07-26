using Microsoft.Win32.SafeHandles;

namespace Castr.Core.Chunking;

/// <summary>
/// Disk-backed <see cref="IFileSink"/> using <see cref="RandomAccess"/> for thread-safe positional writes.
/// Writes to a `.part` sibling file so a partially-received transfer never masquerades as a complete one;
/// call <see cref="Complete"/> to atomically rename it into place once every chunk has verified.
/// </summary>
/// <remarks>
/// Also an <see cref="IReadableFileSink"/>: the handle is opened <see cref="FileAccess.ReadWrite"/> so the
/// receiver can read plaintext back out of the `.part` file and re-encrypt it to serve a peer a chunk whose
/// ciphertext has been evicted from its bounded cache. Read-back is the only reason the handle is not
/// write-only; nothing in the write path changed.
/// </remarks>
public sealed class FileSystemFileSink : IReadableFileSink, IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _partPath;
    private readonly string _finalPath;
    // Volatile because ReadAsync may run on a different thread from Complete(): the receiver rebuilds evicted
    // chunks off its state gate, while completion happens on the gated packet path. A stale read here is still
    // safe — it means using the just-disposed handle, which throws ObjectDisposedException and is recovered
    // below — but making the flag volatile means the common case does not rely on that recovery.
    private volatile bool _completed;

    public FileSystemFileSink(string finalPath, long expectedLength)
    {
        _finalPath = finalPath;
        _partPath = finalPath + ".part";

        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _handle = File.OpenHandle(_partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, expectedLength);
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        RandomAccess.WriteAsync(_handle, data, offset, cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Before Complete(), read through the live write handle. After it, the handle is gone and the bytes live
        // at _finalPath — open a short-lived read-only view rather than holding a lock on a file the user now
        // owns. Cold reads are rare by construction (they only happen for a chunk a peer asked for that has been
        // evicted), so a per-read open is the right trade against keeping a handle on a completed download.
        if (!_completed)
        {
            try
            {
                return await ReadFullyAsync(_handle, offset, buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Complete() ran between the check above and the read. Fall through to the final path — the
                // bytes still exist, they have just moved.
            }
        }

        using var readHandle = File.OpenHandle(
            _finalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        return await ReadFullyAsync(readHandle, offset, buffer, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadFullyAsync(
        SafeFileHandle handle, long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await RandomAccess.ReadAsync(handle, buffer[total..], offset + total, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    /// <summary>Flushes and atomically moves the `.part` file into its final, receiver-owned destination.</summary>
    public void Complete()
    {
        _handle.Dispose();
        File.Move(_partPath, _finalPath, overwrite: true);
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed)
            _handle.Dispose();
    }
}
