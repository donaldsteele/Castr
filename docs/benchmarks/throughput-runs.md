# Throughput run log

Append-only record of every real throughput measurement taken against Castr, with enough
configuration detail to re-run it. This exists because the M6 investigation produced a
substantial amount of hard data that previously survived only as prose in
[`wiki/synthesis/m6-throughput-pipelining.md`](../../wiki/synthesis/m6-throughput-pipelining.md) —
which made it impossible to compare a new run against an old one, or to notice that two
rounds of benchmarking disagreed with each other.

See [`../METHODOLOGY.md`](../METHODOLOGY.md) for how these runs are conducted and why the
approaches were chosen.

## Conventions

- **Measured** rows come from running the real shipped `castr` / `Castr.Gui.Desktop` binaries as
  separate OS processes over a real UDP multicast socket. No in-memory transport, no mocks.
- **Derived** rows are computed from the code's own constants (message encodings, packetizer
  arithmetic). They are predictions, not observations, and are labelled as such — when a
  derived number and a measured number disagree, that disagreement is the finding.
- Every row records who produced it, because "the person who wrote the change also ran the
  benchmark" is a known failure mode in this project's history (see M6 round 1).
- Throughput is receiver-observed goodput (verified plaintext bytes / wall-clock), not wire rate.
  Wire rate is higher and, as it turns out, much higher than anyone assumed — see the overhead
  table below.

---

## Measured runs

### Pre-M6 baseline — the plateau that started the investigation

| Date | Surface | File | Chunk | Receivers | Result | Source |
|---|---|---|---|---|---|---|
| 2026-07-24 | GUI / CLI / TUI | 60–100 MB | 8192 | 1–3 | **~1.6–2.4 MB/s** on all three surfaces | M5 demo recording |

The cross-surface consistency was the first real clue: three independent UI layers hitting the
same ceiling pointed at `Castr.Core`, not at any one presentation layer.

### M6 round 1 — sender-side send-window pipelining

Real two-process `castr send` / `castr receive`. Run by the implementer.

| Window | Result vs. window=1 | Note |
|---|---|---|
| 1–2 | Noisy, marginal — sometimes ~1.8× faster, sometimes a wash, **one measured regression** | Sample too small to distinguish from noise |
| 3–4 | **Consistent 2–5× regression** | |
| 64 | **Outright stall** — sender reported 100% complete, receiver frozen below 40% | First value tried |

Shipped `DefaultSendWindowSize = 2` on this evidence. That decision was later reversed.

### M6 round 2 — independent QA re-measurement of round 1

Same code as round 1, measured by a reviewer who did not write it.

| Window | Result vs. window=1 | Delta from round 1's own reading |
|---|---|---|
| 2 | **Consistent ~1.8–2.7× regression** | Round 1 called this "roughly neutral, occasionally faster" |
| 64 | Stall reproduced exactly | Confirmed |

This is the single most instructive row in the file: the implementer's benchmark and an
independent benchmark of *identical code* disagreed on the sign of the effect.

### M6 round 2 — after the receiver-side fix

Channel-decoupled socket reader + explicit 4 MB `SO_RCVBUF`/`SO_SNDBUF`. Real two-process.

| Chunk | Window=1 | Window=2 | Ratio |
|---|---|---|---|
| 8192 | **≈8.1 MB/s** | **≈11.2 MB/s** | 1.38× |
| 60000 | **≈10.1 MB/s** | **≈16.4 MB/s** | 1.62× |

- Window ≥4 gave back most of the gain (roughly back to window=1, no longer a catastrophic collapse).
- 3-receiver fan-out, one receiver deliberately pinned to a single low-priority CPU core:
  window=2 completed faster than window=1; every receiver byte-identical; no failures at any window.

### M6 round 2 — shared-gate prototype (rejected)

A shared `SemaphoreSlim` to make the send window a true global bound rather than per-loop.

| Config | Result |
|---|---|
| Gate bypassed | ≈11 MB/s |
| Gate active | **≈7.6 MB/s** |

80 MB, 8192 chunk, window=2, **zero contention occurring in the test**. A ~30% cost for a
`WaitAsync`/`Release` pair per chunk that never actually blocked. Reverted.

### M6 round 3 — independent confirmation of the receiver fix

| Test | Pre-fix | Post-fix |
|---|---|---|
| Window=64 | Hung indefinitely | **Completed in 47 s** |

The strongest single piece of evidence that the bottleneck was receiver-side: the same
pathological sender configuration that used to deadlock now merely runs slowly.

### M5/M6 showcase demo captures

Real screen captures of the shipped binaries; timings read off the recordings.

| Surface | File | Chunk | Receivers | Wall clock | Goodput |
|---|---|---|---|---|---|
| CLI | 60 MB | 8192 | 1 | ~6 s | ~10 MB/s |
| TUI | 80 MB | 8192 | 3 | ~12 s | ~6.7 MB/s per receiver |
| GUI | 100 MB | 8192 | 1 | ~28 s | ~3.6 MB/s |

**Do not read these three against each other as a like-for-like comparison.** They differ in
file size, receiver count, and process topology (the GUI runs sender and receiver as two windows
with live Avalonia rendering). They are recorded here because they are the numbers published in
[`../SHOWCASE.md`](../SHOWCASE.md) and should be reproducible.

### Post-M6 field report

| Date | Reporter | Observation |
|---|---|---|
| 2026-07-25 | User | ~8 MB/s, but **bursty — visibly bursts then stalls**, not a steady rate |

This report opened the current investigation. Average throughput had improved ~5× over the
pre-M6 baseline while *consistency* had not been measured at all by any round of M6 — every
number above is an average over a whole transfer, which cannot distinguish a steady 8 MB/s from
alternating 20 MB/s bursts and multi-second stalls.

---

## Derived overhead (computed from code, not measured)

For an 80 MiB transfer at the shipped `CastrPaths.DefaultChunkSize = 8192` and
`WirePacketizer.DefaultMaxDatagramPayload = 1200`:

| Quantity | Value | Source |
|---|---|---|
| Chunks | 10,240 | `80 MiB / 8192` |
| Merkle proof depth | 14 steps → 472 bytes encoded | `ChunkPacketizer.ProofEncodedSize` |
| Ciphertext per chunk | 8,208 bytes | 8192 + AEAD tag |
| Bytes per wire packet | `1200 − 47 − 472` = **681** | `ChunkPacketizer.Split` |
| Datagrams per chunk | **13** | `ceil(8208 / 681)` |
| Packet 0 size | 1,200 bytes (carries the proof) | |
| Packets 1–12 size | **728 bytes in a 1200-byte budget — 39% wasted** | Proof rides only on packet 0, but *every* packet is sized against it |
| Data-plane datagrams | 133,120 | |
| `PEER_HAVE` encoded size | 1,328 bytes → **exceeds 1200 → 2 datagrams** | `MessageCodec`, bitmap = 1,280 bytes |
| `PEER_HAVE` datagrams (1 receiver) | **20,480** | One per verified chunk, ×2 fragments |
| `PEER_HAVE` bytes (1 receiver) | **~13.6 MB** | |
| **Total wire cost, zero loss, zero repair** | **153,600 datagrams / ~113 MB for 80 MiB of file** | **71% efficiency; 15% of all datagrams are gossip** |

### The gossip term is quadratic in file size

Total `PEER_HAVE` bytes = `chunks × ceil(chunks/8)` = **`FileSize² / (8 · chunkSize²)`**.

| File | Chunk | Gossip | As % of payload |
|---|---|---|---|
| 80 MiB | 8 KiB | 13.1 MB | 16% |
| 80 MiB | 60 KB | 0.3 MB | 0.4% |
| 80 MiB | 256 KiB | 12.8 KB | 0.015% |
| 800 MiB | 8 KiB | **1.31 GB** | **164%** |
| 100 MB (GUI demo) | 8 KiB | 21.1 MB | 21% |
| 80 MB × 3 receivers (TUI demo) | 8 KiB | 40.8 MB | 51% |

At 800 MiB the progress-reporting traffic exceeds the file being transferred. This is a scaling
wall, not an inefficiency.

### Independent corroboration from data already in this file

The round-2 measurement of chunk 8192 → 60000 (8.1 → 10.1 MB/s, +25%) was originally attributed
to reduced datagram count. But raising the chunk size that way only cuts data datagrams ~20%,
while it collapses gossip from 20,480 datagrams / 13.6 MB to 1,365 datagrams / 0.3 MB. The
measured gain tracks the gossip collapse far better than the datagram-count change — meaning the
`PEER_HAVE` cost was already visible in M6's own numbers and was misattributed at the time.

---

---

## 2026-07-25 — Instrumented measurement campaign (91 real transfers)

80 MB payload, `castr send` / `castr receive` as separate processes over loopback multicast.
Windows 11, 12 logical cores, .NET 10.0.302, Release. Every run SHA-256 verified byte-identical
unless noted. **All A/B rows warm-cache, interleaved, median of 3 reps.**

This section supersedes several derived estimates above. Where it contradicts them, it wins.

### ⚠️ Read this first: the OS page cache is a ~2× confounder

Identical configuration, identical datagram counts: **12.6 s cold vs 6.5 s warm (1.94×)**.
`SendToAsync` went 29 → 53 µs/datagram and chunk read 32 → 57 µs when cold. This is very likely
why M6 recorded ~8 MB/s where warm runs here show 13.6 MB/s. **Any A/B not run warm, interleaved,
with ≥3 reps is noise.** Cache state was verified via `\Memory\Standby Cache Normal Priority Bytes`
(2,229 MB → 15 MB on eviction).

