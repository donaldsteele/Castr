---
type: synthesis
title: "M6 — Send-path throughput/pipelining: investigation and fix"
tags: [milestone, protocol, decision]
sources: [castr-project-plan]
created: 2026-07-25
updated: 2026-07-25
---

# M6 — Send-path throughput/pipelining: investigation and fix

M6 is complete: all three UI surfaces (`Castr.Cli`, `Castr.Tui`, `Castr.Gui.Desktop`) plateaued around ~1.6-2.4 MB/s while recording [[roadmap]]'s M5 showcase demos, well below what loopback/LAN hardware should allow. The investigation ran three implementation rounds, each independently reviewed by a QA agent and a systems-design agent before the next round proceeded — the first round's fix turned out to be the wrong lever; the second round found and fixed the real bottleneck. Final state: **367 tests passing** (up from 359 pre-M6), 0 warnings, real-socket `ChaosTransport` loss/reorder/duplication integration tests unmodified and green throughout all three rounds. Merged to `main`.

## What was ruled out first

Before any code changed: explicit rate-limiting or throttling anywhere in the send path (none found by code search), and multicast traffic routing over a real NIC instead of true loopback (ruled out by forcing `--interface "Loopback Pseudo-Interface 1"` explicitly on both sender and receiver in a real empirical test — no meaningful throughput change).

## Round 1: pipeline the sender (partially right, wrong default)

Direct code read of `SenderSession.RunChunkCarouselAsync` → `SendChunkAsync` → `SendMessageAsync` confirmed the send path was fully sequential — one `await transport.SendAsync(...)` per ~1200-byte wire packet, no pipelining or batching. For an 80 MB demo file at M3's chunk/wire-packet sizes, that's on the order of 70,000 one-at-a-time awaited sends.

Changed `RunChunkCarouselAsync` and `HandleChunkRequestAsync` to use `Parallel.ForEachAsync` with a bounded concurrency window (new `sendWindowSize` constructor parameter). Verified via `PacketReassembler`/`ChunkPacketAssembler` that concurrent, out-of-order sends are safe — both are already fully index-keyed and order-independent, so this required no wire-format change. Shipped `DefaultSendWindowSize = 2`.

Real two-process (`castr send`/`castr receive`) benchmarking found throughput does **not** scale monotonically with concurrency: window 1-2 gave noisy, marginal results (occasionally ~1.8x faster, occasionally a wash, one measured regression); window 3-4 was a consistent 2-5x *regression*; window 64 (the first value tried) caused an outright stall — sender reported 100% complete while the receiver sat frozen under 40%. Working theory: the old sequential loop was accidentally providing flow control, pacing packet emission to roughly what `ReceiverSession`'s processing could keep up with.

## Round 2: the real fix is receiver-side

Independent QA and systems-design review, run in parallel against the round-1 worktree, both rejected shipping `DefaultSendWindowSize = 2`:

- **QA** independently reproduced the window=64 stall exactly, and re-measured window=2 as a *consistent* ~1.8-2.7x regression versus window=1 — worse than round 1's own "roughly neutral" characterization, not just occasionally so. QA also found the "window" wasn't actually a global bound (the carousel and repair-handler loops each cap concurrency independently, so simultaneous heavy repair traffic could transiently double real concurrent sends) and a new bug the change made much more likely to trigger: `Castr.Tui.ThroughputSampler.Record` mutated an unlocked `Queue<T>` from what was now routinely concurrent sends.
- **Systems-design** independently confirmed via direct code read — not speculation — that `ReceiverSession.RunAsync`'s per-packet handling (Merkle/AEAD verify → **disk write** → outbound `PEER_HAVE` multicast broadcast) was fully serialized under one lock shared with the repair pass, and `UdpMulticastTransport.ReceiveAsync` had no decoupling from that chain — so the OS receive buffer only drained as fast as the whole chain ran. Concurrent sending broke the old loop's accidental self-throttling and overwhelmed it. Recommended: revert to `sendWindowSize=1` (a true no-op, since the carousel and repair listener already ran as concurrent tasks even pre-M6) and fix the receiver side instead — decouple the socket-drain loop from processing via a bounded channel.

