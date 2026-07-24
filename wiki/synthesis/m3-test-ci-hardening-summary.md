---
type: synthesis
title: "M3 — Test/CI hardening: implementation summary"
tags: [milestone, protocol, security, platform-quirk]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# M3 — Test/CI hardening: implementation summary

M3 is complete: the real two-level chunk/wire-packet split finally landed in `Castr.Core` (closing a gap carried since [[m1-core-summary]]), a genuine multi-container E2E fan-out suite now drives the shipped `castr` binary under real kernel-level packet loss, a dedicated security test pass filled several coverage gaps, a long-flaky TUI test got a real fix, and — the milestone's biggest surprise — CI turned out to have been silently broken on `macos-latest` since M1, which is now fixed and independently verified on real GitHub Actions runs. Full solution: **295 tests passing** (Core 236, Cli 30, Tui 16, Gui 7, IntegrationTests 6), plus a separate opt-in Docker-gated E2E tier (3 tests), 0 build warnings. Two rounds of QA: a dedicated review of the chunk/wire-packet split (PASS-WITH-CONCERNS, one defect fixed), then a combined review of the full M3 surface (PASS).

## The chunk/wire-packet split (the M1 gap, finally closed)

`src/Castr.Core/Protocol/WirePacketizer.cs`/`PacketReassembler.cs` (generic MTU-safe fragmentation for large control messages — `PacketFragmentMessage`, tag 10) and `ChunkPacketizer.cs`/`ChunkPacketAssembler.cs` (chunk-data-specific — `ChunkPacketMessage`, tag 11) implement the two-level chunking [[wire-protocol]] always described but M1 never built. Chunk ciphertext is sliced into ordered, identity-keyed packets (`(fileIndex, chunkIndex, packetIndex)`) at a configurable wire-packet size (default 1200 bytes, the documented MTU-safe target); the Merkle inclusion proof rides only on packet 0. A receiver accumulates a chunk's packets **across repair rounds and across sources** (proven by a 256 KB chunk completing byte-identically at 10% real per-packet UDP loss — statistically impossible without cross-round accumulation), so large chunks now survive real loss rather than needing a single lossless round. Encryption/hashing happen entirely above this layer — chunks are encrypted and Merkle-hashed first, then sliced — so packetization is a pure transport-layer concern with zero interaction with [[security-model]]'s crypto.

QA found the dedicated chunk-split review's implementer had actually *undersold* the fix: default 8 KB chunks now genuinely packetize down to ≤1200-byte datagrams (verified with a crafted-packet harness and byte-level capture — largest datagram observed was exactly 1200 bytes), not merely "chunks over 65 KB stop crashing." A stale comment claiming otherwise (written mid-task while updating the E2E fixture's loss filter) was corrected once QA traced the actual threshold logic.

`Castr.Cli`'s M2-era `--chunk-size` fast-fail guard (`MaxSafeUdpPayloadBytes`/`ChaCha20Poly1305TagBytes`, capped at ~65 KB) is gone; `CastrPaths.MaxChunkSize` is now a 16 MiB memory-safety ceiling instead. A real two-process loopback transfer at `--chunk-size 262144` (256 KB, well above the old ceiling) completes SHA-256 byte-identical.

## Defect found and fixed: unauthenticated reassembly memory exhaustion

The dedicated QA pass demonstrated a real, pre-authentication DoS: both `ChunkPacketAssembler.Offer` and `PacketReassembler.Offer` pre-allocated `new byte[PacketCount][]` sized directly from an attacker-controlled wire field, with no upper bound — a single 31-byte crafted packet claiming `PacketCount = 20,000,000` allocated ~152 MB, and `PacketCount = int.MaxValue` threw an uncaught `OutOfMemoryException` that faulted the receive loop. `PacketReassembler` is reachable **pre-authentication** (every raw datagram is fed to it before any manifest/trust check), so this needed no valid session or secrets to trigger. Fixed: `ChunkPacketAssembler` is rebuilt per-transfer once a manifest is trusted, bounded to that transfer's actual chunk size (plus AEAD tag) rather than a generous default, with an LRU-style cap on concurrent pending chunks; `PacketReassembler` bounds `TotalLength`/`PacketCount` against a fixed 16 MiB ceiling. A related, smaller pre-existing bug (out-of-range `chunkIndex`/`fileIndex` from the wire threw an uncaught `ArgumentOutOfRangeException` in the receive loop — not new to M3, but newly reachable via the added packet path) was fixed alongside it: indices are now range-checked and invalid packets silently dropped. 10 new regression tests lock both in. Re-verified independently (not just trusting the fix agent): 0 warnings, 295/295 passing.

