---
type: synthesis
title: "M4 — Mobile: implementation summary"
tags: [milestone, protocol, security, platform-quirk]
sources: [castr-project-plan]
created: 2026-07-25
updated: 2026-07-25
---

# M4 — Mobile: implementation summary

M4 is complete: mobile (iOS/Android) joins the Castr swarm as a unicast TCP client rather than a multicast participant, sharing the exact same signed-manifest/Merkle/AEAD verification and TOFU trust model the desktop multicast tier has used since M1 — not a parallel, independently-trusted design. Built in two phases (core protocol + discovery, then the two mobile GUI heads in parallel), with a real security defect found and fixed in the *original M1 multicast path* along the way, not just new mobile code. Full solution: **359 tests passing**, 0 build warnings. Combined QA verdict: **PASS-WITH-CONCERNS** — no security, trust-gate, or chunk-verification defects found; two low-severity resource/UX findings and one known cross-platform GUI duplication, none blocking.

## The mobile swarm-pull tier (`Castr.Core`)

A new TCP unicast protocol, parallel to the existing UDP multicast tier — see [[wire-protocol]]'s new section for the message-level detail. `LengthPrefixedFramer` provides length-prefixed stream framing with the same before-allocation bounds check M3 established for the multicast path (verified by QA at `LengthPrefixedFramer.cs:56-65` — the length is checked against a 16 MiB ceiling *before* `new byte[length]`, not after). `SwarmPullSession` (client) and `SwarmServeListener`/`ISwarmContentSource` (server) implement the pull; `ManifestAdmission` was extracted from `ReceiverSession` so the trust/signature-verification logic is one shared implementation rather than two that could silently drift — QA confirmed both `ReceiverSession` and `SwarmPullSession` actually call it, with no duplicated inline logic left behind.

