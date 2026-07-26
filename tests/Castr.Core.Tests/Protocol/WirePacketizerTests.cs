using Castr.Core.Chunking;
using Castr.Core.Protocol;
using Castr.Core.Time;
using Castr.Core.Transport;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;

namespace Castr.Core.Tests.Protocol;

/// <summary>
/// Unit tests for the two-level-chunking transport layer (<see cref="WirePacketizer"/> /
/// <see cref="PacketReassembler"/>): split-then-reassemble is exact, reassembly waits for every fragment,
/// arrival order/duplication don't matter, and a truly-incomplete group is never surfaced as data.
/// </summary>
public class WirePacketizerTests
{
    [Fact]
    public void SmallMessage_IsNotFragmented_AndPassesThroughVerbatim()
    {
        var encoded = Bytes(500, seed: 1);

        var datagrams = WirePacketizer.Fragment(encoded, maxDatagramPayload: 1200);

        Assert.Single(datagrams);
        Assert.Same(encoded, datagrams[0]); // returned verbatim, no wrapper
        Assert.Equal(encoded, new PacketReassembler().Offer(datagrams[0]));
    }

    [Fact]
    public void MessageEqualToLimit_IsNotFragmented()
    {
        var encoded = Bytes(1200, seed: 2);

        var datagrams = WirePacketizer.Fragment(encoded, maxDatagramPayload: 1200);

        Assert.Single(datagrams);
    }

    [Theory]
    [InlineData(1201, 1200)]
    [InlineData(8208, 1200)]     // ~8 KB default chunk ciphertext
    [InlineData(262_160, 1200)]  // ~256 KB documented chunk ciphertext
    [InlineData(1_048_592, 1200)] // ~1 MB documented chunk ciphertext
    [InlineData(50_000, 576)]    // conservative minimum-MTU budget
    public void SplitThenReassemble_RoundTripsExactly(int size, int maxDatagram)
    {
        var encoded = Bytes(size, seed: size);

        var datagrams = WirePacketizer.Fragment(encoded, maxDatagram);

        Assert.True(datagrams.Count > 1);
        Assert.All(datagrams, d => Assert.True(d.Length <= maxDatagram, $"datagram {d.Length} exceeds {maxDatagram}"));

        var reassembler = new PacketReassembler();
        byte[]? completed = null;
        for (int i = 0; i < datagrams.Count; i++)
        {
            var result = reassembler.Offer(datagrams[i]);
            if (i < datagrams.Count - 1)
                Assert.Null(result); // incomplete until the very last fragment arrives
            else
                completed = result;
        }

        Assert.NotNull(completed);
        Assert.Equal(encoded, completed);
    }

    [Fact]
    public void Reassembly_HandlesOutOfOrderArrival()
    {
        var encoded = Bytes(10_000, seed: 3);
        var datagrams = WirePacketizer.Fragment(encoded, 1200).ToList();

        var shuffled = datagrams.OrderBy(_ => Guid.NewGuid()).ToList();

        var reassembler = new PacketReassembler();
        byte[]? completed = null;
        foreach (var d in shuffled)
            completed = reassembler.Offer(d) ?? completed;

        Assert.Equal(encoded, completed);
    }

    [Fact]
    public void Reassembly_IgnoresDuplicateFragments_AndStillCompletesOnce()
    {
        var encoded = Bytes(6000, seed: 4);
        var datagrams = WirePacketizer.Fragment(encoded, 1200);

        var reassembler = new PacketReassembler();
        var completions = new List<byte[]>();

        // Feed every fragment twice, interleaved.
        foreach (var d in datagrams)
        {
            if (reassembler.Offer(d) is { } a) completions.Add(a);
            if (reassembler.Offer(d) is { } b) completions.Add(b);
        }

        Assert.Single(completions);
        Assert.Equal(encoded, completions[0]);
    }

