---
type: concept
title: "Castr repair protocol"
tags: [protocol, decision, platform-quirk]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-25
---

# Castr repair protocol

How a [[castr-project]] receiver that's missing chunks gets them from peers instead of re-burdening the original sender — the mechanism that makes "one send, hundreds of receivers" hold up under real-world packet loss.

## Algorithm

1. Receiver tracks a per-file bitmap of received-and-verified chunks (verification uses the Merkle proof described in [[wire-protocol]]).
2. On stall or a detected gap, it ranks candidate peers from an `IPeerTable`: not-the-original-sender first (this is the bandwidth-offload goal), then most-complete-file, then a random jitter tiebreak.
3. Missing indices are split across multiple candidate peers in parallel, each request short-timeout with retry against a different candidate on failure.
4. **Falls back to the original sender only if no peer answers.**

## Desktop vs. mobile delivery mode

- **Desktop (real multicast available)**: repair responses are themselves **multicast**, rate-limited/deduplicated — one fulfilled repair request then self-heals every receiver with that same gap, not just the requester. This is NORM (RFC 5740) / FLUTE (RFC 3926) -style behavior, chosen deliberately over pure unicast repair because it directly serves the bandwidth-fan-out goal. Repair *requests* can also be multicast with randomized-delay NACK-style suppression: a receiver that overhears someone else already asking for the same chunk skips its own duplicate request. **⚠️ The suppression and response rate-limiting/deduplication described in this bullet are design intent, not shipped behavior — see "Design intent vs. implemented behavior" below.**
- **Mobile (no multicast, see [[castr-project]])**: peer discovery is via native mDNS instead of multicast gossip, and repair is strictly unicast — there is no fan-out win available on this tier, only the swarm-pull benefit of not depending solely on the original sender. **Implemented in M4** — see [[m4-mobile-summary]] and [[wire-protocol]]'s new swarm-pull section: `SwarmPullSession` over TCP is the mobile repair/pull mechanism, `IServiceDiscovery` (`NsdManager`/`NWBrowser`, see [[tech-stack]]) is the mobile peer-discovery mechanism, and both share the exact same manifest/Merkle/AEAD verification the desktop multicast tier uses — a pull from an untrusted or malicious mobile peer is exactly as safe as a multicast repair response.

## The `IPeerTable` abstraction (cross-tier design constraint)

`IPeerTable` is populated differently per tier — multicast-carried `PEER_HAVE` gossip on desktop, mDNS + gossip on mobile — but is consumed identically by one `RepairCoordinator`. This abstraction had to be designed during the [[roadmap]]'s core milestone (M1) even though mobile is the last milestone (M4): building the repair coordinator multicast-first without this seam would force a rework once mobile work started, quietly breaking the "mobile last is free" assumption in the milestone sequencing. **That bet paid off in M4**: `PeerTable.ObserveDiscovered(Endpoint, DateTimeOffset)` was added to feed mDNS-discovered peers in (with an `UnknownChunkPopCount = -1` sentinel, distinct from gossip-confirmed zero, so a peer only *discovered* but never confirmed to have any chunks still sorts strictly last in `RepairCoordinator`'s ranking) without any change to `RepairCoordinator` itself or to how the desktop tier populates the same table.

## Failure modes handled by design

- **Sender offline, no peer has the chunk**: surface a stalled/failed status in the TUI/GUI after a configurable max-retry window; keep the partial `.part` file (chunk-level resumability makes this natural) and auto-resume if sender or any peer reappears.
- **Peer goes offline mid-repair**: request timeout triggers retry against the next candidate; `PEER_HAVE` entries expire on a TTL (~15s) so stale peers age out of the table on their own.
- **Thundering herd** (many receivers requesting the same missing chunk at once): randomized jitter before the first request, exponential backoff on retry, and — on desktop — the multicast-repair-response behavior above, where one response satisfies everyone at once. **⚠️ Only the multicast-response half of this is implemented; the jitter and backoff are not — see below.**

## Design intent vs. implemented behavior

**Everything above describes the intended design. Four specific defenses it names are not present in the code**, discovered 2026-07-25 while investigating a bursty-throughput report (see [[m6-throughput-pipelining]] and `docs/METHODOLOGY.md`):

| Documented above | Actual state in code |
|---|---|
| Randomized jitter before a first repair request | **Not implemented.** `RepairCoordinator.PlanRepairs` plans immediately; the only randomness is `PickAmongTopRanked`'s tiebreak among equally-complete peers. |
| Exponential backoff on retry | **Not implemented.** Retry cadence is a fixed 250 ms poll in `ReceiveRunner.RunRepairLoopAsync`, plus a flat 5 s `RepairOptions.RequestTimeout` before a pending chunk is re-requestable. |
| NACK-style suppression by overhearing another receiver's request | **Not implemented.** `ReceiverSession.HandlePacketAsync` does see every peer's `ChunkRequestMessage`, but nothing feeds those indices into the local `RepairCoordinator`, so duplicate asks are never suppressed. |
| Rate-limited / deduplicated repair responses | **Not implemented.** `ChunkRequestMessage` carries no target field — `RepairRequestPlan.Target` is computed by the coordinator and then discarded when the wire message is built — so every peer holding a requested chunk answers, unthrottled. |

Consequences measured/derived so far: repair fires 250 ms into a transfer and requests *every* not-yet-arrived chunk, which the sender re-reads, re-encrypts and re-sends concurrently with the still-running carousel; and `_pending` is marked before the request is sent, so a single lost request fragment costs a full 5 s of silence. Tracked as open work in [[roadmap]].

This entry exists because three independent review rounds read this page alongside the code without noticing they disagreed. Documentation that states intent in the present tense reads as a description of behavior and stops people from checking — the lesson is recorded in `docs/METHODOLOGY.md`.

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[m4-mobile-summary]]
- [[m6-throughput-pipelining]]
- [[roadmap]]
