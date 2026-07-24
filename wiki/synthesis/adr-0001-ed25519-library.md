---
type: synthesis
title: "ADR-0001: Ed25519 signing library — NSec.Cryptography, targeting net10.0"
tags: [decision, spike-result, security]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# ADR-0001: Ed25519 signing library — NSec.Cryptography, targeting net10.0

**Status: Decided (M0 spike complete).** Resolves the open risk flagged in [[tech-stack]] and [[security-model]].

## Decision

Use **NSec.Cryptography** (libsodium-backed) for Ed25519 manifest signing/verification in [[castr-project]]. This requires the solution to target **net10.0** (not net8.0 as originally scaffolded) — see the framework-retarget note below.

## Why

Two concerns were flagged during planning: license compatibility with Apache-2.0 redistribution, and NativeAOT/trimming viability given the mobile targets.

- **License**: NSec is MIT-licensed; the underlying libsodium it wraps is ISC-licensed. Both are fully compatible with Apache-2.0 redistribution — no copyleft, no restriction. Confirmed via [nsec.rocks/license](https://nsec.rocks/license) and the NuGet package listing.
- **Platform/mobile fit**: NSec 26.4.0 (current as of this spike, Jan 2026 release) explicitly multi-targets `net9.0-ios18.0`, `net9.0-maccatalyst18.0`, and `net9.0-tvos18.0` in addition to plain `net9.0` — the library maintainers ship an iOS-specific build, which is a strong positive signal for the mobile unicast tier described in [[castr-project]]. Contrast with `System.Security.Cryptography`'s built-in Ed25519 support, which depends on the OS's own crypto backend and is inconsistently available across Windows/macOS/Linux — unacceptable ambiguity for a trust-critical primitive that Cli, Tui, Gui.Desktop, and eventually the mobile heads all build against.
- **Trimming**: hands-on validation (see below) found no AOT-hostile trimmer warnings and correct sign/verify behavior in a fully trimmed, self-contained publish.

## Validation performed

A throwaway spike project (`Ed25519Spike`, scratchpad only, not committed) was built and tested locally:

1. Basic sign/verify round-trip via `SignatureAlgorithm.Ed25519`, `Key.Create`, `algorithm.Sign`, `algorithm.Verify` — correct signature accepted, tampered message correctly rejected. **Pass.**
2. `dotnet publish -r win-x64 -c Release -p:PublishTrimmed=true -p:TrimMode=full` — full IL trimming with `TrimMode=full` produced **zero trimmer warnings** (no `IL2026`/`IL3050`-class AOT-hostile diagnostics), and the resulting self-contained trimmed binary still passed the sign/verify round-trip. **Pass.**
3. Full `PublishAot=true` (true Native AOT, not just trimming) could **not** be completed in this environment — the local machine lacks the native C++ linker toolchain (`vswhere.exe`/MSVC link.exe not found). This is a local tooling gap, not a library finding.

## Open follow-up (deferred, not blocking M0)

- True Native AOT publish (Windows, with VS Build Tools installed) and a real iOS device/simulator NativeAOT build (`net10.0-ios`, once mobile workloads are installed per [[roadmap]] M4) should both be run before M4 ships, as the definitive confirmation. The trimming-only result here is strong supporting evidence, not a substitute for it.
- Re-run this validation against whatever NSec version is current at M1 implementation time, since it ships frequent releases (26.1.0 → 26.4.0 observed within the same year).

## Consequence: framework retarget

NSec 26.4.0 dropped `net8.0` support entirely (NuGet restore fails with `NU1202` against net8.0). Rather than pin to an older NSec release for net8.0 compatibility, the solution was retargeted from **net8.0 to net10.0** (the current LTS, released Nov 2025, installed via `winget install Microsoft.DotNet.SDK.10` during this spike) rather than net9.0 (STS, shorter support window). All `src/*.csproj` and `tests/*.csproj` `TargetFramework` values and `global.json`'s pinned SDK version were updated accordingly. NSec's `net9.0` build resolves and runs correctly under net10.0 apps via standard .NET forward TFM compatibility (confirmed by re-running the spike under net10.0 directly).

## Where this fits

- [[castr-project]]
- [[tech-stack]]
- [[security-model]]
- [[roadmap]]
