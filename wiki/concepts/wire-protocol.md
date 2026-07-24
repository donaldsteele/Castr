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

**Chunks** (256 KB–1 MB) are the hash/repair granularity. The design calls for each chunk to be split into **wire packets** (~1200 bytes, MTU-safe) for actual UDP datagrams, keeping the repair layer's chunk-index granularity (see [[repair-protocol]]) distinct from the transport layer's datagram granularity.

**This split was never actually implemented in M1** — a gap that stayed latent until M2 built `Castr.Cli` against real UDP sockets instead of the in-memory test transport (see [[m2-ui-summary]]). `SenderSession` and the UDP transports put an entire encrypted chunk into a single datagram; any chunk whose ciphertext exceeds the practical UDP payload limit (~65507 bytes) fails a send outright and, worse, leaves an already-listening receiver hanging forever with a 0-byte `.part` file and no error. `Castr.Cli` mitigates this in M2 with an upfront fail-fast validation on `--chunk-size` (exit code 5, before any network activity starts), and all three M2 UI surfaces default to an 8 KB chunk size rather than the 256 KB documented here. **The 256 KB–1 MB range above is the target design, not yet a safe operating range** — actually implementing the wire-packet split is tracked as open work for M3.

## Message types

All messages travel over a configurable administratively-scoped multicast group (default `239.192.55.55`, TTL=1 by default, link-local-only for safety):

- `ANNOUNCE` (sender → multicast, periodic) — session ID, sender pubkey ID, Merkle root, transfer name, issued-at.
- `MANIFEST` (sender → multicast/unicast) — full signed manifest: per-file Merkle root (over ciphertext chunk hashes — see [[security-model]] and [[adr-0003-payload-encryption]]), chunk size/count, signature, sender's X25519 encryption public key.
- `JOIN_REQUEST` (receiver → sender, unicast) — session ID, receiver ID, receiver's X25519 public key. Sent once a receiver has verified and trusted the sender's manifest; requests the per-transfer content key. New in [[adr-0003-payload-encryption]].
- `KEY_GRANT` (sender → receiver, unicast) — session ID, receiver ID, the per-transfer content key wrapped (ChaCha20-Poly1305) under a key derived from X25519(sender, receiver) + HKDF-SHA256. New in [[adr-0003-payload-encryption]].
- `CHUNK_DATA` (sender → multicast, carousel) — file/chunk index, **encrypted** payload (ChaCha20-Poly1305 ciphertext under the content key, nonce derived from file/chunk index), Merkle inclusion proof over the ciphertext hash.
- `PEER_HAVE` (receiver → multicast on desktop / mDNS+gossip on mobile) — per-file chunk bitmap + receiver endpoint. This message doubles as free peer discovery — no separate discovery machinery is needed on the desktop tier.
- `CHUNK_REQUEST` / `CHUNK_RESPONSE` (receiver ↔ peer or sender) — targeted repair; see [[repair-protocol]] for why desktop repair responses are themselves multicast. `CHUNK_RESPONSE` payload is ciphertext, same as `CHUNK_DATA` — any peer relaying a chunk it already holds is relaying ciphertext it can't itself read unless it separately joined and holds the content key, and the receiving end verifies via the same Merkle-proof-over-ciphertext + AEAD-tag check either way.
- `TRANSFER_COMPLETE` (receiver → sender/multicast) — status telemetry.

## Manifest: signed Merkle root, not a flat hash list

**Decision**: the signed manifest carries a Merkle root over BLAKE3 chunk hashes, not a flat list of per-chunk hashes. A flat list scales linearly with chunk count (~131 KB for a 1 GB file at 256 KB chunks) and must be redistributed reliably; a Merkle root is a fixed ~32 bytes regardless of file size. Critically, this is what makes chunks arriving from an **untrusted peer** during repair verifiable: the receiver only needs to trust the sender's Ed25519 signature over the root (see [[security-model]]); any peer can then hand over a chunk plus an O(log n) inclusion proof and the receiver verifies it independently. Since [[adr-0003-payload-encryption]], the hashes in this tree are computed over each chunk's **ciphertext**, not its plaintext — the Merkle proof and the AEAD authentication tag now verify two different things (position/identity vs. content integrity), and neither substitutes for the other.

## Payload encryption and the JOIN_REQUEST/KEY_GRANT handshake

See [[adr-0003-payload-encryption]] for the full design. In short: chunk payloads (`CHUNK_DATA`/`CHUNK_RESPONSE`) are ChaCha20-Poly1305-encrypted under a per-transfer content key that never travels over multicast in the clear. A receiver obtains it via a small unicast handshake (`JOIN_REQUEST` → `KEY_GRANT`) *after* it has already decided to trust the sender's signed manifest — the data plane (chunk carousel, repair) stays fully multicast; only this per-receiver key exchange is unicast. **Implemented in M1.5** — see [[m1.5-encryption-summary]]. One deliberate deviation from the description above: `JOIN_REQUEST`/`KEY_GRANT` actually travel over the same shared multicast channel as everything else, not a separate unicast socket, matching the existing MVP trim already applied to `CHUNK_REQUEST`/`CHUNK_RESPONSE` (see [[m1-core-summary]]); `KEY_GRANT` stays confidential because it's cryptographically readable only by its addressed receiver.

## Replay protection

Session ID = 16 random bytes, sender-generated per transfer, plus a freshness window on `issued-at`. Trust is keyed to the sender's Ed25519 public-key fingerprint, not the session ID, so replaying an old legitimate announce is low-severity — worst case is a redundant, hash-verified rewrite of a file the receiver already has.

## Where this fits

- [[castr-project]]
- [[repair-protocol]]
- [[security-model]]
- [[adr-0003-payload-encryption]]
- [[m1.5-encryption-summary]]
- [[m2-ui-summary]]