    [Fact]
    public void Reassembly_MissingOneFragment_NeverSurfacesData()
    {
        var encoded = Bytes(9000, seed: 5);
        var datagrams = WirePacketizer.Fragment(encoded, 1200).ToList();

        var reassembler = new PacketReassembler();
        // Deliver everything except one middle fragment.
        for (int i = 0; i < datagrams.Count; i++)
        {
            if (i == datagrams.Count / 2)
                continue;
            Assert.Null(reassembler.Offer(datagrams[i]));
        }

        Assert.Equal(1, reassembler.PendingGroupCount); // buffered, but never completed => treated as not-received
    }

    [Fact]
    public void MissingFragment_ThenRetransmittedLater_Completes()
    {
        // Models chunk-level repair: the whole chunk is re-sent, so the previously-lost fragment eventually
        // arrives (as a fresh group) and reassembly succeeds.
        var encoded = Bytes(5000, seed: 6);
        var reassembler = new PacketReassembler();

        var firstAttempt = WirePacketizer.Fragment(encoded, 1200).ToList();
        for (int i = 0; i < firstAttempt.Count - 1; i++)
            Assert.Null(reassembler.Offer(firstAttempt[i])); // last one "lost"

        var retransmit = WirePacketizer.Fragment(encoded, 1200).ToList(); // new group id
        byte[]? completed = null;
        foreach (var d in retransmit)
            completed = reassembler.Offer(d) ?? completed;

        Assert.Equal(encoded, completed);
    }

    [Fact]
    public void TwoInterleavedMessages_ReassembleIndependently()
    {
        var a = Bytes(7000, seed: 7);
        var b = Bytes(11_000, seed: 8);
        var fa = WirePacketizer.Fragment(a, 1200).ToList();
        var fb = WirePacketizer.Fragment(b, 1200).ToList();

        var reassembler = new PacketReassembler();
        byte[]? gotA = null, gotB = null;
        int max = Math.Max(fa.Count, fb.Count);
        for (int i = 0; i < max; i++)
        {
            if (i < fa.Count) gotA = reassembler.Offer(fa[i]) ?? gotA;
            if (i < fb.Count) gotB = reassembler.Offer(fb[i]) ?? gotB;
        }

        Assert.Equal(a, gotA);
        Assert.Equal(b, gotB);
    }

    [Fact]
    public void EvictionCap_BoundsMemory_ForNeverCompletedGroups()
    {
        var reassembler = new PacketReassembler(maxConcurrentGroups: 4);

        // Start 10 distinct multi-fragment groups but only ever deliver their first fragment.
        for (int i = 0; i < 10; i++)
        {
            var first = WirePacketizer.Fragment(Bytes(5000, seed: 100 + i), 1200)[0];
            reassembler.Offer(first);
        }

        Assert.True(reassembler.PendingGroupCount <= 4);
    }

    [Fact]
    public void Offer_MalformedFragment_ReturnsNull_DoesNotThrow()
    {
        var reassembler = new PacketReassembler();
        // A datagram that claims to be a PacketFragment (tag 10 at index 1) but has garbage after it.
        var bogus = new byte[] { 1, (byte)MessageType.PacketFragment, 0xAA, 0xBB };

        Assert.Null(reassembler.Offer(bogus));
    }

