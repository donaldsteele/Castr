# How Castr's performance work is done

The demos in [`SHOWCASE.md`](SHOWCASE.md) got roughly 5× faster between two recordings. This page
is about *how* that happened, because the process turned out to be more interesting than the
result — the first fix we shipped was aimed at the wrong half of the system, and the only reason
we found out was a review discipline that assumes the person who wrote a change is the worst
person to validate it.

Raw numbers for every run referenced here live in
[`benchmarks/throughput-runs.md`](benchmarks/throughput-runs.md).

---

## The three rules

### 1. Measure the real binaries over a real socket, or don't claim a number

Every throughput figure in this repo comes from launching the actual shipped `castr send` and
`castr receive` as separate OS processes, talking over a real UDP multicast socket, with real
ChaCha20-Poly1305 encryption, real BLAKE3 Merkle verification, and real disk writes. Castr has a
perfectly good `InMemoryMulticastTransport` used heavily in unit tests — it is never used for a
performance claim, because the entire class of bug we were hunting lives in the interaction
between a kernel socket buffer and a userspace processing loop, which an in-memory transport
models away by construction.

The same rule produced the demo GIFs: they are screen captures of the real application, and when
the throughput fix landed we re-recorded all three rather than editing the captions.

### 2. The person who wrote the change cannot be the one who validates it

This is not a courtesy rule; it is in place because ignoring it cost us a wrong shipped default.

In round 1 of the throughput investigation, the implementer benchmarked a new sender-side send
window and read the results as "roughly neutral, occasionally faster," and shipped a default of 2
on that basis. An independent reviewer re-measured *the identical code* and found a **consistent
1.8–2.7× regression**. Same binary, same machine, opposite conclusion — the difference was sample
size and a willingness to disbelieve the result.

So every round of performance work here is reviewed by two independent agents before merge: one
QA-focused (reproduce the numbers, hunt regressions) and one systems-design-focused (read the
code paths and say whether the mechanism makes sense). Between them they caught four distinct
real defects across three rounds that the implementer's own passes missed, including a data race
in `ThroughputSampler`, a non-idempotent `DisposeAsync`, and silently swallowed exceptions in the
new receive loop.

### 3. Predict the cost from the code first, then measure — the disagreement is the finding

Before running a benchmark, derive what the code *should* cost from its own constants: message
encodings, packetizer arithmetic, datagram counts. Then measure. When prediction and measurement
agree, you understand the system. When they disagree, you have found something.

This is how the largest remaining problem surfaced. The derived cost of Castr's per-chunk
progress gossip is `FileSize² / (8 · chunkSize²)` — quadratic in file size. Nobody had computed
that during three rounds of M6, and once computed it retroactively explained a measurement M6
had already taken and misattributed (see "what we got wrong," below).

---

## Approaches considered, and why we chose what we chose

### Ruled out before writing any code

**Explicit rate limiting or throttling in the send path.** A code search found none — there was
nothing to remove. Worth checking first precisely because it would have been the cheapest
possible explanation.

**Traffic silently routing over a real NIC instead of loopback.** Ruled out empirically by
forcing `--interface "Loopback Pseudo-Interface 1"` on both ends. No meaningful change.

### Approach A — Pipeline the sender's send loop *(shipped, then reverted to a no-op)*

The send path was fully sequential: one awaited `transport.SendAsync` per ~1200-byte wire packet,
on the order of 70,000 one-at-a-time awaits for an 80 MB file. Replacing that with
`Parallel.ForEachAsync` under a bounded concurrency window is an obvious-looking win, and safe —
`PacketReassembler` and `ChunkPacketAssembler` are both index-keyed and order-independent, so
concurrent out-of-order sends required no wire-format change.

It did not work. Throughput did not scale monotonically with concurrency: windows of 3–4 were a
consistent 2–5× *regression*, and a window of 64 stalled outright with the sender reporting 100%
while the receiver sat frozen below 40%.

