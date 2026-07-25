using Castr.Core.Transport;

namespace Castr.Core.Protocol;

/// <summary>
/// Prebuilt <see cref="DatagramFilter"/>s that let a session drop the multicast traffic its own role can never
/// act on, before the transport pays to copy, queue, or decode it.
/// </summary>
/// <remarks>
/// <para>
/// Every Castr datagram starts <c>[FormatVersion:1][MessageType:1]</c> (see <see cref="MessageCodec"/> and
/// <see cref="ChunkPacketizer.FixedEnvelopeOverhead"/>), so a role's admission decision is a single byte
/// lookup — no parsing, no allocation, no dependence on the rest of the payload being well-formed.
/// </para>
/// <para>
/// Why this matters: <c>MulticastLoopback</c> is on for both roles (a sender and a receiver on one host must be
/// able to reach each other), so a sender receives its own entire transmission back. At an 80 MiB / 8 KiB-chunk
/// transfer that is well over a hundred thousand chunk datagrams that get copied into a
/// <c>ReceivedPacket</c>, occupy a slot in the transport's bounded inbox, and are fully decoded — only for
/// <c>SenderSession.HandleIncomingAsync</c>'s switch to discard them. That crowds out the two message types the
/// sender genuinely needs (CHUNK_REQUEST and JOIN_REQUEST), so the kernel drops real repair and join requests
/// for want of buffer space.
/// </para>
/// <para>
/// Both filters deliberately accept <see cref="MessageType.PacketFragment"/>: a message too large for one
/// datagram travels as <see cref="PacketFragmentMessage"/> slices (see <see cref="WirePacketizer"/>) and the
/// type of the <i>reassembled</i> message is not knowable from any individual fragment. Dropping fragments
/// would silently break every large message, so they always pass and are filtered (if at all) by the session's
/// own handler after reassembly.
/// </para>
/// <para>
/// Both are also conservative about short datagrams: anything under 2 bytes carries no readable type tag and is
/// accepted, leaving <see cref="MessageCodec.Decode"/> to reject it. A filter must never be the thing that
/// silently breaks a message.
/// </para>
/// </remarks>
public static class DatagramFilters
{
    /// <summary>Shortest datagram that carries a readable <c>[version][type]</c> prefix.</summary>
    private const int TypeTagLength = 2;

    /// <summary>
    /// Filter for a sender's transport: accepts only the message types a <c>SenderSession</c> acts on —
    /// <see cref="MessageType.ChunkRequest"/>, <see cref="MessageType.JoinRequest"/>, and
    /// <see cref="MessageType.PacketFragment"/> (which may reassemble into either of those). Everything else is
    /// the sender's own loopback echo of traffic it produced: CHUNK_PACKET, CHUNK_DATA, CHUNK_RESPONSE,
    /// ANNOUNCE, MANIFEST, KEY_GRANT — plus PEER_HAVE, which a sender tracks no peer table for.
    /// </summary>
    public static DatagramFilter Sender { get; } = AcceptForSender;

    /// <summary>
    /// Filter for a receiver's transport: accepts everything except <see cref="MessageType.JoinRequest"/>, which
    /// is always another receiver's request to the <i>sender</i> for the content key and can never be answered
    /// by a receiver (it holds no sender private key). Every other type is either data a receiver consumes or
    /// peer traffic it legitimately relays or gossips on.
    /// </summary>
    public static DatagramFilter Receiver { get; } = AcceptForReceiver;

    /// <inheritdoc cref="Sender"/>
    public static bool AcceptForSender(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < TypeTagLength)
            return true; // no readable type tag — let the decoder reject it rather than guessing here

        return (MessageType)datagram[1] switch
        {
            MessageType.ChunkRequest => true,
            MessageType.JoinRequest => true,
            MessageType.PacketFragment => true, // reassembled type is unknowable per-fragment
            _ => false,
        };
    }

    /// <inheritdoc cref="Receiver"/>
    public static bool AcceptForReceiver(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < TypeTagLength)
            return true; // no readable type tag — let the decoder reject it rather than guessing here

        return (MessageType)datagram[1] != MessageType.JoinRequest;
    }
}
