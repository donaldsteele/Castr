namespace Castr.Core.Chunking;

/// <summary>In-memory <see cref="IFileSink"/> for unit tests. Readable, so the receiver's evicted-chunk cold
/// path (read plaintext back, re-encrypt) is exercised by in-memory tests too, not only by disk-backed ones.</summary>
public sealed class MemoryFileSink(int length) : IReadableFileSink
{
    private readonly byte[] _buffer = new byte[length];

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset + data.Length > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        data.Span.CopyTo(_buffer.AsSpan((int)offset));
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset > _buffer.Length)
            return ValueTask.FromResult(0);

        int count = (int)Math.Min(buffer.Length, _buffer.Length - offset);
        _buffer.AsSpan((int)offset, count).CopyTo(buffer.Span);
        return ValueTask.FromResult(count);
    }

    public byte[] ToArray() => _buffer.ToArray();
}
