using Castr.Core.Chunking;
using Castr.Core.Manifest;
using Castr.Core.Protocol;
using Castr.Core.Security;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Coverage for the per-role datagram admission filters that stop a session paying to copy, queue, and decode
/// multicast traffic its role can never act on — most importantly a sender's own loopback echo of the entire
/// transfer it is transmitting. See <see cref="DatagramFilters"/> and <see cref="Castr.Core.Transport.DatagramFilter"/>.
/// </summary>
public class DatagramFiltersTests
{
    // ---- sender filter: accepts only what SenderSession.HandleIncomingAsync actually acts on ----

    [Theory]
    [InlineData(MessageType.ChunkRequest)]  // a receiver asking for repair — the sender must see this
    [InlineData(MessageType.JoinRequest)]   // a receiver asking for the content key — the sender must see this
    [InlineData(MessageType.PacketFragment)]
    public void Sender_AcceptsTheTypesASenderActsOn(MessageType type) =>
        Assert.True(DatagramFilters.Sender(Datagram(type)));

    [Theory]
    [InlineData(MessageType.ChunkPacket)]   // the sender's own carousel output, looped straight back
    [InlineData(MessageType.ChunkData)]
    [InlineData(MessageType.ChunkResponse)]
    [InlineData(MessageType.PeerHave)]
    [InlineData(MessageType.Announce)]
    [InlineData(MessageType.Manifest)]
    [InlineData(MessageType.KeyGrant)]
    [InlineData(MessageType.TransferComplete)]
    public void Sender_RejectsItsOwnEchoAndEverythingElseItIgnores(MessageType type) =>
        Assert.False(DatagramFilters.Sender(Datagram(type)));

    // ---- receiver filter: everything except another receiver's JOIN_REQUEST ----

    [Fact]
    public void Receiver_RejectsOnlyJoinRequest() =>
        Assert.False(DatagramFilters.Receiver(Datagram(MessageType.JoinRequest)));

    [Theory]
    [InlineData(MessageType.Announce)]
    [InlineData(MessageType.Manifest)]
    [InlineData(MessageType.ChunkData)]
    [InlineData(MessageType.PeerHave)]
    [InlineData(MessageType.ChunkRequest)]
    [InlineData(MessageType.ChunkResponse)]
    [InlineData(MessageType.TransferComplete)]
    [InlineData(MessageType.KeyGrant)]
    [InlineData(MessageType.PacketFragment)]
    [InlineData(MessageType.ChunkPacket)]
    [InlineData(MessageType.KeyUnavailable)]
    public void Receiver_AcceptsEverythingElse(MessageType type) =>
        Assert.True(DatagramFilters.Receiver(Datagram(type)));

    [Fact]
    public void Receiver_AcceptsAnUnknownFutureMessageType()
    {
        // Forward compatibility: a type this build does not know must not be silently swallowed by the filter.
        // The decoder is the component allowed to reject it, and it will (with InvalidDataException).
        Assert.True(DatagramFilters.Receiver([1, 250]));
        Assert.False(DatagramFilters.Sender([1, 250])); // a sender still only wants its two control types
    }

    // ---- both filters must accept PacketFragment, whatever it reassembles into ----

    [Fact]
    public void BothFilters_AcceptPacketFragment_BecauseTheReassembledTypeIsUnknowable()
    {
        // A fragment carries only [version][PacketFragment][groupId][index][count][totalLength][slice] — the type
        // of the message it is a slice OF appears nowhere in it. Dropping fragments would silently break every
        // message too large for one datagram (a PEER_HAVE bitmap, a bulk CHUNK_REQUEST, a large KEY_GRANT).
        var large = MessageCodec.Encode(new PeerHaveMessage(new byte[16], new byte[16], 0, new byte[4000], "", 0));
        var datagrams = WirePacketizer.Fragment(large, WirePacketizer.DefaultMaxDatagramPayload);

        Assert.True(datagrams.Count > 1, "test fixture must actually fragment");
        foreach (var datagram in datagrams)
        {
            Assert.Equal((byte)MessageType.PacketFragment, datagram[1]);
            Assert.True(DatagramFilters.Sender(datagram));
            Assert.True(DatagramFilters.Receiver(datagram));
        }
    }

    [Fact]
    public void SenderFilter_AcceptsEveryFragmentOfALargeChunkRequest()
    {
        // The specific case that would break if fragments were dropped by role: a bulk repair request from a
        // receiver, large enough to fragment, must still reach the sender intact.
        var request = MessageCodec.Encode(new ChunkRequestMessage(
            new byte[16], new byte[16], new byte[16], 0, [.. Enumerable.Range(0, 5_000)], "", 0));
        var datagrams = WirePacketizer.Fragment(request, WirePacketizer.DefaultMaxDatagramPayload);

        Assert.True(datagrams.Count > 1);
        Assert.All(datagrams, d => Assert.True(DatagramFilters.Sender(d)));
    }

