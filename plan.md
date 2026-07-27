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
| Offset-keyed fragment reassembly | `src/Castr.Core/Protocol/ChunkPacketAssembler.cs:238` — `new byte[packetCount][]` sized from a wire-supplied count | The largest item here. Key fragments by **byte offset**, not packet index: that retires M9's mixed-budget stranding class *and* the tracked M8 "stop pre-sizing from the claimed count" item in one change, and subsumes the "`--datagram-size` must match on every peer" contract that currently holds by documentation alone. Existing bounds at `:110-115` and `:151-153` stay until replaced. Hot path — wants its own review. |
| `SwarmServeListener` never prunes connection tasks | `src/Castr.Core/Swarm/SwarmServeListener.cs:26,41,46` | `List<Task>` grows one entry per accepted connection, drained only by `Task.WhenAll` in `finally`. No concurrency cap either. |
| Session-ID uniqueness is unenforced | fix site: `src/Castr.Core/Trust/ManifestAdmission.cs:34-55` | No registry of seen session IDs exists; only length is validated. `ContentKeyWrap.cs:85` uses the session ID as the HKDF salt, so reuse re-derives the same wrapping key. `ManifestAdmission` is the shared gate both `ReceiverSession` and `SwarmPullSession` already run manifests through. Safe in practice today only because `Castr.Cli` generates a fresh random ID per invocation. |
| `ManifestFileEntry.ChunkSize` is never range-checked | `src/Castr.Core/Manifest/ManifestCodec.cs:63`; same fix site as above | Unbounded `ReadInt32`. A value near `int.MaxValue` overflows `CiphertextBoundForChunkSize` negative and throws straight out of the receive loop (`ReceiverSession.cs:704,728`); a large-but-not-overflowing value re-opens the allocation ceiling. Needs a trusted sender, so robustness rather than a remote hole — but it changes which manifests are *accepted*, so it wants review. Gap already documented in-code at `ReceiverSession.cs:714-723`. |
| FIFO-vs-LRU naming | `PacketReassembler.cs:16` vs `:113-127`; `ChunkPacketAssembler.cs:172` vs `:206-222` | Docstrings claim LRU; `Sequence` is assigned once in the ctor and never touched on access, so eviction is FIFO-by-creation. Correctness holds. Naming only. |
| `TransferDashboard` re-renders unthrottled | `src/Castr.Tui/TransferDashboard.cs:80` | `new SemaphoreSlim(0)` → `new SemaphoreSlim(0, 1)`. Without a max count `Release()` can never throw, so the `catch (SemaphoreFullException)` coalescing guard at `:88` is dead code and permits accumulate one per progress event. TUI/GUI only — the CLI path is percent-bucketed. |
| E2E netem loss filter is stale | `tests/Castr.Core.E2ETests/Infrastructure/CastrFanOut.cs:139-143` | `u32 match u16 0x0400 0xfc00 at 2` matches the IP total-length field over `[1024, 2047]`. The justifying comment is written against the **1200-byte datagram budget and 8 KiB chunks**; shipped defaults are now 1472 and 256 KiB. Everything under 1024 bytes is spared — control traffic by design, but also each chunk's short tail packet. `:133-137` records that the filter has never been re-verified in-loop. |
| iOS `MobileReceiveViewModel` cannot cancel a pull | `src/Castr.Gui/ViewModels/MobileReceiveViewModel.cs:152`, `Dispose` at `:218-222` | Method-local `using var cts`, so nothing outside `PullAsync` can cancel it, and there is no `CancelPull` command. Android's `SwarmReceiveViewModel` already has the shape to copy: `_pullCts` field `:41`, command `:181`, cancelled + disposed in `Dispose` `:213-214`. |
| `Castr.Core.Discovery` swarm transport receive shape | `UdpUnicastTransport` / TCP framing | Carried from M6 round 3 as **unverified**: plausibly has the pre-M6 synchronous receive-then-process architecture the multicast tier had before M6's fix. Verify before fixing — unlike the nine above, this one may turn out to be a no-op. |

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
