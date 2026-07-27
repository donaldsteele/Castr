using Castr.Core.Protocol;

namespace Castr.Cli;

/// <summary>
/// Decides the one datagram payload budget a session runs on: the operator's explicit <c>--datagram-size</c> if
/// given, otherwise <see cref="WirePacketizer.DefaultMaxDatagramPayload"/>. Nothing else — see below for why this
/// is deliberately not clever.
///
/// <para><b>Resolved exactly once per session, before the session is constructed.</b> Not for correctness any
/// more — see below — but because one session emitting several slicings of the same chunk wastes wire for no
/// gain, and because a knob whose value can drift mid-run is a knob nobody can reason about. This type exists so
/// "resolve once, up front" is a visible step rather than an assumption spread across two runners.</para>
///
/// <para><b>The budget no longer has to match across peers (M11).</b> It used to, and nothing on the wire
/// enforced it: <see cref="ChunkPacketAssembler"/> keyed a chunk's fragments by packet index, so a receiver
/// serving repair from its chunk cache re-sliced at <i>its own</i> budget and any peer already holding a partial
/// for that chunk rejected the entire re-slice on a <c>PacketCount</c> check — no log, no metric, and worst in
/// exactly the sender-offline case peer repair exists for. Fragments now carry their byte <i>offset</i>, so two
/// slicings of the same ciphertext describe the same byte ranges and combine freely. A mismatch costs some
/// duplicate bytes on the wire where two slicings overlap; it no longer costs relay.</para>
///
/// <para><b>Why there is still no MTU auto-derivation here.</b> The objection that killed it in M9 review — that
/// a laptop on a 1500-MTU LAN and a peer behind a 1400-MTU VPN would silently pick different budgets and lose
/// peer relay between them — is answered by offset keying, which is precisely the precondition that review named.
/// What is left is a smaller argument: automatic selection would still make the wire shape depend on invisible
/// host configuration, and there is no measurement yet showing it beats a shipped default that already fits the
/// standard 1500-byte MTU. Worth revisiting with numbers, not worth reinstating on the strength of the blocker
/// having been removed.</para>
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
