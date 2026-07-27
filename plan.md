# Castr — milestone plan

The scannable top-level view of what is done, what is left, and in what order.

This file is deliberately thin. **`wiki/synthesis/roadmap.md` is the source of truth for detail** —
every measurement, every review round, every earned rule lives there or in
`docs/benchmarks/throughput-runs.md`. This page links into them and does not restate them.

Last reconciled against `main` at `c4a899b` on 2026-07-27.

---

## Part 1 — Completed

| M | Scope | Outcome | Detail |
|---|---|---|---|
| M0 | Scaffolding, spikes | Solution + CI + Ed25519/mDNS ADRs; retargeted net8.0 → net10.0 LTS | `wiki/synthesis/adr-0001-ed25519-library.md`, `adr-0002-mobile-discovery.md` |
| M1 | `Castr.Core` protocol | Chunker/Merkle/manifest/trust/transport; QA found a `UInt16` repair-batch length overflow | `wiki/synthesis/m1-core-summary.md` |
| M1.5 | Payload encryption retrofit | X25519 + ChaCha20-Poly1305, Merkle-over-ciphertext, `JOIN_REQUEST`/`KEY_GRANT` | `wiki/synthesis/m1.5-encryption-summary.md` |
| M2 | CLI, TUI, Desktop GUI | Core progress/trust-prompt contract first, then three surfaces in parallel | `wiki/synthesis/m2-ui-summary.md` |
| M3 | Test/CI hardening | Real chunk/wire-packet split, Testcontainers netem E2E; macOS CI multicast broken since M1, found + fixed | `wiki/synthesis/m3-test-ci-hardening-summary.md` |
| M4 | Mobile | TCP swarm-pull tier, native mDNS, real signed APK; Merkle `LeafIndex` relabel defect found + fixed | `wiki/synthesis/m4-mobile-summary.md` |
| M5 | Showcase docs | `docs/SHOWCASE.md` with real captured media — **release automation was displaced, see M13** | `docs/SHOWCASE.md` |
| M6 | Send-path throughput | Root cause was receiver-side serialization, not sender-side; channel-decoupled receive loop | `wiki/synthesis/m6-throughput-pipelining.md` |
| M7 | Repair amplification | Wire amplification 2.39× → 1.13×; two liveness bugs; the "+112.6% goodput" claim **withdrawn** | `wiki/synthesis/m7-repair-amplification.md` |
| M8 | Default chunk size 8 KB → 256 KB | 1.33×, and **2.80× under real netem loss** | `50e4cf4`; run log |
| M9 | Packetization efficiency | 309 → 184 datagrams/chunk, **1.41×**; a stranded-chunk defect found + fixed | `wiki/synthesis/m9-datagram-efficiency.md` |
| M10 | Bounded receiver chunk cache | Peak private bytes 2,461.7 → 93.3 MB; cold rebuild path moved off `_stateGate` | `c4a899b`; run log |

Tree state: **498 tests, 0 warnings** under `-warnaserror`, Docker netem E2E tier green, `main` clean
and in sync with `origin/main`.

### The throughput programme is closed

M6 → M9 took Castr from ~1.6 MB/s to **10.2 MB/s over real Ethernet — about 94% of this LAN's hard
~11.8 MB/s multicast ceiling**. That ceiling is diagnosed: the link partner (an eero-class mesh router)
meters multicast to exactly 100 Mbps. Not the NIC, not Windows, not Castr. Prediction and measurement
agree within 6%.

**Do not resume transport optimisation without a dumb gigabit switch or a direct host-to-host cable.**
Loopback runs at 2.6× the real ceiling, so any loopback A/B measures a regime that does not exist on
the wire — the 60,000-byte-datagram mistake with the sign flipped, and this project has paid for it
once already.

Closed means *done*, not *abandoned*. The remaining single-receiver work competes for the last ~6%.

---

## Part 2 — Remaining

Ordered per the decision recorded in `841944e`. Deliberately **not** highest-value-first: the backlog
is cheap and leaves the tree clean before a structural change lands in the same files; fan-out is the
largest genuine engineering item and wants a clean base; there is no point shipping a v1 before the
fan-out claim it advertises has been validated.

### M11 — Clear the small backlog

