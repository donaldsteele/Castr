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
    /// <summary>Default MTU-safe target size for a single datagram, matching wiki/concepts/wire-protocol.md's ~1200-byte wire-packet target.</summary>
    public const int DefaultMaxDatagramPayload = 1200;

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
