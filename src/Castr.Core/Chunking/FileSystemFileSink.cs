using Microsoft.Win32.SafeHandles;

namespace Castr.Core.Chunking;

/// <summary>
/// Disk-backed <see cref="IFileSink"/> using <see cref="RandomAccess"/> for thread-safe positional writes.
/// Writes to a `.part` sibling file so a partially-received transfer never masquerades as a complete one;
/// call <see cref="CompleteAsync"/> to atomically rename it into place once every chunk has verified.
/// </summary>
public sealed class FileSystemFileSink : IFileSink, IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _partPath;
    private readonly string _finalPath;
    private bool _completed;

    public FileSystemFileSink(string finalPath, long expectedLength)
    {
        _finalPath = finalPath;
        _partPath = finalPath + ".part";

        var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _handle = File.OpenHandle(_partPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, expectedLength);
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        RandomAccess.WriteAsync(_handle, data, offset, cancellationToken);

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