**Independent research, user-sourced**: `uftp-multicast` (a mirror of Dennis Bush's UFTP, a mature C implementation reaching near-wire-speed multicast transfer) corroborated the receiver-capacity diagnosis and surfaced one concrete lever neither review agent had flagged — explicit UDP socket buffer sizing (`SO_RCVBUF`/`SO_SNDBUF`, raised past the OS default). Confirmed by direct code read that `UdpMulticastTransport.cs` never set either option, running on whatever the OS default was.

Round 2 implemented all of this: reverted the default to 1; added the missing lock to `ThroughputSampler.Record`; decoupled `UdpMulticastTransport`'s socket read into a dedicated reader task feeding a bounded `Channel<ReceivedPacket>` (capacity 4096) independent of consumer speed; set an explicit, best-effort 4 MB `SO_RCVBUF`/`SO_SNDBUF`; added `castr send --send-window-size` (default 1) so a validated deployment can opt into a higher window without a rebuild.

**Re-benchmarked with the receiver-side fix in place**, same real two-process methodology: window=2 became a *consistent* ~1.4-1.6x win with no collapse — at chunk=8192, window=1 ≈8.1 MB/s vs window=2 ≈11.2 MB/s; at chunk=60000, ≈10.1 vs ≈16.4 MB/s. A new 3-receiver benchmark (one receiver deliberately pinned to a single low-priority CPU core, simulating a weak machine — the actual "one send, many receivers" scenario) showed the same pattern: window=2 completed faster than window=1, every receiver always byte-identical, no failures at any window tested. window≥4 no longer collapses catastrophically like round 1's window≥3, but gives back most of the gain.

A shared-`SemaphoreSlim` gate was prototyped to close the carousel/repair independent-window gap properly, but measured a reproducible ~30% throughput regression even under zero contention — reverted, and documented in-code as a known limitation instead, since at the shipped default (1) the gap's worst case is exactly window=2, a value round 2's own data calls consistently safe.

Despite the encouraging round-2 numbers, the default was deliberately kept at 1: a second good-looking number from the same benchmarking process/person isn't an outside second opinion, especially after round 1's initial default had already looked plausible and turned out wrong under closer scrutiny.

## Round 3: final sign-off

Both reviewers returned to review the actual round-2 diff (not a self-report):

- **Systems-design** confirmed the channel decoupling is a real fix, not cosmetic — `BoundedChannelFullMode.Wait` (the default) means the channel genuinely blocks under sustained mismatch rather than dropping, converting an accidental zero-slack coupling into deliberate bounded buffering. Recommended **merge**, with two non-blocking should-fixes: `ReceiveLoopAsync`'s catch-all was silently swallowing unexpected exceptions instead of surfacing them, and the bounded channel (capacity-bounded, not byte-bounded) is a somewhat larger memory-exhaustion surface than the old unbuffered design, worth a security-lens sanity check against the same-subnet threat model even though it's bounded. Also flagged: `ReceiverSession` itself is unmodified — the channel buys slack against bursts, it doesn't raise the sustained processing ceiling.
- **QA** verdict PASS-WITH-CONCERNS: independently confirmed the bounded channel provides real backpressure (traced the block-vs-drop question directly in code), independently reproduced the window=1-vs-2 throughput win and, critically, independently reproduced the round-1 stall's disappearance — window=64 completed in 47s instead of hanging forever, the single strongest piece of evidence the receiver-side diagnosis was correct. Found one new regression neither prior pass caught: `UdpMulticastTransport.DisposeAsync()` was no longer idempotent (threw `ObjectDisposedException` on a second call, unlike its sibling `UdpUnicastTransport` and unlike the pre-M6 implementation) — narrow blast radius, not hit by any shipped call site, but a real regression against `IAsyncDisposable` convention.

Round 3 fixed all three: added a `_disposed` guard to `UdpMulticastTransport.DisposeAsync` (with a new dedicated test file, `UdpMulticastTransportTests.cs`, that didn't exist before); hardened `ReceiveLoopAsync` to surface unexpected exceptions via `Writer.TryComplete(ex)` instead of silently ending the stream; added a code-level comment on `SenderSession._sendWindowSize` stating explicitly that the carousel/repair double-window gap must be re-validated before the default (or any recommended `--send-window-size`) goes above 1. 367 tests passing, 0 warnings.

## What's still open

- **The default stays at 1.** Both reviewers agreed independently: all validation to date is single-machine, same-OS, loopback-only — a materially different regime from real NIC hardware, cross-machine scheduling, or a genuinely separate weak device. Bar for raising it: independent, cross-machine, real-LAN validation, ideally by someone who didn't write the round-2/3 benchmarks. `castr send --send-window-size 2` is available today for anyone who wants to opt in ahead of that.
- **The carousel/repair "up to 2x window" gap** is real, measured, and deliberately left open (a shared-gate fix cost ~30% throughput for zero contention) — self-limits to a validated-safe spike of 2 at the current default, but would need re-validation before any higher default or recommended value.
- **`Castr.Core.Discovery`'s mobile swarm-pull transport** (`UdpUnicastTransport`/TCP framing) was not touched — it plausibly has the same synchronous receive-then-process shape the multicast tier had before this fix, per systems-design's round-3 review, but wasn't independently verified.
- **`ReceiverSession`'s own per-packet processing cost** (verify, decrypt, disk write, and a synchronous outbound broadcast on every verified chunk) is unchanged by M6 — the channel buys slack against bursts, not a higher sustained ceiling. A future pass could target this directly if higher sustained throughput is ever needed.

## Where this fits

- [[roadmap]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
