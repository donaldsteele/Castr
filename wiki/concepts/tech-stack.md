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
- **Transport**: raw `System.Net.Sockets.Socket`, not `UdpClient` — needed for explicit `AddMembership`/interface-index control, `MulticastLoopback` (required for same-host loopback integration tests), and `ReuseAddress`. **M3 added explicit `IP_MULTICAST_IF` handling on macOS** (`UdpMulticastTransport`, `OperatingSystem.IsMacOS()`-gated) — see the platform-quirk note below and [[m3-test-ci-hardening-summary]] for why this was necessary and how it was verified on real macOS CI runners.
- **Mobile discovery**: platform-native only — Android `NsdManager`, iOS/macOS `NWBrowser`/Bonjour — behind an `IServiceDiscovery` abstraction. Pure-managed mDNS libraries are themselves multicast-socket-based and hit the exact same iOS restriction that motivated the unicast-swarm mobile tier in the first place (see [[castr-project]]), so only OS-mediated native APIs actually work here. **Implemented in M4** — see [[m4-mobile-summary]]: `Castr.Core.Discovery`'s Android head (`NsdServiceDiscovery.Android.cs`) is CI-verified to build with the real Android SDK/JDK; its iOS head (`NetworkServiceDiscovery.iOS.cs`) is CI-verified to build *and link* against real Xcode — the first real toolchain verification either binding has had.
- **CLI**: System.CommandLine 2.0 (reached GA). **Implemented in M2** — see [[m2-ui-summary]]: `send`/`receive`/`trust` commands, headless progress output, `--tui` flag.
- **TUI**: Spectre.Console — `LiveDisplay`/`Progress` plus a custom `IRenderable` for a chunk-bitmap heatmap and per-peer throughput view. **Implemented in M2** as the `Castr.Tui` library (`TransferDashboard`), consumed by `Castr.Cli`'s `--tui` flag — see [[m2-ui-summary]]. The heatmap/throughput views approximate from `Castr.Core`'s aggregate `TransferProgress` snapshot (completion density and overall MB/s) rather than exact per-chunk/per-peer data, since Core doesn't expose bitmap- or per-peer-level detail in its progress event.
- **GUI**: Avalonia UI — the only realistic single-codebase option spanning Windows/macOS/Linux desktop *and* Android/iOS (`.NET MAUI` was disqualified outright for lacking Linux desktop support). **`Castr.Gui.Desktop` implemented in M2** — see [[m2-ui-summary]]: MVVM via CommunityToolkit.Mvvm, Send/Receive views, a modal TOFU trust-prompt dialog, verified with `Avalonia.Headless` (real rendering/interaction, no physical display needed). **`Castr.Gui.Android`/`Castr.Gui.iOS` implemented in M4** — see [[m4-mobile-summary]]: real CI-verified builds (a debug-signed sideloadable APK on Android; a real Xcode-linked `Castr.Core.Discovery` head on iOS, though the full iOS app link is currently blocked by a separate `libsodium` packaging gap — see the platform-quirk note below), but the two heads ended up wired to two independently-designed receive view-models (`SwarmReceiveViewModel` for Android, `MobileReceiveViewModel` for iOS) rather than one shared implementation — a real, documented duplication from two parallel build efforts, tracked in [[roadmap]] as a future unification candidate rather than something either head is currently broken by.
- **Core observability contract** (new in M2, added specifically to unblock CLI/TUI/GUI): a `TransferProgress` snapshot record + `ProgressChanged` event on both `SenderSession`/`ReceiverSession`, and an `ITrustPrompt` interface letting an interactive UI resolve `TrustOutcome.PromptRequired` (previously that outcome could never lead to trust — it silently denied). See [[m2-ui-summary]].

## Resolved via M0 spike