**Why we kept the mechanism but set the default to 1:** the diagnosis that explains those numbers
is that the sequential loop had been *accidentally providing flow control*. Each awaited send's
own latency paced emission to roughly what the receiver could absorb. Adding concurrency removed
the accidental brake without fixing the thing it was braking for. The windowing code is retained,
tested, and exposed as `--send-window-size` for anyone who has validated a higher value on their
own hardware, but ships at 1 — which is a genuine no-op, since the carousel and repair listener
already ran as concurrent tasks before this change existed.

### Approach B — A shared gate to make the send window a true global bound *(prototyped, rejected)*

The window is enforced per-loop, so a repair burst overlapping the carousel can transiently
double real concurrency. A shared `SemaphoreSlim` fixes that correctly. It also cost **~30%
throughput at zero contention** (≈11 MB/s → ≈7.6 MB/s), because a `WaitAsync`/`Release` pair per
chunk is not free at tens of thousands of chunks even when it never blocks.

**Why we rejected it:** the fix's cost directly undermined the goal it was in service of, and the
gap it closes self-limits to a spike of 2 at the shipped default — a value the data calls safe.
Documented as a known limitation in code instead of shipped. Worth revisiting with a cheaper
primitive than `SemaphoreSlim` if the double-counting ever proves to matter in practice.

### Approach C — Decouple the socket read from downstream processing *(shipped — this was the real fix)*

`UdpMulticastTransport.ReceiveAsync`'s own `await foreach` **was** the read loop. The next
`ReceiveFromAsync` was not issued until the caller had fully finished handling the previous
packet — Merkle verify, AEAD decrypt, disk write, and an outbound broadcast, all serialized under
one lock. So the kernel receive buffer drained only as fast as that entire chain ran, and any
attempt to send faster just overflowed it.

The fix is a dedicated reader task that does nothing but pull datagrams into a bounded
`Channel<ReceivedPacket>`, with `ReceiveAsync` reduced to enumerating that channel. Bounded
rather than unbounded on purpose: `BoundedChannelFullMode.Wait` means sustained mismatch produces
real backpressure rather than unbounded memory growth or silent drops.

**Why this was the right lever:** it converted an accidental zero-slack coupling into deliberate
bounded buffering, and it is what took the demos from ~1.6–2.4 MB/s to ~8 MB/s. The decisive
evidence was not the throughput number but a *qualitative* change — the window=64 configuration
that previously hung forever now merely completed slowly (47 s). A change that turns a deadlock
into slow-but-correct is acting on the real constraint.

### Approach D — Explicit socket buffer sizing *(shipped, adopted from UFTP)*

