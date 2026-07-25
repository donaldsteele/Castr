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

## Pending

A live instrumentation run is in progress to measure the things every round of M6 left
unmeasured, because they are the ones that distinguish the remaining candidate causes:

- **Throughput as a time series**, not an average — to characterise the burst/stall period and amplitude.
- **Sender `_inbox` high-water mark** and count of its own multicast echo received and discarded.
- **`CHUNK_REQUEST` messages sent by the receiver vs. actually received by the sender** — the delta
  measures repair requests lost to congestion.
- **Duplicate-chunk ratio** — how many times the file is actually transmitted.
- **Per-stage receiver CPU** (Merkle verify / AEAD decrypt / disk write / `PEER_HAVE` send) under `_stateGate`.

Results will be appended here as a new dated section rather than editing the rows above.
