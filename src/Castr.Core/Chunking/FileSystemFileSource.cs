using Microsoft.Win32.SafeHandles;

namespace Castr.Core.Chunking;

/// <summary>Disk-backed <see cref="IFileSource"/> using <see cref="RandomAccess"/> for thread-safe positional reads.</summary>
public sealed class FileSystemFileSource : IFileSource, IDisposable
{
    private readonly SafeFileHandle _handle;

    public FileSystemFileSource(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        RandomAccess.ReadAsync(_handle, buffer, offset, cancellationToken);

    public void Dispose() => _handle.Dispose();
}
