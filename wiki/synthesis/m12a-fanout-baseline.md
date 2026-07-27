---
type: synthesis
title: "M12a — the real fan-out baseline: goodput, amplification, and the receiver's datagram ceiling"
tags: [measurement, fan-out, throughput, protocol]
sources: [castr-project-plan]
created: 2026-07-27
updated: 2026-07-27
---

# M12a — the real fan-out baseline

Measurement only. No protocol change, by design: M12 was split so that `SECTION_REPORT` gets designed against
numbers that describe shipped code. Full tables and methodology in the 2026-07-27 M12a section of
`docs/benchmarks/throughput-runs.md`; raw CSVs in `tools/bench/results/`; the harness is committed at
`tools/bench/`.

## Why it existed

Every fan-out figure this project quoted was unusable. **3 receivers at 8.01 MB/s against 13.65 at one, and
5.08× wire amplification** — loopback, pre-M9, pre-M10, and the 5.08× row came from the withdrawn
60,000-byte-datagram configuration. Two of the three inputs to that number no longer exist in the code.

A second gap: the M8/M9 "passive external sniffer" was never committed, so a wire-composition row could be
quoted but not reproduced. M12a closes both.

## The headline numbers

100 MiB, 256 KiB chunks, 1472-byte budget (all shipped defaults), warm, interleaved, three repeats per arm,
every arm asserting the receiver's own completion line **and** a byte-identical SHA-256 per receiver. 12/12
arms passed on each path.

| Receivers | Ethernet goodput/receiver | vs 1 | loopback goodput/receiver | vs 1 |
|---|---|---|---|---|
| 1 | **10.163 MB/s** | 1.000 | **22.886 MB/s** | 1.000 |
| 2 | 9.955 MB/s | 0.980 | 13.391 MB/s | 0.585 |
| 3 | 8.545 MB/s | **0.841** | 9.860 MB/s | 0.431 |
| 5 | 6.167 MB/s | 0.607 | 6.771 MB/s | 0.296 |

The single-receiver Ethernet row reproduces the 2026-07-26 wire measurement to within 0.4%, which is the
cheapest available check that this harness measures the same thing the last one did.

**Three receivers cost 16% of goodput on the wire, not 41%.**

## The wire does not change with receiver count — 5.08× is withdrawn

Passive read-only sniffer (`tools/bench/Castr.Bench.Sniffer`), which references nothing in `src/` and knows
only the two-byte `[FormatVersion][MessageType]` prefix, so its numbers cannot be an artifact of the product
code agreeing with itself.

| Path | Receivers | `CHUNK_PACKET` | `PEER_HAVE` | `CHUNK_REQUEST` | amplification |
|---|---|---|---|---|---|
| loopback | 1 / 2 / 3 / 5 | **73,600** in every arm | 22 / 66 / 129 / 305 | **0** | 1.0286× / 1.0287× / 1.0287× / 1.0289× |
| Ethernet | 1 / 5 | **73,600** in both | 33 / 215 | **0** | 1.0286× / 1.0288× |

73,600 = 400 chunks × 184 datagrams, [[m9-datagram-efficiency]]'s prediction, exact at every receiver count on
both paths. **Not one chunk retransmitted; the repair path never engaged.** Amplification is flat to the
fourth decimal from one receiver to five.

So the goodput curve above **is not paid for on the wire.** The sender emits identical traffic regardless of
how many receivers listen.

## Where the fan-out cost actually is

Every extra receiver *on this host* is another inline multicast copy the kernel performs inside the sender's
`sendto`. Measured directly, one sender thread, interleaved:

| Local receivers | 1 | 2 | 3 | 5 |
|---|---|---|---|---|
| µs per `SendTo` | 30.13 | 53.70 | 76.97 | 130.27 |

Linear to within 4%: **`latency ≈ 5.1 + 25.0·N µs`**, so 1.84 s per receiver per 100 MiB. Measured marginal
cost from the goodput tables: Ethernet 1.68 s/receiver, loopback 2.57 s/receiver. **The kernel copy accounts
for essentially all of the Ethernet fan-out cost and ~72% of the loopback one.**

And the host is not saturated: it delivers ~137k–175k datagram-copies/s however they are divided (four
blasting threads reach 137k where one reaches 33k), while Castr uses **10–14% of that budget**.

**The sharp finding: the same-host fan-out cost is per-send *latency*, and it converts into lost goodput only
because `SenderSession.DefaultSendWindowSize` is 1 — sender throughput is exactly 1/latency.** Recorded as a
diagnosis, deliberately **not** as a recommendation to raise the default: that has its own gate (independent
cross-machine real-LAN validation, see [[m6-throughput-pipelining]]) and M6's shared-gate double-counting
caveat still applies.