- **Ed25519 signing library**: **NSec.Cryptography** (libsodium-backed), MIT/ISC licensed (Apache-2.0-compatible), ships explicit `net9.0-ios18.0`/`net9.0-maccatalyst18.0` targets, passed a trimming-only AOT-hostility check with zero warnings. See [[adr-0001-ed25519-library]] for the full validation. **Consequence**: this required retargeting the whole solution from net8.0 to **net10.0** (LTS), since NSec 26.x dropped net8.0 support — `global.json` and every `csproj` were updated accordingly during M0.
- **Mobile discovery**: native `Android.Net.Nsd.NsdManager` (Android) and `NWBrowser`/Network.framework via `dotnet/macios` (iOS/macOS) are both directly usable from C# with no extra binding work, confirming the `IServiceDiscovery` design. iOS additionally requires `NSLocalNetworkUsageDescription` + `NSBonjourServices: ["_castr._tcp"]` in Info.plist. See [[adr-0002-mobile-discovery]] for details and what's still deferred to M4 (hands-on device validation).

## Platform quirks that shaped the design

- **Windows**: must bind `IPAddress.Any` then join the multicast group specifying the interface explicitly — binding directly to the multicast address (as Linux permits) doesn't work the same way.
- **Linux**: usable for genuine multi-instance testing on one box via multiple loopback aliases (`127.0.0.2`, `127.0.0.3`, …); Windows is effectively single-address loopback, so this test technique doesn't transfer.
- **macOS**: default-route interface selection is unreliable when VPN/virtual adapters are present — Castr must enumerate `NetworkInterface.GetAllNetworkInterfaces()` filtering `OperationalStatus.Up && SupportsMulticast` and let the user override via a CLI flag rather than trusting auto-selection. **Confirmed concretely at M3**: this wasn't just a VPN-adapter edge case — macOS requires `IP_MULTICAST_IF` to be set explicitly before a multicast *send* succeeds at all (Windows/Linux fall back to routing-table resolution without it), which had been silently failing every CI run on `macos-latest` since M1's real-UDP integration tests were added. Fixed in `UdpMulticastTransport`: when no interface is explicitly chosen, resolve the single unambiguous candidate NIC (or fall back to loopback) rather than leaving the send interface unset — see [[m3-test-ci-hardening-summary]].
- **Windows Defender** commonly blocks inbound UDP for a new app on first run — this is a signed-installer-and-firewall-guidance problem, not something fixable purely in code.
- **iOS** requires `NSLocalNetworkUsageDescription` + `NSBonjourServices` entries in `Info.plist` for mDNS to work at all — easy to miss during development since Xcode debug builds can bypass the entitlement check, so this only surfaces as a failure at TestFlight/distribution time if not caught early (hence being called out as an explicit M0 spike item rather than left to be discovered in M4).
- **iOS Simulator + `NSec.Cryptography`/`libsodium`** (confirmed at M4, real CI failure): `NSec.Cryptography` pins `libsodium` to `[1.0.22, 1.0.23)`, and that version's NuGet package ships a native static lib for `ios-arm64` (real devices) but **no `iossimulator-arm64` slice at all** (confirmed by inspecting `runtimes/` in the package directly) — Apple's linker correctly refuses to link a device-platform object file into a simulator binary. This blocks a full `Castr.Gui.iOS` app-level link in CI (`ci-mobile-ios.yml`, `continue-on-error`-tolerated and documented inline) even though everything above the native-link layer, including `Castr.Core.Discovery`'s real NWBrowser/NWListener iOS head, builds and links fine. Not fixable in Castr's code; needs an upstream NSec/libsodium release with simulator slices, or a move to a signed device build (M5 scope). See [[roadmap]] and [[m4-mobile-summary]].

## Where this fits

- [[castr-project]]
- [[wire-protocol]]
- [[security-model]]
- [[roadmap]]
- [[adr-0001-ed25519-library]]
- [[adr-0002-mobile-discovery]]
- [[adr-0003-payload-encryption]]
- [[m1.5-encryption-summary]]
- [[m2-ui-summary]]
- [[m3-test-ci-hardening-summary]]
- [[m4-mobile-summary]]
