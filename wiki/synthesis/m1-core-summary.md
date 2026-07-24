---
type: synthesis
title: "M1 — Castr.Core: implementation summary"
tags: [milestone, protocol, security]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# M1 — Castr.Core: implementation summary

M1 (the largest, highest-risk milestone per [[roadmap]]) is complete: the full protocol core — chunking, Merkle verification, manifest signing, trust, path safety, wire messages, transport, and the sender/receiver session state machines — is implemented, unit-tested (167 tests in `Castr.Core.Tests`), and integration-tested over real UDP sockets (4 tests in `Castr.Core.IntegrationTests`).

> **Superseded security posture, kept here as accurate history.** Everything below describes M1 exactly as it shipped, including chunk payloads traveling in the clear (matching the original M0 "integrity/authenticity only, no encryption" decision that was in force at the time). That decision was reversed shortly after M1 shipped — see [[adr-0003-payload-encryption]] and the current [[security-model]] — because LAN multicast traffic is visible to any device on the segment, not just devices the receiver has chosen to trust. The plaintext-payload behavior described here has since been retrofitted and closed in **M1.5** (see [[m1.5-encryption-summary]]) — `Castr.Core` now sends ciphertext-only chunk payloads. Don't build new work against the wire format as described in this page without checking [[wire-protocol]] and [[m1.5-encryption-summary]] first.

## What was built

- **Chunking** (`Castr.Core.Chunking`): `ChunkLayout`/`ChunkRange` (offset math), `ChunkHash` (BLAKE3, domain-separated leaf vs. internal-node hashing), `IFileSource`/`IFileSink` with both in-memory (test) and disk-backed (`FileSystemFileSource`/`FileSystemFileSink`, `.part`-file write pattern) implementations.
- **Manifest** (`Castr.Core.Manifest`): `MerkleTree` (build/prove/verify), a deterministic binary `ManifestCodec` (not JSON — see [[wire-protocol]]), `ManifestSigner`/`ManifestVerifier` over NSec Ed25519 (see [[adr-0001-ed25519-library]]).
- **Trust** (`Castr.Core.Trust`): TOFU `ITrustStore` (in-memory + JSON-file-backed with atomic writes), `TrustSeedMerger` (local store always wins), `TrustDecisionEngine` as pure policy logic covering the full deny/queue/prompt × interactive/headless matrix.
- **Security** (`Castr.Core.Security`): `PathSafety` (rejects `..`, absolute/rooted paths, UNC paths, drive-relative and NTFS-ADS tricks), `PublicKeyId`.
- **Protocol** (`Castr.Core.Protocol`): all 7 wire messages + `MessageCodec`, `ChunkBitmap`, `IPeerTable`/`PeerTable` (TTL-expiring, popcount-ranked), `RepairCoordinator` (pure planning logic), and the two orchestrators — `SenderSession` and `ReceiverSession` — that drive an actual transfer end-to-end.
- **Transport** (`Castr.Core.Transport`): `IMulticastTransport`/`IUnicastTransport`, `InMemoryNetwork` (chaos-capable pub/sub bus for pure unit tests), `UdpMulticastTransport`/`UdpUnicastTransport` (real sockets, `ReuseAddress`, `MulticastLoopback`, explicit `AddMembership`).

## Key design realization during implementation

A receiver re-serving a chunk to a peer during repair does **not** need to reconstruct the file's full Merkle tree (which it never has). It only needs to cache the exact `(payload, proof)` pair it already verified for that chunk — a `MerkleProof` is a self-contained artifact of leaf index + tree shape, so it verifies identically no matter who relays it. This simplified `ReceiverSession` considerably versus the original assumption that tree reconstruction would be needed.

## Documented M1 scope trims (deliberate, not silently dropped)

