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
is set (branch `worktree-agent-aad9c2ea41236900d`, uncommitted).

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