## Testcontainers E2E fan-out suite

`tests/Castr.Core.E2ETests/` now drives the actual shipped `castr` CLI binary across separate Docker container network namespaces (`Infrastructure/CastrClusterFixture.cs`, `CastrFanOut.cs`), gated behind `[E2EFact]` (`CASTR_E2E` env var + reachable Docker) so a plain `dotnet test` skips it in ~5ms. Docker's default **bridge** network passed real IP multicast with zero special configuration — verified first with a raw two-container multicast probe, then with full `castr` runs. Confirmed real fan-out: 7 receivers/no loss, 5 receivers/20% real `tc netem`-induced loss (249 kernel-dropped packets), 9 receivers/10% loss (167 dropped) — all byte-identical via repair. The netem loss filter matches on IP total-length (`0x0400/0xfc00`) specifically because chunk traffic no longer IP-fragments post-packetization; the combined QA pass cross-checked this against the chunk-split QA's traffic capture and confirmed it's accurate (not just "still happens to work"). A documented harness constraint: `SenderSession` broadcasts `ANNOUNCE`/`MANIFEST` exactly once with no re-request path, so the harness starts all receivers before the sender and can't itself exercise manifest loss — a real, pre-existing scope trim (see [[m1-core-summary]]), not a new gap.

## Security test pass

11 new tests in `Castr.Core.Tests` (built on top of already-solid path-traversal and tamper coverage): null-byte-injection path traversal; a composed Merkle-position + AEAD-content binding test (proving the two independent checks [[security-model]] describes actually compose — a relabeled-but-Merkle-valid ciphertext is caught by the AEAD tag); MITM X25519-key-swap rejection (a swapped encryption key in the manifest breaks the Ed25519 signature — previously only verified as a throwaway QA repro, now permanent); trust-store tampering (malformed JSON and a corrupt on-disk file both fail closed rather than silently starting empty; duplicate conflicting entries resolve last-in-file-wins); and TOFU-bypass (a blocked sender is denied even with an accepting prompt, and the prompt is never consulted; a throwing prompt propagates and persists nothing). One gap was found and explicitly **not** silently fixed: [[wire-protocol]]'s documented freshness window on `issued-at` is not implemented anywhere in `ReceiverSession` — flagged as a real, low-severity gap (trust is keyed to the sender's fingerprint, not session/timestamp, so the worst case is a redundant hash-verified rewrite) rather than quietly patched in a test-only pass.

## The `Castr.Tui` flaky-test fix

`TransferDashboard.RunLoopAsync` used to treat a live `isComplete()` poll as equally authoritative to the cached `ProgressChanged` snapshot; under real scheduler contention (parallel `dotnet test` runs), the poll could observe completion a few statements before the terminal event landed, freezing the dashboard on a stale pre-completion frame. Fixed with a bounded 2-second grace period that waits for the actual terminal snapshot before falling back — verified with 5 consecutive full-solution runs post-fix, and confirmed by the combined QA pass to be independent of chunk size (the gap it bridges is a single small broadcast + emit, not proportional to transfer size).

## CI: a milestone carried forward finally paid off, and then some

