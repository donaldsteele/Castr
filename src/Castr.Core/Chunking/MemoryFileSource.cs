namespace Castr.Core.Chunking;

/// <summary>In-memory <see cref="IFileSource"/> for unit tests.</summary>
public sealed class MemoryFileSource(byte[] data) : IFileSource
{
    public long Length { get; } = data.Length;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset > data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int available = (int)Math.Min(buffer.Length, data.Length - offset);
        data.AsSpan((int)offset, available).CopyTo(buffer.Span);
        return ValueTask.FromResult(available);
    }
}
