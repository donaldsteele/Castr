---
type: concept
title: "Castr repair protocol"
tags: [protocol, decision, platform-quirk]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# Castr repair protocol

How a [[castr-project]] receiver that's missing chunks gets them from peers instead of re-burdening the original sender — the mechanism that makes "one send, hundreds of receivers" hold up under real-world packet loss.

## Algorithm

1. Receiver tracks a per-file bitmap of received-and-verified chunks (verification uses the Merkle proof described in [[wire-protocol]]).
2. On stall or a detected gap, it ranks candidate peers from an `IPeerTable`: not-the-original-sender first (this is the bandwidth-offload goal), then most-complete-file, then a random jitter tiebreak.
3. Missing indices are split across multiple candidate peers in parallel, each request short-timeout with retry against a different candidate on failure.
4. **Falls back to the original sender only if no peer answers.**

## Desktop vs. mobile delivery mode

- **Desktop (real multicast available)**: repair responses are themselves **multicast**, rate-limited/deduplicated — one fulfilled repair request then self-heals every receiver with that same gap, not just the requester. This is NORM (RFC 5740) / FLUTE (RFC 3926) -style behavior, chosen deliberately over pure unicast repair because it directly serves the bandwidth-fan-out goal. Repair *requests* can also be multicast with randomized-delay NACK-style suppression: a receiver that overhears someone else already asking for the same chunk skips its own duplicate request.
- **Mobile (no multicast, see [[castr-project]])**: peer discovery is via native mDNS instead of multicast gossip, and repair is strictly unicast — there is no fan-out win available on this tier, only the swarm-pull benefit of not depending solely on the original sender.

## The `IPeerTable` abstraction (cross-tier design constraint)

`IPeerTable` is populated differently per tier — multicast-carried `PEER_HAVE` gossip on desktop, mDNS + gossip on mobile — but is consumed identically by one `RepairCoordinator`. This abstraction had to be designed during the [[roadmap]]'s core milestone (M1) even though mobile is the last milestone (M4): building the repair coordinator multicast-first without this seam would force a rework once mobile work started, quietly breaking the "mobile last is free" assumption in the milestone sequencing.

## Failure modes handled by design

- **Sender offline, no peer has the chunk**: surface a stalled/failed status in the TUI/GUI after a configurable max-retry window; keep the partial `.part` file (chunk-level resumability makes this natural) and auto-resume if sender or any peer reappears.
- **Peer goes offline mid-repair**: request timeout triggers retry against the next candidate; `PEER_HAVE` entries expire on a TTL (~15s) so stale peers age out of the table on their own.
- **Thundering herd** (many receivers requesting the same missing chunk at once): randomized jitter before the first request, exponential backoff on retry, and — on desktop — the multicast-repair-response behavior above, where one response satisfies everyone at once.

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[security-model]]
