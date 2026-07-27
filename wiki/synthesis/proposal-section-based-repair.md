---
type: synthesis
title: "Proposal: section-based repair gating (replace the carousel-idle heuristic)"
tags: [protocol, decision, proposal]
sources: [castr-project-plan]
created: 2026-07-25
updated: 2026-07-27
---

# Proposal: section-based repair gating

**Status: PROPOSAL. Not a decision, not scheduled, no code written.** Raised during the M8 review
cycle. Recorded here so the reasoning survives, and so the design can be contested before anyone
implements it — this area has already produced two liveness bugs, and both were design errors rather
than coding errors.

## The idea

Today a receiver decides whether a chunk is eligible for repair by *inferring* that the sender has
already transmitted it. Replace that inference with an explicit signal from the sender: a
**section-completion message** marking the end of a bounded run of chunks. A receiver requests repairs
only for sections the sender has declared finished.

The refinement that matters: gate on **sections**, not on the end of the whole transmission. See
"Why not wait for the end of the transfer" below — that variant is the intuitive version and it is
the wrong one.

## What this is *not*: a throughput change

This must be stated up front, because the intuition that motivates the idea — "stop the packet storm,
go faster" — is no longer accurate. **The storm is already fixed.** See [[m7-repair-amplification]]
and `docs/benchmarks/throughput-runs.md`:

| Metric | Pre-M7 | Post-M7 | Post-M8 | Post-M9/M11 (M12a) |
|---|---|---|---|---|
| Duplicate chunk datagrams (100 MB) | 120,060 | **324** | — | **0** |
| Wire amplification | 2.39× | 1.13× | 1.05× | **1.029×** |

At 1.029× the theoretical floor is 1.00×, so **there is under 3% of wire traffic left to remove in
total**, and this proposal could not capture all of it. Meanwhile the measured bottleneck is not wire
volume at all: both sides are bound by per-datagram cost, and `SendToAsync` alone is 66% of sender CPU.

**M12a strengthened this section rather than changing it, and killed the one number that pointed the
other way.** See [[m12a-fanout-baseline]]. Measured at 1, 2, 3 and 5 receivers, on loopback and on the
real wire: `CHUNK_PACKET` is **73,600 in every arm** — 400 chunks × 184 datagrams, no chunk sent twice —
`CHUNK_REQUEST` and `CHUNK_RESPONSE` are **zero**, and amplification is **1.029× flat against receiver
count**. The **5.08× fan-out amplification figure that this workstream was partly scoped against is
withdrawn**: it was loopback, pre-M9/M10, and from the withdrawn 60,000-byte-datagram configuration.

The practical consequence for anyone implementing this: **on a lossless segment at N ≤ 5 the repair path
never engages at all**, so a benchmark there cannot show this change doing anything, in either direction.
Validate it under real loss (the Docker `netem` E2E tier) or not at all.

Worse for the throughput case: the M7 campaign measured deferring repair *in isolation* at **−11%**,
because the redundant repair stream was inadvertently supplying the sender's only send-path
parallelism at `DefaultSendWindowSize = 1`. Any further deferral inherits that risk.

**Expected throughput effect: approximately zero, possibly slightly negative.** Do not justify this
work on performance. The throughput levers are tracked separately — the `{P0, P0+w2, P0+w4}` window
matrix and the datagram-budget item in [[roadmap]].

## The actual motivation: deleting a bug class

The current mechanism is a heuristic standing in for information the protocol does not carry. A
receiver concludes "the sender has passed this chunk" from a watermark plus a 1-second
`CarouselIdleThreshold`, guarded by a three-condition valve (file started / a later file started /
session quiet).

That heuristic has produced **both** liveness defects in this workstream, two rounds apart, and
neither reviewer predicted the second from the first:

- **M7 round 1 hung.** A single global idle timer was refreshed by any chunk-bearing datagram
  including `CHUNK_RESPONSE`, so repair traffic — which is multicast — masked a never-reached tail
  indefinitely. Reproduced: a 12 KB file with the final chunk dropped and one already-held chunk
  replayed every 200 ms never completed.
- **M7 round 2 over-requested.** Per-file timers were seeded at manifest acceptance, so a file whose
  carousel had not started yet became fully repair-eligible one threshold later — the premature-repair
  storm re-created per file as a sustained drip.

Both are the same root error: **at seed time, "never reached" and "finished" are indistinguishable,
and the code treated them identically.** An explicit end-of-section signal makes them distinguishable
by construction, which removes the class rather than patching instances.

Secondary benefits:

- **Deletes `CarouselIdleThreshold`**, which both reviewers independently called arbitrary — it is
  derived from nothing in the codebase (not the repair poll, not `RequestTimeout`, not the measured
  ~600 ms sender oscillation it must clear).
- **Restores an invariant M8 regressed.** After the chunk-size increase, `MaxRequestsPerPass` no
  longer bounds amplification for any file under 268 MB, leaving the carousel watermark as the *sole*
  defense against a full-file storm. An explicit signal is a second, independent guard.
