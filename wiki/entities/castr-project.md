---
type: entity
title: "Castr"
tags: [decision]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-25
graph:
  node_id: product:castr
  node_type: product
  aliases: [Castr]
---

# Castr

A cross-platform LAN multicast file transfer tool: a trusted sender broadcasts a file once over IP multicast and any number of receivers on the network pick it up simultaneously, verifying it via a signed chunk-hashed manifest and repairing lost chunks from peers rather than the sender. Targets Windows, macOS, Linux, iOS, and Android; ships as a CLI with a colorful TUI, plus an optional GUI.

## Product shape

- CLI (System.CommandLine) + TUI (Spectre.Console) as the primary interface; GUI (Avalonia) is a parallel, optional surface — see [[tech-stack]].
- Repo: `Castr`, public on GitHub, Apache-2.0 licensed.
- Full automated test suite is a hard requirement for every component, not an afterthought — see [[roadmap]] for how test coverage is sequenced per milestone.

## Architecture

The two load-bearing designs are the [[wire-protocol]] (how a file gets from sender to many receivers in one send) and the [[repair-protocol]] (how gaps get filled by peers instead of re-burdening the sender). Both sit on top of the [[security-model]] (Ed25519 trust + BLAKE3 chunk integrity + **ChaCha20-Poly1305 payload encryption**, with the Merkle tree built over ciphertext hashes). Note this reverses the original plan's "no payload encryption" call, which was a mistake — LAN multicast traffic is visible to every device on the segment, not just those the receiver has chosen to trust, so plaintext payloads leaked regardless of the sender/receiver trust relationship. See [[adr-0003-payload-encryption]] for the reversal and [[m1.5-encryption-summary]] for the implementation.

## Why mobile is architecturally different

iOS and Android cannot reliably join true IP multicast groups — Apple gates the multicast networking entitlement outside approved AV-streaming use cases, and Android restricts/throttles multicast for battery reasons. Castr's mobile devices therefore participate as a **unicast swarm client** tier: they discover peers via native mDNS/Bonjour (`NsdManager` on Android, `NWBrowser` on iOS/macOS) and pull chunks over unicast TCP from any peer — sender or another receiver — that already has them, verifying each chunk against the same signed Merkle root as the multicast tier. This is why `IPeerTable` (see [[repair-protocol]]) had to be designed as an abstraction from the start even though mobile is built last (per [[roadmap]]).

## Where this fits

- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[roadmap]]
