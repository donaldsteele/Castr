using Castr.Core.Chunking;

namespace Castr.Core.Tests.Chunking;

public class ChunkerTests
{
    [Fact]
    public async Task ComputeChunkHashesAsync_ProducesOneHashPerChunk()
    {
        var data = RandomBytes(seed: 1, length: 1000);
        var source = new MemoryFileSource(data);

        var hashes = await Chunker.ComputeChunkHashesAsync(source, chunkSize: 100);

        Assert.Equal(10, hashes.Length);
    }

    [Fact]
    public async Task ComputeChunkHashesAsync_MatchesDirectHashOfEachRange()
    {
        var data = RandomBytes(seed: 2, length: 250);
        var source = new MemoryFileSource(data);

        var hashes = await Chunker.ComputeChunkHashesAsync(source, chunkSize: 100);

        Assert.Equal(ChunkHash.Compute(data.AsSpan(0, 100)), hashes[0]);
        Assert.Equal(ChunkHash.Compute(data.AsSpan(100, 100)), hashes[1]);
        Assert.Equal(ChunkHash.Compute(data.AsSpan(200, 50)), hashes[2]);
    }

    [Fact]
    public async Task ComputeChunkHashesAsync_IsDeterministic()
    {
        var data = RandomBytes(seed: 3, length: 500);

        var hashesA = await Chunker.ComputeChunkHashesAsync(new MemoryFileSource(data), chunkSize: 64);
        var hashesB = await Chunker.ComputeChunkHashesAsync(new MemoryFileSource(data), chunkSize: 64);

        Assert.Equal(hashesA, hashesB);
    }

    [Fact]
    public async Task ComputeChunkHashesAsync_SingleByteDifference_ChangesOnlyThatChunkHash()
    {
        var original = RandomBytes(seed: 4, length: 300);
        var tampered = (byte[])original.Clone();
        tampered[150] ^= 0xFF; // flip a byte inside chunk index 1 (chunk size 100)

        var originalHashes = await Chunker.ComputeChunkHashesAsync(new MemoryFileSource(original), chunkSize: 100);
        var tamperedHashes = await Chunker.ComputeChunkHashesAsync(new MemoryFileSource(tampered), chunkSize: 100);

        Assert.Equal(originalHashes[0], tamperedHashes[0]);
        Assert.NotEqual(originalHashes[1], tamperedHashes[1]);
        Assert.Equal(originalHashes[2], tamperedHashes[2]);
    }

    [Fact]
    public async Task ReadChunkAsync_ReturnsExactBytesForRange()
    {
        var data = RandomBytes(seed: 5, length: 250);
        var source = new MemoryFileSource(data);

        var chunk = await Chunker.ReadChunkAsync(source, chunkSize: 100, index: 2);

        Assert.Equal(data.AsSpan(200, 50).ToArray(), chunk);
    }

    private static byte[] RandomBytes(int seed, int length)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