Ten items, one to three files each, no new design decisions. All ten were verified still present in
code at `c4a899b` — none had been silently fixed. Anchors are recorded so the implementer does not
re-hunt them.

| Item | Anchor | Note |
|---|---|---|
| **[x]** `SwarmPullSession._chunkCache` is unbounded | `src/Castr.Core/Swarm/SwarmPullSession.cs:59` (written :257, read :268, never removed) | Done. Byte-bounded LRU on M10's shape (`DefaultChunkCacheBytes` 32 MiB, `SwarmPullSessionOptions.ChunkCacheBytes`). Two deliberate departures, both because this class is **not** an `ISwarmContentSource`: proofs are not retained (nothing reads one after verification, so retaining them would be write-only state unbounded in chunk count), and there is no cold rebuild (the only reader is the decrypt path, which by definition runs before any plaintext exists on disk). Eviction therefore cannot pin undecrypted chunks the way M10 does — on this tier "ciphertext held with no key" is a whole-transfer relay mode, not a startup window — so a dropped chunk has its bitmap bit cleared and is re-pulled. |
| **[x]** Offset-keyed fragment reassembly | `src/Castr.Core/Protocol/ChunkPacketAssembler.cs:238` — `new byte[packetCount][]` sized from a wire-supplied count | Done. `ChunkPacketMessage` carries `FragmentOffset` in place of `PacketIndex`+`PacketCount`; a partial is one ciphertext-sized buffer plus its covered byte ranges. Retires the mixed-slicing stranding class (slicings now combine rather than one winning), removes the claimed-count allocation entirely, and drops the "budget must match on every peer" contract from CLI help, `DatagramBudget` and `SendRunner`'s note. Envelope 43 → 39 bytes; **`MessageCodec.FormatVersion` bumped 1 → 2**, since the body layout moved and bodies are not self-describing. Replacement resource bound: coverage may fragment into at most `ceil(len / minFragmentBytes) + 2` disjoint ranges. |
| **[x]** `SwarmServeListener` never prunes connection tasks | `src/Castr.Core/Swarm/SwarmServeListener.cs:26,41,46` | Done. Successfully-completed tasks are pruned each accept (faulted ones kept, so `Task.WhenAll` still surfaces a defect), and a `SemaphoreSlim` caps concurrency at `DefaultMaxConcurrentConnections` = 64, taken **before** `AcceptAsync` so excess dials wait in the transport backlog rather than being accepted and starved. `TrackedConnectionCount` exposed for the bound. Note the pruning test passed spuriously at first — the counter is zeroed at shutdown, so a post-cancellation read is not a control. |
| **[x]** Session-ID uniqueness is unenforced | fix site: `src/Castr.Core/Trust/ManifestAdmission.cs:34-55` | Done. New `ISessionRegistry` binds a session ID to (sender, manifest digest); `ManifestAdmission` returns `SessionIdConflict` when it is presented for a different transfer. Recorded **only on acceptance**, so a valid-signature-but-untrusted sender cannot burn an ID a legitimate transfer is about to use. Registry is **persistent** (`FileSessionRegistry`, beside the trust store) — a process-lifetime one would enforce nothing, because the CLI runs one transfer per invocation. Bounded and oldest-first evicted so it cannot become the leak it prevents. Wired at all four composition roots (CLI receive, GUI desktop, Android, iOS). |
| **[x]** `ManifestFileEntry.ChunkSize` is never range-checked | `src/Castr.Core/Manifest/ManifestCodec.cs:63`; same fix site as above | Done. New `ManifestLimits` checks structure once at admission (`ManifestAdmissionOutcome.Malformed`): chunk size in `[1, 16 MiB]`, non-negative size, non-empty path, and `ChunkCount` self-consistent with `(Size, ChunkSize)`. `CastrPaths.MaxChunkSize` now derives from it, so the CLI ceiling and the receiver's accept ceiling are one constant. The M8 in-code gap note at `ReceiverSession.cs:714-723` is retired. Covered end to end: a signed `ChunkSize = int.MaxValue` manifest on the wire no longer faults the receive loop. |
| **[x]** FIFO-vs-LRU naming | `PacketReassembler.cs:16` vs `:113-127`; `ChunkPacketAssembler.cs:172` vs `:206-222` | Done. Both now say FIFO-by-establishment and say why; `Sequence` renamed `EstablishedAt` so the field carries the fact. `SwarmPullSession`'s new held-ciphertext queue was written with the same wrong name in this milestone and is corrected with them (`_lru` → `_order`) — nothing there promotes on access either. `ReceiverSession`'s cache genuinely is LRU (`PlanChunkServe` promotes on hit) and is left alone. |
| **[x]** `TransferDashboard` re-renders unthrottled | `src/Castr.Tui/TransferDashboard.cs:80` | Done, but as a named `RenderSignal` type rather than the one-character `new SemaphoreSlim(0, 1)` the anchor suggests: a bare bounded semaphore leaves the coalescing intent as an un-testable implementation detail, which is how the dead `catch (SemaphoreFullException)` survived in the first place. Four tests on the signal itself; the unbounded mutation fails three. |
| **[x]** E2E netem loss filter is stale | `tests/Castr.Core.E2ETests/Infrastructure/CastrFanOut.cs:139-143` | Done. Now selects by Castr **message type** (3/6/11 at IP offset 29) rather than by IP total length, which removes the size coupling entirely: no chunk tail packet is undroppable (one in 184 was), and no large control datagram is dropped by accident (the `PacketFragment` slices of a big manifest were — the exact traffic the old comment claimed to protect). `CHUNK_RESPONSE` is deliberately included so repair traffic is lossy too. **Verified in-loop against Docker**, which the M3-era note said had never been done: three fan-out arms green, 13,934 netem drops on the 5-receiver/20% arm, every receiver hash byte-identical. |
| **[x]** iOS `MobileReceiveViewModel` cannot cancel a pull | `src/Castr.Gui/ViewModels/MobileReceiveViewModel.cs:152`, `Dispose` at `:218-222` | Done, copying Android's shape exactly: `_pullCts` field, `CancelPullCommand`, cancelled and disposed in `Dispose`. `MobileReceiveView.axaml` gains a Cancel button shown only while `IsPulling`. Tested against a peer that stalls mid-chunk-serve — the case where the old code hung with `IsPulling` stuck true and the Pull button disabled by `CanExecute`. Mutation-verified: a no-op `CancelPull` fails it. |
| **[x]** `Castr.Core.Discovery` swarm transport receive shape | `UdpUnicastTransport` / TCP framing | Verified, then split. **TCP framing: a genuine no-op.** M6's defect is specific to a lossy datagram socket with a bounded kernel buffer — slow draining loses packets. TCP has flow control and retransmission, so slow draining is backpressure, and the swarm protocol is request/response over an ordered stream with no unsolicited inbound. **`UdpUnicastTransport`: real, but latent.** It did still have the pre-M6 shape (its own iteration *was* the read loop), and nothing in the shipped composition enumerates it — the mobile tier is TCP, despite this type's summary claiming it was "the sole transport on the mobile tier", which is what put this item on the list. Fixed anyway (bounded-channel reader loop, idempotent dispose) and the misleading summary corrected. |

