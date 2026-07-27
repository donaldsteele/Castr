---
type: synthesis
title: "M11 — clearing the small backlog: ten items, one wire-format break, four latent-defect corrections"
tags: [protocol, security, robustness, memory, testing]
sources: [castr-project-plan]
created: 2026-07-27
updated: 2026-07-27
---

# M11 — clearing the small backlog

Ten tracked items, all verified still present in code at `c4a899b` before work began, cleared in ten commits.
Deliberately sequenced first (see the decision recorded in `841944e`): the backlog is cheap, and it leaves the
tree clean before M12's structural change lands in the same files.

**Result: 538 tests (up from 498), 0 warnings under `-warnaserror`, Docker netem fan-out tier green — and that
tier was run in-loop, which it had not been since M3.**

## The one that changed the wire

**Fragments are keyed by byte offset, not packet index.** `ChunkPacketMessage` carried `PacketIndex` and
`PacketCount`; a chunk's partial buffer was `new byte[packetCount][]` indexed by packet number. A packet index
only means something relative to the slicing that produced it, and three separate problems followed:

1. **Mixed datagram budgets could not interoperate at all.** Two peers on different `--datagram-size` values
   slice a chunk into different numbers of packets, so the assembler compared `PacketCount`s and rejected one
   side. That is what made the budget a whole-transfer parameter enforced *by documentation alone* — no wire
   check, no log, no metric — and it bit hardest in exactly the sender-offline case peer repair exists for.
2. **M9's stranding class was mitigated, not removed.** Whichever slicing established a chunk's buffer first
   pinned it; `Forget()` runs only after a *successful* assembly, so a mismatched partial was terminal for that
   chunk. M9's fix (newest slicing resets the buffer) meant two sources transmitting at once reset each other.
3. **`PacketCount` sized an allocation.** Bounded, not absent: a legitimate 16 MiB chunk size admitted a claim
   of ~16.7M packets — ~134 MB of references from one small datagram.

A partial is now one ciphertext-sized buffer plus the set of byte ranges written into it. Byte ranges are a
property of the ciphertext rather than of a sender's slicing, so **all three problems dissolve together**:
slicings combine freely, no source can reset another's progress, and nothing on the wire sizes an array by
count. Overlap resolution is first-writer-wins, so a hostile peer cannot overwrite bytes a good source
delivered; it can only fill holes nobody has filled, which produces a chunk that fails Merkle verification and
is dropped *and forgotten* whole, so the next round starts clean.

The replacement resource bound is on **fragmentation** rather than count: coverage may split into at most
`ceil(len / minFragmentBytes) + 2` disjoint ranges, which stops scattered one-byte fragments from growing the
interval list while admitting every packet a real transfer produces.

**`MessageCodec.FormatVersion` 1 → 2.** Bodies are not self-describing and the field layout moved, so the
version byte is what turns a cross-version peer into a clean rejection at `Decode` instead of a silent
misparse. The envelope shrank 43 → 39 bytes (one field where there were two); M9's datagram-count predictions
of 227 and 184 per 256 KiB chunk both survive it unchanged, so those measurements still describe shipped code.

Retires the "`--datagram-size` must match on every peer" contract from CLI help, `DatagramBudget` and
`SendRunner`'s operator note. See [[wire-protocol]].

## Security and robustness

**Session ids are now bound to transfers.** The id was length-checked and otherwise taken on faith. It is
`ContentKeyWrap`'s HKDF salt and every chunk's AEAD domain separator, so two transfers sharing one re-derive
the same wrapping key from the same X25519 pair. It was safe only because `Castr.Cli` mints a fresh random id
per invocation — a property of one client, not of the protocol. New `ISessionRegistry` binds an id to
(sender, manifest digest); `ManifestAdmission` returns `SessionIdConflict` for a mismatch. Three properties the
design turns on:

- Reuse for the **same** transfer stays accepted — a resume, a re-announce, a second peer relaying the same
  transfer all present the same id, and refusing those would break every resumable path in the system.
- Only **accepted** manifests record, or any holder of a valid Ed25519 key could burn an id a legitimate
  transfer was about to use, making the check itself a denial-of-service primitive.
- The registry is **persistent** (`FileSessionRegistry`, beside the trust store). This is the load-bearing
  choice: the CLI runs one transfer per invocation, so a process-lifetime registry would classify every id as
  fresh and enforce nothing — a check that is not a check. Bounded and oldest-first evicted so it cannot become
  the leak it prevents.

**Manifests are range-checked at admission.** `ManifestFileEntry.ChunkSize` was an unbounded `ReadInt32` and
was never checked afterwards. Being signed makes a manifest *authentic*, not *well-formed*. A trusted-but-buggy
sender's `ChunkSize` near `int.MaxValue` made `CiphertextBoundForChunkSize` wrap negative and throw straight out
of a receive loop that does not wrap manifest handling — one signed manifest took the whole receiver down. New
`ManifestLimits` checks chunk size in `[1, 16 MiB]`, non-negative size, non-empty path, and — independently of
any range — that `ChunkCount` agrees with `(Size, ChunkSize)`, since `ReceiverSession` sizes every `ChunkBitmap`
from the first while every byte offset comes from the other two. `CastrPaths.MaxChunkSize` now derives from it,
so the CLI ceiling and the receiver's accept ceiling are one constant. Retires the M8-era in-code gap note.