**Key-grant authorization is a cryptographic impossibility, not a policy check**: only `SenderSession.CreateSwarmContentSource()` can grant the content key (it alone holds the sender's X25519 private key needed to derive the wrap); `ReceiverSession.CreateSwarmContentSource()` — a receiver serving chunks to another mobile peer — returns `KeyUnavailableMessage` because it structurally cannot do otherwise. QA verified this by reading both code paths, not just trusting the design intent.

## Native mDNS discovery (`Castr.Core.Discovery`)

`IServiceDiscovery` (`AdvertiseAsync`/`BrowseAsync`) behind an in-memory fake for tests and two real platform bindings, multi-targeted behind an opt-in MSBuild property so a default `dotnet build` never requires mobile workloads:

- **Android** (`NsdServiceDiscovery.Android.cs`, net10.0-android): `Android.Net.Nsd.NsdManager`-backed. CI-verified to build with the real Android SDK + JDK on a dedicated `ci-mobile-android.yml` workflow (`ubuntu-latest`), which also produces a real, debug-signed, sideloadable APK artifact.
- **iOS** (`NetworkServiceDiscovery.iOS.cs`, net10.0-ios): `NWBrowser`/`NWListener` (Apple Network.framework)-backed. CI-verified on a dedicated `ci-mobile-ios.yml` workflow (`macos-latest`, real Xcode 26.6) — the first time this binding has ever actually been compiled and linked against real Apple frameworks rather than just reference-compiled on Windows.
- Research resolved a real open question from M0/M1: `NsdManager` service discovery does **not** require Android's `NEARBY_WIFI_DEVICES` runtime permission (that gates Wi-Fi Direct/Aware/RTT/LocalOnlyHotspot only) — `Castr.Gui.Android`'s manifest declares only the install-time `INTERNET`/`ACCESS_WIFI_STATE`/`ACCESS_NETWORK_STATE`.

`IPeerTable` gained `ObserveDiscovered(Endpoint, DateTimeOffset)` to feed mDNS-discovered peers in, with an `UnknownChunkPopCount = -1` sentinel (distinct from gossip-confirmed zero) so a peer only discovered but never confirmed to have chunks sorts strictly last in `RepairCoordinator`'s ranking — no change to `RepairCoordinator` itself was needed, confirming the cross-tier design bet made back in M1 (see [[repair-protocol]]).

## Defect found and fixed: Merkle proof position-relabeling (affects the original M1 multicast path too)

The biggest finding of M4 wasn't in new mobile code — it was a real, previously-unknown defect in `MerkleProof.Verify`, the core primitive every tier (multicast since M1, swarm-pull since M4) relies on to accept a chunk from any peer, trusted or not. `Verify` recomputed the root from the proof's `Steps` (sibling hashes + sides) but never checked that those `Steps` actually committed to the wire-supplied, plaintext `LeafIndex` field. A malicious relaying peer could keep chunk A's genuine, valid `Steps` (which still correctly reproduce the real root) while rewriting `LeafIndex` to claim chunk B's position — Merkle verification alone would pass, and the receiver would mark position B "have" without ever actually writing real data there, permanently stalling the transfer at that position with no further re-request.

This was found in two layers: an initial session-level guard (`if (proof.LeafIndex != chunkIndex) return;` in `ReceiverSession.HandleChunkAsync`) closed the immediately-reachable exploit, then a QA pass found a deeper bypass — an attacker who rewrites *both* the wire `ChunkIndex` and `Proof.LeafIndex` consistently sails right past that guard. The real, sound fix moved the check into the primitive itself: `MerkleProof.Verify` now derives the committed leaf position directly from `Steps`' sibling-side pattern (LSB-first: a `Left` sibling means this node was the right child, bit 1; a `Right` sibling means it was the left child, bit 0) and rejects if it disagrees with `LeafIndex`, *before* even attempting root recomputation. QA independently re-verified the fix is mathematically sound — `MerkleTree.GetProof`'s encoding is the exact bijective inverse of this derivation, including the odd-level duplicate-node case (a duplicated node is always even-indexed, so its sibling side is always `Right`/bit 0, consistent either way).

Both session-level guards (`ReceiverSession.HandleChunkAsync`, `SwarmPullSession.AcceptChunkAsync`) remain in place as defense-in-depth on top of the primitive fix — QA confirmed neither was accidentally dropped. Test coverage is adversarial, not tautological: `EndToEndTransferTests.RelabeledChunk_AlsoRewritingProofLeafIndex_StillNeverStalls` rewrites *both* the wire index and the proof's `LeafIndex` to the same consistent lie, so only the `Steps`-derived binding — not either session-level guard — can catch it; the swarm-pull side has its own equivalent test. This gap existed since M1 and was missed by M1's and M3's own security test passes because both tested `MerkleProof.Verify` and the session-level guards in isolation, never through a real session state machine exercising an actual relabeling attack end to end.

## Mobile GUI heads (`Castr.Gui.Android`, `Castr.Gui.iOS`)

Built in parallel, deliberately *not* reusing the desktop `SendViewModel`/`ReceiveViewModel` (which are hardwired to `IMulticastTransport` — architecturally wrong for a receive-only unicast swarm client). Both are CI-verified against their platform's real toolchain:

- **Android**: `Castr.Gui.Android` builds, and `ci-mobile-android.yml` produces a real debug-signed APK. Getting here required two real fixes, not workarounds: (1) `Avalonia.Android` 12.1.0's actual API is `AvaloniaMainActivity` (non-generic) with the `TApp`-generic bootstrap moved to a new `AvaloniaAndroidApplication<TApp> : Android.App.Application` class — confirmed by inspecting the package's IL metadata directly, since it ships no XML docs for these types — requiring a new `MainApplication.cs` and `App : Avalonia.Application` (fully qualified against the ambiguous `Android.App.Application` implicit global using); (2) a NuGet-restore gap where `Castr.Core.Discovery`'s own restore needed a narrower `CastrAndroidTarget` MSBuild property (adding only `net10.0-android`, never `net10.0-ios`) because a downstream `ProjectReference` override (tried first as `AdditionalProperties`, then as `SetTargetFramework`) cannot retroactively add a framework the referenced project's own restore never evaluated — restore is scoped to the referenced project's own global properties, not the consumer's.
- **iOS**: `Castr.Core.Discovery`'s net10.0-ios head (the actual new mDNS binding) genuinely compiles and links against real Xcode 26.6 — the hard CI gate. The full `Castr.Gui.iOS` app-level link is currently blocked by an external dependency gap: `NSec.Cryptography` pins `libsodium` to `[1.0.22, 1.0.23)`, and that version's package ships no `iossimulator-arm64` native slice at all (confirmed directly by inspecting the package's `runtimes/` folder — QA independently re-confirmed this too), so `ld` correctly refuses to link a device-platform object into a simulator binary. Getting the CI leg this far also required selecting Xcode 26.6 explicitly (`macos-latest`'s default active Xcode was one version behind what the .NET iOS workload requires — GitHub's macOS images carry more than one Xcode side by side, just not selected by default). The app-build step runs with `continue-on-error: true` and an inline comment explaining the gap, so CI gates on everything actually verifiable today without either hiding the problem or permanently red-X-ing a check blocked on something outside this project's control.

**Real, documented design duplication, assessed as low-risk by QA**: the Android head ended up wired to `SwarmReceiveViewModel` (+ `SwarmReceiveView`, `InAppTrustPrompt`) while the iOS head ended up wired to a separately-designed `MobileReceiveViewModel` (+ `MobileReceiveView`, implementing `ITrustPrompt` itself) — both living in the shared `Castr.Gui` library, both compiling, both covered by their own substantive test file (`SwarmReceiveFlowTests.cs`/`MobileReceiveFlowTests.cs`, both driving a real `SwarmPullSession` over in-memory transport, not mocked-away). This is genuine unintended duplication from two independent build efforts that never converged — not dead code (both are live, each required by its respective head) and not a security concern: QA confirmed *all* security enforcement (TOFU trust gate, Merkle+LeafIndex verification, AEAD, path safety) lives inside the shared `SwarmPullSession`/`ManifestAdmission` that both view-models funnel through unchanged, so neither can be "less safe." Worth consolidating deliberately in a future pass rather than as a rushed fix; tracked in [[roadmap]].

A flaky test surfaced during real CI (macOS only) while chasing this down: `MobileReceiveFlowTests.PullFromUntrustedSender_IsRejected_NoData` asserted `Status` contains `"untrusted"`, but the view-model's synchronous "peer rejected" write and its `OnTrustDenied` handler's `Dispatcher.UIThread.Post`-queued "trust denied" write both fire for that scenario — whichever lands last wins, and that ordering is platform/dispatcher-pump-timing-dependent, not a real bug (both messages correctly signal rejection). The sibling `SwarmReceiveFlowTests.cs` had already discovered and documented this exact race with an order-tolerant assertion; `MobileReceiveFlowTests.cs` was updated to match.

## QA findings

Combined QA verdict: **PASS-WITH-CONCERNS**, no security bypass or correctness defect found in the shared trust/verification core. Independently rebuilt and re-ran the full suite: 359 tests, 0 failed, 0 skipped, 0 warnings — confirmed exactly matching the claimed count. Two low-severity, explicitly non-blocking findings:

- **Unbounded connection-task accumulation in `SwarmServeListener`** — `RunAsync` accumulates every accepted connection's handler `Task` in a list and only awaits/prunes them at cancellation, so a serve loop living across many mobile pull/reconnect cycles slowly leaks completed-but-retained `Task` references; there's also no cap on concurrent handlers. Not a crash, not security-relevant (each handler's own allocations are still bounded) — worth fixing before a sender is expected to serve many mobile pullers over a long-lived session.
- **iOS `MobileReceiveViewModel` can't cancel an in-flight pull and doesn't cancel on dispose** — its local `CancellationTokenSource` is never triggered and `Dispose` doesn't cancel before disposing the session, unlike the Android `SwarmReceiveViewModel` (which has a working `CancelPull` command). A benign teardown race (disposing the session's internal pull-gate semaphore while a pull may still hold it) exists on both but is swallowed by each `PullAsync`'s catch-all. UX/robustness divergence only, not a security issue.

Neither finding blocks M4; both are tracked as open items in [[roadmap]] alongside the view-model duplication and the libsodium iOS Simulator gap.

## Where this fits

- [[roadmap]]
- [[wire-protocol]]
- [[repair-protocol]]
- [[security-model]]
- [[tech-stack]]
- [[m1-core-summary]]
- [[m3-test-ci-hardening-summary]]
