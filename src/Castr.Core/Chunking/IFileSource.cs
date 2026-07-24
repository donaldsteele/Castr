namespace Castr.Core.Chunking;

/// <summary>Read-only, random-access byte source. Abstracts the filesystem so chunking/hashing logic is unit-testable without disk I/O.</summary>
public interface IFileSource
{
    long Length { get; }

    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
}