Also: **`netstat -s -p udp` "Receive Errors" read 0 in all 91 runs** despite 200,000–370,000
demonstrably dropped datagrams. Windows does not report `SO_RCVBUF` overflow there. Do not use it.

Requested `SO_RCVBUF`/`SO_SNDBUF` of 4,194,304 was **granted exactly — no clamping** (honored up to 64 MB).

### The stall is 5.10 s, and it is `RepairOptions.RequestTimeout`

Successive repair bursts are spaced **5,091–5,210 ms** (median 5,102 ms, n=40+). Never near 250 ms.
The 250 ms poll runs 61–69 passes per transfer but **only 4 ever emit a request**, because
`PlanRepairs` filters anything already in `_pending` and `ExpireStalePending` releases only after
5 s. The loop sets granularity; the 5 s timeout sets the period.

Amplitude is not a dip — it is **0.00 MB/s to 39 MB/s and back**. Dead air where the receiver made
literally zero progress:

| Config | Stalls | Total dead air | % of transfer |
|---|---|---|---|
| base (window 1) | 1 | 0.2–0.9 s | 3–15% |
| `--send-window-size 2` | 1 | 1.6–2.5 s | 24–37% |
| `--send-window-size 4` | 3 | **11.1–11.7 s** | **70%** |
| datagram 8000 | 1 | 2.8–3.4 s | 47–57% |
| repair disabled | 0 | 0 | 0% |
| chunk 256K + datagram 60000 | 0 | 0 | 0% |

A second, distinct **~600 ms sender-side oscillation** appears only cold (hard on/off square wave,
sender at 0% CPU during the off phase; period 599/500/406 ms at window 1/2/4). A
256 KB→64 MB `SO_SNDBUF` sweep did not move it — **refuted** as a buffer effect. It vanishes warm,
so it is sender I/O/scheduling, not protocol.

### A/B matrix

| Config | sec | MB/s | vs base | carousel | tail | wire | amp | dup% | inbox peak |
|---|---|---|---|---|---|---|---|---|---|
| **base** (chunk 8192, w1, dgram 1200) | 5.86 | **13.65** | — | 4.76 | 0.97 | 201.2 MB | 2.52× | 43.3% | 4096 |
| PEER_HAVE off | 4.90 | 16.33 | **+20%** | 4.90 | 0.00 | 176.8 MB | 2.21× | 48.1% | 4096 |
| PEER_HAVE every 64th | 4.82 | 16.59 | **+22%** | 4.84 | 0.00 | 179.6 MB | 2.25× | 49.5% | 3016 |
| repair off | 6.60 | 12.12 | **−11%** | 6.60 | 0.00 | 103.3 MB | 1.29× | 0.0% | 998 |
| repair off + PEER_HAVE off | 6.28 | 12.74 | −7% | 6.28 | 0.00 | 89.8 MB | 1.12× | 0.0% | 824 |
| repair loop 2 s (not 250 ms) | 5.50 | 14.54 | +6% | 5.52 | 0.00 | 165.7 MB | 2.07× | 40.5% | 3069 |
| `--send-window-size 2` | 6.70 | 11.93 | **−13%** | 2.88 | **3.82** | 231.8 MB | 2.90× | 15.7% | 4096 |
| `--send-window-size 4` | 15.85 | 5.05 | **−63%** | 1.83 | **14.00** | 309.7 MB | 3.87× | 3.6% | 4096 |
| datagram 8000 | 5.96 | 13.43 | −2% | 1.47 | **4.49** | 214.8 MB | 2.68× | 17.5% | 4096 |
| **chunk 256K** (dgram 1200) | 3.32 | **24.10** | **+77%** | 3.37 | 0.00 | 164.4 MB | 2.05× | 32.4% | 80 |
| **chunk 256K + dgram 60000** | **0.81** | **98.60** | **+622%** | 0.44 | 0.40 | 145.6 MB | 1.82× | 24.7% | 1950 |
| chunk 256K + dgram 60000, repair off | 0.75 | **106.89** | +683% | 0.38 | 0.37 | 80.2 MB | **1.00×** | 0.0% | 925 |
| 3 receivers | 9.99 | 8.01 | −41% | 10.19 | 0.00 | 226.0 MB | 2.83× | 48.9% | 3903 |
| 3 receivers, PEER_HAVE off | 10.18 | 7.86 | −42% | 10.31 | 0.00 | 186.0 MB | 2.32× | 47.6% | 4094 |
| **3 receivers, chunk 256K + dgram 60000** | **1.42** | **56.15** | +311% | 0.72 | 0.77 | 406.2 MB | 5.08× | 31.0% | 4096 |

### The file is sent twice — confirmed directly

The first `CHUNK_REQUEST`, issued ~250 ms after the manifest while the carousel is **~1% done**,
asks for **10,212 of 10,240 chunks**. The sender's own marks show `repair-served value=10239`
landing **20 ms before `carousel-complete`**. 43–49% of chunk datagrams the receiver receives are
for chunks it already holds. Wire amplification **2.52× on a lossless path** — zero packets had
actually been lost when the request was issued. 42% of sender CPU serves it.

### ⚠️ But the waste is accidentally load-bearing

**Disabling premature repair alone measured 11% *slower*** (6.60 s vs 5.86 s), and the carousel
itself slowed from 4.76 s to 6.60 s. `HandleChunkRequestAsync` runs concurrently with
`RunChunkCarouselAsync`, so the redundant repair stream is currently **the only thing giving the
sender send-path parallelism**. Fixing the amplification without adding real send batching is a
throughput regression. This is the same trap M6 round 1 fell into, approached from the other side.

### Both sides are bound by per-datagram cost, not bytes and not crypto

Receiver, warm baseline (10,469 ms CPU / 8,087 ms wall):

| Stage | Total | µs/call | Share of per-packet work |
|---|---|---|---|
| **PeerHave** (bitmap encode + 2 awaited sends, **under `_stateGate`**) | **999 ms** | 97.5 | **38%** |
| MerkleVerify | 516 ms | 50.4 | 20% |
| DiskWrite | 465 ms | 45.4 | 18% |
| Decode | 198 ms | 0.8 | 8% |
| ProgressEmit | 140 ms | 13.7 | 5% |
| Decrypt (AEAD) | 133 ms | 13.0 | 5% |
| **Unaccounted per-datagram overhead** | **7,840 ms** | **31.6/datagram** | — |

Sender: **`SendToAsync` is 8,508 ms — 66% of all sender CPU — at 32.5 µs across 261,532 calls.**
Chunk read 801 ms, encrypt 245 ms, proof 38 ms. **GC is not a factor** (0.9–2.2% of wall, 4–8 gen2).

### Receiver-side loss is entirely self-inflicted; the sender is deaf

| Run | Role | Offered | Seen by app | Dropped |
|---|---|---|---|---|
| base | recv | 282,054 | 247,833 | **12.1%** |
| base | **send** | 282,054 | 20,707 | **92.7%** |
| window 4 | recv | 425,830 | 218,126 | **48.8%** |
| repair off | recv | 143,364 | 143,364 | **0.0%** |
| 3-recv | **send** | 315,282 | 7,889 | **97.5%** |

0% loss with repair off; 12% at baseline; 49% at window 4. Nothing is lost until Castr pushes
faster than the receiver absorbs. And **the sender drops 87–97.5% of what its own socket is
offered**, drowning in its own loopback echo — meaning **receivers' `CHUNK_REQUEST` and
`JOIN_REQUEST` are dropped at the sender with >90% probability during the carousel.** That is a
correctness hazard, not just waste.

### Corrections to earlier records in this file

- **`PEER_HAVE`'s *share* was overstated** in the derived table above. The absolute figure was
  right (~13.5 MB measured vs ~13.6 MB derived), but it is **6.7–13.1% of wire bytes at 1 receiver**,
  not ~15% of datagrams — because the repair storm doubles total wire and dilutes the share. It is
  still the **single most expensive per-chunk receiver stage** and worth +20% at 1 receiver, but
  **worth 0% at 3 receivers** (9.99 → 10.18 s) because the bottleneck moves.
- **M6 round 2's "consistent 1.4–1.6× win at window 2" did not reproduce.** Warm, n=3: window 1 =
  5.86 s, window 2 = **−13%**, window 4 = **−63%** with 48.8% real receiver loss. Both regressions
  are pure repair tail. Keeping `DefaultSendWindowSize = 1` is vindicated. The most likely
  explanation for the original reading is the page-cache confounder plus a smaller sample.
- **Syscall count alone does not explain the ceiling.** Raising the datagram budget 1200 → 60000 at
  chunk 8192 cut datagrams 12.8× for only ~9%, because the carousel then outruns the receiver and
  the win returns as a 75% repair tail. Datagram size only pays **together with** a larger chunk.
- **Retracted:** an intermediate finding that `--interface "Loopback Pseudo-Interface 1"` was 3.1×
  faster than auto-select was a cache-regime artifact. Interleaved A/B: auto 6.53/6.54/5.97 s vs
  loopback 6.50/6.01 s — **no difference.** M6's original conclusion was correct.

### Known test issue, pre-existing

