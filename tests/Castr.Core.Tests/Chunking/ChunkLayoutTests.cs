using Castr.Core.Chunking;

namespace Castr.Core.Tests.Chunking;

public class ChunkLayoutTests
{
    [Fact]
    public void ComputeChunkCount_EmptyFile_ReturnsZero()
    {
        Assert.Equal(0, ChunkLayout.ComputeChunkCount(0, 100));
    }

    [Theory]
    [InlineData(1, 100, 1)]
    [InlineData(99, 100, 1)]
    [InlineData(100, 100, 1)]
    [InlineData(101, 100, 2)]
    [InlineData(250, 100, 3)]
    public void ComputeChunkCount_MatchesExpected(long fileLength, int chunkSize, int expected)
    {
        Assert.Equal(expected, ChunkLayout.ComputeChunkCount(fileLength, chunkSize));
    }

    [Fact]
    public void ComputeChunkCount_NegativeChunkSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkLayout.ComputeChunkCount(10, 0));
    }

    [Fact]
    public void GetRange_LastChunk_IsShorterThanChunkSize()
    {
        var range = ChunkLayout.GetRange(fileLength: 250, chunkSize: 100, index: 2);
        Assert.Equal(2, range.Index);
        Assert.Equal(200, range.Offset);
        Assert.Equal(50, range.Length);
    }

    [Fact]
    public void GetRange_OutOfBoundsIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkLayout.GetRange(250, 100, 3));
    }

    [Fact]
    public void EnumerateRanges_CoversWholeFileWithoutGapsOrOverlaps()
    {
        const long fileLength = 1_000_003; // deliberately not a multiple of chunk size
        const int chunkSize = 65_536;

        long expectedOffset = 0;
        foreach (var range in ChunkLayout.EnumerateRanges(fileLength, chunkSize))
        {
            Assert.Equal(expectedOffset, range.Offset);
            expectedOffset += range.Length;
        }

        Assert.Equal(fileLength, expectedOffset);
    }
}