- **Removes a documented imprecision.** Because repair sends are sparse, a single high-index repair
  packet lifts the scalar watermark over a range the carousel never sent, so
  `index ≤ watermark ⇒ has been transmitted` does not strictly hold today.

## Prior art: this is UFTP's design

Confirmed from UFTP's source during the M6/M7 investigation (see `docs/METHODOLOGY.md`). UFTP uses
three interlocking guards Castr lacks:

1. Receivers arm a NAK timer **only at a section boundary**, never mid-section, so they never ask for
   data still in flight.
2. The server **explicitly discards** status for the section it is currently transmitting.
3. Repair runs in a **later pass**, gated on end-of-pass, never interleaved with first transmission.

Castr's watermark approximates (1) and (3) by inference. This proposal makes them explicit.

Note the correction recorded in `docs/METHODOLOGY.md`: UFTP's throughput advantage is **not** its
congestion control (its default rate is 1000 Kbps, slower than Castr; its wire-speed mode is unpaced
exactly like Castr). Its advantage is feedback discipline — which is what this proposal borrows, and
another reason not to expect a speed win from it.

## Design sketch

**A new message type is additively backward-compatible.** `MessageCodec.Decode` throws on unknown
tags, and *both* `ReceiverSession.HandlePacketAsync` and `SenderSession.TryDecode` already swallow
that. So an old receiver ignores the new message and falls back to today's timeout path. Tags 1-15 are
in use; 16 is free.

**Critical asymmetry — do not append a field instead.** An old reader tolerates trailing bytes, but a
new reader hits end-of-span decoding an old message and throws. A new *type* is safe; extending an
existing message is not. Any future `Target` field on `ChunkRequestMessage` has the same constraint.

Sketch:

- `SECTION_COMPLETE(sessionId, fileIndex, sectionIndex)` — sender emits after finishing a section's
  carousel run. Emit more than once (cheap, and it is lossy multicast).
- Section size in the manifest, or derived from chunk count. Needs to be bounded in *chunks* so it
  does not scale pathologically with file size the way the gossip term did.
- Receiver: a chunk is repair-eligible once its section is declared complete. The existing watermark
  becomes a fallback for peers that never send the message, not the primary mechanism.
- Sender: consider discarding `CHUNK_REQUEST` for the section currently being transmitted, as UFTP
  does — cheap, and it closes the same race from the other side.

**What it would delete:** `CarouselIdleThreshold`, the three-condition valve, `_carouselAdvancedAt`,
and the session-quiet fallback. That is a meaningful net simplification of the most defect-dense code
in the repair path.

## Why not wait for the end of the transfer

The intuitive version — receivers stay silent until the whole transmission finishes — is worse than
sections, and worse than what ships today:

- A receiver that loses chunk 3 of 400 must sit through the entire carousel before it may ask. On a
  multi-gigabyte transfer that is minutes of known-missing data it is forbidden to request.
- It pushes *all* repair into a tail after the sender has moved on, converting a smooth transfer into
  a fast phase plus a slow serialized recovery phase — the exact burst-then-stall shape the original
  field report complained about, reintroduced deliberately.
- It removes the overlap that currently lets repair for early sections proceed while later sections
  transmit.

Sections keep repair bounded and overlapping-but-not-colliding, which is the property actually wanted.

## Open questions

- **Section size.** Fixed chunk count, fixed byte count, or adaptive? It interacts with
  `MaxChunksPerRequest` and with the byte-denomination follow-up in [[roadmap]] — ideally sections are
  byte-denominated for the same reason those should be.
- **Does it cost the −11%?** The campaign's finding was that repair traffic supplied the sender's only
  concurrency. If sections make repair strictly later, does that regress again? Likely entangled with
  the window-matrix follow-up; **measure them together, not separately.**
- **Multi-round carousel.** [[m1-core-summary]] tracks repeating carousel rounds as a future addition.
  Sections compose with it naturally, but the interaction needs thought.
- **Does the watermark stay as a fallback forever**, or is there a version gate that eventually removes
  it? Keeping two mechanisms indefinitely is its own maintenance cost.
- **Does this help or hurt the mobile swarm-pull tier?** That path has no carousel at all, so sections
  may be meaningless there — check before assuming symmetry.
- **What is the acceptance evidence, now that the lossless case shows nothing?** M12a measured zero
  repair traffic at N ≤ 5 on a lossless segment, so the only place this change is observable is under
  loss or at receiver counts one host cannot produce. The Docker `netem` tier is the available vehicle;
  decide up front what it must show, because "no regression on a clean LAN" is not evidence of anything
  here.

## Prerequisites

- **M8 must merge first.** ✅ Merged (`50e4cf4`).
- **M12a must land first**, so the design is argued against numbers that describe shipped code. ✅ Done
  2026-07-27 — and it removed one of the two figures this work was scoped against. See
  [[m12a-fanout-baseline]].
- Should be specified and reviewed *before* implementation. The two prior defects here were design
  errors that survived code review precisely because the design was never written down separately from
  the code.

## Where this fits

- [[roadmap]]
- [[repair-protocol]]
- [[wire-protocol]]
- [[m7-repair-amplification]]
- [[m6-throughput-pipelining]]
- [[m12a-fanout-baseline]]