`UdpMulticastTransportTests.SendThenReceive_OverRealLoopbackSocket_DeliversPayload` binds
**hardcoded UDP port 46101** and fails with `WSAEACCES` when a system process holds it. Verified
failing identically on the pristine tree — not a regression from any of this work. The port should
be made dynamic. Everything else green: 282/283 Core, plus 16/16, 14/14, 30/30, 10/10, 14/14. 0 warnings.

### Ranked by measured payoff

1. **Raise chunk size** — chunk 256K alone is **+77%** with no other change (amortizes the 439-byte
   Merkle proof and shrinks the premature-repair window). Previously ranked 5th on derived reasoning; the
   data puts it first among single changes.
2. **Chunk 256K + datagram 60000** — **0.81 s / 98.6 MB/s, 7.2×**, at *lower* wire amplification than today.
3. **Stop requesting chunks the carousel hasn't reached** — 2.52× → ~1.1× amplification, **but pair it
   with send batching or it costs 11%.**
4. **Throttle `BroadcastPeerHaveAsync`** — every-64th captures the whole +20% at 1 receiver.
5. **Filter the sender's own loopback echo** — cheap, and fixes a >90% control-traffic drop rate.
6. **Leave `DefaultSendWindowSize` at 1.**

Instrumentation lives in `tools/bench-m7/` plus a `BenchMetrics` type, inert unless `CASTR_BENCH`
is set, on branch **`bench/m7-instrumentation`**. That branch is the reproduction path for every
number in this section — check it out and run `tools/bench-m7/Run-Matrix3.ps1` to regenerate the
A/B table. It is deliberately kept off `main` because it adds `BENCH`-tagged hooks to product code
that were never reviewed for merge; the hooks are inert unless `CASTR_BENCH` is set (`Enabled` is a
`static readonly bool`, so the JIT elides the bodies).

---

## 2026-07-25 — M7 implementation A/B (P2 filters, P1 PEER_HAVE coalescing, P0 repair bounding)

Measured by the **implementer** of the change — not an independent party; weigh accordingly.
Real two-process `castr send` / `castr receive`, Release binaries published per stage, own
multicast group (239.192.57.60) and rotating ports. Warm: one discarded warm-up per stage, then
**stages interleaved within each rep**, n=3, so page-cache state and machine drift hit every stage
equally. Wire figures come from a passive external sniffer that joins the group read-only and
counts datagrams by `MessageType` — no product-code instrumentation, deliberately independent of
the `BenchMetrics` harness above.

Stages are cumulative: `stage1 = stage0 + P2`, `stage2 = stage1 + P1`, `stage3 = stage2 + P0`.
`stage4` is `stage3` with only the carousel watermark disabled, to isolate it.

### ⚠️ Host state was degraded — goodput figures here are NOT comparable to the campaign above

This run set's own unfixed baseline measured **4.68 MiB/s (≈4.9 MB/s)** against the campaign's warm
baseline of **13.65 MB/s** for the same config on the same machine. That is 2.8× low — far more than
the ~1.94× cold/warm page-cache confounder explains, so something else was also wrong (concurrent
load from a second agent benchmarking the same box is the most likely cause; not isolated).
**Every absolute MB/s figure below should be treated as invalid**, and the relative goodput column
as a property of a degraded host rather than of the change.

| Stage | goodput MiB/s (median, n=3) | wall s | stalls ≥400 ms | stall ms | period ms | wire | amp | dup chunk dgrams |
|---|---|---|---|---|---|---|---|---|
| stage0 baseline | 4.68 (4.68–4.71) | 19.28 | 24 | 468 | 695 | 191.1 MB | 2.39× | 120,060 |
| stage1 +P2 | 4.63 (4.61–4.64) | 19.58 | 25 | 466 | 684 | 192.0 MB | 2.40× | 121,224 |
| stage2 +P1 | 5.07 (5.00–5.07) | 18.03 | 21 | 444 | 695 | 178.1 MB | 2.23× | 120,589 |
| stage4 +P0 minus watermark | 5.12 (5.09–5.13) | 17.92 | 20 | 446 | 771 | 176.2 MB | 2.20× | 117,999 |
| stage3 +P0 full | 9.96 (9.73–9.98) | 10.29 | **0** | 0 | — | **90.1 MB** | **1.13×** | **324** |

Wire composition (datagram counts, median run per stage):

| Stage | PACKET_FRAGMENT | CHUNK_PACKET | CHUNK_REQUEST | JOIN / KEY_GRANT |
|---|---|---|---|---|
| stage0 baseline | 20,558 | 245,760 | 0 | 2 / 2 |
| stage1 +P2 | 20,557 | 245,076 | 0 | 1 / 1 |
| stage2 +P1 | **161** | 243,469 | 1 | 1 / 1 |
| stage4 | 90 | 241,569 | 60 | 1 / 1 |
| stage3 +P0 full | **68** | **123,228** | 29 | 1 / 1 |

320 MB, n=2, baseline vs stage3: wire **980.5 MB → 384.9 MB** (2.92× → 1.15× amplification),
duplicate chunk datagrams **567,831 → 1,862**, PEER_HAVE fragment gossip **218.5 MB → 0.79 MB**.
The wire byte-rate series flattens from a 7–15 MiB/s sawtooth to 9.6–10.4 MiB/s in every 1 s bucket.
Every run in every stage delivered a byte-identical file.

### What this run set does and does not support

**Supported:**

- **Wire amplification 2.39× → 1.13×** on an 80 MB lossless transfer; 2.92× → 1.15× at 320 MB. This
  lands almost exactly on the campaign's `repair off + PEER_HAVE off` row (1.12×) — a useful
  cross-check between two independently built measurement rigs.
- **Duplicate chunk transmissions −99.7%** (120,060 → 324 datagrams at 80 MB).
- **~100 MB of wire traffic removed per 80 MB transfer**; ~596 MB per 320 MB transfer.
- **PEER_HAVE fragment gossip effectively eliminated** — 20,557 → 68 datagrams, i.e. the quadratic
  term is gone rather than merely reduced. P1 coalesces rather than disables, so discovery is kept.
- **The periodic stall pattern is gone.** 24–25 stalls of ~468 ms on a ~695 ms period → 0, with the
  wire busy at ~10 MiB/s *throughout* the baseline's stalls — i.e. the receiver was drowning in
  duplicates, not waiting on an idle network. This claim rests on mechanism, not on the goodput
  column, and both round-2 reviewers accepted it independently.

**Not supported — explicitly withdrawn:**

- ~~"+112.6% goodput / 2.13×"~~. **Retracted.** stage3's 9.96 MiB/s ≈ 10.4 MB/s is *slower* than the
  campaign's unfixed warm baseline (13.65 MB/s) and slower than its `repair off` row (12.12 MB/s).
  The doubling measured here is recovery from a degraded host state, not an absolute gain.
  **Treat the net goodput effect of this change as unresolved.**
- The campaign's ⚠️ finding that removing premature repair costs **−11%** (the redundant repair
  stream being the sender's only send-path parallelism) is **not refuted** by this run set. Its
  arithmetic stands: base pushes 282,054 dgrams / 5.86 s = 48,132/s vs repair-off 143,364 / 6.60 s
  = 21,722/s — 2.22× the datagram rate. This run set's ~10 MiB/s ≈ 9–11k dgrams/s is a *quarter* of
  that, so a "wire rate stayed pinned" reading offered here originally was an artifact of the
  degraded host and is withdrawn too.
- An earlier hypothesis that the watermark *preserves* the repair stream for genuine gaps (and so
  keeps its incidental parallelism) is **refuted by this run set's own data**: on a lossless loopback
  path there are no below-watermark gaps, and the 120,060 → 324 duplicate drop is the measurement
  proving the repair stream was eliminated, not preserved.
- One untested explanation worth recording: **P2 is a lever the campaign never measured** and may be
  offsetting some of the predicted −7…−11%. Not isolated, and not claimed.

### P2 is a correctness fix, and must not borrow credit from P1

P2 alone measured **−1.3%** (4.68 → 4.63 MiB/s) — a wash, inside run-to-run noise — and its wire
composition is essentially unchanged from baseline: PACKET_FRAGMENT **20,558 → 20,557**,
CHUNK_PACKET 245,760 → 245,076. That is the expected result, not a disappointing one: at the 8 KiB
default chunk size the fragments in flight are almost entirely **PEER_HAVE**, and *both* role filters
must accept `PacketFragment` (a fragment carries no information about the type it will reassemble
into). So P2 cannot reduce fragment traffic even in principle. **The fragment collapse to 161 arrives
at stage2 and belongs entirely to P1.**

**P2's justification is therefore correctness, full stop: closing the >90% `CHUNK_REQUEST` /
`JOIN_REQUEST` drop rate at the sender** that the 91-run campaign measured (282,054 datagrams
offered to the sender's own socket, 20,707 seen). It should not be credited with any share of the
throughput or amplification numbers above. The only external evidence this rig could produce for it
is weak — the baseline needed a JOIN_REQUEST retry in 1 of 3 runs and P2 in 0 of 3 (see the
JOIN/KEY_GRANT column), n=3, suggestive at best. Measuring the sender-side inbox drop rate directly
requires the `BenchMetrics` harness.

### Watermark isolated (stage4 vs stage3)

Cap + mark-after-send + backoff + jitter, with only the watermark disabled, moved goodput +1.0% and
amplification 2.23× → 2.20× — within noise. **The watermark accounts for essentially the entire
amplification reduction.** Round 2 therefore added `RepairOptions.MaxRequestsPerPass` (default 4) so
amplification has a bound that does not depend on the watermark being right; before that, any
false-idle in the valve restored the full storm, self-reinforcingly.

### What to measure next

A warm, interleaved, n≥3 A/B **on a host that reproduces the campaign's 13.65 MB/s baseline**,
ideally by someone who did not write the change. Until then the merge case rests on amplification,
stall elimination and the two liveness fixes — not on throughput.

### Test-suite note

`UdpMulticastTransportTests`' hardcoded ports are now dynamic (`FreeUdpPort()` asks the OS for a
bindable ephemeral port), closing the known issue recorded above. Root cause confirmed:
`DnsService` (PID 5208 on this host) holds UDP 46101. The test still binds real sockets and asserts
real delivery. Suite after round 2: **431 passed, 3 skipped (container E2E), 0 failed, 0 warnings**,
stable across 5 consecutive full-suite runs.