**Acceptance:** 498+ tests green, 0 warnings, Docker netem tier green, and each item covered by a test
that actually fails against the pre-fix code. That last clause is M10's earned lesson: a determinism
control that stayed green against a mutated `BuildNonce` was not a control at all.

### M12 — Fan-out scaling

The product's headline claim — *"one send, hundreds of receivers"* — and the least-validated axis in
the codebase. Split so the protocol work is designed against real numbers rather than stale ones.

**M12a — measurement only.** Establish a real multi-receiver baseline. The figures currently quoted
(3 receivers at 8.01 MB/s against 13.65 at one, 5.08× wire amplification) are **loopback and
pre-M9/M10**, and the 5.08× row comes from the withdrawn 60,000-byte-datagram configuration — it does
not describe shipped code. Also measure the receiver's sustained datagram ceiling, which several
decisions depend on and which does not exist as a number today. No protocol change in this stage.

**M12b — protocol.** `CAROUSEL_STATUS` (tag 16) and `SECTION_REPORT` (tag 17), per
`wiki/synthesis/proposal-section-based-repair.md` **as amended by the architecture review**: a
*cumulative monotone heartbeat, not an edge event* — an edge event re-creates the "never reached vs.
finished" conflation that produced both M7 liveness bugs. New message types are additively
backward-safe (unknown tags throw and both sessions swallow); appending a field to an existing type is
not. Write and review the design before implementing — both prior defects here were design errors that
survived code review. Expect 3–5 review rounds; this is the most defect-dense area in the system.

