namespace Castr.Core.Chunking;

public static class Chunker
{
    /// <summary>Reads every chunk of <paramref name="source"/> and computes its BLAKE3 leaf hash, in order.</summary>
    public static async Task<ChunkHash[]> ComputeChunkHashesAsync(
        IFileSource source, int chunkSize, CancellationToken cancellationToken = default)
    {
        int count = ChunkLayout.ComputeChunkCount(source.Length, chunkSize);
        var hashes = new ChunkHash[count];
        var buffer = new byte[chunkSize];

        foreach (var range in ChunkLayout.EnumerateRanges(source.Length, chunkSize))
        {
            var slice = buffer.AsMemory(0, range.Length);
            int read = await ReadFullyAsync(source, range.Offset, slice, cancellationToken).ConfigureAwait(false);
            if (read != range.Length)
                throw new InvalidOperationException($"Expected to read {range.Length} bytes at offset {range.Offset}, got {read}.");

            hashes[range.Index] = ChunkHash.Compute(slice.Span);
        }

        return hashes;
    }

    /// <summary>Reads a single chunk's raw bytes from <paramref name="source"/>.</summary>
    public static async Task<byte[]> ReadChunkAsync(
        IFileSource source, int chunkSize, int index, CancellationToken cancellationToken = default)
    {
        var range = ChunkLayout.GetRange(source.Length, chunkSize, index);
        var buffer = new byte[range.Length];
        int read = await ReadFullyAsync(source, range.Offset, buffer, cancellationToken).ConfigureAwait(false);
        if (read != range.Length)
            throw new InvalidOperationException($"Expected to read {range.Length} bytes at offset {range.Offset}, got {read}.");

        return buffer;
    }

    private static async ValueTask<int> ReadFullyAsync(
        IFileSource source, long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await source.ReadAsync(offset + totalRead, buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }
        return totalRead;
    }
}