- **Single-pass chunk carousel.** `SenderSession` sends each chunk once rather than repeating rounds (FLUTE-style self-healing). Fault tolerance instead relies entirely on the (fully implemented and tested) repair path. Repeating carousel rounds is a natural, low-risk future addition.
- **CHUNK_REQUEST/RESPONSE travel over the shared multicast channel, not unicast-targeted, with no NACK suppression.** `RepairCoordinator` still computes a ranked `Target` per plan (tested, and required regardless for the mobile unicast tier in M4), but in this MVP both a peer *and* the original sender may answer the same broadcast request — there's no mechanism yet for one to suppress its answer on overhearing the other. Correctness is unaffected (the receiver just ignores a redundant answer for a chunk it already has), but it doesn't yet capture the full bandwidth-efficiency story from [[repair-protocol]].
- **One-shot receiver sessions.** A `ReceiverSession` handles exactly one transfer; there's no mechanism yet for a long-running receiver daemon to accept many sequential/concurrent sessions. That's CLI/TUI-layer orchestration, appropriately deferred to M2.

## Testing approach validated

- `Castr.Core.Tests`: pure unit tests for every component in isolation, plus `EndToEndTransferTests` — real `SenderSession` + `ReceiverSession`(s) wired over `InMemoryNetwork`, covering the happy path, 5-receiver fan-out, untrusted-sender rejection, tampered-chunk detection with repair recovery, and peer-served repair for a deterministically dropped chunk (via a test-only `FilteringMulticastTransport` decorator).
- `Castr.Core.IntegrationTests`: the same stack over **real UDP sockets** on loopback, including a `ChaosTransport` decorator (seeded-RNG loss, distinct from `InMemoryNetwork`'s built-in chaos) proving repair also works against actual OS socket behavior, not just the in-memory abstraction.
- A real bug was caught during integration-test authoring: an initial draft passed a frozen `FakeClock` into a test running over real wall-clock time, silently preventing `RepairCoordinator`'s timeout-based retry from ever firing. Fixed by using `SystemClock.Instance`. Worth remembering as a pattern: **`FakeClock` belongs in deterministic unit tests only — any test with real sockets/real delays needs the real clock.**

## QA review and fix

An independent QA subagent pass reviewed M1 against the wiki's own stated design (not just the code in isolation) and attempted to actively break path safety, Merkle verification, and manifest signature checks — no bypass was found for any of those (a meaningful negative result, not just "looked fine"). It did find one real, confirmed defect: `MessageCodec.WriteInt32Array` (used for `ChunkRequestMessage.ChunkIndices`) length-prefixed the array with a `UInt16` count, capping a single repair-request batch at 65,535 entries. `RepairCoordinator`/`ReceiverSession` batch *all* missing chunks for one target into a single plan with no size limit, so a receiver cold-starting a bulk repair against the sender (the common case: no peers announced yet) on a large file — realistically ≥16–64 GB at default 256 KB–1 MB chunk sizes — would throw `OverflowException` and crash the repair path. Confirmed by direct reproduction, not just code reading. **Fixed**: widened the count prefix to `UInt32` (matching the width already used for `Payload`), removing any realistic ceiling; added a regression test (`ChunkRequest_MoreThan65535Indices_RoundTrips`, 70,000 indices) reproducing the exact scenario QA found.

## Open risk carried into M3

CI multicast behavior on GitHub-hosted runners (windows-latest, macos-latest, ubuntu-latest) is **unverified** — this M1 work only confirms multicast loopback works on this local Windows dev machine. Runner-specific firewall/sandboxing differences (especially macOS) could cause `Castr.Core.IntegrationTests` to fail or hang in CI even though it passes locally. Flag as the first thing to check when `ci.yml` first runs the full matrix against this code (M3 explicitly owns the CI hardening pass, but this is worth verifying as soon as this code is pushed, not deferred silently).

## Where this fits

- [[roadmap]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[adr-0001-ed25519-library]]
- [[adr-0003-payload-encryption]]
- [[m1.5-encryption-summary]]
