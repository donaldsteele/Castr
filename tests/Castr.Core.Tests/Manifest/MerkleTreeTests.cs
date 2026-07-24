using Castr.Core.Chunking;
using Castr.Core.Manifest;

namespace Castr.Core.Tests.Manifest;

public class MerkleTreeTests
{
    [Fact]
    public void Build_ZeroLeaves_Throws()
    {
        Assert.Throws<ArgumentException>(() => MerkleTree.Build([]));
    }

    [Fact]
    public void Build_SingleLeaf_RootEqualsLeaf()
    {
        var leaf = ChunkHash.Compute("only chunk"u8);
        var tree = MerkleTree.Build([leaf]);

        Assert.Equal(leaf, tree.Root);
        Assert.Equal(1, tree.LeafCount);
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var leaves = MakeLeaves(7);

        var rootA = MerkleTree.Build(leaves).Root;
        var rootB = MerkleTree.Build(leaves).Root;

        Assert.Equal(rootA, rootB);
    }

    [Fact]
    public void Build_DifferentLeafOrder_DifferentRoot()
    {
        var leaves = MakeLeaves(4);
        var reordered = (ChunkHash[])leaves.Clone();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        Assert.NotEqual(MerkleTree.Build(leaves).Root, MerkleTree.Build(reordered).Root);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(100)]
    public void GetProof_EveryLeaf_VerifiesAgainstRoot(int leafCount)
    {
        var leaves = MakeLeaves(leafCount);
        var tree = MerkleTree.Build(leaves);

        for (int i = 0; i < leafCount; i++)
        {
            var proof = tree.GetProof(i);
            Assert.True(MerkleProof.Verify(tree.Root, leaves[i], proof), $"leaf {i} of {leafCount} failed to verify");
        }
    }

    [Fact]
    public void Verify_TamperedLeaf_Fails()
    {
        var leaves = MakeLeaves(9);
        var tree = MerkleTree.Build(leaves);
        var proof = tree.GetProof(3);

        var tamperedLeaf = ChunkHash.Compute("not the real chunk"u8);

        Assert.False(MerkleProof.Verify(tree.Root, tamperedLeaf, proof));
    }

    [Fact]
    public void Verify_LeafAndProofFromWrongTree_FailsAgainstOtherRoot()
    {
        var leavesA = MakeLeaves(5);
        var treeA = MerkleTree.Build(leavesA);
        var treeB = MerkleTree.Build(MakeLeaves(5, seedOffset: 1000));

        var proofFromA = treeA.GetProof(2);

        // A malicious/wrong peer's chunk (and its valid proof against ITS OWN tree A) must not verify
        // against a different signed manifest's root (tree B) — trust is anchored to the root, not the peer.
        Assert.False(MerkleProof.Verify(treeB.Root, leavesA[2], proofFromA));
    }

    [Fact]
    public void GetProof_OutOfRangeIndex_Throws()
    {
        var tree = MerkleTree.Build(MakeLeaves(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.GetProof(3));
    }

    private static ChunkHash[] MakeLeaves(int count, int seedOffset = 0)
    {
        var leaves = new ChunkHash[count];
        for (int i = 0; i < count; i++)
            leaves[i] = ChunkHash.Compute(BitConverter.GetBytes(i + seedOffset));
        return leaves;
    }
}
