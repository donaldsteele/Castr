---
type: synthesis
title: "ADR-0003: Payload encryption (reverses the M0 no-encryption decision)"
tags: [decision, spike-result, security, protocol]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# ADR-0003: Payload encryption (reverses the M0 no-encryption decision)

**Status: Decided, design validated, not yet implemented in code.** This reverses the decision recorded in [[security-model]]'s original "No payload encryption" section and in the founding plan ([[castr-project-plan]]).

## Decision

Castr **must** encrypt chunk payloads. This is now a mandatory property of the protocol, not an optional mode — there is no plaintext fallback.

## Why the original decision was wrong

The M0 plan reasoned: "this is a trusted-LAN tool... anyone already trusted can see plaintext on the wire." That framing conflated two different things: *trusting a sender's identity* (which the Ed25519/TOFU trust model already handles) and *trusting every device that can passively observe LAN multicast traffic* (which no part of the design ever actually established). Multicast traffic is visible to any device with network access to the segment — a compromised IoT device, an unauthorized laptop on shared office/guest WiFi, a passive packet capture — regardless of whether that device runs Castr or has any opinion about the sender's trustworthiness. A receiver choosing to trust a sender says nothing about who else is listening. For a tool explicitly designed to move "sensitive" files, that gap is a real leak, not a theoretical one.

## Scheme (validated against NSec.Cryptography — no new dependency)

- **Identity keys, unchanged**: each Castr identity keeps its Ed25519 signing keypair exactly as before ([[adr-0001-ed25519-library]]), used for manifest/trust exactly as today.
- **New: an X25519 encryption keypair per identity**, kept deliberately separate from the Ed25519 signing keypair (not derived via Ed25519→X25519 conversion) to avoid cross-protocol key-reuse pitfalls. Both sender and receiver identities carry one.
- **Content key**: the sender generates a fresh random 256-bit symmetric key per transfer ("content key"). Never reused across transfers.
- **Chunk encryption**: each chunk is encrypted with ChaCha20-Poly1305 (AEAD) under the content key. Nonce is deterministic — derived from `(file index, chunk index)` — which is safe precisely because the content key is fresh per transfer and never reused. Additional Authenticated Data (AAD) binds each ciphertext to `(session ID, file index, chunk index)`, so a valid ciphertext can't be replayed into a different position even by someone who legitimately holds the content key.
- **Merkle tree now covers ciphertext, not plaintext.** This is a deliberate separation of concerns: the Merkle proof answers "is this the right encrypted chunk from the signed transfer" (position/identity binding back to the sender's signature), while the AEAD tag independently answers "is this ciphertext unmodified and authentic" (its own tamper-detection, unrelated to the Merkle tree). A relaying peer during repair (see [[repair-protocol]]) still can't feed a receiver corrupted or substituted data — verification composes the same way it did before, just one layer earlier.
- **Content-key distribution (the actual hard part)**: Castr's trust model is dynamic — a receiver decides to trust a sender's signed manifest on the fly (TOFU), not via a pre-arranged pairwise secret. So the content key can't simply ride along in the clear inside the manifest; it has to reach each trusting receiver confidentially, one at a time. New handshake:
  1. Once a receiver verifies and trusts a sender's manifest, it sends a unicast **JOIN_REQUEST** (session ID, receiver ID, receiver's X25519 public key) to the sender.
  2. The sender computes an X25519 shared secret with that receiver's public key, derives a wrapping key via HKDF-SHA256, wraps the content key with ChaCha20-Poly1305, and returns it via unicast **KEY_GRANT**.
  3. The receiver reverses the ECDH computation, unwraps the content key, and can now decrypt the multicast chunk stream.
  
  This keeps the **data plane** (the chunk carousel and repair traffic) exactly as multicast and one-send-many-receive as before — only the small per-receiver key handshake is unicast, not the file data itself.
- **Authorization boundary is unchanged, deliberately**: the sender grants a key to anyone who completes JOIN_REQUEST — i.e. anyone who already independently verified and trusted the sender's Ed25519 signature. Encryption's job here is to stop *passive* eavesdroppers who never made that trust decision at all, not to introduce a new sender-side receiver allowlist. A sender-side allowlist is a plausible future hardening (noted, not required now).
- **Known residual disclosure, flagged deliberately rather than silently**: this design encrypts chunk **payloads** only. The MANIFEST message (file names, sizes) is not encrypted. For deployments where filename/metadata confidentiality matters as much as content confidentiality, encrypting the manifest's file list under the same content key is a documented, straightforward follow-up — just not in this initial scope.

## Validation performed

A throwaway spike (scratchpad only, not committed) exercised the full flow against NSec.Cryptography 26.4.0 — the same library already used for Ed25519 signing, so **no new dependency is introduced**:

1. X25519 key agreement (`KeyAgreementAlgorithm.X25519`) between two parties produces matching shared secrets on both sides. **Pass.**
2. HKDF-SHA256 (`KeyDerivationAlgorithm.HkdfSha256`) derives a ChaCha20-Poly1305 key from the shared secret. **Pass.**
3. Wrapping a random 32-byte content key with ChaCha20-Poly1305 produces the expected 48-byte ciphertext (32 + 16-byte tag); unwrapping on the other side recovers the exact original key. **Pass.**
4. Encrypting a chunk payload with the content key and a nonce derived from `(fileIndex, chunkIndex)` produces the expected ciphertext length (plaintext + 16-byte tag); decryption recovers the exact plaintext. **Pass.**
5. Flipping a single ciphertext byte causes AEAD decryption to correctly return null/fail rather than silently producing garbage. **Pass** — confirms tamper detection.
6. Decrypting with the wrong AAD (simulating a chunk relayed into the wrong position) correctly fails. **Pass** — confirms position-binding.

## Consequence: implementation status and sequencing

**This is a design decision only as of this ADR — the M1 code currently in `Castr.Core` sends plaintext chunk payloads**, since M1 was implemented before this reversal. This must be retrofitted before [[roadmap|M2]] (CLI/TUI/desktop GUI) builds further on top of the current wire format, since M2 would otherwise need rework once encryption lands. Concretely, the retrofit touches:

- New X25519 keypair generation alongside existing Ed25519 keys.
- Two new wire messages: `JOIN_REQUEST`, `KEY_GRANT` (see [[wire-protocol]]).
- `ChunkDataMessage`/`ChunkResponseMessage` payload semantics: ciphertext, not plaintext.
- `MerkleTree`/manifest: built over ciphertext chunk hashes.
- `SenderSession`: handle `JOIN_REQUEST`, encrypt chunks before sending.
- `ReceiverSession`: send `JOIN_REQUEST` after trusting a manifest, hold the unwrapped content key, decrypt chunks after Merkle verification (or as part of the same verify step).
- New unit + integration tests: key agreement, wrap/unwrap, chunk encrypt/decrypt round-trip, tamper rejection, wrong-AAD rejection, and an end-to-end encrypted-transfer test analogous to the existing `EndToEndTransferTests`.

See [[roadmap]] for how this is now sequenced.

## Where this fits

- [[castr-project]]
- [[security-model]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[tech-stack]]
- [[adr-0001-ed25519-library]]
- [[m1-core-summary]]
- [[roadmap]]