### M13 — Release automation

M5's original scope, displaced by the showcase work. The only real blocker between Castr and being
something a person can download and run.

Tag-triggered `release.yml`, self-contained per-RID publishes, checksums plus detached signatures,
generated release notes, README install instructions. `ci.yml`'s existing `package` job (lines 93-142)
already publishes and zips `Castr.Cli` + `Castr.Gui.Desktop` for five RIDs from a single ubuntu runner,
so this is mostly wiring and signing.

**Prerequisite not recorded elsewhere: the repo has no version property at all** — no
`Directory.Build.props`, no `<Version>` in any csproj. Versioning has to be established before a tag
can mean anything. macOS zips stay unsigned/unnotarized unless notarization is explicitly taken on.

### M14 — Documentation reconciliation

Several surfaces have drifted from the code:

- `wiki/synthesis/roadmap.md`'s milestone table has **no M8, M9, or M10 row** — those exist only in
  prose, commit messages, and the run log.
- No M10 wiki page exists, though every other milestone has one.
- `README.md` claims "M0 through M8 are complete", quotes the **withdrawn** ~37 MB/s syscall-ceiling
  framing, and calls the datagram budget "the tracked next step" (shipped in M9).
- `docs/METHODOLOGY.md`'s "Current state" says M7 is not merged, and predates M8, M9, M10 and the LAN
  multicast-ceiling diagnosis.

Also: delete the stale worktree at `.claude/worktrees/agent-aad9c2ea41236900d`, and record that
`bench/m7-instrumentation` is based on pre-M7 `702de7c` and must be rebased before it is used to
measure current code.

---

## Part 3 — Deferred, with the blocking reason

| Item | Blocked on |
|---|---|
| Further transport optimisation | A dumb gigabit switch or a direct host-to-host cable |
| `{P0, P0+w2, P0+w4}` send-window matrix | A host that reproduces the 13.65 MB/s warm baseline |
| Raising `DefaultSendWindowSize` above 1 (`SenderSession.cs:77`) | Independent cross-machine real-LAN validation, ideally by someone who did not write the M6 benchmarks |
| Byte-denominating `MaxRequestsPerPass` + `MaxChunksPerRequest` (`RepairCoordinator.cs:115-116`, serve cap `SenderSession.cs:272-275`) | Nothing technical — but do both together, since fixing either alone leaves the other as the binding count-denominated term. One request datagram currently commands ~84 MB of multicast |
| Carrying the datagram budget in the manifest | A manifest format-version bump. **Not** additively safe: `ManifestVerifier.VerifySignature` re-encodes the decoded manifest before verifying, so an old reader rejects the transfer outright |
| iOS Simulator app link | Upstream — `NSec` pins libsodium `[1.0.22, 1.0.23)`, which ships no iOS Simulator native slice |
| Mobile Android↔iOS discovery interop | No device, emulator, or macOS host on the dev machine |
| Unifying `SwarmReceiveViewModel` / `MobileReceiveViewModel` | Nothing — worth a deliberate pass rather than a rushed one |
| Replay `issued-at` freshness window; `ANNOUNCE` handling | A reassessment of session-replay risk; trust is keyed to sender fingerprint today |
| Manifest re-request path (large manifests are loss-fragile) | Natural to do alongside repeating carousel rounds |
| Repeating carousel rounds; NACK-suppressed unicast-targeted repair | M1 scope trims, not correctness gaps — opportunistic |
| Sender-side receiver allowlist; encrypted manifest metadata | Deliberate scope boundaries, revisit if a deployment needs them |

---

## Definition of done, every milestone

Per `wiki/sources/castr-project-plan.md`: tests passing, a `/wiki:ingest` summary committed,
`graphify --update` run if code changed, and an independent QA subagent pass before moving on.
