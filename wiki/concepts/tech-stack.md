---
type: concept
title: "Castr technology stack"
tags: [decision, platform-quirk, spike-result]
sources: [castr-project-plan]
created: 2026-07-24
updated: 2026-07-24
---

# Castr technology stack

Library and framework choices for [[castr-project]], and the platform-specific quirks that constrained them. Written in C# throughout.

## Chosen

- **Hashing**: BLAKE3 via the `Blake3` NuGet package (xoofx, v3.0.2) as the Merkle-tree leaf hash — see [[wire-protocol]]. Chosen over SHA-256 (slower) and CRC32C-only (not collision-resistant, unsafe once peers relay chunks during repair per [[security-model]]). **Validated at M1 start**: the package ships as pure managed IL (`lib/net8.0/Blake3.dll`, no `runtimes/` native assets, no companion native package) — confirmed by inspecting the NuGet package contents directly, not just trusting the package description. A spike verified one-shot and incremental hashing agree, hashing is deterministic and tamper-sensitive, and a `TrimMode=full` trimmed publish produces zero trimmer warnings and the identical hash output post-trim.
- **Payload encryption**: ChaCha20-Poly1305 (AEAD) for chunk encryption, X25519 for per-receiver key agreement, HKDF-SHA256 for key derivation — all via **NSec.Cryptography** (`AeadAlgorithm.ChaCha20Poly1305`, `KeyAgreementAlgorithm.X25519`, `KeyDerivationAlgorithm.HkdfSha256`), the same library already used for Ed25519 signing, so **no new dependency**. See [[adr-0003-payload-encryption]] for the full design and a validation spike confirming key agreement, wrap/unwrap, chunk encrypt/decrypt, tamper rejection, and AAD-based position-binding all work correctly against NSec 26.4.0. **Implemented in M1.5** — see [[m1.5-encryption-summary]].
- **Transport**: raw `System.Net.Sockets.Socket`, not `UdpClient` — needed for explicit `AddMembership`/interface-index control, `MulticastLoopback` (required for same-host loopback integration tests), and `ReuseAddress`.
- **Mobile discovery**: platform-native only — Android `NsdManager`, iOS/macOS `NWBrowser`/Bonjour — behind an `IServiceDiscovery` abstraction. Pure-managed mDNS libraries are themselves multicast-socket-based and hit the exact same iOS restriction that motivated the unicast-swarm mobile tier in the first place (see [[castr-project]]), so only OS-mediated native APIs actually work here.
- **CLI**: System.CommandLine 2.0 (reached GA).
- **TUI**: Spectre.Console — `LiveDisplay`/`Progress` plus a custom `IRenderable` for a chunk-bitmap heatmap and per-peer throughput view.
- **GUI**: Avalonia UI — the only realistic single-codebase option spanning Windows/macOS/Linux desktop *and* Android/iOS (`.NET MAUI` was disqualified outright for lacking Linux desktop support). Desktop support is solid; **Android/iOS heads are beta-quality as of 2026** — this maturity gap is why the [[roadmap]] treats mobile GUI as a separate, later milestone from desktop GUI rather than bundling all Avalonia heads together.

## Resolved via M0 spike

- **Ed25519 signing library**: **NSec.Cryptography** (libsodium-backed), MIT/ISC licensed (Apache-2.0-compatible), ships explicit `net9.0-ios18.0`/`net9.0-maccatalyst18.0` targets, passed a trimming-only AOT-hostility check with zero warnings. See [[adr-0001-ed25519-library]] for the full validation. **Consequence**: this required retargeting the whole solution from net8.0 to **net10.0** (LTS), since NSec 26.x dropped net8.0 support — `global.json` and every `csproj` were updated accordingly during M0.
- **Mobile discovery**: native `Android.Net.Nsd.NsdManager` (Android) and `NWBrowser`/Network.framework via `dotnet/macios` (iOS/macOS) are both directly usable from C# with no extra binding work, confirming the `IServiceDiscovery` design. iOS additionally requires `NSLocalNetworkUsageDescription` + `NSBonjourServices: ["_castr._tcp"]` in Info.plist. See [[adr-0002-mobile-discovery]] for details and what's still deferred to M4 (hands-on device validation).

## Platform quirks that shaped the design

- **Windows**: must bind `IPAddress.Any` then join the multicast group specifying the interface explicitly — binding directly to the multicast address (as Linux permits) doesn't work the same way.
- **Linux**: usable for genuine multi-instance testing on one box via multiple loopback aliases (`127.0.0.2`, `127.0.0.3`, …); Windows is effectively single-address loopback, so this test technique doesn't transfer.
- **macOS**: default-route interface selection is unreliable when VPN/virtual adapters are present — Castr must enumerate `NetworkInterface.GetAllNetworkInterfaces()` filtering `OperationalStatus.Up && SupportsMulticast` and let the user override via a CLI flag rather than trusting auto-selection.
- **Windows Defender** commonly blocks inbound UDP for a new app on first run — this is a signed-installer-and-firewall-guidance problem, not something fixable purely in code.
- **iOS** requires `NSLocalNetworkUsageDescription` + `NSBonjourServices` entries in `Info.plist` for mDNS to work at all — easy to miss during development since Xcode debug builds can bypass the entitlement check, so this only surfaces as a failure at TestFlight/distribution time if not caught early (hence being called out as an explicit M0 spike item rather than left to be discovered in M4).

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[security-model]]
- [[roadmap]]
- [[adr-0001-ed25519-library]]
- [[adr-0002-mobile-discovery]]
- [[adr-0003-payload-encryption]]
- [[m1.5-encryption-summary]]
