---
type: concept
title: "Castr security model"
tags: [security, decision]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# Castr security model

The trust, signing, integrity, confidentiality, and path-safety design for [[castr-project]]. Confirmed posture: **integrity, authenticity, AND confidentiality** — chunk payloads are encrypted, not just signed and hashed. (This reverses an earlier "integrity/authenticity only" decision — see [[adr-0003-payload-encryption]] for why: LAN multicast traffic is visible to any device on the segment, not just devices the receiver has decided to trust, so plaintext payloads leaked to passive eavesdroppers regardless of the sender/receiver trust relationship.)

## Trust store (TOFU)

- Trust store is trust-on-first-use: entries are `trusted` / `blocked` / `unknown`, keyed by the sender's Ed25519 public-key fingerprint (not by session — see [[wire-protocol]] replay-protection notes).
- **Pre-deployment**: a seed trust file (`trusted-senders.seed.json`) ships alongside the receiver package/config and is merged into the local trust store on first run; the local store is authoritative for all changes made afterward.
- **Unknown-sender policy is configurable, default-deny in headless/non-interactive mode**: `--on-unknown-sender=deny|queue|prompt`. Interactive contexts (TUI/GUI) can prompt the user directly. This was an explicit decision point — the alternative (silently trusting or silently dropping) was rejected in favor of an operator-controlled, safe-by-default policy.

## Signing and integrity

- Ed25519 signs the manifest's Merkle root (see [[wire-protocol]]) — not individual chunks, since the Merkle proof already ties each chunk back to the signed root.
- BLAKE3 is the chunk/file hash function (see [[tech-stack]] for the library rationale); a CRC32C pre-filter may be used only as a cheap, non-security-relevant garbage filter before paying for BLAKE3 + Merkle-proof verification — CRC32C is explicitly *not* collision-resistant and unsafe as the trust boundary once chunks are relayed by untrusted peers during repair.
- With encryption (below), the Merkle tree is built over **ciphertext** chunk hashes, not plaintext. The Merkle proof and the AEAD authentication tag now do two distinct jobs: the proof says "this is the right encrypted chunk from the signed transfer" (position/identity binding), the tag says "this ciphertext is unmodified and authentic" (its own independent tamper-detection). A relaying peer during repair still can't feed a receiver corrupted or substituted data — verification composes the same way, one layer earlier. **M3 confirmed this composition holds even after the [[wire-protocol]] chunk/wire-packet split**: encryption and Merkle-hashing happen before a chunk is sliced into wire packets, so packetization is purely a transport-layer concern; tampering a single wire packet mid-transfer is still caught post-reassembly by the Merkle proof or the AEAD tag (see [[m3-test-ci-hardening-summary]]).

## Payload encryption (confirmed decision, reverses the original M0 choice)

**See [[adr-0003-payload-encryption]] for the full design and rationale — summarized here:**

- Every identity (sender and receiver) holds an X25519 encryption keypair, separate from its Ed25519 signing keypair.
- The sender generates a fresh random 256-bit "content key" per transfer and encrypts every chunk with ChaCha20-Poly1305 (AEAD) under it — nonce derived from `(file index, chunk index)`, AAD binds the ciphertext to `(session ID, file index, chunk index)` so it can't be replayed into the wrong position.
- The content key is distributed confidentially, per receiver: once a receiver trusts a sender's signed manifest, it sends a unicast `JOIN_REQUEST` with its X25519 public key; the sender derives a shared secret via X25519 + HKDF-SHA256, wraps the content key with ChaCha20-Poly1305, and returns it via unicast `KEY_GRANT`. The chunk carousel and repair traffic itself stay fully multicast — only this small per-receiver key handshake is unicast.
- The authorization boundary is unchanged: a sender grants the key to anyone who completes the handshake (i.e. anyone who already independently trusted the sender's signature). Encryption stops *passive* eavesdroppers who never made that trust decision, not active participants — that's a receiver-side decision today, same as before.
- **Known scope boundary**: only chunk payloads are encrypted in this design, not the MANIFEST's file names/sizes. Encrypting manifest metadata too is a documented, straightforward follow-up if a deployment needs it — not required for the initial design.
- **Implementation status**: implemented in [[m1.5-encryption-summary]] (milestone M1.5). `Castr.Core` now sends only ciphertext chunk payloads; QA-reviewed with confirmed adversarial checks (tamper rejection, cross-context AAD rejection, nonce-uniqueness across files, MITM-proof manifest binding, TOFU not bypassable via the new handshake).

## M3 security test pass

A dedicated test pass (see [[m3-test-ci-hardening-summary]]) added 11 tests across path-traversal (null-byte injection), tamper detection (a MITM X25519-key-swap rejection test — swapping the manifest's encryption key breaks the Ed25519 signature — plus a composed Merkle-position + AEAD-content binding test), trust-store tampering (malformed/corrupt trust files fail closed rather than silently starting empty; conflicting duplicate entries resolve last-in-file-wins), and TOFU bypass (a blocked sender stays denied even with an accepting prompt; a throwing prompt propagates and persists nothing). One real gap was found and explicitly left unfixed rather than silently patched: the replay-protection freshness window described in [[wire-protocol]] is not implemented anywhere in `ReceiverSession` — see that page's "Replay protection" section for the full detail; low severity since trust is keyed to the sender's fingerprint, not session/timestamp.

## Path safety (traversal prevention)

A sender may suggest a relative destination filename/subpath, but:
- Relative paths **must never contain `..`** — no upward traversal is permitted, subdirectories going down are fine.
- A receiver-configured **explicit absolute destination** always takes precedence over any sender-supplied hint.

This directly answers the "optional location to write the file" requirement from the original request: the "optional location" is a receiver-side override, not something a sender can dictate unrestricted. This was flagged during planning as a real path-traversal vulnerability class (`../../../etc/passwd`-style attacks) if the naive "just honor the sender's suggested path" approach had been taken.

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[tech-stack]]
- [[adr-0003-payload-encryption]]
- [[m1.5-encryption-summary]]
- [[m3-test-ci-hardening-summary]]
