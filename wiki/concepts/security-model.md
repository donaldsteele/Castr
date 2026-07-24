---
type: concept
title: "Castr security model"
tags: [security, decision]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# Castr security model

The trust, signing, integrity, and path-safety design for [[castr-project]]. Confirmed posture: **integrity and authenticity, not confidentiality** — this is a trusted-LAN tool, not a confidential-transport tool, and that framing drove every choice below.

## Trust store (TOFU)

- Trust store is trust-on-first-use: entries are `trusted` / `blocked` / `unknown`, keyed by the sender's Ed25519 public-key fingerprint (not by session — see [[wire-protocol]] replay-protection notes).
- **Pre-deployment**: a seed trust file (`trusted-senders.seed.json`) ships alongside the receiver package/config and is merged into the local trust store on first run; the local store is authoritative for all changes made afterward.
- **Unknown-sender policy is configurable, default-deny in headless/non-interactive mode**: `--on-unknown-sender=deny|queue|prompt`. Interactive contexts (TUI/GUI) can prompt the user directly. This was an explicit decision point — the alternative (silently trusting or silently dropping) was rejected in favor of an operator-controlled, safe-by-default policy.

## Signing and integrity

- Ed25519 signs the manifest's Merkle root (see [[wire-protocol]]) — not individual chunks, since the Merkle proof already ties each chunk back to the signed root.
- BLAKE3 is the chunk/file hash function (see [[tech-stack]] for the library rationale); a CRC32C pre-filter may be used only as a cheap, non-security-relevant garbage filter before paying for BLAKE3 + Merkle-proof verification — CRC32C is explicitly *not* collision-resistant and unsafe as the trust boundary once chunks are relayed by untrusted peers during repair.

## No payload encryption (confirmed decision)

Chunk payloads travel in the clear. This was a deliberate choice given the trusted-LAN framing: anyone already trusted can see plaintext on the wire, but tampering or corruption is always caught by hash/signature verification. Revisit only if a future requirement introduces untrusted network segments between sender and receiver.

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
