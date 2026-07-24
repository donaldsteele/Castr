using System.Buffers.Binary;
using System.Text;
using Castr.Core.Chunking;
using Castr.Core.Manifest;

namespace Castr.Core.Protocol;

/// <summary>
/// Fixed, deterministic binary encoding for every wire message: [FormatVersion:1][MessageType:1][body].
/// Not JSON, for the same reason as <see cref="ManifestCodec"/> — a wire protocol needs one unambiguous
/// byte layout, not a format with encoding-order/whitespace freedom.
/// </summary>
public static class MessageCodec
{
    private const byte FormatVersion = 1;
    private const int SessionIdSize = TransferManifest.SessionIdSize;
    private const int IdSize = 16;
    private const int PublicKeySize = 32;
    private const int SignatureSize = 64;

    public static byte[] Encode(object message)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(FormatVersion);

        switch (message)
        {
            case AnnounceMessage m:
                stream.WriteByte((byte)MessageType.Announce);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.SenderPublicKey, PublicKeySize);
                stream.Write(m.ManifestDigest.AsSpan());
                WriteString(stream, m.TransferName);
                WriteInt64(stream, m.IssuedAt.ToUnixTimeSeconds());
                break;

            case ManifestMessage m:
                stream.WriteByte((byte)MessageType.Manifest);
                WriteFixed(stream, m.SignedManifest.SenderPublicKey, PublicKeySize);
                WriteFixed(stream, m.SignedManifest.Signature, SignatureSize);
                WriteVarBytes(stream, ManifestCodec.Encode(m.SignedManifest.Manifest));
                break;

            case ChunkDataMessage m:
                stream.WriteByte((byte)MessageType.ChunkData);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteInt32(stream, m.FileIndex);
                WriteInt32(stream, m.ChunkIndex);
                WriteVarBytes(stream, m.Payload);
                WriteMerkleProof(stream, m.Proof);
                break;