## Memory and lifetime bounds

- **`SwarmPullSession`'s chunk cache** was the last instance in the tree of the defect M10 fixed in the
  multicast tier. Now a byte-bounded queue on M10's shape, with two deliberate departures that follow from this
  class not being an `ISwarmContentSource`: no proof retention (nothing reads a proof after verification, so
  retaining one per chunk would be write-only state unbounded in chunk count — the defect, not the fix) and no
  cold rebuild (the only reader runs before any plaintext exists on disk). That also rules out M10's *pinning*
  of undecrypted chunks: on this tier "verified but no key" is an ordinary way to run a whole transfer — a
  puller taking ciphertext from relaying receivers, which cannot grant the key — not a startup window, so
  pinning would mean no bound at all. An evicted undecrypted chunk instead has its bitmap bit cleared with it,
  putting it back in the missing set rather than stranding it.
- **`SwarmServeListener`** kept one `Task` per connection ever accepted, drained only at shutdown — growth
  tracked *uptime*, not concurrency, which is why nothing measuring peak concurrency would have caught it. Now
  pruned per accept, with concurrency capped at 64 and the slot taken *before* `AcceptAsync`, so excess dials
  wait in the transport backlog rather than being accepted and starved.
- **`UdpUnicastTransport`** still had the pre-M6 shape — its own iteration *was* the read loop. Latent, because
  nothing in the shipped composition enumerates it; the mobile tier is unicast TCP, despite this type's summary
  claiming it was "the sole transport on the mobile tier", which is what put the item on the backlog. Fixed
  anyway, on the multicast sibling's pattern: two datagram transports differing in concurrency model is the kind
  of asymmetry that gets copied.
- **`TransferDashboard`** woke on a maximum-less `SemaphoreSlim(0)` wrapped in a
  `catch (SemaphoreFullException)` that could never fire, so permits accumulated one per progress event — one
  per verified chunk. Fixed as a named `RenderSignal` type rather than the one-character bounded-semaphore
  change, because a bare bounded semaphore leaves the coalescing intent untested, which is how the dead catch
  survived in the first place.

## Correctness of what things are called

Two paired eviction policies documented themselves as LRU while `Sequence` was assigned once in the constructor
and never touched on access. Both are FIFO by establishment; the field is now `EstablishedAt`. `ReceiverSession`'s
cache genuinely *is* LRU (`PlanChunkServe` promotes on hit) and was left alone. Notably the held-ciphertext queue
added earlier in this same milestone had already inherited the wrong name — evidence the item was worth doing.

## The E2E loss filter was measuring something other than what it claimed

The sender-egress `tc netem` filter matched IP total length in `[1024, 2047]`, justified by a comment written
against a 1200-byte datagram budget and 8 KiB chunks. Both defaults have since moved (1472, 256 KiB), and:

- it **spared every datagram under 1024 bytes**, which is control traffic by design but also the short tail
  packet each chunk ends with — 228 payload bytes at the shipped pair, so one packet in every 184 was
  undroppable and the loss receivers saw was neither the configured rate nor uniform across a chunk;
- it **dropped large control datagrams**, including the `PacketFragment` slices a big manifest travels as: the
  exact traffic the comment claimed to protect. Invisible only because the fixture's manifests are tiny.

Now selects by Castr **message type** (3/6/11 at IP offset 29), sparing all control traffic at any size.
`CHUNK_RESPONSE` is included deliberately so repair traffic is lossy too. **Run in-loop against Docker**, which
the previous filter's M3-era note said had never been done: three fan-out arms green, 13,934 netem drops on the
5-receiver/20% arm, every receiver's hash byte-identical.

## Rules earned

- **A check scoped so it can never fire is not a check.** Both the session registry (persistence) and the
  dashboard signal (the dead `catch`) turned on this. It is M10's `BuildNonce` lesson in two new costumes.
- **A test that passes for a reason unrelated to the property is worse than no test.** The connection-pruning
  test passed against an un-pruned implementation because the counter is zeroed at shutdown and the assert ran
  after cancellation. Caught only by mutating the fix and expecting a failure that did not come.
- **Pace a burst when the property is "was it drained", not "was it fast".** The unicast-transport test failed
  once under a full parallel run because an unpaced 420 KB burst also measured CPU contention. Pacing costs the
  control nothing — the old shape issues no read at all until enumeration.
- **Signing makes a message authentic, not well-formed.** Stated because the M8 note treated a
  trusted-sender-only path as therefore lower priority; the failure it produced was still a whole-receiver crash.
- **Verify before fixing pays.** The last item split into a genuine no-op (TCP framing — M6's defect needs a
  lossy datagram socket, and TCP gives backpressure instead) and a real-but-latent one, and the suspicion that
  put it on the list came from a stale docstring rather than from the code.

## Not done

No independent QA subagent pass: this session ran under an explicit instruction not to spawn subagents. Every
item is instead covered by a test **mutation-verified against the pre-fix behaviour** — for items 1-5 and 7-10,
the fix was reverted or disabled and the corresponding test confirmed to fail. Item 6 is naming-only and has no
behavioural control by construction.

See [[roadmap]] for what M12 needs next.
