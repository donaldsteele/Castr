using Castr.Core.Protocol;

namespace Castr.Cli;

/// <summary>
/// Decides the one datagram payload budget a session runs on: the operator's explicit <c>--datagram-size</c> if
/// given, otherwise <see cref="WirePacketizer.DefaultMaxDatagramPayload"/>. Nothing else — see below for why this
/// is deliberately not clever.
///
/// <para><b>Resolved exactly once per session, before the session is constructed.</b> The budget slices every
/// chunk (<see cref="ChunkPacketizer.Split"/>) and <see cref="ChunkPacketAssembler"/> rejects packets whose
/// slicing metadata disagrees with the first one seen for a chunk, so a budget that changed mid-session would
/// make a session reject its own retransmissions. This type exists so "resolve once, up front" is a visible step
/// rather than an assumption spread across two runners.</para>
///
/// <para><b>⚠️ The budget must be the same on every peer participating in a transfer.</b> A sender and a receiver
/// on different budgets still complete a normal transfer — the receiver reassembles whatever self-consistent
/// fragment lengths the sender emits — but <b>peer-to-peer relay breaks silently between mismatched peers</b>. A
/// receiver serving repair from its chunk cache re-slices at <i>its own</i> budget, and a peer that already holds
/// a partial for that chunk rejects the whole re-slice on <see cref="ChunkPacketAssembler.Offer"/>'s
/// <c>PacketCount</c> check: no log, no metric. It bites hardest in exactly the sender-offline case peer repair
/// exists for, and because a partially-received chunk can then never be completed by the mismatched peer while
/// pending partials are only evicted under cap pressure, the stuck partial is effectively permanent for any file
/// that never reaches the cap. Treat <c>--datagram-size</c> as a whole-transfer parameter.</para>
///
/// <para><b>Why there is no MTU auto-derivation here, though it was implemented and then removed in review.</b>
/// Deriving the budget from the named interface's MTU is sound in isolation (Castr multicasts at TTL=1, so there
/// are no routers and the path MTU <i>is</i> the interface MTU) but it manufactures exactly the mismatch above
/// <b>without any operator deciding anything</b>: a laptop on a 1500-MTU LAN and a peer behind a 1400-MTU VPN
/// would silently pick different budgets and lose peer relay between them. An explicit flag at least makes the
/// mismatch someone's decision. Automatic per-peer selection only becomes safe once fragments are keyed by
/// <i>byte offset</i> rather than packet index, which makes two slicings of the same ciphertext interchangeable —
/// tracked in wiki/synthesis/roadmap.md alongside the assembler rewrite.</para>
///
/// <para>If a probe is ever attempted anyway: <b>do not use DontFragment on Windows multicast.</b> The socket
/// option is silently ignored there — it reads back <c>true</c> and then fragments anyway, accepting payloads up
/// to 65,507 bytes — so a DF-based probe reports that a 60,000-byte datagram "fits" and is catastrophically wrong
/// on a 1500-MTU segment.</para>
/// </summary>
internal static class DatagramBudget
{
    public static int Resolve(int? requested) => requested ?? WirePacketizer.DefaultMaxDatagramPayload;
}
