---
type: synthesis
title: "M9 stage 1 — datagram efficiency: proof-aware slicing and an MTU-derived budget"
tags: [throughput, protocol, measurement]
sources: [castr-project-plan]
created: 2026-07-25
updated: 2026-07-25
---

# M9 stage 1 — datagram efficiency

Stage 1 of the throughput programme. Two narrow, separately-attributable changes to how a chunk becomes
datagrams, and nothing else — no repair-protocol change, no `DefaultSendWindowSize` change, no `_stateGate`
change. Full numbers in the 2026-07-25 M9 section of `docs/benchmarks/throughput-runs.md`.

## What changed

1. **`ChunkPacketizer.Split` reserves proof space on packet 0 only.** The Merkle proof rides only on packet
   0, but every fragment was sized against packet 0's proof-carrying envelope, so every packet after the
   first wasted `ProofEncodedSize(proof)` bytes. At the shipped 256 KiB chunk and the old 1200-byte budget,
   a 100 MiB transfer sent 308 of every 309 datagrams at 893 bytes with 307 of those bytes unused. `Split`
   stays pure and deterministic in `(ciphertext, proof, budget)`, which is the property
   [[repair-protocol]]'s cross-round, cross-source accumulation depends on.
2. **`WirePacketizer.DefaultMaxDatagramPayload` 1200 → 1472**, the largest UDP payload that does not
   IP-fragment at a standard 1500-byte Ethernet MTU (1500 − 20 − 8), plus a `--datagram-size` option on
   both `send` and `receive` — the value was previously exposed by no CLI or GUI surface at all.

## Measured, against a prediction made first

| | before | after | |
|---|---|---|---|
| Datagrams per 256 KiB chunk | 309 | **184** | 1.68× fewer |
| `CHUNK_PACKET` for 100 MiB | 123,600 | **73,600** | prediction exact, to the datagram *and* the byte |
| Payload in the median datagram | 846 B | **1,425 B** | +68% |
| Wall clock (n≥17, warm, interleaved) | 5.80 s | **4.07 s** | **1.42×** |
| Goodput | 17.24 MiB/s | **24.54 MiB/s** | |
| Wire amplification | 1.052× | **1.031×** | |
| Wire efficiency (incl. IP/UDP headers) | 92.2% | 95.1% | **the small number — see below** |

**Frame this as datagram count, not bandwidth.** Efficiency moves three points; datagram count falls 40%.
Both sides of a transfer are bound by per-datagram cost, so the win is in the number of datagrams, and
quoting the efficiency figure would make a real 1.68× look like a miss.

**Do not frame it as lifting a syscall ceiling.** A parallel transport investigation measured per-datagram
cost directly: 4.7 µs with no member joined, **32.1 µs with one receiver joined**, 7.3 µs on a real NIC.
The 32 µs is loopback multicast fan-out — the kernel performing the delivery copy inline on the sender's
thread — not system-call overhead. The earlier "~37 MB/s syscall ceiling" framing is withdrawn.
Corollary: **always quote the datagram size beside any loopback throughput number**, since loopback at a
correct 1472-byte datagram is bound near 46 MB/s regardless of the code, and the campaign's 98.6 MB/s
figure required 60,000-byte datagrams that no real 1500-MTU segment can carry unfragmented.

## Why this needed no wire-format change — and what the real risk turned out to be

M7's review classified this as requiring sender+receiver lockstep. That was wrong, and the distinction
matters enough to state precisely:

- **`ChunkPacketAssembler.TryAssemble` sums each fragment's *actual* length** and validates the total
  against `CiphertextLength`. It never assumed uniform fragment sizes, so an unmodified receiver
  reassembles variable-length fragments correctly. There is a test that feeds it an old-style uniformly
  sliced chunk and asserts a byte-exact round trip. QA verified compatibility in **both** directions with
  byte-identical results.
- **But "degrades, never corrupts" was only half right.** `Offer` rejects a packet whose
  `PacketCount`/`CiphertextLength` disagree with the first packet seen for that chunk — and *whichever
  source arrives first pins the buffer*. QA measured the consequence: with a mismatched peer's partial
  relay arriving first, a **complete and correct** re-delivery at the session's own slicing could not
  finish the chunk. `Forget()` runs only after a successful assembly, so the poisoned partial had no
  recovery path but LRU eviction, which needs `DefaultMaxPendingChunks` (64) other chunks to go pending —
  and a lossless transfer keeps 1–2 open. **The chunk was stranded for the rest of the transfer.**
