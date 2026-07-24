using System.Numerics;

namespace Castr.Core.Protocol;

/// <summary>A receiver's per-file record of which chunks it has verified so far — the basis for both PEER_HAVE announcements and repair planning.</summary>
public sealed class ChunkBitmap
{
    private readonly byte[] _bits;

    public ChunkBitmap(int chunkCount)
    {
        if (chunkCount < 0)
            throw new ArgumentOutOfRangeException(nameof(chunkCount));
        ChunkCount = chunkCount;
        _bits = new byte[(chunkCount + 7) / 8];
    }

    private ChunkBitmap(int chunkCount, byte[] bits)
    {
        ChunkCount = chunkCount;
        _bits = bits;
    }

    public int ChunkCount { get; }

    public static ChunkBitmap FromBytes(int chunkCount, byte[] bytes)
    {
        int expectedLength = (chunkCount + 7) / 8;
        if (bytes.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} bytes for {chunkCount} chunks, got {bytes.Length}.", nameof(bytes));
        return new ChunkBitmap(chunkCount, (byte[])bytes.Clone());
    }

    public bool Get(int index)
    {
        CheckIndex(index);
        return (_bits[index / 8] & (1 << (index % 8))) != 0;
    }

    public void Set(int index)
    {
        CheckIndex(index);
        _bits[index / 8] |= (byte)(1 << (index % 8));
    }

    public int CountSet()
    {
        int count = 0;
        foreach (var b in _bits)
            count += BitOperations.PopCount(b);
        return count;
    }

    public bool IsComplete => CountSet() == ChunkCount;

    public IEnumerable<int> MissingIndices()
    {
        for (int i = 0; i < ChunkCount; i++)
            if (!Get(i))
                yield return i;
    }

    public byte[] ToBytes() => (byte[])_bits.Clone();

    private void CheckIndex(int index)
    {
        if (index < 0 || index >= ChunkCount)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}
