using Castr.Core.Chunking;

namespace Castr.Core.Tests.Chunking;

public class ChunkHashTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var data = "castr"u8.ToArray();
        Assert.Equal(ChunkHash.Compute(data), ChunkHash.Compute(data));
    }

    [Fact]
    public void Compute_DifferentInput_DifferentHash()
    {
        Assert.NotEqual(ChunkHash.Compute("a"u8), ChunkHash.Compute("b"u8));
    }

    [Fact]
    public void Compute_ProducesExactly32Bytes()
    {
        Assert.Equal(32, ChunkHash.Compute("castr"u8).AsSpan().Length);
    }

    [Fact]
    public void CombineNodes_IsOrderSensitive()
    {
        var a = ChunkHash.Compute("a"u8);
        var b = ChunkHash.Compute("b"u8);

        Assert.NotEqual(ChunkHash.CombineNodes(a, b), ChunkHash.CombineNodes(b, a));
    }

    [Fact]
    public void CombineNodes_DiffersFromLeafHashOfConcatenatedBytes()
    {
        // Domain separation: combining two leaves must not equal hashing their concatenation directly,
        // or a malicious peer could pass off an internal node as a leaf (a second-preimage-style attack).
        var a = ChunkHash.Compute("a"u8);
        var b = ChunkHash.Compute("b"u8);

        var combined = ChunkHash.CombineNodes(a, b);
        var naiveConcat = ChunkHash.Compute([.. a.ToArray(), .. b.ToArray()]);

        Assert.NotEqual(combined, naiveConcat);
    }

    [Fact]
    public void Constructor_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ChunkHash(new byte[16]));
    }

    [Fact]
    public void ToString_IsLowercaseHex()
    {
        var hex = ChunkHash.Compute("castr"u8).ToString();
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex, hex.ToLowerInvariant());
    }
}