Raising `SO_RCVBUF`/`SO_SNDBUF` to a best-effort 4 MB, up from whatever the OS default was
(as low as ~256 KB on some platforms). This came directly from reading
[UFTP](https://github.com/digarok/uftp-multicast), which treats buffer sizing as a primary
throughput lever and exposes it as a `-B` flag.

**Why it is complementary, not a substitute:** a bigger buffer only delays the same overflow if
the consumer stays slower than the arrival rate. It buys slack against bursts; Approach C is what
raised the ceiling. Shipping only this would have looked like a partial fix and hidden the real one.
Castr's 4 MB default is now 16× UFTP's own 256 KB default, so this lever is spent.

### Approach E — Port UFTP's TFMCC congestion control *(rejected)*

UFTP is a mature C implementation with a reputation for near-wire-speed multicast, so it was
worth asking what it does that Castr doesn't. The headline answer looked like TFMCC (RFC 4654)
rate control.

**Why we rejected it — and this is the most useful thing the comparison produced:** reading
UFTP's actual source shows its default rate is `DEF_RATE 128000` bytes/sec, i.e. **1000 Kbps —
slower than Castr already was**. UFTP only reaches wire speed with `-R -1`, which sets
`packet_wait = 0`: unpaced blasting, exactly what Castr does. Its own default for `cc_type` is
`none`. So congestion control is *not* the source of its advantage, and porting a large, subtle
subsystem (CLR selection, feedback rounds, GRTT tracking) would have solved a problem Castr does
not have on a TTL=1 link-local segment with no shared-Internet fairness obligation.

The general lesson: "project X is fast, copy project X's most prominent feature" is a bad
inference. The prominent feature was not the reason.

### Approach F — Adopt UFTP's NACK-only, section-aggregated feedback *(identified; not yet implemented)*

What UFTP actually does differently is feedback discipline, and the contrast is stark rather than
incremental:

| | UFTP | Castr today |
|---|---|---|
| Loss signalling | NACK-only; **silence means success** | Positive acknowledgement of **every chunk** |
| Payload | Small, unicast to the server | **Full bitmap**, multicast to everyone |
| Cadence | At section boundaries, only if something is missing | Per chunk, unconditionally |
| Volume, 80 MB | **≤6 messages** per receiver | **~20,480 datagrams / ~13.6 MB** per receiver |
| Scaling | O(sections), independent of receiver count | O(receivers × chunks); bytes quadratic in file size |

Castr also lacks three interlocking guards UFTP has: receivers arm a NAK timer only at section
boundaries so they never ask for data still in flight; the server explicitly discards status for
the section it is currently transmitting; and repair runs in a *later pass*, never interleaved
with first transmission. Castr's repair fires on a fixed 250 ms poll with no such gating, which
means it requests chunks the sender has not sent yet and the sender dutifully re-sends them
concurrently with the carousel.

**Why we are adopting the direction but not the mechanism wholesale:** Castr's per-chunk
`PEER_HAVE` doubles as free peer discovery, which is a genuinely good design choice — it avoids a
separate discovery subsystem. That dual purpose is fully preserved by emitting on a section or
time boundary instead of per chunk; peers still learn about each other within one section. The
fix is the *frequency* and the *placement* (currently an awaited socket send inside the lock that
serializes all packet processing), not the message.

### Where we are deliberately unlike UFTP, and copying it would be wrong

**End-to-end verifiability.** UFTP's data-path integrity is a group HMAC or AEAD tag under a
group key — it authenticates "someone in the group sent this," which suffices only because in
UFTP *only the server ever sends data*. Castr's Merkle-proof-over-ciphertext with a
signature over the root authenticates "this is chunk *i* of the transfer the sender signed,"
independent of who relayed it. That is precisely what makes **repair from untrusted peers** safe,
and it is the load-bearing primitive for the entire swarm feature and the mobile unicast tier.
The per-chunk hashing cost is the price of a feature UFTP does not have. The correct response is
to amortize it with larger chunks, not to trade it away for a group HMAC.

**Peer-assisted repair.** UFTP has no peer relay; a client that misses data can only ask the
server or a proxy. Castr's `CHUNK_REQUEST`/`CHUNK_RESPONSE` and mobile `SwarmPullSession` are a
strictly larger capability. The pending repair work is about *when* repair fires, not about
removing it.

**Receivers cannot grant keys.** A receiver serving a chunk to a peer returns `null` from
`TryGrantContentKey` — not as a policy check but as a cryptographic impossibility, since it never
holds the sender's X25519 private key. UFTP has no analogue.

---

## What we got wrong, on the record

Keeping this list is the point of the page.

**We shipped a default based on the implementer's own benchmark.** Round 1's
`DefaultSendWindowSize = 2` was a real regression that an independent re-measurement caught. This
is the origin of rule 2.

**We fixed the second-most-important thing first.** Sender-side pipelining was a real
inefficiency, but it was downstream of the receiver-side constraint. Optimising it in isolation
made things *worse*, because the inefficiency was accidentally load-bearing.

**We measured averages and called it throughput.** Every M6 number is an average over a whole
transfer. An average cannot distinguish a steady 8 MB/s from alternating 20 MB/s bursts and
multi-second stalls — and the latter is what a user reported immediately after M6 shipped.
Consistency was never measured because nobody thought to state it as a goal. Time-series
sampling is now part of the method.

**We had a measurement that contained the answer and misread it.** Round 2 measured chunk size
8192 → 60000 as 8.1 → 10.1 MB/s and attributed the gain to fewer datagrams. But that change only
cuts data datagrams ~20%, while it collapses progress-gossip traffic from ~13.6 MB to ~0.3 MB.
The gain tracked the gossip collapse. The quadratic gossip cost was sitting in our own data for
two rounds, unnoticed, because we never derived what the code should cost (rule 3).

**We benchmarked across a ~2× confounder without controlling for it.** The OS page cache changes
measured throughput by 1.94× on *identical* configuration with identical datagram counts (12.6 s
cold vs 6.5 s warm; `SendToAsync` goes 29 → 53 µs/datagram when cold). No M6 round controlled for
it. This is the most likely explanation for the single most embarrassing entry below.

**An M6 result did not reproduce.** Round 2's headline finding — window=2 as a "consistent
1.4–1.6× win" — re-measured warm, interleaved, n=3 as a **13% regression**, with window=4 at
**−63%** and 48.8% real receiver-side packet loss. The shipped default of 1 was right, but it was
right for a reason we had not established: we kept it out of caution about *validation process*,
not because we knew the number was wrong. Caution substituted for correctness here, which is luck,
not method. Rule 1 now includes controlling cache state and interleaving A/B reps.

**We also over-attributed a cause we had correctly identified.** The per-chunk progress-gossip
cost is real and is the single most expensive per-chunk receiver stage (97.5 µs/call, 38% of
measured per-packet work). But its *share of wire bytes* is 6.7–13.1% at one receiver, not the
~15–20% derived here — because the repair storm doubles total wire and dilutes it. And it is worth
**+20% at one receiver but 0% at three**, because at fan-out the bottleneck moves elsewhere. A
correct diagnosis at the wrong magnitude still misdirects effort.

**Our design docs described defenses the code did not have.**
`wiki/concepts/repair-protocol.md` specifies randomized jitter before a first repair request,
exponential backoff on retry, NACK suppression by overhearing another receiver's request, and
rate-limited repair responses. **None of the four are implemented.** Three review rounds read
that page and the code without noticing they disagreed — which is plausibly *why* the repair
behaviour went unexamined. Documentation that describes intent as though it were behaviour is
worse than no documentation, because it stops people from looking.

---

## Current state

The receiver-side decoupling fix is shipped and the demo numbers in
[`SHOWCASE.md`](SHOWCASE.md) reflect it. A 91-run instrumented campaign has since measured the
remaining causes directly — see the 2026-07-25 section of
[`benchmarks/throughput-runs.md`](benchmarks/throughput-runs.md). Headline results:

- **The burst/stall period is 5.10 s**, set by `RepairOptions.RequestTimeout`, not the 250 ms
  repair poll. Amplitude is 0.00 MB/s → 39 MB/s, with up to 70% of a transfer spent in total dead air.
- **The file is sent twice.** The first repair request, issued while the carousel is ~1% done, asks
  for 10,212 of 10,240 chunks. Wire amplification is 2.52× on a *lossless* path.
- **Both sides are bound by per-datagram cost (~32 µs each way)**, not by bytes and not by crypto.
  `SendToAsync` alone is 66% of sender CPU. GC is not a factor.
- **The biggest single win is the one we had ranked fifth on derived reasoning**: raising the chunk
  size is **+77%** by itself, and combined with a larger datagram budget reaches **98.6 MB/s — 7.2×
  baseline — at lower wire amplification than today.** Derived reasoning ranked the elegant protocol
  fixes above the boring parameter change. Measurement reversed that, which is rule 3 doing its job.
- **The waste is accidentally load-bearing.** Removing the premature repair *alone* is **11%
  slower**, because the redundant repair stream is currently the only thing giving the sender any
  send-path parallelism. Exactly the trap M6 round 1 fell into, approached from the other side —
  which is a strong hint that "this inefficiency is load-bearing" is a recurring property of this
  codebase rather than a one-off.

Work is in progress on the feedback and repair discipline of Approach F, tracked in
[`../wiki/synthesis/roadmap.md`](../wiki/synthesis/roadmap.md). Nothing there has merged; per rule 2
it goes through independent QA and systems-design review first, and per the point above any fix to
the amplification must be measured together with send batching rather than in isolation.