- **Fixed**: on a metadata mismatch, `Offer` now drops the **stale partial** and re-establishes the buffer
  from the incoming packet. The newest slicing wins, so any source re-sending the chunk in full completes
  it — which is exactly what chunk-level repair does. Residual cost: two mismatched sources transmitting
  the same chunk *simultaneously* reset each other's progress, a throughput-shaped failure that repair
  retries out of, where stranding was terminal. Both new tests were verified to fail against the old
  behaviour.
- **No version skew is required to hit this.** QA reproduced it with two *same-version* peers on different
  `--datagram-size` values (1372 → 198 packets vs the 1472 default → 184).
- **A budget mismatch is not caught anywhere.** Direct sender→receiver delivery works fine at mismatched
  budgets — the `PacketCount` bound admits both with wide margin — and the only consequence is the
  peer-relay strand, which nothing logs or counts. Hence the explicit must-match contract on
  `--datagram-size`; it is not a claim that misconfiguration fails loudly.
- **Correction to an earlier rationale:** the assembler's 8× `minFragmentBytes` tolerance does **not**
  absorb a budget mismatch. It gates the DoS packet-count bound only; a mismatch fails earlier and
  unconditionally on the `PacketCount` equality check. That claim has been removed from the code comments
  that carried it.
- **Adversarially the fix is an improvement, not a new hole.** Before, one crafted packet could pin a
  chunk's buffer to a slicing nobody else uses and block every legitimate packet for that chunk
  permanently. Now a hostile peer can only reset progress *while it keeps transmitting* — the "degrades
  only while the attack lasts" class the assembler's other bounds already accept — instead of a lasting
  denial from a single datagram.

## Why it is the safe class of change to land before the receiver is fixed

The governing rule: changes reducing *datagrams per byte* are safe now; changes increasing *datagrams per
second* are not. This one presents the receiver with 50,000 fewer datagrams for the same file — it removes
work from the constrained side rather than adding arrival rate to it, so it is structurally immune to the
trap that made M6 round 1 and window=2 regressions. It is also the first change in the log whose datagram
count, wire bytes, wall clock and amplification all move the same way.

## MTU auto-derivation: implemented, then removed — and why that is the right call

Deriving the budget from the named interface's MTU is sound in isolation. Castr multicasts at **TTL=1**, so
there are no routers in the path and *the path MTU is the interface MTU* — `GetIPv4Properties().Mtu − 28`,
clamped to 1472, no probing needed. It was implemented and then **removed in systems-design review**,
because soundness in isolation is not the property that matters here:

**It manufactures a peer mismatch that nobody decided on.** A laptop on a 1500-MTU LAN and a peer behind a
1400-MTU VPN would automatically pick different budgets — and per the strand above, mismatched budgets
silently lose peer-to-peer repair relay, in exactly the sender-offline scenario peer repair exists for. An
explicit flag at least makes the mismatch someone's decision. Two independent reviews reached this hazard
from different directions in the same round, which is the strongest signal available that it is real.

`--datagram-size` stays **explicit-only**, documented in the option help and in `DatagramBudget` as a value
that must match on every sender, receiver and relaying peer in a transfer. All of the measured 1.42× comes
from the 1472 default plus the slicing fix; the flag contributes none of it and exists as the seam for
future jumbo-frame testing.

**If a probe is ever attempted: do not use DontFragment on Windows multicast.** The option is silently
ignored there — it reads back `true` and then fragments anyway, accepting up to 65,507 bytes — so a
DF-based probe reports that a 60,000-byte datagram "fits" and is catastrophically wrong on a 1500-MTU
segment. (It behaves correctly for unicast on Windows and for both on Linux.)

## The budget must be constant for the life of a session

It is an input to `ChunkPacketizer.Split`, so two budgets slice the same chunk into different packet
counts and `Offer` rejects the mismatch — a budget that changed mid-session would make a session reject its
own retransmissions. Enforced structurally rather than by convention: both sessions range-check and capture
it once in their constructor (`WirePacketizer.ValidateMaxDatagramPayload`), `SenderSession` holds a
`readonly` copy rather than re-reading the mutable primary-constructor capture, and the CLI resolves it
once, before the session exists.

## A crash the new knob made reachable