Using the now-authenticated `gh` CLI, real CI history showed **every run since M1's real-UDP integration tests were added had been failing on `macos-latest`** — six runs across M1, M1.5, and M2, silently, because nothing before now could read the failure logs (`gh` wasn't installed; the unauthenticated GitHub API can list runs and job/step conclusions but not download logs or artifacts). Root cause: `UdpMulticastTransport.SendAsync` never set `IP_MULTICAST_IF`; Windows and Linux resolve a working multicast egress interface via routing-table fallback without it, but macOS does not, so sends failed with `SocketException: No route to host`. Fixed (macOS-only code path, `OperatingSystem.IsMacOS()`-gated, so Windows/Linux behavior is untouched): resolve a single unambiguous candidate interface (or fall back to loopback) and set it as both the join interface and the send interface — a second fix iteration was needed because macOS only delivers the `IP_MULTICAST_LOOP` copy to receivers joined on the *same* interface the sender used. An explicitly-passed `--interface` is now also honored on the send side on every platform, closing a latent correctness gap that existed even where the join side worked correctly. This was `tech-stack.md`'s M0-era macOS platform-quirk warning ("default-route interface selection is unreliable... enumerate interfaces and let the user override") finally manifesting concretely, three milestones later.

Verifying the fix required real CI, not just local (Windows-only) testing, so the fix was pushed to a scratch branch/PR, watched via `gh run watch`, iterated on twice, and confirmed green on all three OS legs before being folded back into the working tree as an uncommitted change (the scratch branch and PR were deleted once extracted). That same round-trip surfaced a genuine, unrelated concurrency bug: `ReceiverSession`'s packet-handling loop and its repair loop run concurrently via `Task.WhenAll` with no synchronization over shared mutable state (`_bitmaps`, `_chunkCache`, `RepairCoordinator`, `PeerTable`), which the E2E suite's realistic multi-receiver load exposed as intermittent `KeyNotFoundException`/`Collection was modified` failures. Fixed with a `SemaphoreSlim` gate serializing the two flows. `.github/workflows/ci.yml` also gained a new, separate `e2e-docker` job (`ubuntu-latest` only — Docker support on GitHub's macOS/Windows hosted runners is historically unreliable — `CASTR_E2E=1`, `--filter Category=E2E`), verified passing for real (3/3, ~1m11s) on the same CI run. The main matrix job's test step now explicitly filters `Category!=E2E` as defense-in-depth alongside `[E2EFact]`'s own self-gating.

## QA verdicts

- **Chunk/wire-packet split (dedicated review)**: PASS-WITH-CONCERNS. One should-fix defect (the reassembly DoS above), fixed and re-verified. The `CastrFanOut.cs` comment inconsistency was investigated and resolved in QA's favor of the code being *more* correct than documented, not less.
- **Combined M3 surface**: PASS, no blocking defects. Confirmed the bounded reassembly doesn't spuriously reject the E2E suite's real transfers or evict genuinely in-flight chunks (512 chunks « the 1024 pending-chunk cap in every shipped scenario); confirmed the security-pass tests still hold against the fully-packetized code; confirmed the TUI fix's grace period is chunk-size-independent; found and fixed one stale doc (`Castr.Core.E2ETests/README.md` described the pre-packetization loss filter).

## Non-blocking notes carried forward

- **`issued-at` freshness window still unimplemented** (confirmed gap, see security test pass above) — low severity, revisit if session-replay risk is ever reassessed.
- **`tc netem` drops concentrate on each chunk's packet-0** in the E2E harness (proof-carrying packet), since only packet 0 of an 8 KB chunk's ~9-10 packets lands in the filter's matched size window — the test still validly exercises repair (losing packet 0 strands the whole chunk), but effective per-datagram loss is lower than the nominal percentage; worth tightening if E2E loss-rate precision ever matters.
- **Reassembly eviction is FIFO-by-creation, not true LRU** despite the docstring — never triggered in any shipped scenario (well under caps), and correctness holds even if it did (repair re-requests), but the naming should be corrected if the code is touched again.
- **Large/multi-file manifests are more loss-fragile now**: a manifest between ~1200 bytes and 64 KB is now split across multiple `PacketFragment` datagrams; losing any one strands a receiver, since there's still no manifest re-request path (an M1 scope trim, now amplified from one losable datagram to N). Candidate for the existing "repeating carousel / NACK-suppressed repair" future work already tracked in [[roadmap]].

## Where this fits

- [[roadmap]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[m1-core-summary]]
- [[m1.5-encryption-summary]]
- [[m2-ui-summary]]
