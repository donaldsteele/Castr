namespace Castr.Core.Chunking;

/// <summary>In-memory <see cref="IFileSink"/> for unit tests.</summary>
public sealed class MemoryFileSink(int length) : IFileSink
{
    private readonly byte[] _buffer = new byte[length];

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset + data.Length > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        data.Span.CopyTo(_buffer.AsSpan((int)offset));
        return ValueTask.CompletedTask;
    }

    public byte[] ToArray() => _buffer.ToArray();
}