---

## 2026-07-25 — M8: `CastrPaths.DefaultChunkSize` 8192 → 262144, and re-validation of M7's repair constants

Measured by the **implementer** of the change — not an independent party; weigh accordingly (rule 2).
Post-M7 `main` (M7 is merged as of `02dbab0`). 100 MB payload, real two-process `castr send` /
`castr receive` as separate OS processes over loopback multicast, Release binaries, group
**239.192.55.55** (the shipped default), rotating ports. Warm: one discarded warm-up per arm, then the
four arms **interleaved within each rep**, n=4. **Every run SHA-256 verified byte-identical against the
source — 16 measured runs plus 4 warm-ups, 20/20 identical, no exceptions.**

Two timings are reported per run, because they answer different questions:

- **wall** — sender launch to receiver exit. Includes process start, JIT, and `TransferPreparation`,
  which reads, encrypts and Merkle-hashes the *entire* file before the first chunk goes out and is itself
  strongly chunk-size-sensitive (12,800 leaves at 8 KiB vs 400 at 256 KiB). This is what the M7 harness
  measured, so it is the comparable column.
- **chunkSpan** — first to last `CHUNK_PACKET` on the wire, measured by a **passive external sniffer**
  that joins the group read-only and never sends. Transfer only, no process-start or preparation cost.
  Deliberately independent of product code — no `CASTR_BENCH` hooks, nothing added to `Castr.Core`.

### ⚠️ Read this first: on this host the multicast **group address** is worth up to 1.8x

This invalidated an entire first run set and is a new confounder of the same class as the page cache.
Identical binaries, identical payload, identical chunk size, interleaved A/B — only the group differs:

| Group | 100 MB @ 262144 | Class |
|---|---|---|
| **239.192.55.55** (Castr's shipped default) | **5.59 s** | fast |
| **239.192.57.63** | **5.60 s** | fast |
| 239.192.57.61 | 10.19–10.43 s | capped |
| 239.192.57.62 | 10.18 s | capped |
| 239.192.57.64 | 9.89–10.43 s (n=5) | capped |
| 239.192.57.65 | 10.39 s | capped |
| 239.192.57.70 | 10.16 s | capped |
| 239.192.57.128 | 10.18 s | capped |
| 239.255.1.1 | 10.41 s | capped |

Established properties, so the next person does not have to re-derive them:

- **Stable per address, not drift.** Groups interleaved within a session reproduce their own class every
  time; `.64` stayed capped across 5 consecutive runs, so it is not a "warm up the group" effect.
- **Not foreign traffic.** A read-only sniffer parked on each group with no transfer running saw zero
  datagrams on all of them.
- ~~**Not interface selection.** Auto-select vs forced `--interface "Loopback Pseudo-Interface 1"`,
  interleaved n=3, is a dead heat (5.85 s vs 5.72 s) — independently re-confirming the campaign's
  retraction of the "loopback is 3.1x faster" finding.~~
  **❌ WITHDRAWN 2026-07-25 — this bullet is wrong, and it is exactly backwards.** It was run on
  `239.192.55.55`, the one group that *already* goes to loopback under auto-select, so the comparison was
  structurally incapable of showing a difference. Interface selection is the whole confounder: the "fast"
  groups have leaked loopback memberships and the "capped" ones go over the wire. The "loopback is 3.1x
  faster" finding this was used to dismiss is **un-retracted**. See the root-cause section at the end of
  this file.
- **The capped class is a wire *byte-rate* ceiling of ~11.2 MB/s**, and that is what makes it poison for
  this particular A/B. Chunk size mostly buys datagram-count and gossip reduction; under a byte-rate cap
  none of that can show up, and the measured win collapses to just the wire-byte saving (112.3 → 105.2 MB,
  1.07x). On a capped group the same A/B reads **1.12x**; on an uncapped one it reads **1.33x**. Both are
  internally valid, interleaved, n≥3, tight-variance run sets. The ratio is *not* robust to the group, so
  **record the group with every future run in this file.**

Root cause not established, and deliberately not chased further. What matters is the discipline: the
uncapped class reconciles with every number previously recorded in this file, the capped class does not,
and the numbers below are all from the shipped default group.

### The A/B — 100 MB, 1 receiver, warm, interleaved, n=5

Measured against **the binary that ships**, i.e. after the post-review hardening described below.

| Chunk | wall (avg) | vs 8192 | wall MiB/s | chunkSpan (avg) | vs 8192 | span MiB/s | wire | amp |
|---|---|---|---|---|---|---|---|---|
| **8192** (old default) | 9.30 s | — | 10.75 | 8.23 s | — | 12.15 | 112.3 MB | 1.12× |
| 65536 | 7.69 s | **1.21×** | 13.00 | 6.89 s | 1.20× | 14.52 | 106.1 MB | 1.06× |
| **262144** (new default) | **6.98 s** | **1.33×** | **14.32** | **6.21 s** | **1.33×** | **16.10** | 105.2 MB | **1.05×** |
| 1048576 | 6.48 s | 1.44× | 15.44 | 5.77 s | 1.43× | 17.34 | 104.7 MB | 1.05× |

Per-rep wall clocks (s). Spread is under 1% in three of the four arms, which is what makes a ~30% effect
safe to read:

| Chunk | rep 1 | rep 2 | rep 3 | rep 4 | rep 5 |
|---|---|---|---|---|---|
| 8192 | 9.33 | 9.32 | 9.28 | 9.31 | 9.29 |
| 65536 | 7.51 | 7.48 | 7.72 | 7.78 | 7.98 |
| 262144 | 6.96 | 6.95 | 7.03 | 7.01 | 6.97 |
| 1048576 | 6.46 | 6.48 | 6.48 | 6.48 | 6.48 |

**This is smaller than the 1.40× that prompted the work, and that is the honest number.** The task that
commissioned this measured 8192 → 262144 at 1.40× (7.16 s → 5.10 s); this run set gets **1.33×**. The two
agree closely on shape — 65536 at 1.29× vs 1.21×, 1048576 at 1.47× vs 1.44× — and agree on the
decision-relevant quantity, the **marginal gain of 1 MiB over 256 KiB: ~5% predicted, +7.7% measured**. No
tuning was done to close the remaining gap.

An earlier n=4 set on the pre-hardening binary read **1.36×** (8192 = 9.35 s, 262144 = 6.86 s). The two
sets' 8192 baselines agree to 0.5%, so the 1.36 → 1.33 movement is run-set noise in the 262144 arm
(6.86 → 6.98 s, 1.7%) rather than a cost of the added validation clause — which only rejects, and only
packets a legitimate sender never emits. **1.33× is the figure to quote**, being the one measured against
the shipping binary with the tighter sample.

#### A degraded run set was discarded in between — the rule catching itself

The first re-run after the E2E tier drifted monotonically *within* every arm (8192: 9.40 → 8.63 → 14.60 →
15.93 s) and its 8192 baseline sat 30% below the recorded one. Cause: the 64 MB E2E payloads across 14
containers had pushed free memory to 1.17 GB and the standby cache to 368 MB, against ~2.2 GB in a healthy
state — so the 100 MB payload was being re-read from disk on later reps. That is the documented page-cache
confounder arriving by a new route. The set was discarded rather than reported, the cache re-warmed, and the
table above taken once free memory recovered to 3.76 GB.

Worth noting what it *would* have said: **1.22×**, understating the effect. Contention compresses the ratio
toward the byte-rate ceiling, exactly as the capped-group class does — so a loaded host biases this
particular A/B toward "no difference", which is the direction that would have quietly killed a correct
change. **Rep-to-rep drift within a single arm is the cheapest detector available; check it before reading
any ratio.**

### Sanity-check against the recorded baseline (rule: a row that will not reconcile is a measurement problem)

The campaign's warm baseline is **13.65 MB/s** at 80 MB / chunk 8192 — but that is *pre-M7 code*, whose
2.52× amplification came with a redundant repair stream the campaign measured as the sender's only
send-path parallelism. The right comparison for post-M7 code is the campaign's **`repair off` row, 12.12
MB/s**. This run set's 8192 arm measures **12.05 MiB/s = 12.6 MB/s on chunkSpan** — within 4% of that row.
It reconciles, and it reconciles *via the mechanism the campaign predicted*, which is a stronger result
than the raw number.

### Wire composition at the new default (passive sniffer, one lossless 100 MB run at 262144)

| Message | Datagrams |
|---|---|
| `CHUNK_PACKET` | 124,218 |
| `PEER_HAVE` | **33** |
| `CHUNK_REQUEST` | **2** |
| `ANNOUNCE` / `MANIFEST` / `JOIN_REQUEST` / `KEY_GRANT` | 1 each |
| **Total** | **124,257 datagrams / 110.9 MB for a 100 MB payload — 1.06× amplification** |

M7's bounding plus the larger chunk together leave essentially nothing on the wire but the file. Note
`PEER_HAVE` at 33 and `CHUNK_REQUEST` at 2: neither the gossip term nor the repair planner is doing
meaningful work at this chunk size on a lossless path.

### `CarouselIdleThreshold` re-validated — the margin is 36×, not the 32× degradation predicted

This was flagged as the highest-risk interaction: a false idle re-opens the amplification storm M7 exists
to prevent, and the naive argument is that a 32× larger chunk means 32× fewer watermark advances and so a
32× thinner margin. The sniffer measured the actual quantity — the gap between **strict watermark
advances**, which is what `ObserveChunkActivity` refreshes the valve on — across all 16 runs:

| Chunk | advances | mean | p50 | p90 | p99 | **worst** | gaps >500 ms | gaps >1000 ms |
|---|---|---|---|---|---|---|---|---|
| 8192 | 12,800 | 0.65 ms | 0.59 | 0.82 | 1.15 | **19.6 ms** | 0 | 0 |
| 65536 | 1,600 | 4.37 ms | 4.10 | 5.44 | 8.48 | **21.7 ms** | 0 | 0 |
| **262144** | **400** | **15.33 ms** | 14.58 | 18.91 | 24.11 | **27.7 ms** | **0** | **0** |
| 1048576 | 100 | 57.64 ms | 55.78 | 70.89 | 86.56 | **96.2 ms** | 0 | 0 |

**The prediction was wrong, and the reason is the finding.** The *mean* gap does scale with chunk size
(23× from 8 KiB to 256 KiB, as predicted), but a false idle is driven by the *maximum*, and the maximum
barely moves (19.6 → 27.7 ms, 1.4×) because it is set by host scheduling jitter rather than by the
per-chunk interval.

#### ⚠️ But do not read 36× as a safety factor — it is a property of an unloaded host

An independent QA harness measured the same quantity in-process under deliberate CPU contention and got
**~2.7× at *both* chunk sizes**. The two-process + external-sniffer numbers above are the better-isolated
measurement and stand as absolute figures, but they describe an idle machine. **On a contended host this
threshold is breached at any chunk size**, so "36× margin" is the wrong thing to lean on.

**The measurement that actually settles the risk is a load control, and it is a stronger result:** at CPU
saturation (16 contending tasks), the **old 8 KiB default produced *more* false idles than the new 256 KiB
one — 7 versus 3**. False-idle exposure under contention is therefore **pre-existing, and this change
reduces it.** Two further facts from the same runs:

- Every transfer that false-idled still completed **byte-identical**. The failure mode of a false idle is
  amplification and latency — never correctness.
- **1 MiB at load = 16 timed out entirely.** That is now a *third* independent measurement converging on
  256 KiB, alongside the +7.7% marginal throughput and the 10.4× idle margin.

Supporting adverse-case figure: on the rate-limited group class above — where the same transfer takes 1.8×
longer and the carousel visibly pauses — the worst gap was **382 ms**, a 2.6× margin, still with **no false
idle** in any run. That is closer to a representative worst case than 36×.

`CarouselIdleThreshold` is therefore **kept at 1000 ms**, on the strength of the load control rather than
the margin figure.

### Under real kernel loss, the coarser repair unit is **faster**, not slower

The central risk of this change is that a lost chunk now costs a 256 KB re-request instead of an 8 KB
one. Windows has no `netem`, so this was measured in **containers**, two of them on a Docker bridge,
using the identical `tc netem` technique as `tests/Castr.Core.E2ETests` (which cannot itself vary
`--chunk-size`). 32 MB payload — 4,096 chunks at 8 KiB, 128 at 256 KiB — 10% loss, interleaved, n=6.

| Chunk | avg | median | vs 8192 | reps (s) | byte-identical |
|---|---|---|---|---|---|
| 8192 | 24.23 s | 22.3 s | — | 23.04, 21.94, 11.94, 22.71, 43.97, 21.80 | **6/6** |
| **262144** | **8.65 s** | **9.25 s** | **2.80×** | 10.14, 8.88, 9.13, 4.45, 9.39, 9.91 | **6/6** |

**The loss case does not regress; it improves by more than the lossless case does** (2.80× vs 1.33×).
Mechanism: with the same fraction of chunks lost, 256 KiB needs ~32× fewer repair requests and ~32×
fewer request/serve round trips, and each round trip is quantised by the 5 s `RequestTimeout`. Fewer
round trips beats larger ones on this path.

**Two honest caveats, both of which cut against the headline number:**

1. **What was actually lost is 10% of *chunks*, not 10% of packets.** The netem filter matches IP
   datagrams of 1024–2047 bytes, and only each chunk's proof-carrying packet 0 is that large (the rest
   are ~728 bytes at 8 KiB, ~841 at 256 KiB). This is the pre-existing, already-tracked E2E filter
   limitation. It happens to make the arms *comparable* — the chunk-loss rate is 10% in both, and the
   bytes needing re-request are ~3.4 MB in both — but it is not a general packet-loss result.
