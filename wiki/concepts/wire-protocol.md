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

**Chunks** (256 KB–1 MB) are the hash/repair granularity. Each chunk is split into **wire packets** (~1200 bytes, MTU-safe, configurable) for actual UDP datagrams, keeping the repair layer's chunk-index granularity (see [[repair-protocol]]) distinct from the transport layer's datagram granularity.

**Implemented in M3** — see [[m3-test-ci-hardening-summary]]. `ChunkPacketizer`/`ChunkPacketAssembler` slice a chunk's already-encrypted ciphertext into ordered, identity-keyed packets (`(fileIndex, chunkIndex, packetIndex)`, message tag 11); the Merkle inclusion proof rides only on packet 0. A separate, generic `WirePacketizer`/`PacketReassembler` (tag 10) handles fragmentation for oversized control messages (e.g. a large multi-file `MANIFEST`). Splitting happens *after* encryption and hashing, so packetization has zero interaction with the crypto/Merkle layers described below and in [[security-model]] — verified directly by tampering a single wire packet mid-transfer and confirming the existing Merkle-proof/AEAD-tag checks still catch it post-reassembly. A receiver accumulates a chunk's packets across repair rounds and across sources (the same deterministic slicing means a re-sent packet from the sender or a relaying peer is byte-identical to the original), which is what lets large chunks survive real packet loss rather than needing a single lossless round — proven with a 256 KB chunk completing byte-identically at 10% real per-packet UDP loss. At the documented 1200-byte default, even the 8 KB chunk size every M2 UI surface defaults to now packetizes (confirmed via traffic capture: largest observed datagram is exactly 1200 bytes, well under Ethernet's 1500-byte MTU) — the crash/hang bug M2 found and mitigated with a Cli-side `--chunk-size` guard is gone, and that guard has been removed in favor of a generous 16 MiB memory-safety ceiling on reassembly buffering.

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

## The mobile swarm-pull tier (unicast TCP, M4)

Mobile devices can't reliably join true IP multicast (see [[castr-project]]), so [[repair-protocol]]'s mobile tier needed its own transport — implemented in M4 as a parallel TCP unicast protocol, sharing the same trust/verification model as the multicast tier above rather than inventing a separate one.

- **Framing**: `LengthPrefixedFramer` — a 4-byte big-endian length prefix per message over the TCP stream, with a 16 MiB max-length bound checked *before* allocating the receive buffer (mirroring [[m3-test-ci-hardening-summary]]'s `PacketReassembler` memory-exhaustion fix for the multicast path).
- **New message types**: `ManifestRequestMessage` / a `ManifestMessage` response (reuses the same signed manifest as multicast), `ChunkPullRequestMessage` / `ChunkPullResponseMessage` (has a `Found` flag — the peer may not hold the requested chunk yet), and `KeyUnavailableMessage`.
- **`SwarmPullSession`** (client): connects over TCP, requests the manifest, runs the shared `ManifestAdmission` trust gate (extracted from `ReceiverSession` so both tiers use one implementation, not two that could drift), performs the same `JOIN_REQUEST`/`KEY_GRANT`-equivalent key exchange over the stream, then pulls/verifies/decrypts/writes chunks — exposing the same `TransferProgress`/`ProgressChanged` contract as the multicast tier ([[m2-ui-summary]]) so `Castr.Tui`/`Castr.Gui` don't need tier-specific UI code. Resumable: pulling again from the same or a different peer only requests still-missing chunks.
- **`SwarmServeListener` / `ISwarmContentSource`** (server): either a sender (`SenderSession.CreateSwarmContentSource()`) or a receiver that already has some chunks (`ReceiverSession.CreateSwarmContentSource()`) can serve a manifest + ciphertext to an incoming pull — but **only a sender can grant the content key**. A receiver-side content source returns `KeyUnavailableMessage` instead, and this is a real cryptographic impossibility, not just a code-level check: receivers never hold the sender's X25519 private key needed to derive it.
- Any chunk pulled this way is verified identically to the multicast path: the same signed Merkle root, the same `MerkleProof.Verify` (see the defect-and-fix note below), the same AEAD tag check. A pull from an untrusted or malicious peer is just as safe as a multicast repair response from one.

## Manifest: signed Merkle root, not a flat hash list

**Decision**: the signed manifest carries a Merkle root over BLAKE3 chunk hashes, not a flat list of per-chunk hashes. A flat list scales linearly with chunk count (~131 KB for a 1 GB file at 256 KB chunks) and must be redistributed reliably; a Merkle root is a fixed ~32 bytes regardless of file size. Critically, this is what makes chunks arriving from an **untrusted peer** during repair verifiable: the receiver only needs to trust the sender's Ed25519 signature over the root (see [[security-model]]); any peer can then hand over a chunk plus an O(log n) inclusion proof and the receiver verifies it independently. Since [[adr-0003-payload-encryption]], the hashes in this tree are computed over each chunk's **ciphertext**, not its plaintext — the Merkle proof and the AEAD authentication tag now verify two different things (position/identity vs. content integrity), and neither substitutes for the other.

**Defect found and fixed in M4** (see [[m4-mobile-summary]]): `MerkleProof.Verify` recomputes the root from a leaf hash and the proof's `Steps` (sibling hashes + sides), but never checked that the proof's separate, plaintext, wire-mutable `LeafIndex` field actually matched the position those `Steps` commit to. A malicious relaying peer could keep a genuine chunk's valid `Steps` (which correctly reproduce the root) while rewriting `LeafIndex` to claim a different wire position — Merkle verification alone would still pass, permanently stalling a receiver at whatever position got relabeled. Fixed by deriving the committed leaf position from `Steps`' sibling-side pattern inside `Verify` itself and rejecting if it disagrees with `LeafIndex`, rather than trusting the field or relying only on a session-level "does the claimed index match?" guard (`ReceiverSession`/`SwarmPullSession` both still carry that guard too, as defense-in-depth, but the primitive-level fix is what actually closes the gap). This affects the original M1 multicast repair path as much as M4's new swarm-pull tier, since both rely on the same `MerkleProof.Verify`.

## Payload encryption and the JOIN_REQUEST/KEY_GRANT handshake

See [[adr-0003-payload-encryption]] for the full design. In short: chunk payloads (`CHUNK_DATA`/`CHUNK_RESPONSE`) are ChaCha20-Poly1305-encrypted under a per-transfer content key that never travels over multicast in the clear. A receiver obtains it via a small unicast handshake (`JOIN_REQUEST` → `KEY_GRANT`) *after* it has already decided to trust the sender's signed manifest — the data plane (chunk carousel, repair) stays fully multicast; only this per-receiver key exchange is unicast. **Implemented in M1.5** — see [[m1.5-encryption-summary]]. One deliberate deviation from the description above: `JOIN_REQUEST`/`KEY_GRANT` actually travel over the same shared multicast channel as everything else, not a separate unicast socket, matching the existing MVP trim already applied to `CHUNK_REQUEST`/`CHUNK_RESPONSE` (see [[m1-core-summary]]); `KEY_GRANT` stays confidential because it's cryptographically readable only by its addressed receiver.

## Send/receive pipelining and socket buffering (M6)

Neither of these is a wire-format change — no new message types, no change to what any message contains — but both change how fast the existing messages actually move, and are worth documenting alongside the message-level design above. Full investigation detail (three review rounds, benchmark numbers, what didn't work) is in [[m6-throughput-pipelining]]; this section covers the resulting steady-state design.

- **Send path**: `SenderSession`'s chunk carousel and chunk-repair batches send wire packets via a bounded-concurrency `Parallel.ForEachAsync` (constructor parameter `sendWindowSize`) rather than one `await` per packet. The shipped default is **1** — behaviorally a no-op versus the original fully-sequential design — because the safe window is receiver-hardware-dependent and only validated so far on a single loopback machine. `castr send --send-window-size <n>` lets a deployment that has validated a higher value on its own receiver hardware opt in. A known, documented, non-default-affecting gap: the carousel and repair-handler loops each cap concurrency independently, so simultaneous heavy repair traffic during an in-flight carousel can transiently push real concurrent sends to 2× the configured window.
- **Receive path**: `UdpMulticastTransport`'s socket read is decoupled from `ReceiverSession`'s downstream processing (Merkle/AEAD verify, disk write, outbound `PEER_HAVE` broadcast) via a dedicated reader task that drains `socket.ReceiveFromAsync` into a bounded `Channel<ReceivedPacket>` (capacity 4096); `ReceiveAsync` just enumerates the channel at the consumer's own pace. This is the fix that actually mattered — see [[m6-throughput-pipelining]] for why the send-side window alone couldn't safely go above 1 without it. The socket also gets an explicit, best-effort 4 MB `SO_RCVBUF`/`SO_SNDBUF`, since the OS default is often much smaller and this was a design cue taken directly from `uftp-multicast`, a reference near-wire-speed UDP multicast implementation.
- Both are complementary, not substitutes for each other: the channel/buffer combination absorbs bursts and jitter, but sustained throughput is still capped by `ReceiverSession`'s true per-packet processing rate, which M6 did not change.

## Replay protection

Session ID = 16 random bytes, sender-generated per transfer. Trust is keyed to the sender's Ed25519 public-key fingerprint, not the session ID, so replaying an old legitimate announce is low-severity — worst case is a redundant, hash-verified rewrite of a file the receiver already has.

**Gap, confirmed during M3's security test pass (see [[m3-test-ci-hardening-summary]]): the freshness window on `issued-at` described in earlier design notes was never actually implemented.** `ReceiverSession` never inspects `IssuedAt` against the clock, and `ANNOUNCE` is currently ignored entirely. A test does confirm `IssuedAt` is covered by the manifest's Ed25519 signature (so a replayed manifest can't have its timestamp forged fresh), which anchors a future freshness check, but nothing enforces one today. Flagged as low severity for the reason above (fingerprint-keyed trust, not session/timestamp-keyed) rather than fixed silently in a test-only pass — revisit if session-replay risk is ever reassessed.

## Where this fits

- [[castr-project]]
- [[repair-protocol]]
- [[security-model]]
- [[adr-0003-payload-encryption]]
- [[m1.5-encryption-summary]]
- [[m2-ui-summary]]
- [[m3-test-ci-hardening-summary]]
- [[m4-mobile-summary]]
- [[m6-throughput-pipelining]]