    [Fact]
    public void Fragment_MaxDatagramTooSmall_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WirePacketizer.Fragment(Bytes(100, 1), maxDatagramPayload: 10));
    }

    [Fact]
    public void DefaultMaxDatagramPayload_IsTheLargestNonFragmentingPayloadAtA1500ByteMtu()
    {
        // 1500 MTU − 20 IPv4 header − 8 UDP header = 1472. Asserted, not assumed: a larger default would
        // IP-fragment on ordinary Ethernet, where losing one fragment loses the whole datagram.
        Assert.Equal(1472, WirePacketizer.DefaultMaxDatagramPayload);
        Assert.Equal(548, WirePacketizer.MinMaxDatagramPayload);      // 576-byte IPv4 minimum MTU − 20 − 8
        Assert.Equal(65_507, WirePacketizer.MaxMaxDatagramPayload);   // hard UDP-over-IPv4 limit
    }

    [Theory]
    [InlineData(WirePacketizer.MinMaxDatagramPayload)]
    [InlineData(1200)]
    [InlineData(WirePacketizer.DefaultMaxDatagramPayload)]
    [InlineData(WirePacketizer.MaxMaxDatagramPayload)]
    public void ValidateMaxDatagramPayload_InRange_ReturnsTheValue(int value) =>
        Assert.Equal(value, WirePacketizer.ValidateMaxDatagramPayload(value, "budget"));

    [Theory]
    [InlineData(WirePacketizer.MinMaxDatagramPayload - 1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WirePacketizer.MaxMaxDatagramPayload + 1)]
    [InlineData(int.MaxValue)]
    public void ValidateMaxDatagramPayload_OutOfRange_Throws(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WirePacketizer.ValidateMaxDatagramPayload(value, "budget"));

    [Theory]
    [InlineData(WirePacketizer.MinMaxDatagramPayload - 1)]
    [InlineData(0)]
    [InlineData(WirePacketizer.MaxMaxDatagramPayload + 1)]
    public void ReceiverSession_RejectsAnOutOfRangeDatagramBudget_AtConstruction(int value)
    {
        // The budget must be fixed and legal for the life of a session (it slices every chunk this session
        // relays, and ChunkPacketAssembler rejects packets whose slicing metadata disagrees with the first seen),
        // so it is range-checked once in the constructor rather than at first use.
        var network = new InMemoryNetwork();
        var transport = network.CreateMulticastTransport(new Endpoint("receiver", 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => new ReceiverSession(
            [1, 2, 3], new InMemoryTrustStore(), transport, SystemClock.Instance,
            new ReceiverSessionOptions("/root"), (_, length) => new MemoryFileSink((int)length),
            maxDatagramPayloadBytes: value));
    }

    [Fact]
    public void Offer_HugeTotalLength_IsRejected_WithoutAllocating()
    {
        // This path runs pre-auth on every raw datagram, so a crafted fragment claiming a gigabyte TotalLength used
        // to size `new byte[TotalLength]` for an unauthenticated peer. It must now be dropped against the fixed
        // ceiling — silently, without throwing (the receive loop must survive one bad actor).
        var reassembler = new PacketReassembler();
        var malicious = MessageCodec.Encode(
            new PacketFragmentMessage(GroupId: 1, PacketIndex: 0, PacketCount: 2, TotalLength: int.MaxValue, Bytes(10, 1)));

        byte[]? result = null;
        var ex = Record.Exception(() => result = reassembler.Offer(malicious));

        Assert.Null(ex);
        Assert.Null(result);
        Assert.Equal(0, reassembler.PendingGroupCount);
    }

    [Fact]
    public void Offer_HugePacketCount_IsRejected_WithoutAllocating()
    {
        // The other allocation vector: a huge PacketCount sizing `new byte[PacketCount][]` (20,000,000 => ~152 MB;
        // int.MaxValue => ~16 GB). Rejected up front because a legitimate fragmentation never yields more fragments
        // than bytes.
        var reassembler = new PacketReassembler();

        byte[]? result = null;
        var ex152Mb = Record.Exception(() => result = reassembler.Offer(MessageCodec.Encode(
            new PacketFragmentMessage(GroupId: 1, PacketIndex: 0, PacketCount: 20_000_000, TotalLength: 200, Bytes(1, 1)))));
        Assert.Null(ex152Mb);
        Assert.Null(result);

        var exMax = Record.Exception(() => result = reassembler.Offer(MessageCodec.Encode(
            new PacketFragmentMessage(GroupId: 2, PacketIndex: 0, PacketCount: int.MaxValue, TotalLength: 200, Bytes(1, 1)))));
        Assert.Null(exMax);
        Assert.Null(result);

        Assert.Equal(0, reassembler.PendingGroupCount);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