2. **The filter also drops some of the 8 KiB arm's repair requests.** At 4,096 chunks a full
   `CHUNK_REQUEST` is ~1,136 bytes, inside the drop window; at 128 chunks it is ~116 bytes and always
   spared. So the 8192 arm is additionally penalised, and **2.80× should be read as an upper bound** on
   the true advantage. The direction of the result is safe; the magnitude is not load-bearing.

Variance is high in both arms (8192 ranges 11.9–44.0 s) because whether a stranded chunk recovers on
repair round 1 or round 2 is worth a whole 5 s `RequestTimeout`. n=6 interleaved is enough to establish
the sign and rough size, not a precise ratio.

### Docker-gated E2E suite — the strongest single signal, and it is green

`CASTR_E2E=1 dotnet test tests/Castr.Core.E2ETests` on the new default, with the container image
**rebuilt** (see the note below — a stale cached image nearly invalidated this):

| Test | Receivers | Loss | Result | netem drops |
|---|---|---|---|---|
| `SevenReceivers_NoLoss_AllReceiveByteIdenticalFile` | 7 | none | **Pass** (7 s) | — |
| `FiveReceivers_UnderRealNetemLoss_RecoverViaRepair` | 5 | 20% real | **Pass** (12 s) | **5,044** |
| `NineReceivers_UnderModerateLoss_AllRecoverByteIdentical` | 9 | 10% real | **Pass** (13 s) | **3,861** |

All 21 receiver SHA-256 hashes matched their source exactly. Real kernel drops in the thousands, real
fan-out across separate network namespaces, at 256 KiB repair granularity, unmodified tests.

Note the E2E payload is 4 MiB, which was 512 chunks at the old default and is now **16** — the suite's
own comment saying "512 chunks at the CLI's 8 KB default" is updated. The many-chunks case is still
covered by `Castr.Core.IntegrationTests` (which pin their own small chunk sizes deliberately) and by the
32 MB / 4,096-chunk loss A/B above, so the payload was left alone rather than inflating E2E runtime.

### Two environment traps that cost real time here — record them

1. **The E2E fixture reuses a cached container image and will silently test stale code.** `castr-e2e-tests:latest`
   was 3.5 hours old and built from the *previous* default; Testcontainers skips the build when the image
   exists (`WithCleanUp(false)`). The first E2E run would have "validated" 8192. **`docker rmi -f
   castr-e2e-tests:latest` before any E2E run whose result depends on a code change.** The suite's README
   already warns about staleness for a different reason; this is a second, sharper instance.
2. **Testcontainers hangs forever against a Docker Desktop `desktop-linux` context.** It probes the legacy
   `npipe:////./pipe/docker_engine` and never times out — two runs sat ~20 minutes with zero Docker
   activity. `DOCKER_HOST` cannot fix it: the docker CLI accepts only `npipe:////./pipe/...` while
   Docker.DotNet accepts only `npipe://./pipe/...`, and Castr's own skip-gate shells out to the CLI, so
   any single value breaks one side. The working fix is `~/.testcontainers.properties` containing
   `docker.host=npipe://./pipe/dockerDesktopLinuxEngine`, which configures Testcontainers alone.

### ⚠️ The campaign said chunk 256K was **+77%**. This says **1.33×**. Both are right.

This is the most instructive row in the section, and it is the reason this file records configuration
alongside every number.

| | Campaign (2026-07-25, pre-M7) | M8 (post-M7) |
|---|---|---|
| Baseline code | repair storm present, **2.52× amplification** | M7 merged, **1.12× amplification** |
| Baseline goodput @ 8192 | 13.65 MB/s | 12.6 MB/s (chunkSpan) |
| Result @ 256K | 24.10 MB/s — **+77%** | 16.31 MiB/s — **+36%** |

Neither number is wrong and neither supersedes the other. **They are measured against different
baselines, and the difference between those baselines is precisely what M7 removed.** Pre-M7, raising the
chunk size did two things at once: it amortised the per-chunk Merkle proof *and* it collapsed the
premature-repair storm, because a 32× shorter chunk list shrank the window in which the receiver could
ask for chunks the carousel had not reached. The second effect was worth far more than the first, and
M7 has since claimed it — amplification is already 1.12× at 8192 before any of this change lands, so
there is no storm left for a bigger chunk to collapse. What remains is the genuine, permanent part:
fewer datagrams, an amortised proof, and a smaller gossip term.

