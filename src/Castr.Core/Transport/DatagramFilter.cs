namespace Castr.Core.Transport;

/// <summary>
/// A cheap, allocation-free admission test applied to a raw inbound datagram <b>before</b> a transport spends
/// anything on it — no copy, no queue slot, no decode. Return <see langword="true"/> to accept the datagram,
/// <see langword="false"/> to drop it as if the network had never delivered it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because IP multicast loopback delivers a sender's own datagrams straight back to it: without a
/// filter, a sender receives, copies, queues, and fully decodes every one of the tens of thousands of chunk
/// datagrams it just emitted, only for <c>SenderSession</c>'s handler switch to discard them. That work
/// starves the sender's own socket receive buffer and inbox of room for the control traffic it actually needs
/// (CHUNK_REQUEST, JOIN_REQUEST), so the kernel drops real repair and join requests. See
/// <c>Castr.Core.Protocol.DatagramFilters</c> for the prebuilt per-role filters and the reasoning behind which
/// message types each role accepts.
/// </para>
/// <para>
/// Declared as a delegate rather than <c>Func&lt;ReadOnlySpan&lt;byte&gt;, bool&gt;</c> because
/// <see cref="ReadOnlySpan{T}"/> is a <c>ref struct</c> and cannot be used as a generic type argument. Taking a
/// span (not an array or <see cref="ReadOnlyMemory{T}"/>) is the point: a filter can inspect the transport's
/// own reusable receive buffer in place, so rejecting a datagram costs zero allocations.
/// </para>
/// <para>
/// Implementations must be pure, fast, and non-throwing — they run inline on the socket-drain loop, once per
/// datagram. They must also be <b>conservative</b>: when in doubt, accept, and leave rejection to the decoder.
/// A filter that wrongly drops a datagram silently breaks the protocol in a way no error surfaces.
/// </para>
/// </remarks>
/// <param name="datagram">The raw received bytes, exactly as they came off the wire.</param>
public delegate bool DatagramFilter(ReadOnlySpan<byte> datagram);
