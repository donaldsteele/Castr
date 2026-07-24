---
type: concept
title: "Castr wire protocol"
tags: [protocol, decision]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# Castr wire protocol

The message set and manifest design that lets [[castr-project]] send a file once over multicast and have it verified independently by every receiver, including chunks that arrive from a peer during repair rather than from the original sender.

## Two-level chunking

**Chunks** (256 KB–1 MB) are the hash/repair granularity. Each chunk is split into **wire packets** (~1200 bytes, MTU-safe) for actual UDP datagrams. Keeping these two concepts distinct matters: the repair layer (see [[repair-protocol]]) operates on chunk indices, the transport layer operates on datagrams, and conflating them was flagged as a design trap to avoid.

## Message types

All messages travel over a configurable administratively-scoped multicast group (default `239.192.55.55`, TTL=1 by default, link-local-only for safety):

- `ANNOUNCE` (sender → multicast, periodic) — session ID, sender pubkey ID, Merkle root, transfer name, issued-at.
- `MANIFEST` (sender → multicast/unicast) — full signed manifest: per-file Merkle root, chunk size/count, signature.
- `CHUNK_DATA` (sender → multicast, carousel) — file/chunk index, payload, Merkle inclusion proof.
- `PEER_HAVE` (receiver → multicast on desktop / mDNS+gossip on mobile) — per-file chunk bitmap + receiver endpoint. This message doubles as free peer discovery — no separate discovery machinery is needed on the desktop tier.
- `CHUNK_REQUEST` / `CHUNK_RESPONSE` (receiver ↔ peer or sender) — targeted repair; see [[repair-protocol]] for why desktop repair responses are themselves multicast.
- `TRANSFER_COMPLETE` (receiver → sender/multicast) — status telemetry.

## Manifest: signed Merkle root, not a flat hash list

**Decision**: the signed manifest carries a Merkle root over BLAKE3 chunk hashes, not a flat list of per-chunk hashes. A flat list scales linearly with chunk count (~131 KB for a 1 GB file at 256 KB chunks) and must be redistributed reliably; a Merkle root is a fixed ~32 bytes regardless of file size. Critically, this is what makes chunks arriving from an **untrusted peer** during repair verifiable: the receiver only needs to trust the sender's Ed25519 signature over the root (see [[security-model]]); any peer can then hand over a chunk plus an O(log n) inclusion proof and the receiver verifies it independently.

## Replay protection

Session ID = 16 random bytes, sender-generated per transfer, plus a freshness window on `issued-at`. Trust is keyed to the sender's Ed25519 public-key fingerprint, not the session ID, so replaying an old legitimate announce is low-severity — worst case is a redundant, hash-verified rewrite of a file the receiver already has.

## Where this fits

- [[castr-project]]
- [[repair-protocol]]
- [[security-model]]