**The general lesson, which is the point of keeping this log: a speedup figure is meaningless without its
baseline, and "we already fixed the thing that made the old number big" is a completely ordinary reason
for a number to shrink.** A reviewer who saw only "+77% expected, 36% delivered" would reasonably suspect
a botched implementation. The correct reading is that two independent changes were partially claiming the
same win, exactly as the campaign warned when it noted that P0 and P5 multiply rather than add. Anyone
re-running this should expect ~1.33×, not ~1.77×, and should treat a result near 1.77× on post-M7 code as
evidence that M7 regressed rather than as good news.

### Post-review hardening (systems-design MERGE-WITH-CHANGES)

Review corrected the scoping of the `ChunkPacketAssembler` finding above, and the correction is worth
recording because it changes who the bug belongs to. **The allocation ceiling is manifest-derived, so it is
set by `CastrPaths.MaxChunkSize` (16 MiB), not by the default chunk size** — a sender legitimately choosing
`--chunk-size 16777216` on pre-M8 `main` already produced `new byte[16777232][]` ≈ 134 MB from a single
crafted datagram. M8 raised *typical* exposure 32× but did not create the ceiling, so "defer it with the
chunk-size change" was the wrong split. Two bounds were therefore tightened here, while the data-structure
rewrite stays deferred:

| | Before | After |
|---|---|---|
| `PacketCount` admitted for a 256 KiB chunk | 262,160 | **≤1,749** (legitimate split is ~310) |
| `DefaultMaxPendingChunks` | 1,024 | **64** |
| Worst-case pending allocation | ~2.1 GB | **~0.9 MB** |

The new predicate bounds `PacketCount <= ceil(CiphertextLength / minFragmentBytes) + 1`, with
`minFragmentBytes` plumbed from the session's own datagram budget at a deliberate 8× tolerance so a peer
relaying on a smaller budget still interoperates — 5.6× headroom for correct senders, ~150× off the attack.
Both directions are tested, and the rejection test was **verified to fail when the clause is removed**, so
it cannot be silently deleted (the mutation-coverage discipline M7 round 3 established). `1024` was itself
an 8 KiB-regime artifact: at 256 KiB it is ~16× more partial chunks than the 4096-slot transport inbox can
hold in flight, so it bounded nothing a real transfer reaches while sizing the attacker's ceiling.

Severity class, stated precisely because the first write-up understated it: ~1024 datagrams (~1.2 MB)
forcing GBs of long-lived LOH allocation is **~1800× asymmetric and terminates the process**, unlike a
packet flood, which degrades only while it lasts.

Re-verified after the change, since it touches the reassembly path the loss tests exercise hardest:
**441 passed / 3 skipped / 0 warnings** (up 2 from the new coverage), the `ChaosTransport` loss/reorder/
duplication tests still passing **unmodified with no timeout changes**, and the Docker netem E2E tier
re-run green on a **rebuilt** image.

---

## 2026-07-25 — Showcase demo re-capture (post-M8)

Real screen captures of the shipped `Castr.Cli` / `Castr.Tui` / `Castr.Gui.Desktop` binaries on
merged `main`, at the **shipped 256 KB default** — the capture scripts' previous
`--chunk-size 60000` override was removed, since the new default is both better than that
workaround and what a user actually gets. Timings read off the recordings.

| Surface | File | Chunks | Receivers | Wall clock | Goodput |
|---|---|---|---|---|---|
| CLI | 60 MB | 240 | 1 | **~4 s** | ~15 MB/s |
| TUI | 80 MB | 320 | 3 | **~10 s** | ~9.8 MB/s per receiver (dashboard-reported) |
| GUI | 100 MB | 400 | 1 | **~10 s** | ~10 MB/s |

Previous published figures were 6 s / 12 s / 28 s. **Do not read the deltas as pure M7+M8 gains** —
the older captures were taken under unknown cache and machine state, which the page-cache confounder
(~1.94×) and the multicast-group confounder (~1.8×) both make unsafe to compare against. The
controlled A/B numbers earlier in this file are the defensible ones; these are what the artifacts
show.

The GUI is the surface that changed most, because it was the one still running at 8 KB chunks — its
send view now shows `262144` in the chunk-size field, which is also the first end-to-end confirmation
that M8's `MainWindow.axaml` `Maximum` fix works (the old cap of 60000 would have silently clamped
the new default back down and made the change invisible).

### Capture-tooling notes worth not rediscovering

- `ddagrab` records the **whole screen**. Anything the demo windows do not cover lands in the GIF —
  in a first pass that included an editor window with the session that was writing these very docs.
  The capture scripts now tile the windows edge-to-edge and `ConvertTo-Gif.ps1` gained a `-Crop`
  parameter to trim to their bounds.
- **Tiling on `GetWindowRect` is not enough.** It reports 895×483 for a 94×22 console, but ~7 px per
  side is invisible DWM drop-shadow rather than painted pixels, so windows placed "flush" by those
  numbers leave a ~14 px seam of visible desktop between them. All three capture scripts now overlap
  by the shadow width. This cost three re-records to work out; the arithmetic is in each script.
- Verify a crop region by extracting one frame (`ffmpeg -ss <t> -i in.mp4 -frames:v 1 -vf crop=...`)
  and **looking at it** before converting. Guessing the geometry wastes a full re-record.
- Do not pipe `ConvertTo-Gif.ps1` through `2>&1` in PowerShell: ffmpeg writes its banner to stderr,
  and combined with the script's `$ErrorActionPreference = 'Stop'` that aborts the run before ffmpeg
  finishes, leaving the old GIF silently in place.

---

## 2026-07-25 — M9 stage 1: proof-aware chunk slicing, and a 1472-byte datagram budget

Measured by the **implementer** of the change — not an independent party; weigh accordingly (rule 2).
Post-M8 `main` plus this change. **100 MiB** payload (104,857,600 random bytes), chunk size 262144 (the
shipped default), 1 receiver, real two-process `castr send` / `castr receive` as separate OS processes
over loopback multicast, Release binaries, group **239.192.55.55** (the shipped default, the documented
fast class), rotating ports, interface auto-selected. Warm: one discarded warm-up per arm, then the
three arms **interleaved within each rep**, n=5 × 3 independent sets, plus a confirming n=3 set on the
final binaries. **52 of 54 runs SHA-256 byte-identical to the source; the other 2 never started (see
below) and were retried — so 52/52 completed runs are byte-identical, no exceptions.**

Three arms, so the two halves of the change are separately attributable:

| Arm | Binary | Slicing | Datagram budget |
|---|---|---|---|
| **A** | pre-change | every packet sized against packet 0's proof | 1200 |
| **B** | post-change | proof reserved on packet 0 only | 1200 (`--datagram-size 1200`) |
| **C** | post-change | proof reserved on packet 0 only | **1472 (new default)** |

### Predictions, derived from the code and written down before the run

Packet 0 carries `budget − 43 − proofBytes`; every later packet carries `budget − 43`. A 256 KiB chunk's
ciphertext is 262,160 bytes, and a 400-chunk file's Merkle proof is 307 bytes (depth 9):

| Arm | Derived datagrams/chunk | Derived `CHUNK_PACKET` total |
|---|---|---|
| A | 1 + ⌈(262160 − 850)/850⌉ = **309** | **123,600** |
| B | 1 + ⌈(262160 − 850)/1157⌉ = **227** | **90,800** |
| C | 1 + ⌈(262160 − 1122)/1429⌉ = **184** | **73,600** |

### Measured wire composition (passive read-only sniffer, one lossless run per arm)

The sniffer joins the group read-only and never sends; it knows only the `[version][type]` byte layout,
so it is independent of product code. Re-run after the final CLI refactor with identical counts.

| Arm | `CHUNK_PACKET` | predicted | `PEER_HAVE` | `CHUNK_REQUEST` | total datagrams | total wire bytes | amplification |
|---|---|---|---|---|---|---|---|
| A | **123,600** | 123,600 ✅ | 25 | **0** | 123,629 | 110,304,549 | 1.052× |
| B | **90,800** | 90,800 ✅ | 20 | **0** | 90,824 | 108,893,659 | 1.039× |
| C | **73,600** | 73,600 ✅ | 17 | **0** | 73,621 | 108,153,765 | **1.031×** |

**The prediction is exact in all three arms, to the datagram — and also to the byte.** The derived
per-chunk wire cost (`1200 + 307×893 + 403` for A; `1472×183 + 1003` for C) reproduces the measured
`CHUNK_PACKET` byte totals of 110,301,600 and 108,151,600 exactly. Nothing in the data path at this
configuration is unaccounted for.

**Headline: 123,600 → 73,600 datagrams for the same delivered bytes — a 1.68× reduction, and 846 → 1,425
bytes of payload in the median datagram (+68%).** The slicing fix alone accounts for 1.36× of that; the
budget raise for the remaining 1.23×. An independent transport-side analysis reached the same split
(the per-packet proof reservation is worth more than the 1200→1472 raise by itself).

#### Two corrections to previously recorded numbers, both found by predicting first

1. **`ChunkPacketizer.FixedEnvelopeOverhead` is 43, not 47.** The constant's own comment listed the right
   fields and summed them wrong, and the wrong figure had propagated into the derived prediction this
   change was commissioned against (which expected 310 datagrams/chunk, not 309). The sniffer settles it:
   123,600 = 400 × 309 exactly, and the 893-byte later-packet size that the same brief quotes is itself
   `850 + 43`. Comment corrected in code.
