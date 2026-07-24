using System.Buffers.Binary;
using System.Text;
using Castr.Core.Chunking;

namespace Castr.Core.Manifest;

/// <summary>
/// Fixed, deterministic binary encoding for <see cref="TransferManifest"/> — not JSON. JSON's whitespace,
/// key-ordering, and number-formatting ambiguity make byte-for-byte signature stability fragile; this
/// codec has exactly one valid encoding per manifest, which is what a signature scheme requires.
/// </summary>
public static class ManifestCodec
{
    private const byte FormatVersion = 1;

    public static byte[] Encode(TransferManifest manifest)
    {
        if (manifest.SessionId.Length != TransferManifest.SessionIdSize)
            throw new ArgumentException($"SessionId must be exactly {TransferManifest.SessionIdSize} bytes.", nameof(manifest));
        if (manifest.SenderEncryptionPublicKey.Length != TransferManifest.EncryptionPublicKeySize)
            throw new ArgumentException($"SenderEncryptionPublicKey must be exactly {TransferManifest.EncryptionPublicKeySize} bytes.", nameof(manifest));

        using var stream = new MemoryStream();

        stream.WriteByte(FormatVersion);
        stream.Write(manifest.SessionId);
        WriteString(stream, manifest.TransferName);
        WriteInt64(stream, manifest.IssuedAt.ToUnixTimeSeconds());
        stream.Write(manifest.SenderEncryptionPublicKey);
        WriteUInt16(stream, checked((ushort)manifest.Files.Count));

        foreach (var file in manifest.Files)
        {
            WriteString(stream, file.RelativePath);
            WriteInt64(stream, file.Size);
            WriteInt32(stream, file.ChunkSize);
            WriteInt32(stream, file.ChunkCount);
            stream.Write(file.MerkleRoot.AsSpan());
        }

        return stream.ToArray();
    }

    public static TransferManifest Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new SpanReader(bytes);

        byte version = reader.ReadByte();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported manifest format version {version}.");

        var sessionId = reader.ReadBytes(TransferManifest.SessionIdSize).ToArray();
        string transferName = reader.ReadString();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
        var senderEncryptionPublicKey = reader.ReadBytes(TransferManifest.EncryptionPublicKeySize).ToArray();
        ushort fileCount = reader.ReadUInt16();

        var files = new ManifestFileEntry[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            string relativePath = reader.ReadString();
            long size = reader.ReadInt64();
            int chunkSize = reader.ReadInt32();
            int chunkCount = reader.ReadInt32();
            var root = new ChunkHash(reader.ReadBytes(ChunkHash.Size));
            files[i] = new ManifestFileEntry(relativePath, size, chunkSize, chunkCount, root);
        }

        return new TransferManifest(sessionId, transferName, issuedAt, senderEncryptionPublicKey, files);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;

        public byte ReadByte()
        {
            byte value = _remaining[0];
            _remaining = _remaining[1..];
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            var slice = _remaining[..count];
            _remaining = _remaining[count..];
            return slice;
        }

        public ushort ReadUInt16()
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(_remaining);
            _remaining = _remaining[2..];
            return value;
        }

        public int ReadInt32()
        {
            int value = BinaryPrimitives.ReadInt32BigEndian(_remaining);
            _remaining = _remaining[4..];
            return value;
        }

        public long ReadInt64()
        {
            long value = BinaryPrimitives.ReadInt64BigEndian(_remaining);
            _remaining = _remaining[8..];
            return value;
        }

        public string ReadString()
        {
            ushort length = ReadUInt16();
            var bytes = ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