            case PeerHaveMessage m:
                stream.WriteByte((byte)MessageType.PeerHave);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.ReceiverId, IdSize);
                WriteInt32(stream, m.FileIndex);
                WriteVarBytes(stream, m.ChunkBitmap);
                WriteString(stream, m.EndpointHost);
                WriteInt32(stream, m.EndpointPort);
                break;

            case ChunkRequestMessage m:
                stream.WriteByte((byte)MessageType.ChunkRequest);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.RequesterId, IdSize);
                WriteFixed(stream, m.RequestNonce, IdSize);
                WriteInt32(stream, m.FileIndex);
                WriteInt32Array(stream, m.ChunkIndices);
                WriteString(stream, m.ReturnHost);
                WriteInt32(stream, m.ReturnPort);
                break;

            case ChunkResponseMessage m:
                stream.WriteByte((byte)MessageType.ChunkResponse);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.RequestNonce, IdSize);
                WriteInt32(stream, m.FileIndex);
                WriteInt32(stream, m.ChunkIndex);
                WriteVarBytes(stream, m.Payload);
                WriteMerkleProof(stream, m.Proof);
                break;

            case TransferCompleteMessage m:
                stream.WriteByte((byte)MessageType.TransferComplete);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.ReceiverId, IdSize);
                stream.WriteByte((byte)m.Outcome);
                break;

            case JoinRequestMessage m:
                stream.WriteByte((byte)MessageType.JoinRequest);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.ReceiverId, IdSize);
                WriteFixed(stream, m.ReceiverEncryptionPublicKey, PublicKeySize);
                break;

            case KeyGrantMessage m:
                stream.WriteByte((byte)MessageType.KeyGrant);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteFixed(stream, m.ReceiverId, IdSize);
                WriteVarBytes(stream, m.WrappedContentKey);
                break;

            case PacketFragmentMessage m:
                stream.WriteByte((byte)MessageType.PacketFragment);
                WriteInt64(stream, m.GroupId);
                WriteInt32(stream, m.PacketIndex);
                WriteInt32(stream, m.PacketCount);
                WriteInt32(stream, m.TotalLength);
                WriteVarBytes(stream, m.Fragment);
                break;

            case ChunkPacketMessage m:
                stream.WriteByte((byte)MessageType.ChunkPacket);
                WriteFixed(stream, m.SessionId, SessionIdSize);
                WriteInt32(stream, m.FileIndex);
                WriteInt32(stream, m.ChunkIndex);
                WriteInt32(stream, m.PacketIndex);
                WriteInt32(stream, m.PacketCount);
                WriteInt32(stream, m.CiphertextLength);
                WriteVarBytes(stream, m.Fragment);
                if (m.Proof is null)
                {
                    stream.WriteByte(0);
                }
                else
                {
                    stream.WriteByte(1);
                    WriteMerkleProof(stream, m.Proof);
                }
                break;

            default:
                throw new ArgumentException($"Unknown message type: {message.GetType()}", nameof(message));
        }

        return stream.ToArray();
    }

    public static object Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new SpanReader(bytes);

        byte version = reader.ReadByte();
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported message format version {version}.");

        var type = (MessageType)reader.ReadByte();
        return type switch
        {
            MessageType.Announce => new AnnounceMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(PublicKeySize).ToArray(),
                new ChunkHash(reader.ReadBytes(ChunkHash.Size)),
                reader.ReadString(),
                DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64())),

            MessageType.Manifest => DecodeManifestMessage(ref reader),

            MessageType.ChunkData => new ChunkDataMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadVarBytes(),
                reader.ReadMerkleProof()),

            MessageType.PeerHave => new PeerHaveMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                reader.ReadInt32(),
                reader.ReadVarBytes(),
                reader.ReadString(),
                reader.ReadInt32()),

            MessageType.ChunkRequest => new ChunkRequestMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                reader.ReadInt32(),
                reader.ReadInt32Array(),
                reader.ReadString(),
                reader.ReadInt32()),

            MessageType.ChunkResponse => new ChunkResponseMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadVarBytes(),
                reader.ReadMerkleProof()),

            MessageType.TransferComplete => new TransferCompleteMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                (TransferOutcome)reader.ReadByte()),

            MessageType.JoinRequest => new JoinRequestMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                reader.ReadBytes(PublicKeySize).ToArray()),

            MessageType.KeyGrant => new KeyGrantMessage(
                reader.ReadBytes(SessionIdSize).ToArray(),
                reader.ReadBytes(IdSize).ToArray(),
                reader.ReadVarBytes()),

            MessageType.PacketFragment => new PacketFragmentMessage(
                reader.ReadInt64(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadVarBytes()),

            MessageType.ChunkPacket => DecodeChunkPacketMessage(ref reader),

            _ => throw new InvalidDataException($"Unknown message type tag {(byte)type}."),
        };
    }

    private static ChunkPacketMessage DecodeChunkPacketMessage(ref SpanReader reader)
    {
        var sessionId = reader.ReadBytes(SessionIdSize).ToArray();
        int fileIndex = reader.ReadInt32();
        int chunkIndex = reader.ReadInt32();
        int packetIndex = reader.ReadInt32();
        int packetCount = reader.ReadInt32();
        int ciphertextLength = reader.ReadInt32();
        var fragment = reader.ReadVarBytes();
        MerkleProof? proof = reader.ReadByte() == 1 ? reader.ReadMerkleProof() : null;
        return new ChunkPacketMessage(sessionId, fileIndex, chunkIndex, packetIndex, packetCount, ciphertextLength, fragment, proof);
    }

    private static ManifestMessage DecodeManifestMessage(ref SpanReader reader)
    {
        var publicKey = reader.ReadBytes(PublicKeySize).ToArray();
        var signature = reader.ReadBytes(SignatureSize).ToArray();
        var manifest = ManifestCodec.Decode(reader.ReadVarBytes());
        return new ManifestMessage(new SignedManifest(manifest, publicKey, signature));
    }

    // ---- primitive writers ----

    private static void WriteFixed(Stream stream, byte[] value, int expectedSize)
    {
        if (value.Length != expectedSize)
            throw new ArgumentException($"Expected exactly {expectedSize} bytes, got {value.Length}.");
        stream.Write(value);
    }

    private static void WriteVarBytes(Stream stream, byte[] value)
    {
        Span<byte> lengthBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBuffer, checked((uint)value.Length));
        stream.Write(lengthBuffer);
        stream.Write(value);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteInt32Array(Stream stream, int[] values)
    {
        // UInt32 count prefix, not UInt16: a repair batch (ChunkRequestMessage.ChunkIndices) can
        // legitimately exceed 65,535 entries for a large file requested in bulk from the sender before
        // any peer has announced chunks — a UInt16 cap here overflows in that realistic scenario.
        WriteUInt32(stream, checked((uint)values.Length));
        foreach (var value in values)
            WriteInt32(stream, value);
    }

    private static void WriteMerkleProof(Stream stream, MerkleProof proof)
    {
        WriteInt32(stream, proof.LeafIndex);
        WriteInt32(stream, proof.LeafCount);
        WriteUInt16(stream, checked((ushort)proof.Steps.Length));
        foreach (var step in proof.Steps)
        {
            stream.Write(step.SiblingHash.AsSpan());
            stream.WriteByte((byte)step.SiblingSide);
        }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
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

        public byte[] ReadVarBytes()
        {
            uint length = ReadUInt32();
            return ReadBytes(checked((int)length)).ToArray();
        }

        public ushort ReadUInt16()
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(_remaining);
            _remaining = _remaining[2..];
            return value;
        }

        public uint ReadUInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(_remaining);
            _remaining = _remaining[4..];
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
            return Encoding.UTF8.GetString(ReadBytes(length));
        }

        public int[] ReadInt32Array()
        {
            uint count = ReadUInt32();
            var values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = ReadInt32();
            return values;
        }

        public MerkleProof ReadMerkleProof()
        {
            int leafIndex = ReadInt32();
            int leafCount = ReadInt32();
            ushort stepCount = ReadUInt16();
            var steps = new MerkleProofStep[stepCount];
            for (int i = 0; i < stepCount; i++)
            {
                var hash = new ChunkHash(ReadBytes(ChunkHash.Size));
                var side = (MerkleSide)ReadByte();
                steps[i] = new MerkleProofStep(hash, side);
            }
            return new MerkleProof(leafIndex, leafCount, steps);
        }
    }
}
