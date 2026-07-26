using System.Security.Cryptography;

namespace Castr.Core.Protocol;

/// <summary>
/// The transport-layer half of the two-level chunking described in wiki/concepts/wire-protocol.md: a
/// <b>chunk</b> is the hash/repair granularity, but a chunk's encrypted <see cref="ChunkDataMessage"/> /
/// <see cref="ChunkResponseMessage"/> envelope can be far larger than a single UDP datagram may safely carry
/// (Windows throws <c>SocketException(MessageSize)</c> above 65,507 bytes, and anything past the path MTU
/// risks IP fragmentation). <see cref="Fragment"/> splits an already-encoded wire message into ordered,
/// MTU-safe datagrams; <see cref="PacketReassembler"/> puts them back together on the receive side.
///
/// This is a purely post-encryption, post-encode transport concern: it operates on opaque encoded-message
/// bytes and knows nothing about encryption, Merkle proofs, or chunk semantics. A message that already fits
/// in one datagram is returned verbatim (no wrapper), so small messages travel over the wire byte-for-byte as
/// before. Note chunks are not among them at any shipped chunk size: <see cref="ChunkPacketizer"/> has already
/// split a chunk into per-datagram <c>ChunkPacketMessage</c>s before this type sees anything, which is why the
/// default chunk size (256 KiB since M8) is free to exceed the datagram budget by orders of magnitude. Loss of
/// any fragment simply means the logical message never
/// reassembles; for a chunk that leaves the chunk incomplete, and the existing chunk-level repair path
/// (CHUNK_REQUEST) re-requests the whole chunk — no packet-level NACK/retry machinery is needed.
/// </summary>
public static class WirePacketizer
{
    /// <summary>
    /// Default MTU-safe target size for a single datagram: <b>1472 bytes</b>, the largest UDP payload that still
    /// fits a standard 1500-byte Ethernet MTU without IP fragmentation (1500 − 20 IPv4 header − 8 UDP header).
    ///
    /// <para>Was 1200, a conservative round number carried since M1 and never derived from an MTU. 1472 is the
    /// derived value for the environment Castr targets — a link-local, TTL=1, ordinary-Ethernet LAN segment — and
    /// it is a pure win over 1200 for the same reason a larger chunk is: it is fewer datagrams for the same bytes,
    /// and both sides are bound by per-datagram cost (~32 µs each way) rather than by bytes. See the M9 section of
    /// docs/benchmarks/throughput-runs.md.</para>
    ///
    /// <para><b>Do not raise this default past 1472.</b> Anything larger IP-fragments on a 1500-MTU path, and IP
    /// fragmentation is all-or-nothing: losing any one fragment destroys the whole datagram, so a 60,000-byte
    /// datagram is ~41 fragments whose combined loss probability is far worse than 41 independent datagrams. A
    /// loopback benchmark cannot see this (loopback has an effectively unbounded MTU), which is exactly why an
    /// earlier 60,000-byte experiment measured well and would have been a serious regression on real hardware.
    /// A caller who has measured a jumbo-frame path may pass a larger value explicitly (<c>--datagram-size</c>);
    /// that is an informed opt-in, not a default.</para>
    ///
    /// <para><b>Path-MTU discovery is deliberately not implemented here.</b> This constant plus the per-session
    /// parameter is the seam a future discovery mechanism plugs into: it would choose the value <i>once, before a
    /// session starts</i>, and hand it to <see cref="SenderSession"/>/<see cref="ReceiverSession"/> like any other
    /// configured budget. It must not adapt mid-session — see the constancy note on
    /// <see cref="ValidateMaxDatagramPayload"/>.</para>
    /// </summary>
    public const int DefaultMaxDatagramPayload = 1472;

    /// <summary>
    /// Smallest configurable datagram payload: 548 bytes = the 576-byte IPv4 minimum MTU every host must accept,
    /// less the 20-byte IPv4 and 8-byte UDP headers. Below this a path is not required to deliver at all.
    /// </summary>
    public const int MinMaxDatagramPayload = 576 - 20 - 8;

    /// <summary>
    /// Largest configurable datagram payload: the hard UDP-over-IPv4 limit (65,535 − 20 − 8). Windows throws
    /// <c>SocketException(MessageSize)</c> above it. Values above
    /// <see cref="DefaultMaxDatagramPayload"/> rely on IP fragmentation (or a jumbo-frame path) — see that
    /// constant's remarks before using one.
    /// </summary>
    public const int MaxMaxDatagramPayload = 65_535 - 20 - 8;

    /// <summary>
    /// Range-checks a configured datagram budget and returns it, so every entry point rejects the same values with
    /// the same message.
    ///
    /// <para><b>The budget must be constant for the life of a session.</b> It is an input to
    /// <see cref="ChunkPacketizer.Split"/>, so two different budgets slice the same chunk into different packet
    /// counts, and <see cref="ChunkPacketAssembler.Offer"/> rejects a packet whose metadata disagrees with the
    /// first packet seen for that chunk. A budget that changed mid-session would therefore make a session reject
    /// its own retransmissions. Both sessions capture it once, in their constructor, through this method — never
    /// re-read it per packet, and never make it settable.</para>
    /// </summary>
    public static int ValidateMaxDatagramPayload(int maxDatagramPayload, string paramName)
    {
        if (maxDatagramPayload is < MinMaxDatagramPayload or > MaxMaxDatagramPayload)
            throw new ArgumentOutOfRangeException(
                paramName,
                maxDatagramPayload,
                $"Datagram payload must be between {MinMaxDatagramPayload} and {MaxMaxDatagramPayload} bytes.");
        return maxDatagramPayload;
    }

    /// <summary>
    /// Fixed bytes a <see cref="PacketFragmentMessage"/> adds around its fragment slice:
    /// FormatVersion(1) + MessageType(1) + GroupId(8) + PacketIndex(4) + PacketCount(4) + TotalLength(4) +
    /// fragment length prefix(4) = 26.
    /// </summary>
    internal const int FragmentEnvelopeOverhead = 1 + 1 + 8 + 4 + 4 + 4 + 4;

    /// <summary>
    /// Splits <paramref name="encodedMessage"/> into datagrams no larger than <paramref name="maxDatagramPayload"/>.
    /// Returns the message unchanged as a single element when it already fits; otherwise wraps ordered slices in
    /// <see cref="PacketFragmentMessage"/> datagrams sharing a random group id so a receiver can reassemble them
    /// even under reordering, duplication, or partial loss.
    /// </summary>
    public static IReadOnlyList<byte[]> Fragment(byte[] encodedMessage, int maxDatagramPayload = DefaultMaxDatagramPayload)
    {
        ArgumentNullException.ThrowIfNull(encodedMessage);
        if (maxDatagramPayload <= FragmentEnvelopeOverhead)
            throw new ArgumentOutOfRangeException(
                nameof(maxDatagramPayload),
                $"Max datagram payload must exceed the {FragmentEnvelopeOverhead}-byte fragment envelope.");

        if (encodedMessage.Length <= maxDatagramPayload)
            return [encodedMessage];

        int perFragment = maxDatagramPayload - FragmentEnvelopeOverhead;
        int count = (encodedMessage.Length + perFragment - 1) / perFragment;
        long groupId = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));

        var datagrams = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int offset = i * perFragment;
            int length = Math.Min(perFragment, encodedMessage.Length - offset);
            var slice = new byte[length];
            Array.Copy(encodedMessage, offset, slice, 0, length);
            datagrams[i] = MessageCodec.Encode(new PacketFragmentMessage(groupId, i, count, encodedMessage.Length, slice));
        }
        return datagrams;
    }
}