`ChunkPacketizer.Split` could throw **mid-transfer, out of the carousel**, on a configuration that passed
every startup check: the budget is range-checked in isolation, but proof size depends on chunk count, i.e.
on file size and chunk size. `--datagram-size 548` (the floor) with `--chunk-size 8192` on a 1 GB file is
131,072 chunks → depth 17 → a 571-byte proof → `548 − 43 − 571 = −66`. Unreachable while the budget was
pinned at 1200, so **exposing the budget is what made it reachable**. Now validated in
`TransferPreparation.PrepareFileAsync`, where all three terms are known, failing as bad input before a
single datagram is sent and naming both `--datagram-size` and `--chunk-size` since either can be the
culprit. Every proof in a tree has the same step count, so leaf 0's proof is the exact worst case.

## Deliberately not done, with reasons

- **Carrying the sender's budget in the manifest — declined for this stage, with evidence.** It is the
  structurally right answer, but it is *not* additively backward-compatible:
  `ManifestVerifier.VerifySignature` re-encodes the decoded manifest and verifies the Ed25519 signature
  over those bytes, so an old reader that skipped an appended field would produce different bytes and fail
  verification — a **rejected** transfer, not a degraded one. `ManifestCodec.Decode` also hard-rejects any
  `FormatVersion ≠ 1`. This needs a format-version bump and real lockstep, which is what this stage was
  scoped to avoid. Tracked in [[roadmap]].
- **Path-MTU discovery and jumbo frames** — a separate investigation. The seam is `DatagramBudget.Resolve`.
- **Keying fragments by byte offset instead of packet index** — the structural retirement of the whole
  mixed-budget class, and the precondition for ever auto-selecting a budget per peer. Folded into the
  assembler rewrite already tracked from M8 in [[roadmap]].
- **`RepairOptions.MaxChunksPerRequest` was not pinned.** It is a *function* of the datagram budget (the
  fragmentation bound "how many indices fit in one datagram"), so it moves 268 → 336 by construction.
  Disclosed consequence: the data one inbound `CHUNK_REQUEST` datagram can command goes 67 MB → 84 MB at
  256 KiB chunks. **That blast radius has now silently rescaled twice, monotonically worse — 2.2 MB → 67 MB
  → 84 MB** — because the constant is derived from the *datagram budget* while its meaning is *bytes
  served*, and chunk size decouples the two. Promoted to the next milestone: cap
  `SenderSession.HandleChunkRequestAsync` by **bytes served per request** (~8 MB), one sender-side place
  that bounds it regardless of both knobs. Pinning the count here would have made the constant assert a
  falsehood.

## Two arithmetic corrections this produced

- **`ChunkPacketizer.FixedEnvelopeOverhead` is 43, not 47.** The constant's own comment listed the right
  fields and summed them wrong, and the wrong figure had propagated into the derived prediction this stage
  was commissioned against (310 datagrams/chunk rather than 309).
- **M8's recorded 124,218 `CHUNK_PACKET` count reconciles with 309, not 310**: 124,218 − 400×309 = 618 =
  exactly two chunks re-sent, and M8's own table records 2 `CHUNK_REQUEST`s. Under 310 the residue is not a
  whole number of chunks. Rule 3 catching a stale comment through a year-old capture.

## ⚠️ The absolute throughput figures above are loopback-only

Established after these runs were taken: Castr's shipped default group `239.192.55.55` has a **leaked
loopback multicast membership** on this host (`References = 0`, no owning socket), so every benchmark on
the default group — M6 through M9 — never touched the NIC. The **1.42× ratio survives** (the 309 → 184
datagram reduction is path-independent and independently verified), but read every MB/s figure as a
loopback number. This also un-retracts the campaign's "loopback is 3.1× faster" finding and withdraws M8's
"not interface selection" bullet, which tested the one group that already resolved to loopback. Details in
the root-cause section of `docs/benchmarks/throughput-runs.md`.

## Validation

480 tests / 3 skipped / 0 warnings under `-warnaserror` (441 before). `ChaosTransport` real-socket
loss/reorder/duplication tests pass, including 10% sub-chunk loss on 256 KiB chunks — run **unmodified**
first, then re-run after their hardcoded ports were switched to the existing `FreeUdpPort` probe (an M9
benchmark harness hit `WSAEACCES` twice on OS-reserved ports, once on 46101, the port this repo already
documents `DnsService` as holding). The Docker `netem` fan-out tier is green on a **rebuilt** image (7
receivers lossless, 5 at 20% real loss, 9 at 10%), tests unmodified. 52/52 completed measurement runs
SHA-256 byte-identical.

## Where this fits

- [[roadmap]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[m7-repair-amplification]]
- [[m6-throughput-pipelining]]
