using Castr.Core.Protocol;

namespace Castr.Core.Tests.Protocol;

public class ChunkBitmapTests
{
    [Fact]
    public void NewBitmap_AllChunksMissing()
    {
        var bitmap = new ChunkBitmap(10);
        Assert.Equal(0, bitmap.CountSet());
        Assert.False(bitmap.IsComplete);
        Assert.Equal(Enumerable.Range(0, 10), bitmap.MissingIndices());
    }

    [Fact]
    public void Set_MarksIndexPresent_AndReducesMissing()
    {
        var bitmap = new ChunkBitmap(5);
        bitmap.Set(2);

        Assert.True(bitmap.Get(2));
        Assert.False(bitmap.Get(0));
        Assert.Equal(1, bitmap.CountSet());
        Assert.DoesNotContain(2, bitmap.MissingIndices());
    }

    [Fact]
    public void SettingAllIndices_MakesComplete()
    {
        var bitmap = new ChunkBitmap(3);
        bitmap.Set(0);
        bitmap.Set(1);
        bitmap.Set(2);

        Assert.True(bitmap.IsComplete);
        Assert.Empty(bitmap.MissingIndices());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(100)]
    public void ByteLength_RoundsUpToNearestByte(int chunkCount)
    {
        var bitmap = new ChunkBitmap(chunkCount);
        var expectedByteLength = (chunkCount + 7) / 8;

        Assert.Equal(expectedByteLength, bitmap.ToBytes().Length);
    }

    [Fact]
    public void FromBytes_ThenGet_MatchesOriginalBitmap()
    {
        var original = new ChunkBitmap(20);
        original.Set(0);
        original.Set(19);
        original.Set(10);

        var restored = ChunkBitmap.FromBytes(20, original.ToBytes());

        for (int i = 0; i < 20; i++)
            Assert.Equal(original.Get(i), restored.Get(i));
    }

    [Fact]
    public void FromBytes_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => ChunkBitmap.FromBytes(20, new byte[1]));
    }

    [Fact]
    public void Get_OutOfRange_Throws()
    {
        var bitmap = new ChunkBitmap(5);
        Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.Get(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.Get(-1));
    }

    [Fact]
    public void ZeroChunks_IsImmediatelyComplete()
    {
        var bitmap = new ChunkBitmap(0);
        Assert.True(bitmap.IsComplete);
    }
}
