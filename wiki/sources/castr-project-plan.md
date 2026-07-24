---
type: source
title: "Castr — Multi-OS LAN Multicast File Transfer Tool: Approved Project Plan"
tags: [decision, protocol, security]
authors: [Don Steele]
url: ""
raw: raw/2026-07-24-castr-project-plan.md
ingested: 2026-07-24
created: 2026-07-24
updated: 2026-07-24
---

# Castr — Multi-OS LAN Multicast File Transfer Tool: Approved Project Plan

The founding architecture and milestone plan for **Castr**, a cross-platform (Windows/macOS/Linux/iOS/Android) tool that lets a trusted sender broadcast a file once over LAN IP multicast so any number of receivers can pick it up simultaneously, with cryptographic trust, chunk-level integrity, and peer-assisted repair. Produced via two rounds of clarifying questions with the user plus a dedicated architecture-design pass, then approved. This is the wiki's first ingested source and the canonical reference for every downstream design decision until superseded.

## Key takeaways

- The single hardest constraint discovered during planning: **true IP multicast reception is not viable on iOS/Android** (Apple gates the entitlement, Android restricts it), which forces a two-tier transport design rather than one uniform multicast protocol across all five platforms. This shaped nearly every other architecture choice downstream.
- The manifest design (signed Merkle root over BLAKE3 chunk hashes, not a flat hash list) is the piece that makes peer-relayed repair chunks trustworthy without trusting the relay — this is the crux of how "receivers repair each other" stays safe.
- Several concrete open risks were flagged rather than silently assumed away: Ed25519 library choice (AOT + license viability, unresolved as of this ingest), and Avalonia's mobile-head maturity (beta as of 2026, informs why mobile GUI is sequenced separately from desktop GUI).
- graphify and llm-wiki are treated as load-bearing project infrastructure, not optional tooling — they exist specifically to answer the user's stated worry about context-window usage and session resumability across restarts.

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[roadmap]]
