using Castr.Core.Chunking;

namespace Castr.Core.Manifest;

/// <summary>Indicates which side of a combine step the *sibling* hash occupies, relative to the node being proved.</summary>
public enum MerkleSide
{
    Left,
    Right,
}

public readonly record struct MerkleProofStep(ChunkHash SiblingHash, MerkleSide SiblingSide);

/// <summary>An O(log n) inclusion proof: enough to recompute the Merkle root from a single leaf hash without the rest of the tree.</summary>
public sealed record MerkleProof(int LeafIndex, int LeafCount, MerkleProofStep[] Steps)
{
    public static bool Verify(ChunkHash root, ChunkHash leafHash, MerkleProof proof)
    {
        var current = leafHash;
        foreach (var step in proof.Steps)
        {
            current = step.SiblingSide == MerkleSide.Right
                ? ChunkHash.CombineNodes(current, step.SiblingHash)
                : ChunkHash.CombineNodes(step.SiblingHash, current);
        }
        return current == root;
    }
}

/// <summary>
/// A binary Merkle tree over per-chunk BLAKE3 hashes. Odd-length levels duplicate their last node
/// (Bitcoin/CT-style) rather than promoting it unpaired, so tree shape is a pure function of leaf count.
/// Only <see cref="Root"/> needs to travel in the signed manifest; any peer can hand a receiver a chunk
/// plus a <see cref="MerkleProof"/> and the receiver verifies it without trusting the peer.
/// </summary>
public sealed class MerkleTree
{
    private readonly ChunkHash[][] _levels; // [0] = leaves, [^1] = single-element root level

    private MerkleTree(ChunkHash[][] levels) => _levels = levels;

    public ChunkHash Root => _levels[^1][0];

    public int LeafCount => _levels[0].Length;

    public static MerkleTree Build(ReadOnlySpan<ChunkHash> leafHashes)
    {
        if (leafHashes.Length == 0)
            throw new ArgumentException("Cannot build a Merkle tree over zero leaves.", nameof(leafHashes));

        var levels = new List<ChunkHash[]> { leafHashes.ToArray() };
        var current = levels[0];

        while (current.Length > 1)
        {
            var next = new ChunkHash[(current.Length + 1) / 2];
            for (int i = 0; i < next.Length; i++)
            {
                int leftIndex = i * 2;
                int rightIndex = leftIndex + 1;
                var left = current[leftIndex];
                var right = rightIndex < current.Length ? current[rightIndex] : current[leftIndex];
                next[i] = ChunkHash.CombineNodes(left, right);
            }
            levels.Add(next);
            current = next;
        }

        return new MerkleTree(levels.ToArray());
    }

    public MerkleProof GetProof(int leafIndex)
    {
        if (leafIndex < 0 || leafIndex >= LeafCount)
            throw new ArgumentOutOfRangeException(nameof(leafIndex));

        var steps = new List<MerkleProofStep>();
        int index = leafIndex;

        for (int level = 0; level < _levels.Length - 1; level++)
        {
            var currentLevel = _levels[level];
            bool isRightNode = index % 2 == 1;
            int siblingIndex = isRightNode ? index - 1 : index + 1;
            var siblingHash = siblingIndex < currentLevel.Length ? currentLevel[siblingIndex] : currentLevel[index];
            steps.Add(new MerkleProofStep(siblingHash, isRightNode ? MerkleSide.Left : MerkleSide.Right));
            index /= 2;
        }

        return new MerkleProof(leafIndex, LeafCount, steps.ToArray());
    }
}