**The limit this cannot reach:** every receiver is on the sender's own machine. A switch replicates one frame
across ports, so the sender's per-datagram cost does not grow with receiver count at all. The protocol-side
evidence is consistent with real multi-host fan-out being close to flat — **a prediction, not a
measurement.** It joins the existing gating item: a dumb gigabit switch, or a second host.

## The receiver's sustained datagram ceiling

A number several decisions depended on and which did not exist. `castr-dgram drain` stands up a **real**
`UdpMulticastTransport` (shipped socket options, reader task, bounded inbox, per-datagram copy) and only
counts. Loss comes from sequence stamps, because `netstat -s -p udp` reports 0 receive errors on Windows
while hundreds of thousands of datagrams are dropped — re-confirmed here.

**≥150,000 datagrams/s (210 MB/s at 1472 B) at 0.000% loss; ≥185,000/s (260 MB/s) at ≤0.10%.** Read as a lower
bound: the drain sustained every rate the sender could offer, so it is the *sender* that runs out.

Castr asks for 7,479 datagrams/s on the wire and 16,842/s on loopback. **≥20× headroom on the wire, ≥8.9× on
loopback.** The receiver's socket drain is not the fan-out constraint, and M12b should not spend effort there.

## What this does to M12b's premise

`CAROUSEL_STATUS` / `SECTION_REPORT` can no longer be justified by "3 receivers measure 5.08× amplification."
Measured amplification is 1.029× and does not move with receiver count; repair traffic is zero at N ≤ 5 on a
lossless segment.

The remaining case is the one the architecture review already made, which this data does not touch:
**deleting the "never reached vs. finished" conflation that produced both M7 liveness bugs**, plus behaviour
under real loss and at receiver counts an order of magnitude beyond what one host can measure. That is a
correctness argument and [[proposal-section-based-repair]] should be rewritten to make it as one, rather than
leading with a throughput claim this measurement has removed.

## A defect the harness found that review had not

Two of three same-host receivers completed; the third verified a good manifest and then sat at "0/0 chunks"
forever, logging nothing. Four independent sniffer sockets counted identical datagram totals, which ruled out
the transport and the kernel.

`FileSessionRegistry.Save` wrote through a fixed `<path>.tmp`. The registry path is derived from the trust
store's *directory*, so concurrent receivers collided on that one name; `File.WriteAllText` threw
`IOException` straight out of `Record`, which runs inside `ManifestAdmission` on the receive path, and the
throw unwound manifest admission. Fixed in `b61fcaa` with a process-id-plus-random temp name, and a
persistence failure now degrades enforcement rather than failing the transfer — the same trade the class
already documented for an unreadable file on load. Two tests, mutation-verified.

Worth naming the shape: [[m11-backlog-clearance]] added this code, its tests all passed, and the defect is
invisible to any single-process test. **It took a second concurrent receiver to exist.**

## Test suite

**545 tests discovered — 542 executed plus the 3 Docker-gated E2E — 0 warnings under `-warnaserror`.** Two of those are new here, both covering the `FileSessionRegistry` defect above and both mutation-verified against the previous `Save()`. No other production code changed in M12a: the milestone is measurement, and the one fix it produced is a bug it found rather than scope it took on.

## Rules earned

- **Absolutes drifted 15–35% across one session; ratios did not.** Single-thread send rate measured 33k–63k
  datagrams/s at different points in the same day. Every comparative claim here comes from an interleaved
  run. A blocked schedule would have turned that drift into a fake trend.
- **`Start-Process -PassThru` plus output redirection returns a `Process` whose `ExitCode` is intermittently
  unreadable** (null, which compares unequal to 0), so an exit-code test reports good arms as failures. Assert
  the thing itself — the receiver's own completion line — not a proxy for it.
- **Mutation testing by backup-and-restore breaks incremental builds**: the restored file keeps its old
  timestamp, MSBuild judges the assembly current, and the next run exercises the *mutated* binary. Touch the
  file after restoring. This is the "stale-binary artifact" trap [[roadmap]] already records, in a new costume.
- **Receivers must be joined before the sender's first datagram, and a fixed sleep is not enough.** The
  carousel is single-pass with no manifest re-request path, so a late receiver hangs silently.
- **Windows refuses a multicast `sendto` out the loopback pseudo-interface with `NetworkUnreachable` unless
  the socket is also a group member.** `IP_MULTICAST_IF` alone is insufficient; binding to the interface
  address rather than the wildcard makes it worse. The shipped transport never trips over this because it
  always joins.

## Where this fits

- [[roadmap]]
- [[m9-datagram-efficiency]]
- [[m11-backlog-clearance]]
- [[m6-throughput-pipelining]]
- [[m7-repair-amplification]]
- [[proposal-section-based-repair]]
- [[wire-protocol]]
- [[repair-protocol]]