    // ---- conservative on short datagrams: never let the filter be what breaks a message ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BothFilters_AcceptDatagramsShorterThanTheTypeTag(int length)
    {
        var datagram = new byte[length];

        // Under 2 bytes there is no readable type byte at all. Accepting leaves rejection to MessageCodec.Decode
        // (which throws, and every call site already treats that as "drop"), rather than having the filter guess.
        Assert.True(DatagramFilters.Sender(datagram));
        Assert.True(DatagramFilters.Receiver(datagram));
    }

    [Fact]
    public void ShortDatagramsThatTheFiltersAccept_AreStillRejectedByTheDecoder()
    {
        // Proves the "let the decoder reject it" contract the length<2 case relies on is actually true, so
        // accepting a truncated datagram is safe rather than a hole.
        Assert.ThrowsAny<Exception>(() => MessageCodec.Decode([]));
        Assert.ThrowsAny<Exception>(() => MessageCodec.Decode([1]));
    }

    [Fact]
    public void ExactlyTwoBytes_IsEnoughToClassify()
    {
        Assert.True(DatagramFilters.Sender([1, (byte)MessageType.ChunkRequest]));
        Assert.False(DatagramFilters.Sender([1, (byte)MessageType.ChunkPacket]));
    }

    // ---- the filter must not depend on anything past the type byte ----

    [Fact]
    public void Filters_ClassifyOnTheTypeByteAlone_EvenWhenTheBodyIsGarbage()
    {
        // The filter runs on raw bytes straight off the wire, before any validation, so it must never require a
        // well-formed body — a truncated or corrupt CHUNK_REQUEST still has to reach the sender's decoder.
        Span<byte> corrupt = stackalloc byte[40];
        corrupt.Fill(0xAB);
        corrupt[0] = 1;
        corrupt[1] = (byte)MessageType.ChunkRequest;

        Assert.True(DatagramFilters.Sender(corrupt));
        Assert.True(DatagramFilters.Receiver(corrupt));
    }

    /// <summary>
    /// A minimal, real (codec-produced) datagram of the given type — so the tests assert against the actual wire
    /// encoding's type-byte position rather than a hand-rolled assumption about it.
    /// </summary>
    private static byte[] Datagram(MessageType type)
    {
        var encoded = MessageCodec.Encode(SampleMessage(type));
        Assert.Equal((byte)type, encoded[1]); // guards the [version][type] layout the filters depend on
        return encoded;
    }

    private static object SampleMessage(MessageType type) => type switch
    {
        MessageType.Announce => new AnnounceMessage(
            new byte[16], new byte[32], ChunkHash.Compute([1]), "t", DateTimeOffset.UnixEpoch),
        MessageType.Manifest => new ManifestMessage(SampleSignedManifest()),
        MessageType.ChunkData => new ChunkDataMessage(new byte[16], 0, 0, [1, 2, 3], SampleProof()),
        MessageType.PeerHave => new PeerHaveMessage(new byte[16], new byte[16], 0, [0x01], "", 0),
        MessageType.ChunkRequest => new ChunkRequestMessage(new byte[16], new byte[16], new byte[16], 0, [1], "", 0),
        MessageType.ChunkResponse => new ChunkResponseMessage(new byte[16], new byte[16], 0, 0, [1], SampleProof()),
        MessageType.TransferComplete => new TransferCompleteMessage(new byte[16], new byte[16], TransferOutcome.Completed),
        MessageType.JoinRequest => new JoinRequestMessage(new byte[16], new byte[16], new byte[32]),
        MessageType.KeyGrant => new KeyGrantMessage(new byte[16], new byte[16], new byte[48]),
        MessageType.PacketFragment => new PacketFragmentMessage(1, 0, 1, 3, [1, 2, 3]),
        MessageType.ChunkPacket => new ChunkPacketMessage(new byte[16], 0, 0, 0, 3, [1, 2, 3], SampleProof()),
        MessageType.KeyUnavailable => new KeyUnavailableMessage(new byte[16], new byte[16]),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static MerkleProof SampleProof() => MerkleTree.Build([ChunkHash.Compute([1]), ChunkHash.Compute([2])]).GetProof(0);

    private static SignedManifest SampleSignedManifest()
    {
        using var key = ManifestSigner.CreateSigningKey();
        using var encryptionKey = EncryptionKeys.Create();
        var manifest = new TransferManifest(
            new byte[16], "t", DateTimeOffset.UnixEpoch, EncryptionKeys.ExportPublicKey(encryptionKey),
            [new ManifestFileEntry("f.bin", 1, 1, 1, ChunkHash.Compute([1]))]);
        return ManifestSigner.Sign(manifest, key);
    }
}
