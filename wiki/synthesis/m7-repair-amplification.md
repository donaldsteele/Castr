---
type: synthesis
title: "M7 — Repair amplification, PEER_HAVE gossip, and the sender's own echo"
tags: [milestone, protocol, decision, performance]
sources: [castr-project-plan]
created: 2026-07-25
updated: 2026-07-25
---

# M7 — Repair amplification, PEER_HAVE gossip, and the sender's own echo

Three fixes to the self-inflicted congestion loop the post-M6 investigation and the 91-run
measurement campaign identified (see [[m6-throughput-pipelining]], `docs/METHODOLOGY.md` and
`docs/benchmarks/throughput-runs.md`). Implemented in an isolated worktree; **not merged at time of
writing** — round 1 of review returned QA **FAIL** plus systems-design **MERGE-WITH-CHANGES**, and
round 2 addressed those findings.

## What shipped

**P2 — the sender no longer decodes its own transmission.** *A correctness fix, measured neutral on
throughput (−1.3%, a wash) — it must not be credited with any of the amplification or stall results
below.* At the 8 KiB default the fragments on the wire are almost entirely PEER_HAVE, and both role
filters must accept `PacketFragment` (a fragment does not reveal the type it reassembles into), so P2
cannot reduce fragment traffic even in principle: its wire composition is unchanged from baseline
(PACKET_FRAGMENT 20,558 → 20,557). The fragment collapse belongs to P1. What P2 buys is this:
`MulticastLoopback` is on for both roles,
so a sender received, copied, queued and fully `MessageCodec.Decode`d every chunk datagram it had
just emitted, only for `SenderSession`'s handler switch to discard it — which is why the campaign
measured **87-97.5% of datagrams offered to the sender's own socket being dropped**, taking real
`CHUNK_REQUEST`/`JOIN_REQUEST` control traffic with them. Added a `DatagramFilter` delegate (a
delegate, not `Func<>`, because `ReadOnlySpan` cannot be a generic type argument) applied in
`UdpMulticastTransport.ReceiveLoopAsync` against the reusable receive buffer *in place*, before any
allocation or channel slot. `DatagramFilters.Sender` accepts only `ChunkRequest`, `JoinRequest` and
`PacketFragment`; `DatagramFilters.Receiver` accepts everything except `JoinRequest`. Both accept
`PacketFragment` unconditionally, because a fragment carries no information about the type of the
message it will reassemble into, and both accept datagrams shorter than the 2-byte type tag rather
than letting the filter be the thing that silently breaks a message.

**P1 — `PEER_HAVE` coalesced and moved off the state gate.** The whole per-file bitmap was broadcast
on every verified chunk, as an awaited socket send held inside `_stateGate` — total gossip
`FileSize²/(8·chunkSize²)`, and above ~1328 bytes it fragments into multiple datagrams, which is what
actually costs (both sides are per-datagram bound, not byte bound). Now rate-limited to one emission
per `ReceiverSessionOptions.PeerHaveInterval` (default 250 ms) per file, with two exemptions that
cannot wait: a file's first chunk (so peer discovery still happens early) and its bitmap becoming
complete (so peers learn about a complete repair source promptly). The bitmap is snapshotted under the
gate and sent after `_stateGate.Release()`. The message is retained, not removed — it doubles as peer
discovery per [[repair-protocol]].

**P0 — the repair storm bounded.** Four separate changes, only one of which turned out to matter for
amplification:

- `RepairOptions.MaxChunksPerRequest` (268 at the shipped 1200-byte budget, derived from
  `MessageCodec`'s actual `ChunkRequestMessage` encoding against a measured true maximum of 284) keeps
  every request inside one datagram. This does **not** bound amplification; what it fixes is
  all-or-nothing fragment reassembly, which is what made the stall quantum the 5 s `RequestTimeout`
  instead of the 250 ms poll.
- A per-file **carousel watermark**: a missing index is eligible only if the carousel has demonstrably
  passed it. This is the change that actually removes the amplification.
- `MarkRequested` — `_pending` is marked after a confirmed send, not inside `PlanRepairs`, so a request
  that never reached the wire no longer suppresses a retry for a full timeout.
- `RepairOptions.MaxRequestsPerPass` (default 4), added in round 2: the real amplification bound, and
  the reason the watermark no longer has to be right for amplification to stay bounded.
- Randomized jitter and exponential backoff, which [[repair-protocol]] had documented as implemented
  for three review rounds while the code had neither.

Sender and receiver both cap chunks served per inbound `CHUNK_REQUEST`, bounding head-of-line blocking
in a handler that is awaited inline on the receive loop.

## Round 2: a liveness regression QA reproduced

The carousel watermark's safety valve — "if the chunk stream goes quiet, presume the carousel is done
and make everything eligible" — was originally **one global last-chunk-arrival timestamp refreshed by
any chunk-bearing datagram for any file**. That is a liveness bug, not a tuning choice:

- Repair responses are multicast by design, so **any** peer's repair traffic refreshed **every** other
  receiver's valve. QA hung a transfer with no adversary: 12,000-byte file, final chunk's only carousel
  delivery dropped, one already-held `ChunkData` re-injected every 200 ms → never completed, where
  `main` recovers on the first repair pass.
- Systems-design independently found the multi-file form: while file 1 carousels, file 0's timer is
  continuously refreshed, so file 0's lost tail is unrequestable until the entire carousel finishes.

Fixed by keying the valve on **that file's watermark actually advancing, from a carousel delivery** —
which makes a re-delivery of an already-seen index inert, exactly what it is as evidence about carousel
position. `ChunkResponse` never refreshes it. `ChunkPacket` is deliberately byte-identical between
carousel and repair sends (that is what lets a receiver accumulate a large chunk across rounds) so its
origin is genuinely unknowable; that is sound rather than a gap, because a repair response can only
advance the watermark for an index *above* it, which can only happen after the valve already opened.
Both failure modes are now regression tests, and both were verified to fail against the old behavior
before being kept.

Round 2 also replaced the jitter, which both reviewers independently found **inert**: it counted repair
passes, decremented unconditionally every pass before the watermark decided whether anything would be
sent, and burned down within ~1 s — during exactly the window the watermark suppresses everything
anyway — then never applied again, so all receivers re-aligned on the same 250 ms grid. It also leaked
a `Task.Delay(250)` literal from two other assemblies into wire behavior. Now per-chunk wall-clock:
`InitialRequestJitter` (500 ms) defers a chunk's *first* request, and `RetryJitterFraction` spreads
each retry, both drawn from the injected `Random` and measured on the injected `ISystemClock`.

## Round 3: the same modelling error, in the opposite direction

Systems-design cleared round 2's fixes but found the valve broken again — and diagnosed it as **the
same error as the round-1 hang**, not a new one. Round 2 seeded *every* file's idle timer at manifest
acceptance, but the sender carousels files sequentially, so file 1's valve opened one threshold after
the manifest — **before its carousel had begun** — making its whole chunk set repair-eligible while
file 0 was still transmitting, with the round-robin cursor feeding it budget on alternating passes.
The premature-repair storm, re-created for every file after the first as a sustained drip.

The generalisation worth keeping: **at seed time, "never reached" and "finished" are indistinguishable,
and both versions treated them identically.** Round 1 refreshed one global timer from any traffic and
hung a started file; round 2 seeded optimistically and over-requested an unstarted one. A file's valve
may now open only once that file is known to have been transmitted, established three ways:

- **started** — a chunk of it has been seen, so its own idle timer is meaningful (the tail-loss case);
- **carousel moved past it** — a *later* file has started, which given strictly ordered transmission is
  exact proof this file's carousel already ran and everything was lost. Not a heuristic;
- **session quiet** — no file's watermark has advanced for a whole threshold. The escape hatch for the
  one case neither per-file signal can see: the last (or only) file with every chunk lost. Refreshed
  only by genuine carousel advancement, so repair traffic cannot hold it shut.

Round 3 also corrected a comment that asserted something false. The justification for treating
`ChunkPacket` as a carousel delivery claimed a repair response can only advance the watermark after the
valve has opened — **untrue in the multi-receiver case, which is the design's whole point**, since the
valve that opened may be another receiver's. The real argument is stronger and is now recorded instead:
the refresh is gated behind a *strict* watermark advance, the watermark is monotone and bounded by
`ChunkCount-1`, so there are at most `ChunkCount-1` refreshes in a file's lifetime — either an index
falls at or below the watermark (eligible without the valve) or the watermark stops advancing and the
valve opens. **A termination proof, not a plausibility argument.** Given this project's own recorded
history of comments asserting properties the code lacked, correct behavior carrying an incorrect proof
is exactly the artifact to distrust.

Three further precision fixes: `PlanRepairs` now stops collecting candidates once the pass budget is
covered (the per-pass cap previously bounded only plan *emission*, after a full `GetPeersWithChunk`
sweep of the entire missing set under the state gate); the retry jitter is applied **after** the
backoff clamp, not before, so receivers do not collapse into lockstep at exactly `MaxRequestTimeout`
in the steady state the jitter exists for; and `MaxBackoffDoublings` dropped 4 → 2, because at a 5 s
base and a 20 s clamp the third and fourth doublings were unreachable dead configuration while the
docs advertised a 16× ladder.

QA's mutation testing found one real coverage gap: removing *only* the `fromCarousel` guard left the
entire suite green, because every test used replayed `ChunkData` as noise, which the novelty
early-return alone renders inert. Nothing exercised the `fromCarousel: false` branch at all. Closed
with a test whose noise is a novel above-watermark **`ChunkResponse`** — the actual multi-receiver
shape — verified to fail under that mutation.

## The measurement claim was withdrawn

The implementer's first report claimed **+112.6% goodput (2.13x)**. QA refuted it arithmetically: the
post-fix 9.96 MiB/s ≈ 10.4 MB/s is *slower* than the campaign's unfixed warm baseline of 13.65 MB/s
and slower than its `repair off` row (12.12 MB/s), and the run set's own 4.68 MiB/s baseline is 2.8x
below the documented warm baseline — more than the 1.94x page-cache confounder explains. **The
doubling was recovery from a degraded host state, not an absolute gain**, and the claim is retracted in
`docs/benchmarks/throughput-runs.md`.

What the data does support, and what the merge case now rests on:

- **Wire amplification 2.39x → 1.13x** at 80 MB, 2.92x → 1.15x at 320 MB — landing on the campaign's
  independently-measured `repair off + PEER_HAVE off` row (1.12x).
- **Duplicate chunk transmissions −99.7%** (120,060 → 324 datagrams).
- **PEER_HAVE fragment gossip 20,557 → 68 datagrams**; the quadratic term is gone, not reduced.
- **The periodic stall pattern eliminated** — 24-25 stalls of ~468 ms on a ~695 ms period → 0, with the
  wire busy at ~10 MiB/s throughout the baseline's stalls. Both reviewers accepted this on mechanism
  independently of the goodput numbers.
- **Two liveness bugs fixed** (the ones round 2 found), which is arguably the strongest reason to merge.

The campaign's warning that removing premature repair costs **−11%** — because the redundant repair
stream is currently the sender's only send-path parallelism — is **not refuted**. Its datagram-rate
arithmetic stands (base 48,132 dgrams/s vs repair-off 21,722/s, a 2.22x difference; this run set's
~10 MiB/s is a quarter of that). An implementer hypothesis that the watermark preserves the repair
stream for genuine gaps was refuted by the implementer's own duplicate-count data. One untested
possibility recorded but not claimed: P2 is a lever the campaign never measured and may be offsetting
part of the predicted regression.

## Still open

- **Net goodput effect is unresolved.** Needs a warm, interleaved, n≥3 A/B on a host that reproduces
  the 13.65 MB/s baseline, ideally by someone who did not write the change. Now tracked in [[roadmap]]
  as the `{P0, P0+w2, P0+w4}` send-window matrix: since M7 removes the redundant repair stream that was
  the sender's only send-path parallelism, a deliberate send window is the obvious candidate for
  restoring it, and that matrix is what would either recover the ≤11% risk or confirm it.
- **A known, accepted imprecision in the watermark.** Because repair sends are sparse, a single
  high-index repair packet lifts the scalar watermark over a range the carousel never sent, so
  `index <= watermark ⇒ has been transmitted` does not strictly hold. Reachable only via asymmetric
  false-idle across receivers and bounded by `MaxRequestsPerPass`. Deliberately not fixed: at the 8 KiB
  default *every* chunk travels as `ChunkPacketMessage`, whose carousel-vs-repair origin is
  unknowable by design, so gating the watermark lift on origin would buy nothing. The ambiguity is
  structural; the per-pass cap is the right mitigation.
- **Chunk size — ✅ DONE (2026-07-25, M8), and it re-scored this page's own numbers.** `CastrPaths.DefaultChunkSize`
  is now 262144. Measured **1.33x**, not the campaign's +77%, because that +77% was pre-M7: a bigger chunk used
  to collapse the premature-repair storm as well as amortise the proof, and M7 has since claimed that half.
  Systems-design's prediction that **P1's value approaches zero at 256 KiB was confirmed** — a lossless 100 MB
  transfer now emits **33** PEER_HAVE datagrams and **2** CHUNK_REQUESTs total. P1 was nevertheless kept: it is
  free when it does not bind and it is the only thing keeping the quadratic gossip term harmless for anyone who
  lowers `--chunk-size`. All four M7 constants were re-validated against the new regime and **none changed
  value**; what changed is that two of their doc comments were stating figures that had gone stale by 32x.
  See the 2026-07-25 M8 section of `docs/benchmarks/throughput-runs.md` and [[roadmap]].
  - **`MaxChunksPerRequest` (268) — kept.** Every term in its derivation (the datagram budget and the
    `ChunkRequestMessage` encoding) is independent of chunk size, so it remains exactly correct as the
    *fragmentation* bound it always was. One consequence did move 32x and is now recorded in code: 268 indices
    now command **67 MB** of data from one request datagram, where it was 2.2 MB.
  - **`MaxRequestsPerPass` (4) — kept, but it no longer bounds amplification for ordinary files.** `4 x 268 =
    1,072` chunks/pass exceeds the entire chunk count of any file below **268 MB** at 256 KiB. It still bounds
    gate-held planning work and a hostile receiver's burst, which are now its primary justification.
  - **`CarouselIdleThreshold` (1 s) — kept, and this was the flagged high-risk one.** The 32x-fewer-advances
    argument predicts a 32x thinner margin; measurement says otherwise. The *mean* advance gap scales as
    predicted (0.65 ms -> 15.3 ms) but a false idle is driven by the *maximum*, which is set by host scheduling
    jitter and barely moved (19.6 ms -> 27.7 ms). **Margin 51x -> 36x, not 51x -> 1.6x.** Zero gaps over 500 ms
    in 16 runs; 382 ms worst on a degraded path, still no false idle.
  - **`PeerHaveInterval` (250 ms) — kept**, per the reasoning above.
- **Datagram budget.** Still 1200, deliberately untouched by M8. The campaign's 7.2x figure pairs it with the
  chunk size, so it now needs re-measuring against the post-M7/post-M8 baseline rather than assuming the pairing
  still multiplies — M8 is a worked example of exactly that assumption failing.
- **Suppression by overhearing, and repair-response deduplication** — still the two unimplemented rows
  in [[repair-protocol]]'s design-intent table. Note the target field that response dedup needs cannot
  simply be appended: **a new message type is additively backward-compatible (unknown tags throw in
  `Decode` and both sessions swallow it), but appending a field to an existing message is not** — a new
  reader hits end-of-span on an old message and throws. New type or version bump.
- `CAROUSEL_PASS_COMPLETE` as the principled replacement for the watermark heuristic, and
  source-identity suppression (a separate send socket on an ephemeral port) as the principled
  replacement for type filtering. Both noted, neither in scope.
- Moving the disk write out of `_stateGate` (P3).

## Where this fits

- [[roadmap]]
- [[repair-protocol]]
- [[wire-protocol]]
- [[m6-throughput-pipelining]]
- [[tech-stack]]
