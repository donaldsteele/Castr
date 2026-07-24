using System.Diagnostics.CodeAnalysis;

namespace Castr.Core.Chunking;

/// <summary>A 32-byte BLAKE3 digest identifying a chunk or a Merkle node.</summary>
public readonly struct ChunkHash : IEquatable<ChunkHash>
{
    public const int Size = 32;

    private readonly byte[] _bytes;

    public ChunkHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"Hash must be exactly {Size} bytes, got {bytes.Length}.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    private ChunkHash(byte[] owned) => _bytes = owned;

    public static ChunkHash Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[Size];
        Blake3.Hasher.Hash(data).AsSpan().CopyTo(buffer);
        return new ChunkHash(buffer.ToArray());
    }

    /// <summary>Combines two child hashes into a parent Merkle-node hash (domain-separated from leaf hashing).</summary>
    public static ChunkHash CombineNodes(ChunkHash left, ChunkHash right)
    {
        Span<byte> input = stackalloc byte[1 + Size + Size];
        input[0] = 0x01; // domain separator: internal node, distinct from leaf hashing (no prefix)
        left.AsSpan().CopyTo(input[1..]);
        right.AsSpan().CopyTo(input[(1 + Size)..]);
        return Compute(input);
    }

    public ReadOnlySpan<byte> AsSpan() => _bytes ?? [];

    public byte[] ToArray() => (_bytes ?? []).ToArray();

    public bool Equals(ChunkHash other) => AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ChunkHash other && Equals(other);

    public override int GetHashCode()
    {
        var span = AsSpan();
        if (span.Length < 4) return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    public override string ToString() => Convert.ToHexStringLower(AsSpan());

    public static bool operator ==(ChunkHash left, ChunkHash right) => left.Equals(right);
    public static bool operator !=(ChunkHash left, ChunkHash right) => !left.Equals(right);
}