2. **M8's recorded `CHUNK_PACKET` count of 124,218 reconciles with 309, not 310.**
   124,218 − 400×309 = 618 = exactly two chunks re-sent at 309 datagrams each — and M8's own table records
   **2** `CHUNK_REQUEST`s on that run. Under 310 the residue is 218, which is not a whole number of chunks.
   A stale comment, a derived prediction and an older capture all agree once the arithmetic is right.

### The A/B/C — 100 MiB, 1 receiver, warm, interleaved

Wall = sender launch to receiver exit, the same definition M8 used. It includes process start, JIT and
`TransferPreparation` (which reads, encrypts and Merkle-hashes the whole file before the first datagram
and is *unaffected* by this change), so wall **understates** the transfer-only effect.

| Arm | wall (avg) | sd | min | max | vs A | goodput | datagrams/chunk | wire amp |
|---|---|---|---|---|---|---|---|---|
| **A** baseline, 1200 B | 5.801 s | 0.311 | 5.44 | 6.70 | — | 17.24 MiB/s | 309 | 1.052× |
| **B** fix only, 1200 B | 4.618 s | 0.263 | 4.38 | 5.43 | **1.26×** | 21.65 MiB/s | 227 | 1.039× |
| **C** fix + 1472 B (**ships**) | **4.074 s** | 0.229 | 3.85 | 4.42 | **1.42×** | **24.54 MiB/s** | **184** | **1.031×** |

n = 18 / 17 / 17 valid runs. Per-set averages, to show the effect is not one set's noise:

| Set | A | B | C |
|---|---|---|---|
| 1 | 5.90 s | 4.65 s | 4.19 s |
| 2 | 5.83 s | 4.72 s | 4.00 s |
| 3 | 5.75 s | 4.60 s | 4.03 s |
| 4 | 5.67 s | 4.47 s | 4.14 s |
| 5 (post-review binaries, n=3) | 5.57 s | 4.73 s | 3.95 s |

Set 5 was taken after both review rounds landed (the stale-partial fix, the preparation-time proof check, the
removal of MTU derivation) and re-confirms the result: **1.41× A→C**, and the sniffer re-run on the same
binaries reproduces the datagram counts and wire-byte totals **exactly** — 123,600 / 90,800 / 73,600 and
110,301,600 / 108,891,200 / 108,151,600 bytes, unchanged to the byte.

Transfer-only span (first→last chunk datagram, from the sniffer runs — these carry sniffer overhead but
exclude preparation): **6.23 s → 4.68 s → 3.99 s**, i.e. **1.56×** A→C, larger than the 1.42× wall ratio,
exactly as a fixed preparation cost inside wall predicts.

### Read the win as datagram count, not bandwidth — and not as a syscall ceiling

Wire *efficiency* barely moves, and quoting it as the result would make a real 1.68× look like a miss:

| Arm | payload ÷ UDP payload bytes | payload ÷ (UDP payload + 28 B IP/UDP headers) |
|---|---|---|
| A | 95.1% | 92.2% |
| C | 97.0% | **95.1%** |

Three points of framing overhead recovered, against a **40% reduction in datagram count**. State the
result as *fewer datagrams and more payload per datagram for the same delivered bytes* — **not** as
lifting a syscall ceiling. A parallel transport investigation measured the per-datagram cost directly and
it is **not** syscall time: **4.7 µs with no member joined, 32.1 µs with one receiver joined, 7.3 µs on a
real NIC** — i.e. on loopback the kernel performs the multicast fan-out delivery copy inline on the
sender's thread, which is a *loopback* property, not a system-call wall. The earlier "~37 MB/s syscall
ceiling" framing is withdrawn.

**Consequently: always quote the datagram size next to any loopback throughput number.** At a correct,
non-fragmenting 1472-byte datagram, loopback is bound by that inline fan-out at roughly 46 MB/s no matter
what the code does; this arm measures 25.7 MB/s wall, i.e. the remaining headroom on this path is real but
finite. The campaign's **98.6 MB/s** figure required 60,000-byte datagrams, which loopback carries
unfragmented and a real 1500-MTU segment shatters into ~41 IP fragments — that number does not describe
any shippable configuration and should not be used as a target.

### Why this is the structurally safe class of change

The governing rule from the architecture review: changes that reduce *datagrams per byte* are safe to land
before the receiver is fixed; changes that increase *datagrams per second* are not. This one presents the
receiver with **50,000 fewer datagrams** for the same file — it removes work from the constrained side
rather than adding arrival rate to it, so it cannot reproduce the trap that made M6 round 1 and window=2
regressions. The measurement agrees: datagram count, wire bytes, wall clock and amplification all move the
same way, which no previous change in this log has managed.

### Sanity-check against the recorded baseline (rule: a row that will not reconcile is a measurement problem)

M8 recorded **6.98 s wall / 14.32 MiB/s** for this configuration on this group. This set's *unchanged*
baseline arm measures **5.80 s / 17.24 MiB/s — 20% faster than the recorded baseline.** That is the same
class, not the 2.8×-low anomaly that invalidated M7's first report, but it is not a match either. Two
things support reading it as host state rather than as an unexplained code effect: rep-to-rep drift within
every arm is under 5% (the cheapest degradation detector this log has), and the *sniffer* runs — same
binaries, same payload, one extra process joined to the group — put arm A at **6.96–6.98 s**, precisely
M8's number. **The A→C ratio is what this run set supports; its absolute goodput figures should be
compared only within it.**

### The two failed runs were an environment defect this repo has already recorded by name

Two of 54 runs never started: the receiver printed `Receive failed: An attempt was made to access a socket
in a way forbidden by its access permissions` — **`WSAEACCES` on bind**, before any transfer began. One of
them was on **port 46101**, which is the exact port the roadmap already documents as held by this host's
`DnsService`. The harness rotates ports and occasionally lands on a reserved one; both runs were retried on
the next port and completed byte-identical. Recorded rather than silently dropped, because "2 runs produced
no file" is also the shape a real liveness bug would have — the distinguishing evidence is that the error
is at bind time.

### Loss behaviour — fewer, larger datagrams, re-validated unmodified

- **`Castr.Core.IntegrationTests`, real loopback sockets with `ChaosTransport` loss/reorder/duplication**,
  including `LargeChunkSize_WithSubChunkPacketLoss_RecoversViaChunkLevelRepair` at 10% wire-packet loss on
  256 KiB chunks: **10/10 pass, unmodified, no timeout changes.**
- **Docker `netem` fan-out tier, image rebuilt** (`docker rmi -f castr-e2e-tests:latest` first — the
  documented stale-image trap; the cached image was 2 hours old and built from pre-change code):

| Test | Receivers | Loss | Result |
|---|---|---|---|
| `SevenReceivers_NoLoss_AllReceiveByteIdenticalFile` | 7 | none | **Pass** |
| `FiveReceivers_UnderRealNetemLoss_RecoverViaRepair` | 5 | 20% real | **Pass** |
| `NineReceivers_UnderModerateLoss_AllRecoverByteIdentical` | 9 | 10% real | **Pass** |

3/3 in 1 m 30 s, all 21 receiver hashes matching, tests unmodified.

The direction of the loss argument is worth stating precisely, because "larger datagrams cost more per
loss" is only half of it. The repair unit is unchanged — a whole chunk — so one lost datagram still costs
one chunk re-request. What changes is that a chunk is now 184 datagrams instead of 309, so at a fixed
*per-datagram* loss rate the probability that a given chunk needs repair **falls**
(1 − (1−p)^184 < 1 − (1−p)^309). The larger-datagram effect dominates only if loss is byte-rate-driven
rather than packet-rate-driven, and no measurement here or in the netem tier shows that.

### Two refinements considered, both declined — and the second was implemented first, then removed

**Declined — deriving the budget from the interface MTU.** It was implemented (TTL=1 means no routers, so
the path MTU *is* the interface MTU; `GetIPv4Properties().Mtu − 28` clamped to 1472, applied only when
`--interface` named one) and then **removed in systems-design review**, for a reason that is stronger than
the mechanism is elegant: it **manufactures a peer mismatch that nobody decided on**. A laptop on a
1500-MTU LAN and a peer behind a 1400-MTU VPN would automatically pick different budgets, and mismatched
budgets silently lose peer-to-peer repair relay (see the strand below). An explicit flag at least makes
the mismatch someone's decision. `--datagram-size` stays explicit-only, documented as a value that must
match on every peer in a transfer.

If a probe is ever attempted anyway: **do not use DontFragment on Windows multicast.** The option is
silently ignored there — it reads back `true` and then fragments anyway up to 65,507 bytes — so a
DF-based probe reports that a 60,000-byte datagram "fits" and is catastrophically wrong on a 1500-MTU
segment. It behaves correctly for unicast on Windows and for both on Linux.

**Declined with evidence — carrying the sender's datagram budget in the manifest is not additively
backward-compatible.** It is the structurally right answer (it would make the budget a wire parameter and
enforce session-constancy by construction), but `ManifestVerifier.VerifySignature` **re-encodes the decoded
manifest** and verifies the Ed25519 signature over those bytes, so an old reader that skipped an appended
field would produce different bytes and fail verification — a *rejected* transfer, not a degraded one.
`ManifestCodec.Decode` also hard-rejects any `FormatVersion ≠ 1`. This needs a manifest format-version bump
and genuine lockstep, which is what this stage was scoped to avoid. Tracked as a follow-up.

