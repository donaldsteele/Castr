namespace Castr.Core.Chunking;

public readonly record struct ChunkRange(int Index, long Offset, int Length);

public static class ChunkLayout
{
    public const int DefaultChunkSize = 262_144; // 256 KiB

    public static int ComputeChunkCount(long fileLength, int chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");
        if (fileLength < 0)
            throw new ArgumentOutOfRangeException(nameof(fileLength), "File length cannot be negative.");

        if (fileLength == 0)
            return 0;

        return checked((int)((fileLength + chunkSize - 1) / chunkSize));
    }

    public static ChunkRange GetRange(long fileLength, int chunkSize, int index)
    {
        int count = ComputeChunkCount(fileLength, chunkSize);
        if (index < 0 || index >= count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index must be in [0, {count}).");

        long offset = (long)index * chunkSize;
        int length = (int)Math.Min(chunkSize, fileLength - offset);
        return new ChunkRange(index, offset, length);
    }

    public static IEnumerable<ChunkRange> EnumerateRanges(long fileLength, int chunkSize)
    {
        int count = ComputeChunkCount(fileLength, chunkSize);
        for (int i = 0; i < count; i++)
            yield return GetRange(fileLength, chunkSize, i);
    }
}
