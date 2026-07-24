namespace Castr.Core.Chunking;

/// <summary>Write-only, random-access byte sink. Abstracts the filesystem so receiver logic is unit-testable without disk I/O.</summary>
public interface IFileSink
{
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