### "Degrades, never corrupts" was half right — QA measured a **stranded chunk**, now fixed

The original claim here was that a mismatched slicing costs that peer's contribution to a chunk. QA showed
the failure is worse and order-dependent, with no version skew required — two **same-version** peers at
`--datagram-size 1372` (198 packets) and the 1472 default (184 packets) reproduce it:

```
after partial old-slicing relay:        pending=1  (buffer pinned at PacketCount=309)
after FULL new-sliced delivery (184):   reassembled=False
after repair re-send:                   reassembled=False
after forcing LRU eviction + re-send:   reassembled=True
```

Whichever source arrives **first** pinned the buffer, and `Forget()` runs only after a *successful*
assembly — so a poisoned partial had no recovery path except LRU eviction, which needs 64 other chunks to
go pending. A lossless transfer keeps 1–2 partials open, so in ordinary operation the chunk was **stranded
for the rest of the transfer**, and a complete, correct re-delivery could not rescue it.

**Fixed**: on a metadata mismatch `Offer` now drops the **stale partial** and re-establishes the buffer
from the incoming packet, so the newest slicing wins and any source re-sending the chunk in full completes
it. The residual cost is that two mismatched sources transmitting the *same* chunk simultaneously reset
each other's progress — a throughput-shaped failure that repair retries out of, where stranding was
terminal. Both new tests were verified to fail against the previous behaviour.

**Also correct the record on visibility: a sender/receiver budget mismatch is _not_ caught.** Direct
sender→receiver delivery works fine at mismatched budgets (the `PacketCount` bound admits both with wide
margin). The *only* consequence is the peer-relay strand above, and nothing logs or counts it. That is the
argument for the explicit must-match contract, not a claim that misconfiguration fails loudly.

The structural repair that retires the class — **key fragments by byte offset rather than packet index**,
making two slicings of the same ciphertext interchangeable — is folded into the assembler rewrite already
tracked from M8.

### ⚠️ These absolute numbers are loopback-only

Established after this run set was taken (see the root-cause section at the end of this file): Castr's
shipped default group `239.192.55.55` has a **leaked loopback multicast membership** on this host, so
every benchmark on the default group — including all of the above — never touched the NIC. The **1.42×
ratio survives** (the 309 → 184 datagram reduction is path-independent and independently verified), but
read every MB/s figure here as a loopback number, not as what a LAN would deliver.

### A crash the new knob made reachable, found in review and fixed

`ChunkPacketizer.Split` could throw **mid-transfer, out of the carousel**, on a configuration that passed
every startup check: `ValidateMaxDatagramPayload` range-checks the budget in isolation, but proof size
depends on chunk count, i.e. on file size and chunk size. `--datagram-size 548` (the new floor) with
`--chunk-size 8192` on a 1 GB file is 131,072 chunks → depth 17 → a 571-byte proof →
`548 − 43 − 571 = −66`. It was unreachable while the budget was pinned at 1200 (`1200 − 43 − 571 = 586`),
so **exposing the budget is what made it reachable**. Now validated in `TransferPreparation.PrepareFileAsync`,
where all three terms are known, failing as bad input before a single datagram is sent and naming both
`--datagram-size` and `--chunk-size` since either can be the culprit. Every proof in a tree has the same
step count, so leaf 0's proof is the exact worst case rather than a sample.

### Test suite

**480 passed / 3 skipped / 0 warnings** under `-warnaserror` (441 before; +39 from new coverage —
proof-aware slicing shape and exact counts, an old-style uniformly-sliced chunk still reassembling, mixed
old/new slicing degrading per-chunk without corrupting, `--datagram-size` bounds on both commands, and the
MTU-derivation resolver). Three pre-existing tests had pinned numbers that are *functions of the datagram
budget* and were updated with their derivations rather than their values: the `MaxChunksPerRequestFor` cap
(268 → 336; true single-datagram maximum 284 → 352), a bulk-repair fixture whose "longer than the cap" run
length was the literal 280 (now derived from the cap, so it cannot silently stop exceeding it again), and a
relay-interop fixture's packet-count threshold.

### One derived consequence, disclosed rather than hidden

`RepairOptions.MaxChunksPerRequest` is a *function* of the datagram budget — it is the fragmentation bound
"how many chunk indices fit in one datagram" — so raising the budget moves it **268 → 336** automatically.
Pinning it to the old budget would have made the constant assert something false. The consequence worth
stating: the data one inbound `CHUNK_REQUEST` datagram can command goes **67 MB → 84 MB** at the 256 KiB
chunk size. That is the count-vs-byte denomination gap already tracked in the roadmap, made 25% worse — not
a new class of exposure, and the byte-denominated fix remains the tracked repair.

**This blast radius has now silently rescaled twice, monotonically worse: 2.2 MB → 67 MB → 84 MB per
request datagram.** The structural reason is that the constant is *derived from the datagram budget* while
its meaning is *bytes served*, and chunk size decouples the two — so it will keep rescaling on any future
change to either knob. Review promoted the repair to the next milestone rather than this stage: cap
`SenderSession.HandleChunkRequestAsync` by **bytes served per request** (~8 MB) instead of index count, which
is one sender-side place that bounds it regardless of both knobs. Unpinning 268 → 336 here was right;
pinning would have made the constant assert a falsehood.

---

## 2026-07-25 — Root cause of the multicast group-address confounder: leaked loopback memberships

**This retracts a conclusion recorded twice in this file and once in `METHODOLOGY.md`, and it changes how
every number taken on the default group must be read.** Established by a separate transport investigation,
recorded here because this is where the wrong conclusion lives.

### What it is

`netsh interface ipv4 show joins` shows **stale multicast memberships on `Loopback Pseudo-Interface 1`**
for exactly `239.192.55.55` and `239.192.57.63`, both at `References = 0` — leaked, with no owning socket.
Those are precisely the two groups this file's M8 section classified as the "fast class". Every group in
the "capped class" has a membership only on the Ethernet adapter.

**Causal, not correlational:** joining `239.192.57.64` on loopback flipped it from **11.90 → 329 MB/s**
while control group `.65` stayed at 11.88 MB/s in the same session.

So the fast and capped classes were never two behaviours of one path. **They were two different paths:**
the capped class went over the wire at the NIC's real multicast rate, and the fast class never touched the
NIC at all.

### What this retracts

- **"Not interface selection" (the M8 bullet above) is withdrawn.** That test compared auto-select against
  forced loopback *on `239.192.55.55`* — the one group that already resolved to loopback under auto-select.
  It was structurally incapable of showing a difference, and it was then used to dismiss a finding that was
  correct.
- **The campaign's "loopback is 3.1× faster" finding is un-retracted.** It was right. Its retraction rested
  on the dead-heat comparison above.
- **`METHODOLOGY.md`'s "Traffic silently routing over a real NIC instead of loopback — ruled out
  empirically" is withdrawn** for the same reason, and its multicast-group paragraph is corrected.

### What it means for every number in this file

**Castr's shipped default group has a leaked loopback join on this host, so every benchmark ever taken on
the default group — M6, M7, M8 and M9 alike — has been loopback-only.** Ratios between arms measured on the
same group remain valid (M9's 1.42× rests on a 309 → 184 datagram reduction that is path-independent), but
**no absolute MB/s figure in this file describes a LAN.** Label them.

The three leaked joins (`239.192.55.55`, `239.192.57.63`, and now `239.192.57.64`, which this
investigation's own probe leaked) **clear only on reboot**.

### Two traps to carry forward

1. **Transfers under ~4 MB are meaningless as throughput measurements.** With the shipped 4 MB `SO_SNDBUF`,
   the first ~4–5 MB of a wire transfer drains into the socket buffer at **119–133 MB/s** — about 30 ms at
   line rate — before the cap engages and the transfer stalls for ~354 ms. Any benchmark small enough to fit
   in that window reports buffer-fill speed, not wire speed.
2. **Record the interface a group actually resolves to, not just the group.** M8's rule was "record the
   multicast group with every run". That is necessary and insufficient: the group is a proxy for the path,
   and the mapping is host state that changes without notice.

### The real-NIC ~12 MB/s cap is the link partner, and one systems-design prediction is refuted

Systems-design predicted that the sender's inline `SendChunkAsync` read/encrypt/proof bubble might be
contributing to the ~12 MB/s ceiling observed on the real NIC. **It is not.** The cap is the link partner —
an eero-class mesh router metering multicast to exactly 100 Mbps:

- per-frame time is **linear at 12.4 bytes/µs with a zero fixed intercept**, which is a rate limiter, not a
  per-packet cost;
- it matches the preamble+IFG-inclusive wire model to **0.07%**;
- **broadcast on the same NIC runs at 85–90 MB/s** while only `01:00:5e:*` (IPv4 multicast) destinations are
  capped.

Not the NIC, not Windows, not Castr. Recording the refutation because a plausible in-code explanation for
an environmental cap is exactly the kind of thing this log exists to stop us from believing.

**The inline bubble is still worth tracking — as a future constraint, not a current cause.** It is roughly
300–500 µs per chunk, about a 4% duty cycle at today's rates, and it becomes binding as per-datagram cost
falls. Not a contributor to this cap, and not something to "fix" on the strength of this measurement.
