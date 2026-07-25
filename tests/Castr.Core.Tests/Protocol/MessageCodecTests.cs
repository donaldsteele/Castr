using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;

namespace Castr.Core.Tests.Protocol;

public class MessageCodecTests
{
    [Fact]
    public void Announce_RoundTrips()
    {
        var message = new AnnounceMessage(
            Id(16), Id(32), ChunkHash.Compute("digest"u8), "photos.zip", DateTimeOffset.Parse("2026-07-24T12:00:00Z"));

        var decoded = (AnnounceMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.SenderPublicKey, decoded.SenderPublicKey);
        Assert.Equal(message.ManifestDigest, decoded.ManifestDigest);
        Assert.Equal(message.TransferName, decoded.TransferName);
        Assert.Equal(message.IssuedAt.ToUnixTimeSeconds(), decoded.IssuedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void Manifest_RoundTrips_ViaRealSignedManifest()
    {
        using var key = ManifestSigner.CreateSigningKey();
        var manifest = new TransferManifest(
            Id(16), "photos.zip", DateTimeOffset.Parse("2026-07-24T12:00:00Z"), Id(32),
            [new ManifestFileEntry("beach.jpg", 1000, 100, 10, ChunkHash.Compute("root"u8))]);
        var signed = ManifestSigner.Sign(manifest, key);

        var decoded = (ManifestMessage)MessageCodec.Decode(MessageCodec.Encode(new ManifestMessage(signed)));

        Assert.Equal(signed.SenderPublicKey, decoded.SignedManifest.SenderPublicKey);
        Assert.Equal(signed.Signature, decoded.SignedManifest.Signature);
        Assert.Equal(signed.Manifest.TransferName, decoded.SignedManifest.Manifest.TransferName);
        Assert.True(ManifestVerifier.VerifySignature(decoded.SignedManifest));
    }

    [Fact]
    public void ChunkData_RoundTrips_IncludingMerkleProof()
    {
        var leaves = new[] { ChunkHash.Compute("a"u8), ChunkHash.Compute("b"u8), ChunkHash.Compute("c"u8) };
        var tree = MerkleTree.Build(leaves);
        var message = new ChunkDataMessage(Id(16), FileIndex: 0, ChunkIndex: 1, Payload: [1, 2, 3, 4, 5], tree.GetProof(1));

        var decoded = (ChunkDataMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.FileIndex, decoded.FileIndex);
        Assert.Equal(message.ChunkIndex, decoded.ChunkIndex);
        Assert.Equal(message.Payload, decoded.Payload);
        Assert.True(MerkleProof.Verify(tree.Root, leaves[1], decoded.Proof));
    }

    [Fact]
    public void ChunkData_LargePayload_RoundTrips()
    {
        // Default chunk size is 256 KiB — well past a UInt16 length prefix's 65535-byte ceiling,
        // which is exactly why Payload uses a UInt32-prefixed field, not the UInt16 used for strings.
        var payload = new byte[300_000];
        new Random(9).NextBytes(payload);
        var proof = MerkleTree.Build([ChunkHash.Compute(payload)]).GetProof(0);
        var message = new ChunkDataMessage(Id(16), 0, 0, payload, proof);

        var decoded = (ChunkDataMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public void PeerHave_RoundTrips()
    {
        var message = new PeerHaveMessage(Id(16), Id(16), FileIndex: 2, ChunkBitmap: [0b1010_1010, 0b0000_0001], "192.168.1.42", 5599);

        var decoded = (PeerHaveMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.ReceiverId, decoded.ReceiverId);
        Assert.Equal(message.FileIndex, decoded.FileIndex);
        Assert.Equal(message.ChunkBitmap, decoded.ChunkBitmap);
        Assert.Equal(message.EndpointHost, decoded.EndpointHost);
        Assert.Equal(message.EndpointPort, decoded.EndpointPort);
    }

    [Fact]
    public void ChunkRequest_RoundTrips_WithMultipleIndices()
    {
        var message = new ChunkRequestMessage(Id(16), Id(16), Id(16), FileIndex: 0, ChunkIndices: [3, 7, 12, 900], "10.0.0.5", 6000);

        var decoded = (ChunkRequestMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.RequesterId, decoded.RequesterId);
        Assert.Equal(message.RequestNonce, decoded.RequestNonce);
        Assert.Equal(message.ChunkIndices, decoded.ChunkIndices);
        Assert.Equal(message.ReturnHost, decoded.ReturnHost);
        Assert.Equal(message.ReturnPort, decoded.ReturnPort);
    }

    [Fact]
    public void ChunkRequest_EmptyIndices_RoundTrips()
    {
        var message = new ChunkRequestMessage(Id(16), Id(16), Id(16), 0, [], "host", 1);

        var decoded = (ChunkRequestMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Empty(decoded.ChunkIndices);
    }

    [Fact]
    public void ChunkRequest_MoreThan65535Indices_RoundTrips()
    {
        // Regression test: a UInt16 count prefix caps out at 65,535 entries and overflows for any
        // realistic large-file cold-start repair batch (no peers yet, all missing chunks requested from
        // the sender at once). The count prefix must be UInt32, matching Payload's width, not UInt16.
        var indices = Enumerable.Range(0, 70_000).ToArray();
        var message = new ChunkRequestMessage(Id(16), Id(16), Id(16), 0, indices, "sender-host", 9000);

        var decoded = (ChunkRequestMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(indices, decoded.ChunkIndices);
    }

    [Fact]
    public void ChunkResponse_RoundTrips_AndMatchesRequestNonce()
    {
        var nonce = Id(16);
        var proof = MerkleTree.Build([ChunkHash.Compute("x"u8)]).GetProof(0);
        var message = new ChunkResponseMessage(Id(16), nonce, 0, 5, [9, 8, 7], proof);

        var decoded = (ChunkResponseMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(nonce, decoded.RequestNonce);
        Assert.Equal(message.ChunkIndex, decoded.ChunkIndex);
        Assert.Equal(message.Payload, decoded.Payload);
    }

    [Theory]
    [InlineData(TransferOutcome.Completed)]
    [InlineData(TransferOutcome.Failed)]
    public void TransferComplete_RoundTrips(TransferOutcome outcome)
    {
        var message = new TransferCompleteMessage(Id(16), Id(16), outcome);

        var decoded = (TransferCompleteMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(outcome, decoded.Outcome);
    }

    [Fact]
    public void JoinRequest_RoundTrips()
    {
        var message = new JoinRequestMessage(Id(16), Id(16), Id(32));

        var decoded = (JoinRequestMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.ReceiverId, decoded.ReceiverId);
        Assert.Equal(message.ReceiverEncryptionPublicKey, decoded.ReceiverEncryptionPublicKey);
    }

    [Fact]
    public void KeyGrant_RoundTrips()
    {
        var message = new KeyGrantMessage(Id(16), Id(16), Id(48)); // 32-byte key + 16-byte tag

        var decoded = (KeyGrantMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.ReceiverId, decoded.ReceiverId);
        Assert.Equal(message.WrappedContentKey, decoded.WrappedContentKey);
    }

    [Fact]
    public void PacketFragment_RoundTrips()
    {
        var fragment = new byte[1174];
        new Random(3).NextBytes(fragment);
        var message = new PacketFragmentMessage(GroupId: -0x0123456789ABCDEF, PacketIndex: 5, PacketCount: 224, TotalLength: 262_160, Fragment: fragment);

        var decoded = (PacketFragmentMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.GroupId, decoded.GroupId);
        Assert.Equal(message.PacketIndex, decoded.PacketIndex);
        Assert.Equal(message.PacketCount, decoded.PacketCount);
        Assert.Equal(message.TotalLength, decoded.TotalLength);
        Assert.Equal(message.Fragment, decoded.Fragment);
    }

    [Fact]
    public void ManifestRequest_RoundTrips()
    {
        var decoded = MessageCodec.Decode(MessageCodec.Encode(new ManifestRequestMessage()));

        Assert.IsType<ManifestRequestMessage>(decoded);
    }

    [Fact]
    public void ChunkPullRequest_RoundTrips_WithIndices()
    {
        var message = new ChunkPullRequestMessage(Id(16), FileIndex: 3, ChunkIndices: [0, 5, 42, 99_999]);

        var decoded = (ChunkPullRequestMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.FileIndex, decoded.FileIndex);
        Assert.Equal(message.ChunkIndices, decoded.ChunkIndices);
    }

    [Fact]
    public void ChunkPullResponse_Found_RoundTrips_IncludingProof()
    {
        var leaves = new[] { ChunkHash.Compute("a"u8), ChunkHash.Compute("b"u8) };
        var tree = MerkleTree.Build(leaves);
        var message = new ChunkPullResponseMessage(Id(16), FileIndex: 0, ChunkIndex: 1, Found: true, Payload: [4, 5, 6], Proof: tree.GetProof(1));

        var decoded = (ChunkPullResponseMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.True(decoded.Found);
        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.ChunkIndex, decoded.ChunkIndex);
        Assert.Equal(message.Payload, decoded.Payload);
        Assert.NotNull(decoded.Proof);
        Assert.True(MerkleProof.Verify(tree.Root, leaves[1], decoded.Proof!));
    }

    [Fact]
    public void ChunkPullResponse_NotFound_RoundTrips_WithNoPayloadOrProof()
    {
        var message = new ChunkPullResponseMessage(Id(16), 0, 7, Found: false, Payload: [], Proof: null);

        var decoded = (ChunkPullResponseMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.False(decoded.Found);
        Assert.Equal(7, decoded.ChunkIndex);
        Assert.Empty(decoded.Payload);
        Assert.Null(decoded.Proof);
    }

    [Fact]
    public void KeyUnavailable_RoundTrips()
    {
        var message = new KeyUnavailableMessage(Id(16), Id(16));

        var decoded = (KeyUnavailableMessage)MessageCodec.Decode(MessageCodec.Encode(message));

        Assert.Equal(message.SessionId, decoded.SessionId);
        Assert.Equal(message.ReceiverId, decoded.ReceiverId);
    }

    [Fact]
    public void Decode_UnsupportedVersion_Throws()
    {
        var bytes = MessageCodec.Encode(new TransferCompleteMessage(Id(16), Id(16), TransferOutcome.Completed));
        bytes[0] = 0xFF;

        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes));
    }

    [Fact]
    public void Decode_UnknownMessageTypeTag_Throws()
    {
        var bytes = MessageCodec.Encode(new TransferCompleteMessage(Id(16), Id(16), TransferOutcome.Completed));
        bytes[1] = 0xFF; // corrupt the type tag

        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes));
    }

    [Fact]
    public void Encode_UnknownMessageObjectType_Throws()
    {
        Assert.Throws<ArgumentException>(() => MessageCodec.Encode("not a message"));
    }

    private static byte[] Id(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}
